using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Native;
using PearUI.Shell;
using PearUI.Themes;
using Forms = System.Windows.Forms;

namespace PearUI.Dock;

/// <summary>The dock: pinned + running apps, Downloads and Recycle Bin, with magnification and bounce.</summary>
public partial class DockWindow : OverlayWindow
{
    private enum Kind { App, Separator, Downloads, Trash, AllApps }

    private sealed class Item
    {
        public Kind Kind;
        public string Key;
        public string Name;
        public PinnedApp Pin;
        public RunningApp Running;
        public string IconSource;
        public ItemView View;
        public double Size;
        public double X;
        public double Top;
    }

    private sealed class ItemView
    {
        public Grid Root;
        public Image Image;
        public TranslateTransform Bounce;
        public Ellipse Dot;
        public Rectangle Line; // separators only
    }

    private const double Pad = 6;
    private const double BottomMargin = 6;
    private const double DotSpace = 5;
    private const double TipRoom = 44;
    private const string TrashParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private static readonly string DownloadsPath =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private readonly Forms.Screen _screen;
    private readonly DockBackdropWindow _backdrop = Backdrop.BlurAvailable ? new DockBackdropWindow() : null;
    private readonly Dictionary<string, ItemView> _views = new();
    private List<Item> _items = new();
    private AppBar _appBar;

    private bool _mouseInside;
    private double _mouseX;
    private DateTime _lastInside = DateTime.MinValue;
    private bool _menuOpen;
    private bool _rendering;
    private DateTime _animateUntil;
    private bool _concealed;
    private bool _fullScreenHidden;
    private double _scale = 1;
    private int _winLeftPx, _winTopPx;
    private long _trashCount = -1;

    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private static AppSettings S => SettingsStore.Current;
    private static double IconSize => Math.Clamp(S.DockIconSize, 24, 128);
    private static double MagSize => S.DockMagnification ? Math.Clamp(Math.Max(S.DockMagnifiedSize, IconSize), 24, 160) : IconSize;
    private static double Gap => Math.Round(IconSize * 0.14);
    private static double PlateHeight => IconSize + Pad * 2 + DotSpace;
    private static double WindowHeightDip => MagSize + Pad * 2 + DotSpace + BottomMargin + TipRoom;
    private double ConcealOffset => PlateHeight + BottomMargin + 12;

