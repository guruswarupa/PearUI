using System.Windows.Threading;
using PearUI.Native;

namespace PearUI.Shell;

/// <summary>
/// Sends keyboard shortcuts to another app. Used by the menu bar's File/Edit/View menus:
/// almost every Windows app understands Ctrl+C, Ctrl+Z, Ctrl+S, etc.
/// </summary>
public static class Keys
{
    public const byte Ctrl = 0x11, Shift = 0x10, Alt = 0x12, Win = 0x5B;

    public static void SendTo(IntPtr hwnd, params byte[] keys)
    {
        if (hwnd != IntPtr.Zero && Win32.IsWindow(hwnd)) Win32.ActivateWindow(hwnd);
        // Give the target a moment to become active and the menu popup to close.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Win32.PressKeys(keys);
        };
        t.Start();
    }

    public static byte Letter(char c) => (byte)char.ToUpperInvariant(c);
}
