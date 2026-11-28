using System.Diagnostics;
using PearUI.Core;
using PearUI.Native;
using Forms = System.Windows.Forms;

namespace PearUI.Shell;

public enum ArgKind { None, Target, Command, App }

/// <summary>Everything a shortcut can do. Each action has an id (saved in the settings), a title and a group for the picker.</summary>
public sealed class ShortcutAction
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string Group { get; init; }
    public ArgKind Arg { get; init; }
    public Action<string> Run { get; init; }
}

public static class ShortcutActions
{
    private const string GroupPearUI = "PearUI", GroupWindows = "Windows", GroupSystem = "System", GroupLaunch = "Launch";

    private static ShortcutAction A(string id, string group, string title, Action run, ArgKind arg = ArgKind.None) =>
        new() { Id = id, Group = group, Title = title, Arg = arg, Run = _ => run() };

    private static ShortcutAction A(string id, string group, string title, ArgKind arg, Action<string> run) =>
        new() { Id = id, Group = group, Title = title, Arg = arg, Run = run };

    private static void Key(params byte[] keys) => Win32.PressKeys(keys);

    public static readonly IReadOnlyList<ShortcutAction> All = new List<ShortcutAction>
    {
        // PearUI
        A("search", GroupPearUI, "Open search", () => App.Instance.ToggleLauncher()),
        A("explorer", GroupLaunch, "Open File Explorer", () => RunOrRaise("explorer.exe")),
        A("clipboard", GroupPearUI, "Open clipboard history", () => App.Instance.ToggleClipboardHistory()),
        A("allapps", GroupPearUI, "Open All Apps", () => App.Instance.ToggleAllApps()),
        A("terminal", GroupPearUI, "Show or hide Terminal", () => Terminal.TerminalWindow.Toggle()),
        A("terminal-tab", GroupPearUI, "New Terminal tab", () => Core.Launch.Terminal(true)),
        A("controlcenter", GroupPearUI, "Toggle Control Center", () => App.Instance.ToggleControlCenter(Forms.Screen.FromPoint(Forms.Cursor.Position))),
        A("missioncontrol", GroupPearUI, "Mission Control (all windows)", Features.MissionControl),
        A("stagemanager", GroupPearUI, "Toggle Stage Manager", ToggleStageManager),
        A("settings", GroupPearUI, "Open PearUI Settings", () => App.Instance.ShowSettings()),
        A("darkmode", GroupPearUI, "Toggle Dark Mode", () => SystemStatus.SetSystemDark(!SystemStatus.IsSystemDark())),

        // Windows (the window in front)
        A("win-left", GroupWindows, "Tile window to the left half", () => WindowActions.Place(0, 0, 0.5, 1)),
        A("win-right", GroupWindows, "Tile window to the right half", () => WindowActions.Place(0.5, 0, 0.5, 1)),
        A("win-top", GroupWindows, "Tile window to the top half", () => WindowActions.Place(0, 0, 1, 0.5)),
        A("win-bottom", GroupWindows, "Tile window to the bottom half", () => WindowActions.Place(0, 0.5, 1, 0.5)),
        A("win-topleft", GroupWindows, "Tile window to the top-left corner", () => WindowActions.Place(0, 0, 0.5, 0.5)),
        A("win-topright", GroupWindows, "Tile window to the top-right corner", () => WindowActions.Place(0.5, 0, 0.5, 0.5)),
        A("win-bottomleft", GroupWindows, "Tile window to the bottom-left corner", () => WindowActions.Place(0, 0.5, 0.5, 0.5)),
        A("win-bottomright", GroupWindows, "Tile window to the bottom-right corner", () => WindowActions.Place(0.5, 0.5, 0.5, 0.5)),
        A("win-center", GroupWindows, "Center window", WindowActions.Center),
        A("win-max", GroupWindows, "Maximize or restore window", WindowActions.ToggleMaximize),
        A("win-min", GroupWindows, "Minimize window", WindowActions.Minimize),
        A("win-close", GroupWindows, "Close window", WindowActions.Close),
        A("win-display", GroupWindows, "Move window to the next display", WindowActions.NextDisplay),
        A("desktop", GroupWindows, "Show the desktop", () => Key(Win32.VK_LWIN, (byte)'D')),

        // System
        A("lock", GroupSystem, "Lock the screen", Core.Launch.Lock),
        A("sleep", GroupSystem, "Sleep", Core.Launch.Sleep),
        A("screenshot", GroupSystem, "Take a screenshot (snip)", () => Key(Win32.VK_LWIN, 0x10 /*Shift*/, (byte)'S')),
        A("taskmanager", GroupSystem, "Open Task Manager", () => Core.Launch.Open("taskmgr.exe")),
        A("volume-up", GroupSystem, "Volume up", () => Key(0xAF)),
        A("volume-down", GroupSystem, "Volume down", () => Key(0xAE)),
        A("volume-mute", GroupSystem, "Mute", () => Key(0xAD)),
        A("media-play", GroupSystem, "Play or pause", () => Key(0xB3)),
        A("media-next", GroupSystem, "Next track", () => Key(0xB0)),
        A("media-prev", GroupSystem, "Previous track", () => Key(0xB1)),
        A("brightness-up", GroupSystem, "Brightness up", () => ChangeBrightness(+10)),
        A("brightness-down", GroupSystem, "Brightness down", () => ChangeBrightness(-10)),

        // Launch
        A("open", GroupLaunch, "Open an app, file, folder or web address", ArgKind.Target, arg => Core.Launch.Open(arg)),
        A("run", GroupLaunch, "Run a command (no window)", ArgKind.Command, RunCommand),
        A("raise", GroupLaunch, "Switch to an app, or start it if it isn't running", ArgKind.App, RunOrRaise),
    }.Concat(SlotActions()).Concat(DesktopActions()).Concat(MoveToDesktopActions()).ToList();

