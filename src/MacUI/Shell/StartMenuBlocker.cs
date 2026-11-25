using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacUI.Shell;

/// <summary>
/// Replaces the Windows Start menu: tapping the Windows key on its own opens PearUI's All Apps
/// instead. Windows-key shortcuts (Win+E, Win+D, Win+Tab, …) keep working.
///
/// How: a low-level keyboard hook watches the Windows key. If it is released without another key in
/// between, a harmless unassigned key (VK 0xE8) is injected first; Windows then treats the press as a
/// shortcut, not a tap, and the Start menu doesn't open.
/// </summary>
public sealed class StartMenuBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const byte VK_MASK = 0xE8; // unassigned
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private readonly HookProc _proc; // keep alive for the native hook
    private IntPtr _hook;
    private bool _winDown, _otherKey;

    public StartMenuBlocker()
    {
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & LLKHF_INJECTED) != 0; // PearUI's own Win+Tab etc.
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN, up = msg is WM_KEYUP or WM_SYSKEYUP;
            bool isWin = k.vkCode is VK_LWIN or VK_RWIN;

            if (!injected)
            {
                if (isWin && down)
                {
                    if (!_winDown) { _winDown = true; _otherKey = false; }
                }
                else if (!isWin && down && _winDown)
                {
                    _otherKey = true; // a Win+X shortcut: leave it alone
                }
                else if (isWin && up && _winDown)
                {
                    _winDown = false;
                    if (!_otherKey && !FullScreenAppInFront())
                    {
                        // Lone tap: mask it so Start doesn't open, then show All Apps.
                        keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_MASK, 0, 2 /*KEYUP*/, UIntPtr.Zero);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => App.Instance.ToggleAllApps());
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>In a game or other full-screen app the Windows key keeps its normal behaviour.</summary>
    private static bool FullScreenAppInFront()
    {
        try
        {
            IntPtr fg = MacUI.Native.Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            return Core.WindowTracker.Instance.IsFullScreenOn(MacUI.Native.Win32.GetMonitorInfoFor(fg).rcMonitor);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
