using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MacUI.Core;
using MacUI.Native;
using MacUI.Shell;
using Forms = System.Windows.Forms;

namespace MacUI.StageManager;

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
        bool hasApps = _manager.StripApps.Count > 0;
        if (!hasApps) { SetShown(false); return; }

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
        foreach (var app in _manager.StripApps)
        {
            var w = app.Windows.FirstOrDefault();
            if (w != null && !Win32.IsIconic(w.Hwnd)) _manager.TakeSnapshot(w.Hwnd);
        }
    }

    // ===================== cards =====================
    private void Rebuild()
    {
        _list.Children.Clear();
        foreach (var app in _manager.StripApps)
        {
            var top = app.Windows.FirstOrDefault(w => !w.Minimized) ?? app.Windows.FirstOrDefault();
            if (top == null) continue;
            var snap = _manager.Snapshot(top.Hwnd);
            if (snap == null && !Win32.IsIconic(top.Hwnd))
            {
                _manager.TakeSnapshot(top.Hwnd);
                snap = _manager.Snapshot(top.Hwnd);
            }
            _list.Children.Add(MakeCard(app, top, snap));
        }
    }

    private FrameworkElement MakeCard(RunningApp app, AppWindow top, ImageSource snap)
    {
        var card = new Grid { Width = CardW, Height = CardH, Margin = new Thickness(0, 4, 0, 10), Cursor = Cursors.Hand, Background = Brushes.Transparent };

        AxisAngleRotation3D rotation = null;
        if (snap != null)
        {
            card.Children.Add(Tilted(snap, CardW, CardH, out rotation));
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
            LoadIcon(bigIcon, app.IconSource);
            card.Children.Add(placeholder);
        }

        // App icon badge on the bottom-left corner.
        var badge = new Image
        {
            Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-4, 0, 0, 4),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5 },
        };
        RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.HighQuality);
        LoadIcon(badge, app.IconSource);
        card.Children.Add(badge);

        card.ToolTip = string.IsNullOrWhiteSpace(top.Title) ? app.Name : $"{app.Name} — {top.Title}";
        var scale = new ScaleTransform(1, 1);
        card.RenderTransform = scale;
        card.RenderTransformOrigin = new Point(0.35, 0.5);
        card.MouseEnter += (_, _) =>
        {
            var d = TimeSpan.FromMilliseconds(180);
            rotation?.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(TiltDeg * 0.35, d));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, d));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, d));
        };
        card.MouseLeave += (_, _) =>
        {
            var d = TimeSpan.FromMilliseconds(220);
            rotation?.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(TiltDeg, d));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d));
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            Conceal();
            _manager.Bring(app);
        };
        return card;
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
        var cached = ShellIcons.TryGetCached(source, 64);
        if (cached != null) { target.Source = cached; return; }
        Task.Run(() => ShellIcons.Get(source, 64)).ContinueWith(t => target.Source = t.Result,
            TaskScheduler.FromCurrentSynchronizationContext());
    }
}
