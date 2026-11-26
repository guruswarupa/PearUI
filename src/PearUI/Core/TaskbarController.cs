using System.Runtime.InteropServices;
using System.Windows.Threading;
using PearUI.Native;
using Microsoft.Win32;

namespace PearUI.Core;

/// <summary>
/// Hides the Windows taskbar while PearUI runs and restores it on exit.
/// The taskbar is switched to auto-hide first so it stops reserving screen space.
/// </summary>
public static class TaskbarController
{
    private static bool _hidden;
    private static int _originalState = -1;
    private static DispatcherTimer _keeper;

    private static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = Win32.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr after = IntPtr.Zero;
        while ((after = Win32.FindWindowEx(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return after;
    }

    private static int GetState()
    {
        var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = Win32.FindWindow("Shell_TrayWnd", null) };
        return (int)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref abd).ToInt64();
    }

    private static void SetState(int state)
    {
        var abd = new Win32.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Win32.APPBARDATA>(),
            hWnd = Win32.FindWindow("Shell_TrayWnd", null),
            lParam = (IntPtr)state,
        };
        Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref abd);
    }

    public static void Hide()
    {
        if (_hidden) return;
        _originalState = GetState();
        SaveOriginalState(_originalState);
        SetState(_originalState | Win32.ABS_AUTOHIDE);
        foreach (var h in TaskbarWindows()) Win32.ShowWindow(h, Win32.SW_HIDE);
        _hidden = true;

        // Explorer re-shows the taskbar after some events (Start menu, explorer restart): keep it hidden.
        _keeper ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _keeper.Tick -= Keep;
        _keeper.Tick += Keep;
        _keeper.Start();
    }

    private static void Keep(object s, EventArgs e)
    {
        if (!_hidden) return;
        foreach (var h in TaskbarWindows())
            if (Win32.IsWindowVisible(h)) Win32.ShowWindow(h, Win32.SW_HIDE);
    }

    public static void Show()
    {
        _keeper?.Stop();
        if (!_hidden)
        {
            // A previous session may have crashed with the taskbar hidden.
            RestoreIfCrashed();
            return;
        }
        foreach (var h in TaskbarWindows()) Win32.ShowWindow(h, Win32.SW_SHOW);
        if (_originalState >= 0) SetState(_originalState);
        ClearOriginalState();
        _hidden = false;
    }

    // Persist the original state so a crash never leaves the user without a taskbar.
    private const string Key = @"Software\PearUI";

    private static void SaveOriginalState(int state)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue("TaskbarOriginalState", state); } catch { }
    }

    private static void ClearOriginalState()
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.DeleteValue("TaskbarOriginalState", false); } catch { }
    }

    public static void RestoreIfCrashed()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue("TaskbarOriginalState") is int state)
            {
                foreach (var h in TaskbarWindows()) Win32.ShowWindow(h, Win32.SW_SHOW);
                SetState(state);
                ClearOriginalState();
            }
        }
        catch { }
    }
}
