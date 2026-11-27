using PearUI.Native;

namespace PearUI.Core;

/// <summary>
/// Alt+1 … Alt+9 and Alt+0 (10) jump to the Nth window of the current virtual desktop.
///
/// The numbering is simply the order you see in the Dock, left to right: pinned apps first (in their Dock order), then the other
/// running apps by name, and the windows of one app one after the other. Only windows on the current desktop count, so the numbers
/// follow the Dock as you change desktops.
/// </summary>
public static class WindowSlots
{
    public const int Count = 10;

    /// <summary>The apps in Dock order: pinned apps as pinned, then the rest by name.</summary>
    private static IEnumerable<RunningApp> DockOrder(IReadOnlyList<RunningApp> apps)
    {
        var used = new HashSet<RunningApp>();
        foreach (var pin in SettingsStore.Current.PinnedApps ?? new List<PinnedApp>())
        {
            var run = apps.FirstOrDefault(a => !used.Contains(a) && WindowTracker.Matches(pin, a));
            if (run == null) continue;
            used.Add(run);
            yield return run;
        }
        foreach (var a in apps.Where(a => !used.Contains(a)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)) yield return a;
    }

    /// <summary>The windows of the current desktop in Dock order (window 1 first).</summary>
    public static List<IntPtr> Windows() =>
        DockOrder(WindowTracker.Instance.Apps)
            .SelectMany(app => app.Windows.OrderBy(w => w.Hwnd.ToInt64()).Select(w => w.Hwnd))   // by handle: the order does not change as you switch
            .ToList();

    /// <summary>Brings window number <paramref name="slot"/> to the front (restoring it if it is minimized).</summary>
    public static void Focus(int slot)
    {
        var windows = Windows();
        if (slot >= 1 && slot <= windows.Count && Win32.IsWindow(windows[slot - 1]))
        {
            IntPtr hwnd = windows[slot - 1];
            if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            Win32.ActivateWindow(hwnd);
        }
        // A number with no window does nothing.
    }
}