    public DockWindow(Forms.Screen screen)
    {
        _screen = screen;
        InitializeComponent();

        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        // Files dragged from File Explorer (or the Downloads stack) can be dropped on Dock items.
        AllowDrop = true;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _edgeTimer.Tick += (_, _) => EdgeTick();
        _slowTimer.Tick += (_, _) => SlowTick();
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    /// <summary>Shows the blurred backdrop and the dock above it.</summary>
    public void ShowDock()
    {
        if (_backdrop != null)
        {
            _backdrop.Show();
            Owner = _backdrop; // owned windows always stay above their owner
        }
        Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _appBar = new AppBar(this, Win32.ABE_BOTTOM,
            () => new Win32.RECT(_screen.Bounds.Left, _screen.Bounds.Top, _screen.Bounds.Right, _screen.Bounds.Bottom),
            () => (int)Math.Ceiling((PlateHeight + BottomMargin) * Win32.GetScaleForScreen(_screen)))
        {
            ReserveSpace = !S.DockAutoHide,
            PositionWindow = PositionFromReservedRect,
        };
        _appBar.FullScreenChanged += _ => SlowTick();
        _appBar.Attach();

        if (S.DockAutoHide)
        {
            _concealed = true;
            HideOffset.Y = ConcealOffset;
        }

        WindowTracker.Instance.AppsChanged += RebuildItems;
        RebuildItems();
        _edgeTimer.Start();
        _slowTimer.Start();
        SlowTick();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        WindowTracker.Instance.AppsChanged -= RebuildItems;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        StopRendering();
        _edgeTimer.Stop();
        _slowTimer.Stop();
        _appBar?.Dispose();
        _backdrop?.Close();
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(() =>
    {
        foreach (var v in _views.Values)
        {
            v.Dot?.SetResourceReference(Shape.FillProperty, "Brush.DockIndicator");
        }
    });

    // ===================== placement =====================
    private void PositionFromReservedRect(Win32.RECT rc)
    {
        _scale = Win32.GetScaleForScreen(_screen);
        int h = (int)Math.Ceiling(WindowHeightDip * _scale);
        int bottom = S.DockAutoHide ? _screen.Bounds.Bottom : rc.Bottom;
        _winLeftPx = _screen.Bounds.Left;
        _winTopPx = bottom - h;
        PlacePx(_winLeftPx, _winTopPx, _screen.Bounds.Width, h);
        Width = _screen.Bounds.Width / _scale;
        Height = WindowHeightDip;
        Layout(false);
    }

    // ===================== items =====================
    public void RebuildItems()
    {
        var apps = WindowTracker.Instance.Apps;
        var used = new HashSet<RunningApp>();
        var list = new List<Item>();

        foreach (var pin in S.PinnedApps ?? new List<PinnedApp>())
        {
            var run = apps.FirstOrDefault(a => !used.Contains(a) && WindowTracker.Matches(pin, a));
            if (run != null) used.Add(run);
            list.Add(new Item { Kind = Kind.App, Key = "pin:" + pin.Target, Name = pin.Name, Pin = pin, Running = run, IconSource = pin.Target });
        }
        if (S.DockShowAllApps)
            list.Insert(Math.Min(1, list.Count), new Item { Kind = Kind.AllApps, Key = "allapps", Name = "All Apps" });

        if (S.DockShowRunningApps)
        {
            var extras = apps.Where(a => !used.Contains(a)).ToList();
            if (extras.Count > 0)
            {
                list.Add(new Item { Kind = Kind.Separator, Key = "sep:running" });
                foreach (var a in extras)
                    list.Add(new Item { Kind = Kind.App, Key = "run:" + a.Key, Name = a.Name, Running = a, IconSource = a.IconSource });
            }
        }

        list.Add(new Item { Kind = Kind.Separator, Key = "sep:system" });
        list.Add(new Item { Kind = Kind.Downloads, Key = "downloads", Name = "Downloads", IconSource = DownloadsPath });
        list.Add(new Item { Kind = Kind.Trash, Key = "trash", Name = "Recycle Bin", IconSource = TrashParsingName });

        var old = _items.ToDictionary(i => i.Key, i => i);
        IconLayer.Children.Clear();
        foreach (var it in list)
        {
            it.Size = old.TryGetValue(it.Key, out var o) ? o.Size : IconSize;
            it.View = GetView(it);
            if (it.View.Line != null) IconLayer.Children.Add(it.View.Line);
            else
            {
                IconLayer.Children.Add(it.View.Root);
                IconLayer.Children.Add(it.View.Dot);
                it.View.Dot.Visibility = it.Running != null ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // Forget views of items that disappeared.
        foreach (var k in _views.Keys.Except(list.Select(i => i.Key)).ToList()) _views.Remove(k);

        _items = list;
        Layout(false);
    }

    /// <summary>Moves a pinned app to where the pointer is while dragging it along the Dock.</summary>
    private void ReorderPinned(string key, double x)
    {
        var cur = _items.FirstOrDefault(i => i.Key == key);
        var target = ItemAtX(x);
        if (cur == null || target == null || target == cur || target.Kind != Kind.App || target.Pin == null) return;
        int from = _items.IndexOf(cur), to = _items.IndexOf(target);
        _items.RemoveAt(from);
        _items.Insert(to, cur);
        S.PinnedApps = _items.Where(i => i.Kind == Kind.App && i.Pin != null).Select(i => i.Pin).ToList();
        Layout(false);
        StartRendering(0.4);
    }

    private ItemView GetView(Item it)
    {
        if (_views.TryGetValue(it.Key, out var v)) return v;

        if (it.Kind == Kind.Separator)
        {
            var line = new Rectangle { Width = 1, IsHitTestVisible = false };
            line.SetResourceReference(Shape.FillProperty, "Brush.Separator");
            v = new ItemView { Line = line };
        }
        else
        {
            var bounce = new TranslateTransform();
            var img = new Image { Stretch = Stretch.Uniform, RenderTransform = bounce };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var root = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            root.Children.Add(img);
            var dot = new Ellipse { Width = 3, Height = 3, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, "Brush.DockIndicator");
            v = new ItemView { Root = root, Image = img, Bounce = bounce, Dot = dot };

            string key = it.Key;
            Point? downAt = null;
            bool dragging = false;
            root.MouseLeftButtonDown += (_, e) => { img.Opacity = 0.65; downAt = e.GetPosition(Surface); e.Handled = true; };
            root.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || downAt == null) return;
                var pos = e.GetPosition(Surface);
                if (!dragging)
                {
                    if (Math.Abs(pos.X - downAt.Value.X) < 8) return;
                    var me = _items.FirstOrDefault(i => i.Key == key);
                    if (me?.Kind != Kind.App || me.Pin == null) { downAt = null; return; } // only pinned apps can be moved
                    dragging = true;
                    root.CaptureMouse();
                    img.Opacity = 0.8;
                }
                ReorderPinned(key, pos.X);
            };
            root.MouseLeftButtonUp += (_, e) =>
            {
                img.Opacity = 1;
                downAt = null;
                e.Handled = true;
                if (dragging)
                {
                    dragging = false;
                    root.ReleaseMouseCapture();
                    SettingsStore.Save(raiseEvent: false);
                    return;
                }
                var cur = _items.FirstOrDefault(i => i.Key == key);
                if (cur != null) Activate(cur);
            };
            root.LostMouseCapture += (_, _) => { if (dragging) { dragging = false; img.Opacity = 1; SettingsStore.Save(raiseEvent: false); } };
            root.MouseLeave += (_, _) => { if (!dragging) img.Opacity = 1; };
            root.MouseRightButtonUp += (_, e) =>
            {
                var cur = _items.FirstOrDefault(i => i.Key == key);
                if (cur != null) ShowItemMenu(cur, root);
                e.Handled = true;
            };
            root.MouseDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                var cur = _items.FirstOrDefault(i => i.Key == key);
                if (cur != null) LaunchNew(cur);
            };

            if (it.Kind == Kind.AllApps) img.Source = AllApps.AllAppsIcon.Create();
            else LoadIconAsync(img, AppCatalog.Instance.IconFor(it.IconSource)); // link-style apps (Steam games) have separate icon files
        }
        _views[it.Key] = v;
        return v;
    }

    private static void LoadIconAsync(Image img, string source, bool useCache = true)
    {
        ShellIcons.LoadInto(img, source, 128, useCache);
    }

    // ===================== layout & magnification =====================
    private void StartRendering(double forSeconds = 0)
    {
        if (forSeconds > 0) _animateUntil = DateTime.Now.AddSeconds(forSeconds);
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRender;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRender;
    }

    private void OnRender(object sender, EventArgs e)
    {
        bool settled = Layout(true);
        if (settled && !_mouseInside && DateTime.Now > _animateUntil) StopRendering();
    }

    /// <summary>Positions everything. Returns true when all icons reached their target size.</summary>
    private bool Layout(bool animate)
    {
        if (_items.Count == 0) return true;
        double W = _screen.Bounds.Width / _scale;
        double H = WindowHeightDip;
        double b = IconSize, gap = Gap, mag = MagSize;
        const double sepW = 1;

        // Base (unmagnified) layout: used as the stable reference for the magnification curve.
        double baseTotal = Pad * 2;
        foreach (var it in _items) baseTotal += (it.Kind == Kind.Separator ? sepW : b) + gap;
        baseTotal -= gap;
        double bx = (W - baseTotal) / 2 + Pad;
        var baseCenter = new double[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            double w = _items[i].Kind == Kind.Separator ? sepW : b;
            baseCenter[i] = bx + w / 2;
            bx += w + gap;
        }

        bool settled = true;
        const double range = 2.8; // in icon widths
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (it.Kind == Kind.Separator) continue;
            double target = b;
            if (_mouseInside && mag > b)
            {
                double d = Math.Abs(_mouseX - baseCenter[i]) / (b + gap);
                if (d < range) target = b + (mag - b) * (Math.Cos(Math.PI * d / range) + 1) / 2;
            }
            if (animate)
            {
                double next = it.Size + (target - it.Size) * 0.32;
                if (Math.Abs(target - next) < 0.15) next = target;
                else settled = false;
                it.Size = next;
            }
            else it.Size = target;
        }

        double total = Pad * 2;
        foreach (var it in _items) total += (it.Kind == Kind.Separator ? sepW : it.Size) + gap;
        total -= gap;
        double x0 = Math.Round((W - total) / 2);
        double plateBottom = H - BottomMargin;
        double plateTop = plateBottom - PlateHeight;

        Canvas.SetLeft(Plate, x0);
        Canvas.SetTop(Plate, plateTop);
        Plate.Width = total;
        Plate.Height = PlateHeight;
        // With real blur the radius must match what DWM can clip (see DockBackdropWindow);
        // the see-through fallback is drawn entirely by WPF and can be rounder.
        Plate.CornerRadius = new CornerRadius(_backdrop != null ? DockBackdropWindow.PlateRadius : Math.Min(18, PlateHeight * 0.3));

        double zoneTop = plateBottom - (mag + Pad * 2 + DotSpace) - 8;
        Canvas.SetLeft(HoverZone, x0 - 24);
        Canvas.SetTop(HoverZone, zoneTop);
        HoverZone.Width = total + 48;
        HoverZone.Height = H - zoneTop;

        double x = x0 + Pad;
        Item hovered = null;
        foreach (var it in _items)
        {
            if (it.Kind == Kind.Separator)
            {
                var line = it.View.Line;
                line.Height = PlateHeight - 18;
                Canvas.SetLeft(line, x);
                Canvas.SetTop(line, plateTop + 9);
                x += sepW + gap;
                continue;
            }
            double s = it.Size;
            it.X = x;
            it.Top = plateBottom - Pad - DotSpace - s;
            var v = it.View;
            v.Root.Width = s;
            v.Root.Height = s;
            Canvas.SetLeft(v.Root, x);
            Canvas.SetTop(v.Root, it.Top);
            Canvas.SetLeft(v.Dot, x + s / 2 - 1.5);
            Canvas.SetTop(v.Dot, plateBottom - Pad - 1);
            if (_mouseInside && _mouseX >= x - gap / 2 && _mouseX < x + s + gap / 2) hovered = it;
            x += s + gap;
        }

        UpdateTooltip(hovered);
        UpdateBackdrop(x0, plateTop, total, PlateHeight);
        return settled;
    }

