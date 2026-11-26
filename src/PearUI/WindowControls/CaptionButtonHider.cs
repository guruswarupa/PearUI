using PearUI.Core;
using PearUI.Native;

namespace PearUI.WindowControls;

/// <summary>
/// Removes the standard Windows minimize / maximize / close buttons so only the macOS-style buttons remain.
///
/// For windows with a standard title bar, clearing WS_SYSMENU makes Windows stop drawing the caption
/// buttons (and the app icon). The original style is restored when PearUI quits or the option is turned off.
/// Apps that draw their own buttons (Chrome, VS Code, Store apps…) are handled by <see cref="CaptionCoverWindow"/>.
/// </summary>
public sealed class CaptionButtonHider : IDisposable
{
    private const long WS_CAPTION = 0x00C00000L, WS_SYSMENU = 0x00080000L;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private readonly HashSet<IntPtr> _changed = new();

    public CaptionButtonHider()
    {
        WindowTracker.Instance.AppsChanged += Apply;
        WindowTracker.Instance.ForegroundChanged += Apply;
        Apply();
    }

    public void Apply()
    {
        if (!SettingsStore.Current.HideWindowsCaptionButtons)
        {
            RestoreAll();
            return;
        }
        foreach (var app in WindowTracker.Instance.Apps)
            foreach (var w in app.Windows)
                Strip(w.Hwnd);
        var fg = WindowTracker.Instance.ForegroundWindow;
        if (fg != IntPtr.Zero) Strip(fg);
        _changed.RemoveWhere(h => !Win32.IsWindow(h));
    }

    private void Strip(IntPtr hwnd)
    {
        if (_changed.Contains(hwnd) || !Win32.IsWindow(hwnd)) return;
        // Store-app frames manage their own caption; leave them alone.
        if (Win32.GetClass(hwnd) == "ApplicationFrameWindow") return;
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        if ((style & WS_CAPTION) != WS_CAPTION || (style & WS_SYSMENU) == 0) return;

        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));
        if ((Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64() & WS_SYSMENU) != 0) return; // not allowed (elevated app)
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | SWP_FRAMECHANGED);
        _changed.Add(hwnd);
    }

    public void RestoreAll()
    {
        foreach (var hwnd in _changed)
        {
            if (!Win32.IsWindow(hwnd)) continue;
            long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style | WS_SYSMENU));
            Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        _changed.Clear();
    }

    public void Dispose()
    {
        WindowTracker.Instance.AppsChanged -= Apply;
        WindowTracker.Instance.ForegroundChanged -= Apply;
        RestoreAll();
    }
}
