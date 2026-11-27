using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Launcher;
using PearUI.Native;
using PearUI.Shell;
using Forms = System.Windows.Forms;

namespace PearUI.AllApps;

/// <summary>
/// Full-screen grid of every installed app (Launchpad-style): frosted background, paged grid with
/// page dots, type-to-search, mouse wheel / arrow keys to flip pages, click outside to close.
/// </summary>
public sealed partial class AllAppsWindow : OverlayWindow
{
    private readonly Grid _content = new();
    private Grid _rootGrid;
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TextBox _search = new();
    private readonly TextBlock _placeholder = new();
    private readonly Canvas _viewport = new() { ClipToBounds = true };
    private readonly StackPanel _pagesHost = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform _pageOffset = new();
    private readonly Motion.Spring _snap, _follow;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<string, FrameworkElement> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _tileImages = new(StringComparer.OrdinalIgnoreCase);

    private List<AppEntry> _visible = new();
    private int _page, _pageCount = 1;
    private double _iconSize = 72, _cellW = 130, _cellH = 120, _viewW = 1200;
    private int _cols = 7, _rows = 4;
    private DateTime _lastFlip;
    private bool _closing;
    private bool _iconsLoading;

    public AllAppsWindow() : base(SurfaceKind.Fullscreen, noActivate: false)
    {
        _snap = new Motion.Spring(x => _pageOffset.X = x);
        _follow = new Motion.Spring(x => _pageOffset.X = x);
        Title = "PearUI All Apps";
        Content = BuildUi();
        Deactivated += (_, _) => { if (IsVisible && !_closing) CloseGrid(animated: false); };
        PreviewKeyDown += OnKey;
        AppCatalog.Instance.Loaded += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Rebuild(); });
    }

    private UIElement BuildUi()
    {
        var root = new Grid { Background = Brushes.Transparent };
        _rootGrid = root;
        root.MouseLeftButtonUp += (_, e) => { if (!e.Handled) CloseGrid(animated: true); };
        root.PreviewMouseLeftButtonDown += OnPreviewDown;
        root.PreviewMouseMove += OnPreviewMove;
        root.PreviewMouseLeftButtonUp += OnPreviewUp;

        _content.RenderTransform = _zoom;
        _content.RenderTransformOrigin = new Point(0.5, 0.5);
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });

        // ---- search field ----
        var field = new Border
        {
            Width = 260, Height = 32, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 30, 0, 0),
        };
        var fieldGrid = new Grid();
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var glass = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0),
        };
        glass.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        fieldGrid.Children.Add(glass);
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = Brushes.White;
        _search.CaretBrush = Brushes.White;
        _search.FontSize = 14;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _page = 0;
            if (_openFolder != null && _search.Text.Length > 0) { _openFolder = null; _folderGrid?.Children.Clear(); _folderLayer.Children.Clear(); _folderLayer.Visibility = Visibility.Collapsed; }
            Rebuild();
        };
        Grid.SetColumn(_search, 1);
        fieldGrid.Children.Add(_search);
        _placeholder.Text = "Search";
        _placeholder.FontSize = 14;
        _placeholder.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.Margin = new Thickness(3, 0, 0, 0);
        _placeholder.IsHitTestVisible = false;
        Grid.SetColumn(_placeholder, 1);
        fieldGrid.Children.Add(_placeholder);
        field.Child = fieldGrid;
        field.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _content.Children.Add(field);

        // ---- pages ----
        _pagesHost.RenderTransform = _pageOffset;
        _viewport.Children.Add(_pagesHost);
        Grid.SetRow(_viewport, 1);
        _content.Children.Add(_viewport);

        // ---- page dots ----
        Grid.SetRow(_dots, 2);
        _dots.VerticalAlignment = VerticalAlignment.Center;
        _content.Children.Add(_dots);

        root.Children.Add(_backdropLayer);
        root.Children.Add(_content);
        InitFolders(root);
        return root;
    }

    // ===================== show / hide =====================
    public void Toggle()
    {
        if (IsVisible) CloseGrid(animated: true);
        else Open();
    }

    public void Open()
    {
        if (AppCatalog.Instance.Apps.Count == 0) _ = AppCatalog.Instance.LoadAsync();
        Win32.GetCursorPos(out var p);
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
        double s = Win32.GetScaleForScreen(screen);
        // Everything except the menu bar (when the menu bar is on).
        var full = screen.Bounds;
        int top = SettingsStore.Current.MenuBarEnabled ? (int)Math.Ceiling(MenuBar.MenuBarWindow.BarHeight * s) : 0;
        var b = new System.Drawing.Rectangle(full.Left, full.Top + top, full.Width, full.Height - top);

        _closing = false;
        _search.Text = "";
        _page = 0;
        Width = b.Width / s;
        Height = b.Height / s;
        ComputeGrid(Width, Height);
        if (BuildKey() != _builtKey) Rebuild();
        else ResetToFirstPage();

        UpdateBackdrop(full.Width / s, full.Height / s, top / s);

        // Start from the hidden state, show the window, and only begin the animation once the first frame is
        // about to be drawn: laying out the tiles takes a moment, and an animation that started before it would skip ahead.
        StopSnap();
        _backdropLayer.BeginAnimation(OpacityProperty, null);
        _content.BeginAnimation(OpacityProperty, null);
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _backdropLayer.Opacity = 0;
        _content.Opacity = 0;
        _zoom.ScaleX = _zoom.ScaleY = 1.05;
        Show();
        PlacePx(b.Left, b.Top, b.Width, b.Height);
        Activate();
        Win32.ActivateWindow(Handle); // works even when opened from the Windows key (no foreground rights)
        _search.Focus();
        Keyboard.Focus(_search);

        Motion.OnNextFrame(() =>
        {
            if (_closing) return;
            var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
            _content.CacheMode = new BitmapCache(1.0);
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
            fadeIn.Completed += (_, _) => _content.CacheMode = null;
            _backdropLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
            _content.BeginAnimation(OpacityProperty, fadeIn);
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        });

        LoadIconsInBackground();
    }

    public void CloseGrid(bool animated)
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        if (_openFolder != null)
        {
            _openFolder = null;
            _folderGrid?.Children.Clear();
            _folderLayer.Children.Clear();
            _folderLayer.Visibility = Visibility.Collapsed;
        }
        if (!animated) { Hide(); return; }
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(120);
        _backdropLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
        _content.CacheMode = new BitmapCache(1.0);
        var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => { Hide(); _content.CacheMode = null; };
        _content.BeginAnimation(OpacityProperty, fade);
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.03, dur) { EasingFunction = ease });
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.03, dur) { EasingFunction = ease });
    }

    // ===================== layout =====================
    private void ComputeGrid(double w, double h)
    {
        _iconSize = Math.Clamp(Math.Round(h * 0.092), 48, 96);
        _cellW = Math.Round(_iconSize * 1.8);
        _cellH = _iconSize + 46;
        _cols = Math.Clamp((int)(w * 0.86 / _cellW), 4, 9);
        _rows = Math.Clamp((int)((h - 100 - 60 - 20) / _cellH), 2, 6);
        _viewW = w;
        _viewport.Width = w;
    }

    private string _builtKey;

    private string BuildKey() =>
        $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(AppCatalog.Instance.Apps)}|{_cols}|{_rows}|{_iconSize}|{_cellW}|{_viewW}|{_search.Text.Trim()}";

    private void ResetToFirstPage()
    {
        _page = 0;
        _pageOffset.BeginAnimation(TranslateTransform.XProperty, null);
        _pageOffset.X = 0;
        BuildDots();
    }

    /// <summary>Builds the window, tiles and blurred wallpaper in the background so the first open is instant.</summary>
    public void Prewarm()
    {
        try
        {
            if (IsVisible || AppCatalog.Instance.Apps.Count == 0) return;
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            var screen = Forms.Screen.PrimaryScreen;
            double s = Win32.GetScaleForScreen(screen);
            int top = SettingsStore.Current.MenuBarEnabled ? (int)Math.Ceiling(MenuBar.MenuBarWindow.BarHeight * s) : 0;
            double w = screen.Bounds.Width / s, h = (screen.Bounds.Height - top) / s;
            Width = w;
            Height = h;
            ComputeGrid(w, h);
            Rebuild();
            UpdateBackdrop(screen.Bounds.Width / s, screen.Bounds.Height / s, top / s);
            if (Content is UIElement root)
            {
                root.Measure(new Size(w, h));
                root.Arrange(new Rect(0, 0, w, h));
            }
            LoadIconsInBackground();
        }
        catch { }
    }

    private void Rebuild()
    {
        string q = _search.Text.Trim();
        var apps = AppCatalog.Instance.Apps;
        _visible = q.Length == 0
            ? apps.ToList()
            : apps.Select(a => (a, s: SearchProviders.Score(a.Name, q))).Where(t => t.s > 0)
                  .OrderByDescending(t => t.s).ThenBy(t => t.a.Name).Select(t => t.a).ToList();
        // Searching shows matching apps directly (even ones inside folders); otherwise folders + loose apps.
        var items = q.Length == 0 ? GridItems() : _visible.Cast<object>().ToList();

        int perPage = _cols * _rows;
        _pageCount = Math.Max(1, (int)Math.Ceiling(items.Count / (double)perPage));
        _page = Math.Clamp(_page, 0, _pageCount - 1);

        // Detach reusable tiles from old pages, then lay out new pages.
        foreach (UniformGrid g in _pagesHost.Children.OfType<Grid>().SelectMany(p => p.Children.OfType<UniformGrid>())) g.Children.Clear();
        _pagesHost.Children.Clear();

        for (int pi = 0; pi < _pageCount; pi++)
        {
            var grid = new UniformGrid
            {
                Columns = _cols, Rows = _rows,
                Width = _cols * _cellW, Height = _rows * _cellH,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            };
            foreach (var item in items.Skip(pi * perPage).Take(perPage))
                grid.Children.Add(item is AppFolder folder ? MakeFolderTile(folder) : GetTile((AppEntry)item));
            var page = new Grid { Width = _viewW };
            page.Children.Add(grid);
            _pagesHost.Children.Add(page);
        }

        if (q.Length > 0 && _visible.Count == 0)
        {
            var none = new TextBlock
            {
                Text = "No Results", FontSize = 22, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 120, 0, 0),
            };
            ((Grid)_pagesHost.Children[0]).Children.Add(none);
        }

        BuildDots();
        StopSnap();
        _pageOffset.BeginAnimation(TranslateTransform.XProperty, null);
        _pageOffset.X = -_page * _viewW;
        _builtKey = BuildKey();
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        if (_pageCount <= 1) return;
        for (int i = 0; i < _pageCount; i++)
        {
            int idx = i;
            var dot = new Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(5, 0, 5, 0), Cursor = Cursors.Hand,
                Fill = new SolidColorBrush(i == _page ? Colors.White : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            };
            dot.MouseLeftButtonUp += (_, e) => { GoToPage(idx); e.Handled = true; };
            _dots.Children.Add(dot);
        }
    }

    private void StopSnap() { _snap.Stop(); _follow.Stop(); }

    private void GoToPage(int page) => SnapTo(page);

    /// <summary>Animates to a page (or back to the current one after a short swipe).</summary>
    private void SnapTo(int page, double velocity = 0)
    {
        page = Math.Clamp(page, 0, _pageCount - 1);
        bool changed = page != _page;
        _page = page;
        double target = -_page * _viewW;

        // A critically damped spring: the page keeps the speed of your swipe, then settles in about a quarter of a second
        // without bouncing. It is driven by elapsed time, so it looks the same at any refresh rate, and a new swipe can
        // take over at any moment.
        double onScreen = _follow.IsRunning ? _follow.Velocity : 0;      // how fast the page is moving right now
        double vx = velocity != 0 ? -velocity * 1000 : onScreen;       // DIPs per second along the x axis
        vx = Math.Clamp(vx, -8000, 8000);
        _follow.Stop();
        _pageOffset.BeginAnimation(TranslateTransform.XProperty, null);
        _snap.Start(_pageOffset.X, target, vx, omega: changed ? 23 : 27, done: () => { if (!_panning) _pageOffset.X = target; });
        if (changed) BuildDots();
    }

    // ===================== trackpad / mouse / touch gestures =====================
    // Precision touchpads send two-finger swipes as WM_MOUSEHWHEEL (sideways) and WM_MOUSEWHEEL (up/down)
    // with small deltas, and pinches as Ctrl+WM_MOUSEWHEEL. Pages follow the fingers, then snap.
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, MK_CONTROL = 0x0008;
    private readonly DispatcherTimer _panEnd = new() { Interval = TimeSpan.FromMilliseconds(130) };
    private bool _panning;
    private double _pan;          // DIPs the fingers moved toward the next page
    private double _velocity;     // DIPs per ms, toward the next page
    private DateTime _lastPanAt;
    private double _pinch;
    private Point? _dragStart;
    private double _dragLastX;
    private bool _dragging;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        System.Windows.Interop.HwndSource.FromHwnd(Handle)?.AddHook(GestureHook);
        _panEnd.Tick += (_, _) => { _panEnd.Stop(); EndPan(); };
    }

    private IntPtr GestureHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEWHEEL && msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
        long w = wParam.ToInt64();
        int delta = (short)((w >> 16) & 0xFFFF);
        bool ctrl = (w & MK_CONTROL) != 0;
        handled = true;

        if (msg == WM_MOUSEWHEEL && ctrl)
        {
            // Pinch on the trackpad closes the screen, like macOS Launchpad.
            _pinch += delta;
            if (Math.Abs(_pinch) >= 240) { _pinch = 0; CloseGrid(animated: true); }
            return IntPtr.Zero;
        }

        if (msg == WM_MOUSEWHEEL && delta % 120 == 0 && !_panning)
        {
            // A real mouse wheel notch: one page per notch.
            if ((DateTime.Now - _lastFlip).TotalMilliseconds >= 250)
            {
                _lastFlip = DateTime.Now;
                SnapTo(_page + (delta < 0 ? 1 : -1));
            }
            return IntPtr.Zero;
        }

        // Trackpad: sideways (right = next page) or up/down (down = next page).
        // One swipe = at most one page. After a page turn, the rest of the gesture (including the
        // momentum Windows keeps sending after the fingers lift) is ignored until input goes quiet.
        var now = DateTime.Now;
        bool quiet = (now - _lastWheelAt).TotalMilliseconds > GestureGapMs;
        _lastWheelAt = now;
        if (_gestureLocked)
        {
            if (!quiet) return IntPtr.Zero;
            _gestureLocked = false;
        }

        PanBy((msg == WM_MOUSEHWHEEL ? delta : -delta) * TrackpadScale);

        // Far enough: commit the page turn right away, like macOS.
        int dir = _pan > _viewW * CommitFraction ? 1 : _pan < -_viewW * CommitFraction ? -1 : 0;
        if (dir != 0 && _page + dir >= 0 && _page + dir < _pageCount)
        {
            _panEnd.Stop();
            _panning = false;
            _pan = 0;
            _gestureLocked = true;
            SnapTo(_page + dir);
            return IntPtr.Zero;
        }
        _panEnd.Stop();
        _panEnd.Start();
        return IntPtr.Zero;
    }

    private const double TrackpadScale = 0.75;   // content moves a bit less than raw deltas: calmer, 1:1-feeling
    private const double CommitFraction = 0.2;   // of the page width
    private const int GestureGapMs = 260;        // pause that separates one swipe from the next
    private DateTime _lastWheelAt;
    private bool _gestureLocked;

    private void PanBy(double dx)
    {
        if (!_panning)
        {
            _panning = true;
            double current = _pageOffset.X; // may be mid-animation
            StopSnap();
            _pageOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _pageOffset.X = current;
            _pan = -(current + _page * _viewW);
            _velocity = 0;
            _lastPanAt = DateTime.Now;
        }
        var now = DateTime.Now;
        double dt = Math.Max(8, (now - _lastPanAt).TotalMilliseconds);
        _velocity = _velocity * 0.6 + (dx / dt) * 0.4;
        _lastPanAt = now;
        _pan += dx;

        // Rubber band past the first / last page.
        double shown = _pan;
        bool pastStart = _page == 0 && shown < 0, pastEnd = _page == _pageCount - 1 && shown > 0;
        if (pastStart || pastEnd) shown = Math.Sign(shown) * Math.Min(140, Math.Abs(shown) * 0.3);
        // Follow the fingers smoothly: touchpads report movement less often than a fast screen draws frames.
        _follow.Retarget(-_page * _viewW - shown, omega: 60, valueIfIdle: _pageOffset.X);
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        // Gesture ended before the commit point: a clear flick still turns the page, otherwise spring back.
        int target = _page;
        if (_pan > _viewW * 0.08 && _velocity > 1.2) target = _page + 1;
        else if (_pan < -_viewW * 0.08 && _velocity < -1.2) target = _page - 1;
        double v = _velocity;
        _pan = 0;
        _velocity = 0;
        SnapTo(target, v);
    }

    // Click-drag (and touch, which Windows promotes to mouse) pages like a swipe.
    private void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragLastX = _dragStart.Value.X;
        _dragging = false;
    }

    private void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (_tileDragging) { MoveGhost(e.GetPosition(_dragLayer)); return; }
        if (_dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.Value.X) < 8 && Math.Abs(p.Y - _dragStart.Value.Y) < 8) return;
            // Started on an app: move the app (folder drag & drop). Started on empty space: swipe pages.
            if (_holdApp != null)
            {
                _holdTimer.Stop();
                StartTileDrag();
                if (_tileDragging) { MoveGhost(e.GetPosition(_dragLayer)); return; }
            }
            CancelHold();
            if (_openFolder != null || Math.Abs(p.X - _dragStart.Value.X) < 8) return;
            _dragging = true;
            ((UIElement)sender).CaptureMouse();
        }
        PanBy(_dragLastX - p.X);
        _dragLastX = p.X;
    }

    private void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (_tileDragging)
        {
            EndTileDrag(e.GetPosition(this));
            e.Handled = true;
            return;
        }
        CancelHold();
        if (!_dragging) return;
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        EndPan();
        e.Handled = true; // a drag is not a click: don't launch an app or close the screen
    }

    private FrameworkElement GetTile(AppEntry app)
    {
        if (_tiles.TryGetValue(app.Aumid, out var existing))
        {
            if (existing.Parent is Panel p) p.Children.Remove(existing);
            ApplyTileSize(existing);
            return existing;
        }

        var img = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = app.Name, Foreground = Brushes.White, FontSize = 12.5, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0),
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        stack.Children.Add(img);
        stack.Children.Add(name);
        var tile = new Border
        {
            Child = stack, Background = Brushes.Transparent, CornerRadius = new CornerRadius(14),
            Padding = new Thickness(4, 8, 4, 4), Margin = new Thickness(4), Cursor = Cursors.Hand,
            ToolTip = app.Name,
            Tag = "app:" + app.Aumid, // drop target for folder drag & drop
        };
        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => { tile.Background = Brushes.Transparent; img.Opacity = 1; };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            img.Opacity = 0.6;
            e.Handled = true;
            BeginHold(app, tile); // press and hold to drag into a folder
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            CancelHold();
            img.Opacity = 1;
            e.Handled = true;
            LaunchApp(app);
        };
        tile.MouseRightButtonUp += (_, e) => { ShowTileMenu(app, tile); e.Handled = true; };

        _tiles[app.Aumid] = tile;
        _tileImages[app.Aumid] = img;
        ApplyTileSize(tile);
        var cached = ShellIcons.TryGetCached(app.IconSource, ShellIcons.PixelsFor(96)) ?? ShellIcons.TryGetCached(app.IconSource, 64);
        if (cached != null) img.Source = cached;
        return tile;
    }

    private void ApplyTileSize(FrameworkElement tile)
    {
        var stack = (StackPanel)((Border)tile).Child;
        var img = (Image)stack.Children[0];
        var name = (TextBlock)stack.Children[1];
        img.Width = img.Height = _iconSize;
        name.MaxWidth = _cellW - 16;
    }

    private void LoadIconsInBackground()
    {
        if (_iconsLoading) return;
        _iconsLoading = true;
        var apps = AppCatalog.Instance.Apps.ToList();
        var pending = apps.Select(a => ShellIcons.GetAsync(a.IconSource, ShellIcons.PixelsFor(96)).ContinueWith(t =>
        {
            var icon = t.Result;
            if (icon == null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_tileImages.TryGetValue(a.Aumid, out var img) && img.Source == null) img.Source = icon;
            });
        })).ToArray();
        Task.WhenAll(pending).ContinueWith(_ => _iconsLoading = false);
    }

    // ===================== actions =====================
    private void LaunchApp(AppEntry app)
    {
        CloseGrid(animated: true);
        Launch.Open(app.Target);
    }

    private void ShowTileMenu(AppEntry app, FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
        void Add(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Add("Open", () => LaunchApp(app));
        bool pinned = SettingsStore.Current.PinnedApps.Any(p => p.Target.Equals(app.Target, StringComparison.OrdinalIgnoreCase));
        Add(pinned ? "Remove from Dock" : "Keep in Dock", () =>
        {
            var pins = SettingsStore.Current.PinnedApps;
            var existing = pins.FirstOrDefault(p => p.Target.Equals(app.Target, StringComparison.OrdinalIgnoreCase));
            if (existing != null) pins.Remove(existing);
            else pins.Add(new PinnedApp { Name = app.Name, Target = app.Target, ExePath = app.ExePath });
            SettingsStore.Save();
        });
        if (app.ExePath != null) Add("Show in File Explorer", () => { CloseGrid(animated: true); Launch.RevealInExplorer(app.ExePath); });
        AddFolderMenuItems(m, app);
        m.IsOpen = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_openFolder != null) CloseFolder();
                else if (_search.Text.Length > 0) _search.Clear();
                else CloseGrid(animated: true);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_visible.Count > 0) LaunchApp(_visible.Skip(_page * _cols * _rows).FirstOrDefault() ?? _visible[0]);
                e.Handled = true;
                break;
            case Key.Right when _search.Text.Length == 0:
            case Key.PageDown:
                GoToPage(_page + 1); e.Handled = true; break;
            case Key.Left when _search.Text.Length == 0:
            case Key.PageUp:
                GoToPage(_page - 1); e.Handled = true; break;
        }
    }
}
