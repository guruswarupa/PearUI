using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Control = System.Windows.Forms.Control;
using MouseButtons = System.Windows.Forms.MouseButtons;
using MacUI.Core;
using MacUI.Native;
using MacUI.Shell;

namespace MacUI.WindowControls;

/// <summary>
/// macOS-style close / minimize / zoom buttons drawn in the title bar of the active window.
/// Windows apps draw their own title bars, so this is an overlay that follows the window; the
/// app's own Windows buttons remain at the right.
/// </summary>
public sealed class TrafficLightsWindow : OverlayWindow
{
    private const double Dot = 14, Gap = 9, PadX = 12, BarH = 32;
    private static readonly double W = PadX + Dot * 3 + Gap * 2 + 8;

    private IntPtr _target;
    private readonly List<(Ellipse Circle, Path Glyph, Color Base)> _buttons = new();
    private bool _hover;

    public TrafficLightsWindow()
    {
        Title = "PearUI Window Controls";
        Width = W;
        Height = BarH;
        // Alpha 1 keeps the whole strip clickable between the dots.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(PadX, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
        };
        // Glyph geometry is drawn in a 12×12 box.
        panel.Children.Add(MakeButton(Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0x4D, 0x00, 0x00),
            "M3.6,3.6 L8.4,8.4 M8.4,3.6 L3.6,8.4", false, () => WindowTracker.Close(_target)));
        panel.Children.Add(MakeButton(Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0x99, 0x57, 0x00),
            "M3,6 L9,6", false, () => WindowTracker.Minimize(_target)));
        panel.Children.Add(MakeButton(Color.FromRgb(0x28, 0xC8, 0x40), Color.FromRgb(0x00, 0x65, 0x00),
            "M3.4,3.4 L7.2,3.4 L3.4,7.2 Z M8.6,8.6 L4.8,8.6 L8.6,4.8 Z", true, () => WindowTracker.ToggleZoom(_target)));
        Content = panel;

        // macOS shows the symbols on all three buttons while hovering the group.
        panel.MouseEnter += (_, _) => { _hover = true; Refresh(); };
        panel.MouseLeave += (_, _) => { _hover = false; Refresh(); };

        Loaded += (_, _) =>
        {
            WindowTracker.Instance.ForegroundChanged += Follow;
            WindowTracker.Instance.ForegroundMoved += OnMoved;
            Follow();
        };
        Closed += (_, _) =>
        {
            WindowTracker.Instance.ForegroundChanged -= Follow;
            WindowTracker.Instance.ForegroundMoved -= OnMoved;
            _cover?.Close();
            _resample.Stop();
        };
        _resample.Tick += (_, _) => { _resample.Stop(); Follow(); };
    }

    private CaptionCoverWindow _cover;
    private IntPtr _sampledFor;
    // Title bars fade to their active color; sample again once that settles.
    private readonly System.Windows.Threading.DispatcherTimer _resample = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private void UpdateCover(Win32.RECT r, double s, bool show)
    {
        if (!show || !SettingsStore.Current.HideWindowsCaptionButtons)
        {
            if (_cover?.IsVisible == true) _cover.Hide();
            return;
        }
        _cover ??= new CaptionCoverWindow();
        _cover.Cover(r, s);
    }

    private FrameworkElement MakeButton(Color fill, Color glyphColor, string glyph, bool filled, Action action)
    {
        var circle = new Ellipse
        {
            Width = Dot, Height = Dot,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), StrokeThickness = 0.6,
        };
        var path = new Path
        {
            Data = Geometry.Parse(glyph), Width = 12, Height = 12, Stretch = Stretch.None, // 12x12 glyph centered in the larger dot
            Stroke = filled ? null : new SolidColorBrush(glyphColor), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Fill = filled ? new SolidColorBrush(glyphColor) : null,
            Visibility = Visibility.Hidden, IsHitTestVisible = false,
        };
        var g = new Grid { Width = Dot, Height = Dot, Margin = new Thickness(0, 0, Gap, 0), Cursor = Cursors.Arrow };
        g.Children.Add(circle);
        g.Children.Add(path);
        _buttons.Add((circle, path, fill));

        g.MouseLeftButtonDown += (_, e) => { circle.Fill = new SolidColorBrush(Darken(fill, 0.78)); e.Handled = true; };
        g.MouseLeftButtonUp += (_, e) => { circle.Fill = new SolidColorBrush(fill); action(); e.Handled = true; };
        g.MouseLeave += (_, _) => circle.Fill = new SolidColorBrush(fill);
        return g;
    }

    private static Color Darken(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    private void Refresh()
    {
        foreach (var b in _buttons) b.Glyph.Visibility = _hover ? Visibility.Visible : Visibility.Hidden;
    }

    private void OnMoved(IntPtr hwnd) => Follow();

    private void Follow()
    {
        _target = WindowTracker.Instance.ForegroundWindow;
        if (_target == IntPtr.Zero || !Win32.IsWindow(_target) || Win32.IsIconic(_target) || !HasCaption(_target)
            || WindowTracker.Instance.IsFullScreenOn(Win32.GetMonitorInfoFor(_target).rcMonitor))
        {
            if (IsVisible) Hide();
            UpdateCover(default, 1, false);
            return;
        }

        var r = Win32.GetFrameBounds(_target);
        double s = Win32.GetScaleForMonitorRect(r);
        if (r.Width < 220 * s || r.Height < 80 * s) { if (IsVisible) Hide(); UpdateCover(r, s, false); return; }

        UpdateCover(r, s, true);
        if (_target != _sampledFor && Control.MouseButtons == MouseButtons.None)
        {
            _sampledFor = _target;
            _resample.Stop();
            _resample.Start();
        }
        if (!IsVisible) Show();
        // Vertically centered in a standard ~32 DIP Windows title bar.
        int y = r.Top + (int)Math.Round((32 - BarH) / 2 * s);
        int w = (int)Math.Ceiling(W * s);
        // "Right" puts the buttons where the Windows buttons were, so they never cover app content.
        int x = SettingsStore.Current.WindowControlsPosition == "Right" ? r.Right - w - (int)(4 * s) : r.Left + 1;
        PlacePx(x, Math.Max(r.Top, y), w, (int)Math.Ceiling(BarH * s));
    }

    private static bool HasCaption(IntPtr hwnd)
    {
        // Normal windows (WS_CAPTION) and custom-title-bar apps that are still resizable (WS_THICKFRAME).
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        return (style & 0x00C00000L) == 0x00C00000L || (style & 0x00040000L) != 0;
    }
}
