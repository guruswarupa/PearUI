using System.Runtime.InteropServices;
using PearUI.Core;
using PearUI.Native;
using Forms = System.Windows.Forms;

namespace PearUI.Shell;

/// <summary>Moving and sizing the window you are working in: tiling, centring, sending it to another display.</summary>
internal static class WindowActions
{
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);

    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    /// <summary>The window to act on: the one in front, unless it is the desktop, the taskbar or PearUI itself.</summary>
    public static IntPtr Target()
    {
        IntPtr h = Win32.GetForegroundWindow();
        if (h == IntPtr.Zero || h == Win32.GetShellWindow()) return IntPtr.Zero;
        Win32.GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return IntPtr.Zero;
        string cls = Win32.GetClass(h);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? IntPtr.Zero : h;
    }

    /// <summary>Places the window on a fraction of its display's work area (0–1 for each of x, y, width and height).</summary>
    public static void Place(double fx, double fy, double fw, double fh)
    {
        var h = Target();
        if (h == IntPtr.Zero) return;
        var area = Win32.GetMonitorInfoFor(h).rcWork;
        int w = area.Right - area.Left, ht = area.Bottom - area.Top;
        MoveTo(h, area.Left + (int)(w * fx), area.Top + (int)(ht * fy), (int)(w * fw), (int)(ht * fh));
    }

    public static void Center()
    {
        var h = Target();
        if (h == IntPtr.Zero) return;
        var area = Win32.GetMonitorInfoFor(h).rcWork;
        var frame = Win32.GetFrameBounds(h);
        int w = frame.Right - frame.Left, ht = frame.Bottom - frame.Top;
        MoveTo(h, area.Left + (area.Right - area.Left - w) / 2, area.Top + (area.Bottom - area.Top - ht) / 2, w, ht);
    }

    public static void ToggleMaximize()
    {
        var h = Target();
        if (h == IntPtr.Zero) return;
        Win32.ShowWindowAsync(h, IsZoomed(h) ? Win32.SW_RESTORE : 3 /*SW_MAXIMIZE*/);
    }

    public static void Minimize()
    {
        var h = Target();
        if (h != IntPtr.Zero) WindowTracker.Minimize(h);
    }

    public static void Close()
    {
        var h = Target();
        if (h != IntPtr.Zero) WindowTracker.Close(h);
    }

    /// <summary>Sends the window to the next display, keeping its place and size relative to the screen.</summary>
    public static void NextDisplay()
    {
        var h = Target();
        var screens = Forms.Screen.AllScreens;
        if (h == IntPtr.Zero || screens.Length < 2) return;

        var frame = Win32.GetFrameBounds(h);
        var from = Forms.Screen.FromHandle(h);
        var to = screens[(Array.FindIndex(screens, s => s.DeviceName == from.DeviceName) + 1) % screens.Length];
        var a = from.WorkingArea; var b = to.WorkingArea;
        double sx = (double)b.Width / a.Width, sy = (double)b.Height / a.Height;
        bool zoomed = IsZoomed(h);
        if (zoomed) Win32.ShowWindowAsync(h, Win32.SW_RESTORE);

        int x = b.Left + (int)((frame.Left - a.Left) * sx), y = b.Top + (int)((frame.Top - a.Top) * sy);
        MoveTo(h, x, y, (int)((frame.Right - frame.Left) * sx), (int)((frame.Bottom - frame.Top) * sy));
        if (zoomed) Win32.ShowWindowAsync(h, 3 /*SW_MAXIMIZE*/);
    }

    /// <summary>Moves a window so its visible edges land on the given rectangle (Windows adds invisible borders around windows).</summary>
    private static void MoveTo(IntPtr h, int x, int y, int w, int ht)
    {
        if (IsZoomed(h)) Win32.ShowWindowAsync(h, Win32.SW_RESTORE);
        System.Threading.Thread.Sleep(30);   // let the restore finish before measuring
        if (!Win32.GetWindowRect(h, out var rect)) return;
        var frame = Win32.GetFrameBounds(h);
        int l = frame.Left - rect.Left, t = frame.Top - rect.Top, r = rect.Right - frame.Right, b = rect.Bottom - frame.Bottom;
        Win32.SetWindowPos(h, IntPtr.Zero, x - l, y - t, w + l + r, ht + t + b, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
