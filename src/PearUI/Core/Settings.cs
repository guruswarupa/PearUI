using System.IO;
using System.Text.Json;
using PearUI.Native;
using PearUI.Shell;

namespace PearUI.Core;

public class PinnedApp
{
    public string Name { get; set; }
    /// <summary>Path, URI or "shell:AppsFolder\AUMID" used to launch and to get the icon.</summary>
    public string Target { get; set; }
    /// <summary>Optional exe path used to match running windows to this pin.</summary>
    public string ExePath { get; set; }
}

/// <summary>A folder in All Apps (Launchpad-style). Apps are referenced by their AppsFolder id.</summary>
public class AppFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Folder";
    public List<string> Apps { get; set; } = new();
}

public class AppSettings
{
    /// <summary>The welcome tour has been completed or skipped (it opens once, on the first start).</summary>
    public bool OnboardingDone { get; set; }

    /// <summary>All Apps folders; null until the starter folders have been created.</summary>
    public List<AppFolder> AppFolders { get; set; }

    // General
    public bool StartWithWindows { get; set; }
    public bool HideTaskbar { get; set; }
    /// <summary>The Windows key opens All Apps instead of the Start menu.</summary>
    public bool ReplaceStartMenu { get; set; }
    public string Theme { get; set; } = "System";   // System | Light | Dark
    public string Accent { get; set; } = "System";  // System | Blue | Purple | Pink | Red | Orange | Yellow | Green | Graphite
    public bool UseBlur { get; set; } = true;
    /// <summary>Make app windows slightly see-through (never full-screen apps).</summary>
    public bool TranslucentWindows { get; set; } = true;
    /// <summary>How solid translucent windows are, in percent (60–100).</summary>
    public int WindowOpacity { get; set; } = 90;
    /// <summary>How solid the PearUI Terminal window is (30 = very see-through, 100 = solid).</summary>
    public int TerminalOpacity { get; set; } = 60;
    /// <summary>The shell PearUI Terminal starts with ("bash", "zsh", "cmd"…). Empty = Bash if installed, otherwise Command Prompt.</summary>
    public string TerminalShell { get; set; } = "";
    /// <summary>Key changes for PearUI Terminal's own shortcuts (action id → keys). Missing ones use the defaults.</summary>
    public Dictionary<string, string> TerminalKeys { get; set; } = new();
    /// <summary>Look for a newer PearUI in the background (a few times a day) and offer to update.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>The release tag (such as v1.3.0) the user chose to skip.</summary>
    public string SkippedUpdate { get; set; } = "";
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;
    /// <summary>The user's keyboard shortcuts (Settings → Shortcuts).</summary>
    public List<ShortcutBinding> Shortcuts { get; set; } = PearUI.Shell.Shortcuts.Defaults();

    // Menu bar
    public bool MenuBarEnabled { get; set; } = true;
    public bool MenuBarAllDisplays { get; set; } = true;
    public bool ClockShowSeconds { get; set; }
    public bool ClockShowDate { get; set; } = true;
    public bool Clock24Hour { get; set; }
    public bool ShowBatteryPercent { get; set; } = true;

    // Dock
    public bool DockEnabled { get; set; } = true;
    public double DockIconSize { get; set; } = 48;
    public bool DockMagnification { get; set; } = true;
    public double DockMagnifiedSize { get; set; } = 80;
    public bool DockAutoHide { get; set; } = true;
    public bool DockShowRunningApps { get; set; } = true;
    public List<PinnedApp> PinnedApps { get; set; }

    // Launcher
    public bool LauncherEnabled { get; set; } = true;
    public string LauncherHotkey { get; set; } = "Alt+Space";
    public bool LauncherSearchFiles { get; set; } = true;

    // All Apps (Launchpad-style grid)
    public bool DockShowAllApps { get; set; } = true;
    public string AllAppsHotkey { get; set; } = "Alt+F1";

    // Desktop: Stage Manager & Hot Corners
    public bool StageManager { get; set; }
    public string HotCornerTopLeft { get; set; } = "None";
    public string HotCornerTopRight { get; set; } = "None";
    public string HotCornerBottomLeft { get; set; } = "Mission Control";
    public string HotCornerBottomRight { get; set; } = "Desktop";

    // Windows
    public bool WindowControlsEnabled { get; set; }
    /// <summary>"Left" (macOS) or "Right" (in place of the Windows buttons, never covers app content).</summary>
    public string WindowControlsPosition { get; set; } = "Left";
    /// <summary>Hide the standard Windows minimize / maximize / close buttons.</summary>
    public bool HideWindowsCaptionButtons { get; set; }
    /// <summary>Keep app windows out of the menu bar / Dock / Stage Manager area.</summary>
    public bool KeepWindowsClear { get; set; } = true;
}

public static class SettingsStore
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PearUI");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Raised (on the UI thread) after settings were saved.</summary>
    public static event Action Changed;

    public static void Load()
    {
        // One-time migration from the app's previous name.
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacUI", "settings.json");
            if (!File.Exists(FilePath) && File.Exists(old))
            {
                Directory.CreateDirectory(Folder);
                File.Copy(old, FilePath);
            }
        }
        catch { }

        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch { Current = new AppSettings(); }

        if (Current.PinnedApps == null)
        {
            Current.PinnedApps = DefaultPins();
            Save(raiseEvent: false);
        }
    }

    public static void Save(bool raiseEvent = true)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch { }
        if (raiseEvent) Changed?.Invoke();
    }

    private static List<PinnedApp> DefaultPins()
    {
        var list = new List<PinnedApp>();
        void AddPath(string name, string path)
        {
            if (File.Exists(path)) list.Add(new PinnedApp { Name = name, Target = path, ExePath = path });
        }
        void AddAumid(string name, string aumid)
        {
            string t = @"shell:AppsFolder\" + aumid;
            if (ShellIcons.Exists(t)) list.Add(new PinnedApp { Name = name, Target = t });
        }

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        AddPath("File Explorer", Path.Combine(win, "explorer.exe"));
        string edge = Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe");
        if (!File.Exists(edge)) edge = Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe");
        AddPath("Microsoft Edge", edge);
        AddPath("Google Chrome", Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"));
        AddAumid("Mail", "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail");
        AddAumid("Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App");
        AddAumid("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        AddAumid("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        AddAumid("Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
        AddAumid("Microsoft Store", "Microsoft.WindowsStore_8wekyb3d8bbwe!App");
        AddAumid("Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
        return list;
    }
}
