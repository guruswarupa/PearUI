using System.Diagnostics;
using System.Runtime.InteropServices;
using PearUI.Core;

namespace PearUI.Shell;

/// <summary>
/// PearUI's global keyboard hook. It does three things:
///  • tapping the Windows key on its own opens All Apps instead of the Start menu (if switched on);
///  • your own shortcuts (Settings → Shortcuts), such as Win+S for search or Win+Alt+Left to tile a window;
///  • the shortcut recorder in Settings, which captures any combination, even ones Windows normally keeps for itself.
/// Windows-key shortcuts that you have not rebound (Win+E, Win+D, Win+Tab…) keep working.
///
/// How the Start menu is kept shut: if the Windows key is released without another key in between, a harmless
/// unassigned key (VK 0xE8) is injected first; Windows then treats the press as a shortcut, not a tap.
///
/// In a full-screen app (a game, a movie, a presentation) a lone Windows-key tap does nothing at all, and your
/// shortcuts stay quiet. The key press is held back, and only handed to Windows if you press a combination with it.
/// </summary>
public sealed class StartMenuBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const byte VK_MASK = 0xE8; // unassigned
    private const uint LLKHF_INJECTED = 0x10;
    private static readonly IntPtr Block = new(1);

    /// <summary>While set, the next key combination is reported here (and swallowed) instead of being used. Settings uses it to record a shortcut.</summary>
    public static Action<string> Capture;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private readonly HookProc _proc; // keep alive for the native hook
    private IntPtr _hook;
    private bool _winDown, _otherKey;
    private bool _held;            // full-screen app: the Windows key press is being held back
    private byte _heldVk;
    private bool _swallowedCombo;  // a Win+key combination was taken over, so Windows only saw the Win key
    private readonly HashSet<uint> _consumed = new();   // keys whose release (and repeats) we swallow

    private readonly bool _replaceStart;

    /// <param name="replaceStart">Whether tapping the Windows key opens All Apps instead of the Start menu.</param>
    public StartMenuBlocker(bool replaceStart = true)
    {
        _replaceStart = replaceStart;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    private static bool IsModifier(uint vk) => vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5);

    private KeyMods CurrentMods()
    {
        var m = KeyMods.None;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) m |= KeyMods.Ctrl;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) m |= KeyMods.Alt;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) m |= KeyMods.Shift;
        if (_winDown) m |= KeyMods.Win;
        return m;
    }

    private void Consume(uint vk)
    {
        _consumed.Add(vk);
        if (_winDown) { _otherKey = true; _swallowedCombo = true; }   // Windows will only see the Win key: it must not open Start
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) == 0) // PearUI's own keystrokes pass straight through
            {
                int msg = wParam.ToInt32();
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN, up = msg is WM_KEYUP or WM_SYSKEYUP;
                bool isWin = k.vkCode is VK_LWIN or VK_RWIN;
                if (Handle(k.vkCode, down, up, isWin)) return Block;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Returns true when the key must be swallowed.</summary>
    private bool Handle(uint vk, bool down, bool up, bool isWin)
    {
        // The rest of a key we already took over (auto-repeat and the release).
        if (!isWin && _consumed.Contains(vk))
        {
            if (up) _consumed.Remove(vk);
            return true;
        }

        if (isWin)
        {
            if (down)
            {
                if (!_winDown)
                {
                    _winDown = true;
                    _otherKey = false;
                    _swallowedCombo = false;
                    _heldVk = (byte)vk;
                    _held = _replaceStart && FullScreenGuard.IsFullScreenAppActive();
                }
                return _held;   // full-screen app: swallow the press (and its auto-repeat)
            }
            if (up && _winDown)
            {
                _winDown = false;
                if (_held) { _held = false; return true; }   // lone tap in a full-screen app: nothing happens
                if (!_otherKey)
                {
                    if (!_replaceStart) return false;        // Windows keeps its Start menu
                    keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_MASK, 0, 2 /*KEYUP*/, UIntPtr.Zero);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => App.Instance.ToggleAllApps());
                }
                else if (_swallowedCombo)
                {
                    keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_MASK, 0, 2, UIntPtr.Zero);
                }
                _swallowedCombo = false;
            }
            return false;
        }

        if (!down || IsModifier(vk)) return false;

        // The shortcut recorder in Settings takes the next combination, whatever it is.
        var capture = Capture;
        if (capture != null)
        {
            string spec = Shortcuts.Format(CurrentMods(), (int)vk);
            Consume(vk);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => capture(spec));
            return true;
        }

        if (Shortcuts.HasKey((int)vk) && !_held && Shortcuts.TryGet(CurrentMods(), (int)vk, out var binding) && !FullScreenGuard.IsFullScreenAppActive())
        {
            Consume(vk);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShortcutActions.Run(binding));
            return true;
        }

        if (_winDown)
        {
            _otherKey = true; // a Win+X shortcut that belongs to Windows: leave it alone
            if (_held)
            {
                // It is a shortcut after all: give Windows the Win key back, then let this key through.
                _held = false;
                keybd_event(_heldVk, 0, 0, UIntPtr.Zero);
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
