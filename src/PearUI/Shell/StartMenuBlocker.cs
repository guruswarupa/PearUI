using System.Diagnostics;
using System.Runtime.InteropServices;
using PearUI.Core;

namespace PearUI.Shell;

/// <summary>
/// Replaces the Windows Start menu: tapping the Windows key on its own opens PearUI's All Apps
/// instead. Windows-key shortcuts (Win+E, Win+D, Win+Tab, …) keep working.
///
/// How: a low-level keyboard hook watches the Windows key. If it is released without another key in
/// between, a harmless unassigned key (VK 0xE8) is injected first; Windows then treats the press as a
/// shortcut, not a tap, and the Start menu doesn't open.
///
/// In a full-screen app (a game, a movie, a presentation) a lone Windows-key tap does nothing at all:
/// no Start menu and no All Apps. The key press is held back, and only handed to Windows if you press a
/// shortcut with it (Win+D, Win+Tab…), so shortcuts still work.
/// </summary>
public sealed class StartMenuBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const byte VK_MASK = 0xE8; // unassigned
    private const uint LLKHF_INJECTED = 0x10;
    private static readonly IntPtr Block = new(1);

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
    private bool _held;       // full-screen app: the Windows key press is being held back
    private byte _heldVk;
    private bool _searchUsed; // Win+S was taken over for PearUI search
    private readonly Action _onTap, _onSearch;
    private readonly Func<bool> _shouldHandle, _fullScreen;

    /// <summary>
    /// Normal use (no arguments): taps open All Apps, Win+S opens search. The key-helper (elevated, for
    /// admin windows such as Task Manager that a normal hook cannot see) passes its own callbacks.
    /// </summary>
    public StartMenuBlocker(Action onTap = null, Action onSearch = null, Func<bool> shouldHandle = null, Func<bool> fullScreen = null)
    {
        _onTap = onTap ?? (() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => App.Instance.ToggleAllApps()));
        _onSearch = onSearch ?? (() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => App.Instance.ToggleLauncher()));
        _shouldHandle = shouldHandle;
        _fullScreen = fullScreen ?? FullScreenGuard.IsFullScreenAppActive;
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
                    if (!_winDown)
                    {
                        if (_shouldHandle != null && !_shouldHandle()) return CallNextHookEx(_hook, code, wParam, lParam);
                        _winDown = true;
                        _otherKey = false;
                        _searchUsed = false;
                        _heldVk = (byte)k.vkCode;
                        _held = _fullScreen();
                    }
                    if (_held) return Block; // full-screen app: swallow the press (and its auto-repeat)
                }
                else if (!isWin && _searchUsed && k.vkCode == 0x53)
                {
                    return Block; // the S key's repeat / release after Win+S was handled
                }
                else if (!isWin && down && _winDown && k.vkCode == 0x53 && !_held)
                {
                    // Win+S: open PearUI search instead of Windows Search.
                    _otherKey = true;
                    _searchUsed = true;
                    _onSearch();
                    return Block;
                }
                else if (!isWin && down && _winDown)
                {
                    _otherKey = true; // a Win+X shortcut: leave it alone
                    if (_held)
                    {
                        // It is a shortcut after all: give Windows the Win key back, then let this key through.
                        _held = false;
                        keybd_event(_heldVk, 0, 0, UIntPtr.Zero);
                    }
                }
                else if (isWin && up && _winDown)
                {
                    _winDown = false;
                    if (_held)
                    {
                        _held = false;
                        return Block; // lone tap in a full-screen app: nothing happens
                    }
                    if (!_otherKey)
                    {
                        // Lone tap: mask it so Start doesn't open, then show All Apps.
                        keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_MASK, 0, 2 /*KEYUP*/, UIntPtr.Zero);
                        _onTap();
                    }
                    else if (_searchUsed)
                    {
                        // Windows only saw the Win key (its S was swallowed): mask it so Start doesn't open.
                        _searchUsed = false;
                        keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_MASK, 0, 2 /*KEYUP*/, UIntPtr.Zero);
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
