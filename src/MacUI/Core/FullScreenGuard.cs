using System.Runtime.InteropServices;
using MacUI.Native;

namespace MacUI.Core;

/// <summary>
/// "Is a full-screen app in front?": games, F11 browsers, full-screen video, presentations.
/// PearUI's Windows-key handling and Hot Corners stay quiet while this is true, so they never
/// pop up on top of a game or a movie.
/// </summary>
public static class FullScreenGuard
{
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    // QUERY_USER_NOTIFICATION_STATE
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    public static bool IsFullScreenAppActive()
    {
        try
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Win32.GetShellWindow()) return false;

            // PearUI's own screens (All Apps, the welcome tour…) are not "full-screen apps".
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;

            string cls = Win32.GetClass(fg);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false; // the desktop

            // A borderless window that covers the whole display.
            if (WindowTracker.Instance.IsFullScreenOn(Win32.GetMonitorInfoFor(fg).rcMonitor)) return true;

            // Exclusive full-screen games, presentations, and anything else Windows itself treats as full screen
            // (the same signal it uses to hold back notifications).
            if (SHQueryUserNotificationState(out int state) == 0 &&
                state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE) return true;
        }
        catch { }
        return false;
    }
}
