using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Native;
using PearUI.Shell;
using Forms = System.Windows.Forms;

namespace PearUI.StageManager;

/// <summary>
/// The Stage Manager strip, macOS style: window previews float over the desktop on the left edge,
/// tilted in 3D, with the app icon on each card's corner. No panel behind them.
///
/// It appears by itself while the window on stage leaves the left edge free, and otherwise slides in
/// when the pointer is pushed against the left edge of the screen.
/// </summary>
public sealed class StageStripWindow : OverlayWindow
{
    public const double StripWidth = 190;
    private const double CardW = 150, CardH = 112, TiltDeg = 24;

    private readonly StageManager _manager;
    private readonly Grid _root = new() { Background = Brushes.Transparent };
    private readonly StackPanel _list = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private bool _shown;            // currently visible (auto or revealed)
    private bool _revealedByEdge;   // shown because the pointer hit the left edge
    private DateTime _atEdgeSince = DateTime.MaxValue, _awaySince = DateTime.MaxValue;
    private DateTime _lastSnapshots = DateTime.MinValue;
    private bool _hasCards;
    private int _tickCount;

    public StageStripWindow(StageManager manager) : base(SurfaceKind.Transparent, noActivate: true)
    {
        _manager = manager;
        Title = "PearUI Stage Manager";
        Width = StripWidth;
        _list.RenderTransform = _slide;
        _root.Children.Add(_list);
        Content = _root;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _tick.Stop();
            _manager.StripChanged -= OnStripChanged;
        };
    }

    private static Forms.Screen Screen => Forms.Screen.PrimaryScreen;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Place();
        _list.Opacity = 0;
        _slide.X = -StripWidth;
        _root.IsHitTestVisible = false;
        _manager.StripChanged += OnStripChanged;
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        Rebuild();
    }

    private void Place()
    {
        var wa = Screen.WorkingArea;
        double s = Win32.GetScaleForScreen(Screen);
        PlacePx(Screen.Bounds.Left, wa.Top, (int)Math.Ceiling(StripWidth * s), wa.Height);
    }

    private void OnStripChanged() => Dispatcher.BeginInvoke(() =>
    {
        Place();
        Rebuild();
    });

    // ===================== visibility =====================
    private void Tick()
    {
        if (++_tickCount % 8 == 0) _hasCards = _manager.StripWindows.Count > 0; // 0.5 s; changes also refresh it in Rebuild
        if (!_hasCards) { SetShown(false); return; }

        Win32.GetCursorPos(out var p);
        var b = Screen.Bounds;
        var wa = Screen.WorkingArea;
        double s = Win32.GetScaleForScreen(Screen);
        int stripRight = b.Left + (int)(StripWidth * s);

        // Auto: visible while the window on stage doesn't overlap the strip area.
        bool free = true;
        var fg = WindowTracker.Instance.ForegroundWindow;
        if (fg != IntPtr.Zero && Win32.IsWindow(fg) && !Win32.IsIconic(fg) && Win32.IsOnMonitor(fg, Screen))
        {
            var f = Win32.GetFrameBounds(fg);
            free = f.Left >= stripRight || f.Bottom <= wa.Top || f.Top >= wa.Bottom;
        }

        // Edge reveal: push the pointer against the left edge.
        bool buttons = Forms.Control.MouseButtons != Forms.MouseButtons.None;
        if (!_revealedByEdge)
        {
            bool atEdge = !buttons && p.X <= b.Left + 1 && p.Y > wa.Top && p.Y < wa.Bottom;
            if (atEdge)
            {
                if (_atEdgeSince == DateTime.MaxValue) _atEdgeSince = DateTime.Now;
                if ((DateTime.Now - _atEdgeSince).TotalMilliseconds >= 120) { _revealedByEdge = true; _awaySince = DateTime.MaxValue; }
            }
            else _atEdgeSince = DateTime.MaxValue;
        }
        else
        {
            bool away = p.X > stripRight + (int)(24 * s) || p.Y < wa.Top || p.Y > wa.Bottom;
            if (away)
            {
                if (_awaySince == DateTime.MaxValue) _awaySince = DateTime.Now;
                if ((DateTime.Now - _awaySince).TotalMilliseconds >= 350) { _revealedByEdge = false; _atEdgeSince = DateTime.MaxValue; }
            }
            else _awaySince = DateTime.MaxValue;
        }

        SetShown(free || _revealedByEdge);
    }

    private void SetShown(bool show)
    {
        if (show == _shown) return;
        _shown = show;
        if (show)
        {
            RefreshSnapshots();
            Rebuild();
            // Above app windows while revealed over them.
            PlacePx(Screen.Bounds.Left, Screen.WorkingArea.Top,
                (int)Math.Ceiling(StripWidth * Win32.GetScaleForScreen(Screen)), Screen.WorkingArea.Height);
        }
        _root.IsHitTestVisible = show;
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(show ? 260 : 180);
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(show ? 0 : -StripWidth, dur) { EasingFunction = ease });
        _list.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, dur) { EasingFunction = ease });
    }

    public void Conceal()
    {
        _revealedByEdge = false;
        SetShown(false);
    }

    /// <summary>Visible strip windows still have live content: capture fresh images (at most every few seconds).</summary>
    private void RefreshSnapshots()
    {
        if ((DateTime.Now - _lastSnapshots).TotalSeconds < 3) return;
        _lastSnapshots = DateTime.Now;
        foreach (var (_, w) in _manager.StripWindows)
            if (!Win32.IsIconic(w.Hwnd)) _manager.TakeSnapshot(w.Hwnd);
    }

    // ===================== cards =====================
    private sealed class Card
    {
        public IntPtr Hwnd;
        public RunningApp App;
        public AppWindow Window;
        public Grid Root;
        public readonly Grid Face = new();
        public Image Badge;
        public ImageSource Snap;
        public string IconSource;
        public AxisAngleRotation3D Rotation;
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly TranslateTransform Move = new();
    }

    private readonly Dictionary<IntPtr, Card> _cards = new();

    /// <summary>
    /// Brings the cards in line with the manager. Existing cards are kept (no flicker): new ones slide in,
    /// the others glide to their new place, and removed ones simply disappear.
    /// </summary>
    private void Rebuild()
    {
        double dpi = Win32.GetScaleForScreen(Screen);
        int cap = Math.Max(3, (int)((Screen.WorkingArea.Height / dpi - 40) / (CardH + 14)));
        var desired = _manager.StripWindows.Take(cap).ToList();
        _hasCards = desired.Count > 0;
        var keep = desired.Select(d => d.Window.Hwnd).ToHashSet();

        // Where each card is now (layout position, ignoring running animations), to glide from.
        var before = new Dictionary<IntPtr, double>();
        foreach (var kv in _cards)
            if (_list.Children.Contains(kv.Value.Root)) before[kv.Key] = VisualTreeHelper.GetOffset(kv.Value.Root).Y;

        foreach (var gone in _cards.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _list.Children.Remove(_cards[gone].Root);
            _cards.Remove(gone);
        }

        var added = new List<Card>();
        int index = 0;
        foreach (var (app, w) in desired)
        {
            if (!_cards.TryGetValue(w.Hwnd, out var card))
            {
                card = MakeCard(w.Hwnd);
                _cards[w.Hwnd] = card;
                added.Add(card);
            }
            card.App = app;
            card.Window = w;
            UpdateCard(card);
            if (index >= _list.Children.Count || _list.Children[index] != card.Root)
            {
                _list.Children.Remove(card.Root);
                _list.Children.Insert(Math.Min(index, _list.Children.Count), card.Root);
            }
            index++;
        }

        if (!_shown) return;
        _list.UpdateLayout();

        int n = 0;
        foreach (var card in added)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var delay = TimeSpan.FromMilliseconds(n++ * 45);
            card.Move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-(CardW + 40), 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease, BeginTime = delay });
            card.Root.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease, BeginTime = delay });
        }
        foreach (var kv in before)
        {
            if (!_cards.TryGetValue(kv.Key, out var card) || added.Contains(card)) continue;
            double dy = kv.Value - VisualTreeHelper.GetOffset(card.Root).Y;
            if (Math.Abs(dy) > 1)
                card.Move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private Card MakeCard(IntPtr hwnd)
    {
        var card = new Card { Hwnd = hwnd };
        var root = new Grid { Width = CardW, Height = CardH, Margin = new Thickness(0, 4, 0, 10), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        card.Root = root;
        root.Children.Add(card.Face);

        // App icon badge on the bottom-left corner.
        var badge = new Image
        {
            Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-4, 0, 0, 4),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5 },
        };
        RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.HighQuality);
        card.Badge = badge;
        root.Children.Add(badge);

        root.RenderTransform = new TransformGroup { Children = { card.Scale, card.Move } };
        root.RenderTransformOrigin = new Point(0.35, 0.5);

        void ScaleTo(double v, int ms)
        {
            var d = TimeSpan.FromMilliseconds(ms);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(v, d) { EasingFunction = ease });
            card.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(v, d) { EasingFunction = ease });
        }

        root.MouseEnter += (_, _) =>
        {
            card.Rotation?.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(TiltDeg * 0.35, TimeSpan.FromMilliseconds(180)));
            ScaleTo(1.06, 180);
        };
        root.MouseLeave += (_, _) =>
        {
            card.Rotation?.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(TiltDeg, TimeSpan.FromMilliseconds(220)));
            ScaleTo(1, 220);
        };
        root.MouseLeftButtonDown += (_, _) => ScaleTo(0.97, 90);
        root.MouseLeftButtonUp += (_, _) =>
        {
            ScaleTo(1.06, 120);
            Conceal();
            if (card.App != null && card.Window != null) _manager.Bring(card.App, card.Window);
        };
        return card;
    }

    /// <summary>Refreshes a card's preview, icon and tooltip without recreating it.</summary>
    private void UpdateCard(Card card)
    {
        var top = card.Window;
        var snap = _manager.Snapshot(top.Hwnd);
        if (snap == null && !Win32.IsIconic(top.Hwnd))
        {
            _manager.TakeSnapshot(top.Hwnd);
            snap = _manager.Snapshot(top.Hwnd);
        }

        string icon = card.App.IconSource;
        if (icon != card.IconSource)
        {
            card.IconSource = icon;
            LoadIcon(card.Badge, icon);
        }

        if (!ReferenceEquals(snap, card.Snap) || card.Face.Children.Count == 0)
        {
            card.Snap = snap;
            card.Face.Children.Clear();
            card.Rotation = null;
            if (snap != null)
            {
                card.Face.Children.Add(Tilted(snap, CardW, CardH, out var rotation));
                card.Rotation = rotation;
                if (card.Root.IsMouseOver) rotation.Angle = TiltDeg * 0.35;
            }
            else
            {
                // No image available: a frosted placeholder card with the app icon.
                var placeholder = new Border
                {
                    Width = CardW * 0.78, Height = CardH * 0.62, CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x2A, 0x2A, 0x30)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
                    Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.45 },
                    RenderTransform = new SkewTransform(0, -8), RenderTransformOrigin = new Point(0.5, 0.5),
                };
                var bigIcon = new Image { Width = 40, Height = 40 };
                placeholder.Child = bigIcon;
                LoadIcon(bigIcon, icon);
                card.Face.Children.Add(placeholder);
            }
        }

        card.Root.ToolTip = string.IsNullOrWhiteSpace(top.Title) ? card.App.Name : $"{card.App.Name} \u2014 {top.Title}";
    }

    /// <summary>A window image on a 3D quad, turned around the vertical axis like macOS Stage Manager.</summary>
    private static FrameworkElement Tilted(ImageSource img, double w, double h, out AxisAngleRotation3D rotation)
    {
        double aspect = img.Width / Math.Max(1, img.Height);
        double a = Math.Clamp(aspect, 0.6, 2.4); // half-width of the quad when half-height is 1
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(-a, -1, 0), new(a, -1, 0), new(a, 1, 0), new(-a, 1, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
            TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
        };
        var brush = new ImageBrush(img) { Stretch = Stretch.Fill };
        brush.Freeze();
        var model = new GeometryModel3D(mesh, new DiffuseMaterial(brush)) { BackMaterial = new DiffuseMaterial(brush) };
        rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), TiltDeg);
        model.Transform = new RotateTransform3D(rotation);

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Colors.White));
        group.Children.Add(model);

        // Fit the quad's width into the viewport (45° horizontal field of view).
        // Visible width at distance d is 2·d·tan(22.5°) ≈ 0.83·d; visible height is that × h/w.
        // Fit width (2a, with margin) and height (2, with margin).
        double distance = Math.Max(2.2 * a / 0.83, 2.2 / (0.83 * h / w));
        var viewport = new Viewport3D
        {
            Width = w, Height = h, IsHitTestVisible = false, ClipToBounds = false,
            Camera = new PerspectiveCamera(new Point3D(0, 0, distance), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.5 },
        };
        viewport.Children.Add(new ModelVisual3D { Content = group });
        return viewport;
    }

    private static void LoadIcon(Image target, string source)
    {
        int px = ShellIcons.PixelsFor(64);
        var cached = ShellIcons.TryGetCached(source, px);
        if (cached != null) { target.Source = cached; return; }
        ShellIcons.LoadInto(target, source, px);
    }
}
