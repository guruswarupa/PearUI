using System.Windows.Forms;
using System.Windows.Threading;
using PearUI.Native;

namespace PearUI.Core;

/// <summary>
/// Keeps ordinary app windows inside the work area, so they never sit under the menu bar, the Dock or
/// the Stage Manager strip. Windows only does this for *maximized* windows; many apps with custom title
/// bars "maximize" by simply covering the whole monitor, and dragged windows can hang below the Dock.
///
/// A window is fitted when it appears, becomes active, or finishes moving/resizing. Real full-screen
/// windows (games, F11 video: no caption, exactly the monitor's size) are left alone.
/// </summary>
public sealed class WindowFitter : IDisposable
{
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const long WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L;
    private const int Tolerance = 2; // px of overlap we accept (shadows, rounding)

    private readonly Win32.WinEventDelegate _proc;
    private readonly IntPtr _hook;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly Dictionary<IntPtr, (int Count, DateTime Since)> _history = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public WindowFitter()
    {
        _proc = OnWinEvent;
        _hook = Win32.SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        var tracker = WindowTracker.Instance;
        tracker.ForegroundChanged += OnForeground;
        tracker.ForegroundMoved += OnForegroundMoved;
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (Control.MouseButtons == MouseButtons.None) Fit(tracker.ForegroundWindow);
        };

        // Tidy up windows that are already open.
        foreach (var app in tracker.Apps)
            foreach (var w in app.Windows)
                Fit(w.Hwnd);
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject == Win32.OBJID_WINDOW && idChild == 0) Fit(hwnd);
    }

    private void OnForeground() => Fit(WindowTracker.Instance.ForegroundWindow);

    // Apps that resize themselves (custom maximize) fire location changes: wait until they settle.
    private void OnForegroundMoved(IntPtr hwnd)
    {
        _settle.Stop();
        _settle.Start();
    }

    public void Fit(IntPtr hwnd)
    {
        if (!SettingsStore.Current.KeepWindowsClear) return;
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd)) return;
        if (Win32.IsIconic(hwnd) || Win32.IsZoomed(hwnd)) return; // the OS already fits maximized windows
        if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return; // dialogs follow their owner
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownPid) return;

        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool resizable = (style & WS_THICKFRAME) != 0;
        if (!hasCaption && !resizable) return; // popups, splash screens, games

        var mi = Win32.GetMonitorInfoFor(hwnd);
        var work = mi.rcWork;
        var mon = mi.rcMonitor;
        var f = Win32.GetFrameBounds(hwnd);
        if (f.Width <= 0 || f.Height <= 0) return;

        // Exactly the monitor and no caption = intentional full screen.
        if (!hasCaption && f.Left == mon.Left && f.Top == mon.Top && f.Right == mon.Right && f.Bottom == mon.Bottom) return;

        bool inside = f.Left >= work.Left - Tolerance && f.Top >= work.Top - Tolerance &&
                      f.Right <= work.Right + Tolerance && f.Bottom <= work.Bottom + Tolerance;
        if (inside) return;

        // Don't fight apps that keep re-expanding themselves.
        var now = DateTime.Now;
        if (_history.TryGetValue(hwnd, out var h) && (now - h.Since).TotalSeconds < 10)
        {
            if (h.Count >= 3) return;
            _history[hwnd] = (h.Count + 1, h.Since);
        }
        else _history[hwnd] = (1, now);

        int w = resizable ? Math.Min(f.Width, work.Width) : f.Width;
        int ht = resizable ? Math.Min(f.Height, work.Height) : f.Height;
        int x = Math.Clamp(f.Left, work.Left, Math.Max(work.Left, work.Right - w));
        int y = Math.Clamp(f.Top, work.Top, Math.Max(work.Top, work.Bottom - ht));

        // Window rect includes invisible resize borders; keep them around the new frame.
        Win32.GetWindowRect(hwnd, out var wr);
        int dl = f.Left - wr.Left, dt = f.Top - wr.Top, dr = wr.Right - f.Right, db = wr.Bottom - f.Bottom;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x - dl, y - dt, w + dl + dr, ht + dt + db,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    public void Dispose()
    {
        _settle.Stop();
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
        WindowTracker.Instance.ForegroundChanged -= OnForeground;
        WindowTracker.Instance.ForegroundMoved -= OnForegroundMoved;
    }
}
