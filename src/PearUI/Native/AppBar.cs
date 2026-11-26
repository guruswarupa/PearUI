using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MacUI.Native;

/// <summary>
/// Registers a WPF window as a shell "AppBar" so Windows reserves screen space for it
/// (maximized windows stop at its edge, just like the macOS menu bar).
/// </summary>
public sealed class AppBar : IDisposable
{
    private readonly Window _window;
    private readonly uint _edge;
    private readonly Func<Win32.RECT> _getMonitorBounds;
    private readonly Func<int> _getThicknessPx;
    private readonly uint _callbackMsg;
    private static readonly uint TaskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");
    private HwndSource _source;
    private bool _registered;

    /// <summary>Raised when a full-screen app opens (true) or closes (false) on this bar's monitor.</summary>
    public event Action<bool> FullScreenChanged;

    /// <summary>If true the bar reserves space; if false it only keeps its position (used for auto-hide docks).</summary>
    public bool ReserveSpace { get; set; } = true;

    /// <summary>Called after the reserved rect is settled; receives the rect the window should occupy.</summary>
    public Action<Win32.RECT> PositionWindow { get; set; }

    public AppBar(Window window, uint edge, Func<Win32.RECT> getMonitorBounds, Func<int> getThicknessPx)
    {
        _window = window;
        _edge = edge;
        _getMonitorBounds = getMonitorBounds;
        _getThicknessPx = getThicknessPx;
        _callbackMsg = Win32.RegisterWindowMessage("MacUI.AppBar." + Guid.NewGuid().ToString("N"));
    }

    private IntPtr Hwnd => new WindowInteropHelper(_window).Handle;

    public void Attach()
    {
        _source = HwndSource.FromHwnd(Hwnd);
        _source.AddHook(WndProc);
        Register();
    }

    private void Register()
    {
        if (ReserveSpace && !_registered)
        {
            var abd = NewData();
            abd.uCallbackMessage = _callbackMsg;
            Win32.SHAppBarMessage(Win32.ABM_NEW, ref abd);
            _registered = true;
        }
        UpdatePosition();
    }

    private void Unregister()
    {
        if (!_registered) return;
        var abd = NewData();
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
        _registered = false;
    }

    private Win32.APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<Win32.APPBARDATA>(),
        hWnd = Hwnd,
        uEdge = _edge,
    };

    public void UpdatePosition()
    {
        var mon = _getMonitorBounds();
        int thick = _getThicknessPx();
        var rc = _edge switch
        {
            Win32.ABE_TOP => new Win32.RECT(mon.Left, mon.Top, mon.Right, mon.Top + thick),
            Win32.ABE_BOTTOM => new Win32.RECT(mon.Left, mon.Bottom - thick, mon.Right, mon.Bottom),
            Win32.ABE_LEFT => new Win32.RECT(mon.Left, mon.Top, mon.Left + thick, mon.Bottom),
            _ => new Win32.RECT(mon.Right - thick, mon.Top, mon.Right, mon.Bottom),
        };

        if (ReserveSpace && _registered)
        {
            var abd = NewData();
            abd.rc = rc;
            Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);
            // Keep our thickness after the shell adjusted the proposed edge.
            switch (_edge)
            {
                case Win32.ABE_TOP: abd.rc.Bottom = abd.rc.Top + thick; break;
                case Win32.ABE_BOTTOM: abd.rc.Top = abd.rc.Bottom - thick; break;
                case Win32.ABE_LEFT: abd.rc.Right = abd.rc.Left + thick; break;
                case Win32.ABE_RIGHT: abd.rc.Left = abd.rc.Right - thick; break;
            }
            Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);
            rc = abd.rc;
        }

        if (PositionWindow != null) PositionWindow(rc);
        else Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, rc.Left, rc.Top, rc.Width, rc.Height, Win32.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMsg)
        {
            switch ((uint)wParam.ToInt64())
            {
                case Win32.ABN_POSCHANGED:
                    UpdatePosition();
                    break;
                case Win32.ABN_FULLSCREENAPP:
                    FullScreenChanged?.Invoke(lParam != IntPtr.Zero);
                    break;
            }
            handled = true;
        }
        else if (msg == TaskbarCreatedMsg)
        {
            // Explorer restarted: our registration is gone, register again.
            _registered = false;
            Register();
        }
        else if (msg == 0x007E /*WM_DISPLAYCHANGE*/)
        {
            _window.Dispatcher.BeginInvoke(UpdatePosition);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
    }
}
