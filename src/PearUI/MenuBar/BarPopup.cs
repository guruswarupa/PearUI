using System.Windows;
using System.Windows.Input;
using PearUI.Native;
using PearUI.Shell;

namespace PearUI.MenuBar;

/// <summary>A frosted popup that drops down from a menu bar item and closes when it loses focus or on Esc.</summary>
public abstract class BarPopup : OverlayWindow
{
    private bool _closing;

    public event Action PopupClosed;

    protected BarPopup(double width) : base(SurfaceKind.Popup, noActivate: false)
    {
        FallbackCornerRadius = 12;
        SizeToContent = SizeToContent.Manual;
        Width = width;
        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseMenu(); e.Handled = true; } };
    }

    /// <summary>Opens with the top-left corner at a screen point (device pixels), clamped to the screen.</summary>
    public void ShowAt(int x, int y)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle(); // glass + corners
        double s = Win32.GetScaleForWindow(handle);

        if (Content is UIElement root)
        {
            root.Measure(new Size(Width, double.PositiveInfinity));
            Height = Math.Ceiling(root.DesiredSize.Height);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
            int w = (int)Math.Ceiling(Width * s), h = (int)Math.Ceiling(Height * s);
            if (x + w > screen.Right) x = screen.Right - w - 8;
            if (y + h > screen.Bottom) y = Math.Max(screen.Top, screen.Bottom - h - 4);
            if (x < screen.Left) x = screen.Left;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = x / s; Top = y / s;
        Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Show();
        Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE);
        Win32.ActivateWindow(handle);
        OnShown();
    }

    protected virtual void OnShown() { }

    public void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        PopupClosed?.Invoke();
        Close();
    }
}
