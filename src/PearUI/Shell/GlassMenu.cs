using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using PearUI.Native;

namespace PearUI.Shell;

/// <summary>
/// A drop-down menu drawn as a real frosted-glass window (acrylic blur + native rounded corners),
/// used by the menu bar. WPF's ContextMenu popups are layered windows, which can't show blur cleanly.
/// </summary>
public sealed class GlassMenu : OverlayWindow
{
    public sealed record Entry(string Header, string Gesture = null, Action Action = null, bool Enabled = true, bool Checked = false);

    private readonly List<(Border Row, Entry Entry)> _rows = new();
    private int _hot = -1;
    private bool _closing;

    public event Action MenuClosed;

    public GlassMenu(IEnumerable<Entry> entries) : base(SurfaceKind.Popup, noActivate: false)
    {
        Title = "PearUI Menu";
        FallbackCornerRadius = 8;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 220;

        var list = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
        foreach (var e in entries)
        {
            if (e == null)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(12, 5, 12, 5) };
                sep.SetResourceReference(Border.BackgroundProperty, "Brush.Separator");
                list.Children.Add(sep);
                continue;
            }
            list.Children.Add(MakeRow(e));
        }
        Content = list;

        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += OnKey;
    }

    private Border MakeRow(Entry e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var check = new TextBlock { Text = e.Checked ? "✓" : "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new TextBlock { Text = e.Header, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var gesture = new TextBlock { Text = e.Gesture ?? "", FontSize = 13, Opacity = 0.5, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(header, 1);
        Grid.SetColumn(gesture, 2);
        grid.Children.Add(check);
        grid.Children.Add(header);
        grid.Children.Add(gesture);

        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(5), Margin = new Thickness(5, 0, 5, 0),
            Padding = new Thickness(6, 3, 12, 4), Background = Brushes.Transparent,
            Opacity = e.Enabled ? 1 : 0.35,
        };
        row.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        int index = _rows.Count;
        _rows.Add((row, e));
        if (e.Enabled)
        {
            row.MouseEnter += (_, _) => SetHot(index);
            row.MouseLeave += (_, _) => { if (_hot == index) SetHot(-1); };
            row.MouseLeftButtonUp += (_, _) => Invoke(e);
        }
        return row;
    }

    private void SetHot(int index)
    {
        if (_hot >= 0 && _hot < _rows.Count)
        {
            var old = _rows[_hot].Row;
            old.Background = Brushes.Transparent;
            old.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        }
        _hot = index;
        if (_hot >= 0)
        {
            var row = _rows[_hot].Row;
            row.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            row.SetResourceReference(TextElement.ForegroundProperty, "Brush.AccentText");
        }
    }

    private void Invoke(Entry e)
    {
        CloseMenu();
        if (e.Action != null) Dispatcher.BeginInvoke(e.Action); // after the menu is gone (focus returns to the app)
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var enabled = Enumerable.Range(0, _rows.Count).Where(i => _rows[i].Entry.Enabled).ToList();
        switch (e.Key)
        {
            case Key.Escape: CloseMenu(); break;
            case Key.Down when enabled.Count > 0:
                SetHot(enabled.FirstOrDefault(i => i > _hot, enabled[0])); break;
            case Key.Up when enabled.Count > 0:
                SetHot(enabled.LastOrDefault(i => i < _hot, enabled[^1])); break;
            case Key.Enter when _hot >= 0: Invoke(_rows[_hot].Entry); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Opens with the top-left corner at a screen point (device pixels). The window is created, glassed,
    /// measured and positioned while still hidden, then shown in one step: no jump, no unblurred flash.
    /// </summary>
    public void ShowAt(int x, int y)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle(); // runs OnSourceInitialized (glass, corners)
        double s = Win32.GetScaleForWindow(handle);

        if (Content is UIElement root)
        {
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double wDip = Math.Max(MinWidth, root.DesiredSize.Width), hDip = root.DesiredSize.Height;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
            int w = (int)Math.Ceiling(wDip * s), h = (int)Math.Ceiling(hDip * s);
            if (x + w > screen.Right) x = screen.Right - w - 4;
            if (y + h > screen.Bottom) y = Math.Max(screen.Top, screen.Bottom - h - 4);
            if (x < screen.Left) x = screen.Left;
            SizeToContent = SizeToContent.Manual;
            Width = wDip; Height = hDip;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = x / s; Top = y / s;
        Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Show();
        Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE);
        Win32.ActivateWindow(handle);
    }

    public void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        MenuClosed?.Invoke();
        Close();
    }
}
