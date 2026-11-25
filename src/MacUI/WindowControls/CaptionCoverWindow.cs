using System.Runtime.InteropServices;
using System.Windows.Media;
using MacUI.Native;
using MacUI.Shell;

namespace MacUI.WindowControls;

/// <summary>
/// Covers the Windows caption buttons of the active window when the app draws them itself
/// (Chrome, Edge, VS Code, File Explorer, Store apps…), painted in the title bar's own color so the
/// title bar looks clean. Clicks on the hidden buttons are blocked; use the macOS buttons instead.
/// </summary>
public sealed class CaptionCoverWindow : OverlayWindow
{
    /// <summary>Standard Windows 11 caption buttons: 3 × 46 DIP wide, ~32 DIP tall.</summary>
    public const double CoverW = 140, CoverH = 31;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

    public CaptionCoverWindow()
    {
        Title = "PearUI Caption Cover";
        Width = CoverW;
        Height = CoverH;
        Background = Brushes.Transparent;
    }

    /// <summary>Positions over the target's top-right corner and repaints with the sampled title-bar color.</summary>
    public void Cover(Win32.RECT frame, double scale)
    {
        int w = (int)Math.Ceiling(CoverW * scale), h = (int)Math.Ceiling(CoverH * scale);
        int x = frame.Right - w - 1, y = frame.Top + 1;

        // Sample the title bar just left of the buttons (outside the cover itself).
        var c = Sample(x - (int)(6 * scale), y + (int)(4 * scale));
        if (c is Color color) Background = new SolidColorBrush(color);
        if (!IsVisible) Show();
        PlacePx(x, y, w, h);
    }

    private static Color? Sample(int x, int y)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            uint p = GetPixel(dc, x, y);
            if (p == 0xFFFFFFFF) return null; // CLR_INVALID
            return Color.FromRgb((byte)(p & 0xFF), (byte)((p >> 8) & 0xFF), (byte)((p >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
}