    private void UpdateTooltip(Item hovered)
    {
        if (hovered == null || _menuOpen)
        {
            NameTip.Visibility = Visibility.Collapsed;
            return;
        }
        NameTipText.Text = hovered.Name;
        if (_dropHot != null && _dropHot == hovered) UpdateTooltipFor(hovered); // "Move to Recycle Bin" etc.
        NameTip.Visibility = Visibility.Visible;
        NameTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = NameTip.DesiredSize;
        Canvas.SetLeft(NameTip, Math.Round(hovered.X + hovered.Size / 2 - sz.Width / 2));
        Canvas.SetTop(NameTip, Math.Round(hovered.Top - sz.Height - 8));
    }

    private void UpdateBackdrop(double x, double y, double w, double h)
    {
        if (_backdrop == null || _backdrop.Handle == IntPtr.Zero) return;
        bool show = !_fullScreenHidden && IsVisible;
        double offset = HideOffset.Y;
        double inset = DockBackdropWindow.Inset;
        int px = _winLeftPx + (int)Math.Round((x + inset) * _scale);
        int py = _winTopPx + (int)Math.Round((y + inset + offset) * _scale);
        int pw = (int)Math.Round((w - inset * 2) * _scale);
        int ph = (int)Math.Round((h - inset * 2) * _scale);
        // Hide the blur when the dock is (almost) fully slid away.
        if (offset >= ConcealOffset - 1) show = false;

        if (!show)
        {
            if (_backdrop.IsVisible) _backdrop.Visibility = Visibility.Hidden;
            return;
        }
        if (!_backdrop.IsVisible) _backdrop.Visibility = Visibility.Visible;
        Win32.SetWindowPos(_backdrop.Handle, IntPtr.Zero, px, py, pw, ph,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    // ===================== mouse =====================
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_concealed) return;
        _mouseX = e.GetPosition(Surface).X;
        _lastInside = DateTime.Now;
        if (!_mouseInside)
        {
            _mouseInside = true;
            HoverZone.Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }
        StartRendering();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_menuOpen) return;
        _mouseInside = false;
        _lastInside = DateTime.Now;
        HoverZone.Fill = null;
        StartRendering(0.5);
    }

    // ===================== auto-hide & full screen =====================
    private void EdgeTick()
    {
        if (!S.DockAutoHide || _fullScreenHidden) return;
        Win32.GetCursorPos(out var p);
        bool onScreen = p.X >= _screen.Bounds.Left && p.X < _screen.Bounds.Right;
        bool atEdge = onScreen && p.Y >= _screen.Bounds.Bottom - 2;
        int plateTopPx = _winTopPx + (int)((WindowHeightDip - BottomMargin - PlateHeight - 20) * _scale);

        // Nothing to make room for (empty desktop, everything minimized): keep the Dock showing.
        if (!AnyWindowUnderDock(plateTopPx))
        {
            if (_concealed) SetConcealed(false);
            return;
        }

        if (_concealed && atEdge) SetConcealed(false);
        else if (!_concealed && !_mouseInside && !_menuOpen &&
                 (DateTime.Now - _lastInside).TotalMilliseconds > 700 && (p.Y < plateTopPx || !onScreen))
            SetConcealed(true);
    }

    /// <summary>Does any visible app window on this display reach into the Dock's area?</summary>
    private bool AnyWindowUnderDock(int dockTopPx)
    {
        int left = _winLeftPx + (int)(Canvas.GetLeft(Plate) * _scale);
        int right = left + (int)(Plate.Width * _scale);
        foreach (var app in WindowTracker.Instance.Apps)
            foreach (var w in app.Windows)
            {
                if (Win32.IsIconic(w.Hwnd) || !Win32.IsWindowVisible(w.Hwnd) || Win32.IsCloaked(w.Hwnd)) continue;
                var f = Win32.GetFrameBounds(w.Hwnd);
                if (f.Bottom > dockTopPx && f.Top < _screen.Bounds.Bottom && f.Right > left && f.Left < right) return true;
            }
        return false;
    }

    private void SetConcealed(bool concealed)
    {
        if (_concealed == concealed) return;
        _concealed = concealed;
        if (!concealed) _lastInside = DateTime.Now;
        var anim = new DoubleAnimation(concealed ? ConcealOffset : 0, TimeSpan.FromMilliseconds(concealed ? 260 : 200))
        {
            EasingFunction = new CubicEase { EasingMode = concealed ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        HideOffset.BeginAnimation(TranslateTransform.YProperty, anim);
        StartRendering(0.35);
    }

    private void SlowTick()
    {
        // Full-screen apps (videos, games, presentations) hide the dock.
        var mon = new Win32.RECT(_screen.Bounds.Left, _screen.Bounds.Top, _screen.Bounds.Right, _screen.Bounds.Bottom);
        bool full = WindowTracker.Instance.IsFullScreenOn(mon);
        if (full != _fullScreenHidden)
        {
            _fullScreenHidden = full;
            if (full) { Hide(); if (_backdrop != null) _backdrop.Visibility = Visibility.Hidden; }
            else { Show(); Layout(false); }
        }

        // Recycle Bin: switch between empty/full icons.
        var info = new Win32.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
        if (Win32.SHQueryRecycleBin(null, ref info) == 0)
        {
            long count = info.i64NumItems;
            if ((count == 0) != (_trashCount == 0) || _trashCount < 0)
            {
                _trashCount = count;
                if (_views.TryGetValue("trash", out var tv)) LoadIconAsync(tv.Image, TrashParsingName, useCache: false);
            }
            _trashCount = count;
        }
    }

    // ===================== actions =====================
    private void Activate(Item it)
    {
        switch (it.Kind)
        {
            case Kind.Downloads: ToggleDownloadsStack(it); return;
            case Kind.Trash: Launch.Open("shell:RecycleBinFolder"); return;
            case Kind.AllApps: App.Instance.ToggleAllApps(); return;
        }

        var run = it.Running;
        if (run != null && run.Windows.Count > 0)
        {
            var fg = WindowTracker.Instance.ForegroundApp;
            bool isActive = fg != null && fg.Key == run.Key && !Win32.IsIconic(run.Windows[0].Hwnd);
            if (!isActive)
            {
                // Bring every window of the app forward (like macOS), most recent on top.
                foreach (var w in run.Windows.Where(w => w.Minimized)) Win32.ShowWindowAsync(w.Hwnd, Win32.SW_RESTORE);
                WindowTracker.ActivateApp(run);
            }
            else if (run.Windows.Count > 1) Win32.ActivateWindow(run.Windows[^1].Hwnd); // cycle windows
            else WindowTracker.Minimize(run.Windows[0].Hwnd);
            return;
        }
        LaunchNew(it);
    }

    private void LaunchNew(Item it)
    {
        string target = it.Pin?.Target ?? it.Running?.IconSource;
        if (target == null) return;
        Launch.Open(target);
        Bounce(it);
    }

    private void Bounce(Item it)
    {
        double h = IconSize * 0.5;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(720), RepeatBehavior = new RepeatBehavior(2) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-h, KeyTime.FromPercent(0.45), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.9), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        it.View.Bounce.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void ShowItemMenu(Item it, FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };

        MenuItem Add(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (action != null) mi.Click += (_, _) => action();
            m.Items.Add(mi);
            return mi;
        }

        switch (it.Kind)
        {
            case Kind.Trash:
                Add("Open", () => Launch.Open("shell:RecycleBinFolder"));
                m.Items.Add(new Separator());
                Add("Empty Recycle Bin", () =>
                {
                    Win32.SHEmptyRecycleBin(IntPtr.Zero, null, 0);
                    _trashCount = -1;
                    SlowTick();
                }, _trashCount != 0);
                break;

            case Kind.AllApps:
                Add("Open", () => App.Instance.ToggleAllApps());
                m.Items.Add(new Separator());
                Add("Remove from Dock", () => { S.DockShowAllApps = false; SettingsStore.Save(); });
                break;

            case Kind.Downloads:
                Add("Open", () => Launch.Open(DownloadsPath));
                foreach (var f in RecentDownloads())
                {
                    if (m.Items.Count == 1) m.Items.Add(new Separator());
                    string path = f;
                    Add(System.IO.Path.GetFileName(path), () => Launch.Open(path));
                }
                break;

            default:
                var run = it.Running;
                if (run != null)
                {
                    foreach (var w in run.Windows.Take(12))
                    {
                        var hwnd = w.Hwnd;
                        string title = string.IsNullOrWhiteSpace(w.Title) ? it.Name : w.Title;
                        if (title.Length > 60) title = title[..57] + "…";
                        Add(title, () => Win32.ActivateWindow(hwnd));
                    }
                    m.Items.Add(new Separator());
                }

                bool pinned = it.Pin != null;
                Add(pinned ? "Remove from Dock" : "Keep in Dock", () =>
                {
                    if (pinned) App.Instance.Unpin(it.Pin);
                    else App.Instance.PinApp(run);
                });
                string exe = run?.ExePath ?? it.Pin?.ExePath;
                if (exe != null) Add("Show in File Explorer", () => Launch.RevealInExplorer(exe));
                m.Items.Add(new Separator());
                Add(run != null ? "New Window" : "Open", () => LaunchNew(it));
                if (run != null)
                {
                    Add("Hide", () => { foreach (var w in run.Windows) WindowTracker.Minimize(w.Hwnd); });
                    Add("Quit", () => WindowTracker.QuitApp(run));
                }
                break;
        }

        _menuOpen = true;
        NameTip.Visibility = Visibility.Collapsed;
        m.Closed += (_, _) =>
        {
            _menuOpen = false;
            _mouseInside = false;
            HoverZone.Fill = null;
            _lastInside = DateTime.Now;
            StartRendering(0.5);
        };
        m.IsOpen = true;
    }

    private static IEnumerable<string> RecentDownloads()
    {
        try
        {
            return new DirectoryInfo(DownloadsPath).EnumerateFileSystemInfos()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderByDescending(f => f.LastWriteTime).Take(8).Select(f => f.FullName).ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>
/// A click-through, non-layered window that only provides the frosted glass behind the dock plate.
/// It is clipped to exactly the plate's rounded rectangle; the plate's anti-aliased border is drawn on top.
/// </summary>
internal sealed class DockBackdropWindow : OverlayWindow
{
    public DockBackdropWindow() : base(SurfaceKind.Bar, noActivate: true, useChrome: false)
    {
        WrapContent = false;
        Width = 10;
        Height = 10;
        Title = "PearUI Dock Backdrop";
        SourceInitialized += (_, _) => Win32.AddExStyle(Handle, Win32.WS_EX_TRANSPARENT);
    }

    protected override Color GlassTint => ThemeManager.DockTint;

    /// <summary>
    /// Acrylic ignores SetWindowRgn, but it IS clipped by DWM's native rounded corners (8 DIP).
    /// The Dock plate uses a 12 DIP radius and this window is inset 1 DIP, which keeps the blur's
    /// corner arc inside the plate's arc: (12 - 8 - 1)·√2 + 8 ≈ 12.2 ≤ 12 + border.
    /// </summary>
    protected override void ApplyGlass()
    {
        base.ApplyGlass();
        Backdrop.SetCorners(Handle, round: true, border: null);
    }

    public const double PlateRadius = 14;
    public const double Inset = 2;
}