    /// <summary>"Switch to window 1" … "Switch to window 10": the actions behind Win+1 … Win+0.</summary>
    private static IEnumerable<ShortcutAction> SlotActions() =>
        Enumerable.Range(1, WindowSlots.Count).Select(i => A("slot-" + i, GroupWindows, "Switch to window " + i, () => WindowSlots.Focus(i)));

    /// <summary>"Switch to desktop 1" … "Switch to desktop 10": the actions behind Win+Shift+1 … Win+Shift+0.</summary>
    private static IEnumerable<ShortcutAction> DesktopActions() =>
        Enumerable.Range(1, VirtualDesktops.MaxDesktops).Select(i => A("desktop-" + i, GroupWindows, "Switch to desktop " + i, () => VirtualDesktops.GoTo(i)));

    /// <summary>"Move window to desktop 1" … "Move window to desktop 10": the actions behind Win+Shift+1 … Win+Shift+0.</summary>
    private static IEnumerable<ShortcutAction> MoveToDesktopActions() =>
        Enumerable.Range(1, VirtualDesktops.MaxDesktops).Select(i => A("movedesktop-" + i, GroupWindows, "Move window to desktop " + i, () =>
        {
            IntPtr hwnd = WindowActions.Target();
            if (hwnd == IntPtr.Zero) return;
            if (!VirtualDesktops.MoveWindowTo(hwnd, i))
                ShortcutToast.Show("Win+Shift+" + (i % 10), "Moving windows between desktops needs Windows 11 24H2 or newer");
        }));

    public static ShortcutAction Find(string id) => All.FirstOrDefault(a => a.Id == id);

    public static string ArgLabel(ArgKind k) => k switch
    {
        ArgKind.Target => "App, file, folder or web address",
        ArgKind.Command => "Command to run",
        ArgKind.App => "App to switch to (path or name, e.g. brave.exe)",
        _ => "",
    };

    /// <summary>Runs a binding on the UI thread. Errors are swallowed: a shortcut must never crash the shell.</summary>
    public static void Run(ShortcutBinding b)
    {
        var action = Find(b.Action);
        if (action == null) return;

        // Window actions need a window to act on. PearUI's own windows (such as Settings) never count.
        // ("Show the desktop" and "Switch to window N" do not act on the window in front, so they always run.)
        if (action.Group == GroupWindows && action.Id != "desktop" && !action.Id.StartsWith("slot-") && !action.Id.StartsWith("desktop-") && WindowActions.Target() == IntPtr.Zero)
        {
            ShortcutToast.Show(b.Keys, "No window to act on (click the window first)");
            return;
        }

        // Changing or moving to a desktop is obvious on screen (Windows shows nothing for it either): no popup.
        bool silent = action.Id.StartsWith("desktop-") || action.Id.StartsWith("movedesktop-");
        if (!silent) ShortcutToast.Show(b.Keys, action.Arg == ArgKind.None || string.IsNullOrWhiteSpace(b.Arg) ? action.Title : action.Title + ": " + b.Arg);
        try { action.Run(b.Arg ?? ""); } catch { }
    }

    // ---------- actions that need a little code ----------
    private static void ToggleStageManager()
    {
        bool on = !StageManager.StageManager.Instance.Enabled;
        StageManager.StageManager.Instance.SetEnabled(on);
        SettingsStore.Current.StageManager = on;
        SettingsStore.Save(raiseEvent: false);
    }

    private static void ChangeBrightness(int delta)
    {
        int? cur = SystemStatus.GetBrightness();
        if (cur != null) SystemStatus.SetBrightness(Math.Clamp(cur.Value + delta, 0, 100));
    }

    private static void RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command) { CreateNoWindow = true, UseShellExecute = false });
    }

    /// <summary>"Run or raise" from tiling window managers: jump to the app if it is open, otherwise start it.</summary>
    private static void RunOrRaise(string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return;
        string wanted = System.IO.Path.GetFileName(app.Trim().Trim('"'));
        var running = WindowTracker.Instance.Apps.FirstOrDefault(a =>
            (a.ExePath != null && string.Equals(System.IO.Path.GetFileName(a.ExePath), wanted, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase));
        if (running != null && running.Windows.Count > 0)
        {
            foreach (var w in running.Windows.Where(w => w.Minimized)) Win32.ShowWindowAsync(w.Hwnd, Win32.SW_RESTORE);
            WindowTracker.ActivateApp(running);
        }
        else Core.Launch.Open(app);
    }
}
