using Microsoft.Win32;
using PearUI.Native;

namespace PearUI.Core;

/// <summary>
/// Jumps straight to a Windows virtual desktop by number (desktop 3 = Win+Shift+3), creating desktops when there are not yet
/// enough. Windows has no keyboard shortcut or public API for "go to desktop N", so this drives the shortcuts it does have:
/// Win+Ctrl+Left / Right to move and Win+Ctrl+D to create. It always goes to the first desktop and then steps right, so it does
/// not depend on knowing the current one.
/// </summary>
public static class VirtualDesktops
{
    public const int MaxDesktops = 10;

    private static int _busy;

    /// <summary>How many virtual desktops exist (read from where Explorer keeps them).</summary>
    public static int Count()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (k?.GetValue("VirtualDesktopIDs") is byte[] ids && ids.Length >= 16) return ids.Length / 16;
        }
        catch { }
        return 1;
    }

    /// <summary>
    /// Moves a window (of any program) to desktop <paramref name="number"/>, creating the desktop if needed, and by default goes
    /// there with it. Needs the shell's internal interface (Windows 11 24H2 or newer): false when this PC does not have it.
    /// </summary>
    public static bool MoveWindowTo(IntPtr hwnd, int number, bool follow = true)
    {
        number = Math.Clamp(number, 1, MaxDesktops);
        if (!VirtualDesktopApi.IsSupported) return false;
        bool ok = VirtualDesktopApi.MoveWindowTo(hwnd, number);
        if (ok && follow)
        {
            VirtualDesktopApi.SwitchTo(number);
            RefreshSoon();
            ActivateSoon(hwnd);            // the window you moved stays the one you are working in
        }
        return ok;
    }

    /// <summary>Switches to desktop <paramref name="number"/> (1 to 10), creating desktops if needed.</summary>
    public static void GoTo(int number)
    {
        number = Math.Clamp(number, 1, MaxDesktops);
        if (VirtualDesktopApi.IsSupported && VirtualDesktopApi.SwitchTo(number)) { RefreshSoon(); ActivateSoon(IntPtr.Zero); return; }
        GoToWithKeys(number);
    }

    /// <summary>
    /// After a desktop switch nothing has the keyboard focus, so a shortcut like Win+Shift+3 (move the window) would find no window
    /// until you clicked one. Give the focus to <paramref name="preferred"/> (a window you just moved), or otherwise to the window
    /// that is on top on this desktop, once Windows has finished switching.
    /// </summary>
    private static void ActivateSoon(IntPtr preferred)
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null) return;
        _ = Task.Delay(260).ContinueWith(_ => ui.BeginInvoke(() =>
        {
            try
            {
                WindowTracker.Instance.Refresh();
                if (preferred != IntPtr.Zero && Win32.IsWindow(preferred)) { Win32.ActivateWindow(preferred); return; }

                var windows = WindowTracker.Instance.Apps.OrderBy(a => a.ZOrder).SelectMany(a => a.Windows).ToList();
                IntPtr fg = Win32.GetForegroundWindow();
                if (windows.Any(w => w.Hwnd == fg)) return;                 // a window on this desktop already has the focus
                var top = windows.FirstOrDefault(w => !w.Minimized && !Win32.IsIconic(w.Hwnd));
                if (top != null) Win32.ActivateWindow(top.Hwnd);
            }
            catch { }
        }));
    }

    /// <summary>The Dock, Stage Manager and the window list follow the desktop you are on: update them right after a switch.</summary>
    private static void RefreshSoon()
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null) return;
        foreach (int ms in new[] { 60, 250, 600 })
            _ = Task.Delay(ms).ContinueWith(_ => ui.BeginInvoke(() => WindowTracker.Instance.Refresh()));
    }

    /// <summary>The fallback for Windows builds without the internal interface: press Win+Ctrl+Left/Right for the user.</summary>
    private static void GoToWithKeys(int number)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Run(number); }
            catch { }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private const byte VK_LWIN = 0x5B, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44;
    private const uint KEYUP = 0x2, EXTENDED = 0x1;

    private static void Key(byte vk, bool up, bool extended = false) =>
        Win32.keybd_event(vk, 0, (up ? KEYUP : 0u) | (extended ? EXTENDED : 0u), UIntPtr.Zero);

    /// <summary>Win is held, Ctrl+key pressed and released.</summary>
    private static async Task Combo(byte vk, bool extended)
    {
        Key(VK_CONTROL, false);
        Key(vk, false, extended);
        Key(vk, true, extended);
        Key(VK_CONTROL, true);
        await Task.Delay(45);
    }

    private static async Task Run(int number)
    {
        // The user is holding Win+Shift: let go of Shift (Win+Ctrl+Shift+Left would not switch desktops), and hold Win ourselves.
        Key(VK_SHIFT, true); Key(VK_LSHIFT, true); Key(VK_RSHIFT, true);
        Key(VK_LWIN, false);
        try
        {
            // Create the missing desktops. Each Win+Ctrl+D adds one and switches to it.
            int have = Count();
            for (int guard = 0; have < number && guard < MaxDesktops; guard++)
            {
                await Combo(VK_D, false);
                for (int wait = 0; wait < 12 && Count() <= have; wait++) await Task.Delay(60);   // until Explorer has made it
                int now = Count();
                if (now <= have) break;
                have = now;
            }

            // Go to desktop 1 (moving left at the first desktop does nothing), then step right.
            for (int i = 0; i < Math.Max(have, number); i++) await Combo(VK_LEFT, true);
            for (int i = 1; i < number; i++) await Combo(VK_RIGHT, true);
        }
        finally { Key(VK_LWIN, true); }
    }
}
