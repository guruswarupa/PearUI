using MacUI.Core;
using MacUI.Native;

namespace MacUI.Shell;

/// <summary>Named desktop actions shared by Hot Corners, menus and the Control Center.</summary>
public static class Features
{
    public static readonly string[] HotCornerActions =
    {
        "None", "Mission Control", "Desktop", "Notification Center", "All Apps", "Search",
        "Stage Manager", "Start Menu", "Lock Screen", "Put Display to Sleep",
    };

    /// <summary>Overview of all open windows (Windows Task View).</summary>
    public static void MissionControl() => Win32.PressKeys(Win32.VK_LWIN, 0x09 /*Tab*/);

    public static void ShowDesktop() => Win32.PressKeys(Win32.VK_LWIN, (byte)'D');

    public static void NotificationCenter() => Win32.PressKeys(Win32.VK_LWIN, (byte)'N');

    public static void StartMenu() => Win32.PressKeys(Win32.VK_LWIN);

    public static void DisplaySleep()
    {
        // SC_MONITORPOWER, 2 = off
        Win32.PostMessage(new IntPtr(0xFFFF), Win32.WM_SYSCOMMAND, (IntPtr)0xF170, (IntPtr)2);
    }

    public static void Run(string action)
    {
        switch (action)
        {
            case "Mission Control": MissionControl(); break;
            case "Desktop": ShowDesktop(); break;
            case "Notification Center": NotificationCenter(); break;
            case "All Apps": App.Instance.ToggleAllApps(); break;
            case "Search": App.Instance.ToggleLauncher(); break;
            case "Stage Manager": StageManager.StageManager.Instance.Toggle(); break;
            case "Start Menu": StartMenu(); break;
            case "Lock Screen": Launch.Lock(); break;
            case "Put Display to Sleep": DisplaySleep(); break;
        }
    }
}
