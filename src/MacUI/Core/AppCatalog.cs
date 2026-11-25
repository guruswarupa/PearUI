using System.IO;
using System.Runtime.InteropServices;

namespace MacUI.Core;

public sealed class AppEntry
{
    public string Name { get; init; }
    /// <summary>AppsFolder parsing name: an AUMID for Store apps, or a (known-folder-relative) path for desktop apps.</summary>
    public string Aumid { get; init; }
    public string Target => @"shell:AppsFolder\" + Aumid;
    public string ExePath { get; init; }
    /// <summary>Explicit icon file for link-style apps (e.g. Steam games, "steam://rungameid/…").</summary>
    public string IconPath { get; init; }
    /// <summary>What to load the icon from.</summary>
    public string IconSource => IconPath ?? Target;
}

/// <summary>
/// Index of every installed app, read from the shell's "Apps" folder (the same list as Start → All apps).
/// That covers classic desktop apps and Store apps with one API.
/// </summary>
public sealed class AppCatalog
{
    public static AppCatalog Instance { get; } = new();

    private volatile List<AppEntry> _apps = new();
    private Task _loading;

    public IReadOnlyList<AppEntry> Apps => _apps;
    public event Action Loaded;

    private static readonly string[] SkipExtensions = { ".url", ".chm", ".txt", ".html", ".htm", ".pdf", ".rtf", ".msc" };

    public Task LoadAsync()
    {
        if (_loading != null && !_loading.IsCompleted) return _loading;
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try { _apps = Enumerate(); }
            catch { }
            finally { tcs.TrySetResult(); }
        }) { IsBackground = true, Name = "MacUI.AppCatalog" };
        t.SetApartmentState(ApartmentState.STA); // Shell COM objects require STA
        t.Start();
        _loading = tcs.Task.ContinueWith(_ => Loaded?.Invoke(), TaskScheduler.Default);
        return _loading;
    }

    private static List<AppEntry> Enumerate()
    {
        var list = new List<AppEntry>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return list;
        dynamic shell = Activator.CreateInstance(shellType);
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return list;
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                if (item == null) continue;
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                if (HelperName.IsMatch(name)) continue;
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(path);
                if (!path.Contains('!') && SkipExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                // Which program does it really start? Shortcuts with arguments get an auto-generated id,
                // but the shell still knows their target.
                string exe = ResolveAppsFolderPath(path);
                if (exe == null && !path.Contains('!'))
                {
                    try
                    {
                        string target = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                        if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = target;
                    }
                    catch { }
                }
                list.Add(new AppEntry { Name = name, Aumid = path, ExePath = exe });
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }

        // Link-style apps (steam://, com.epicgames.launcher://, …): the shell can't render their icons,
        // but their Start Menu .url shortcuts name an icon file.
        var urlIcons = UrlShortcutIcons();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.Aumid.Contains("://") && urlIcons.TryGetValue(a.Aumid, out var ico))
                list[i] = new AppEntry { Name = a.Name, Aumid = a.Aumid, ExePath = a.ExePath, IconPath = ico };
        }

        var unique = list
            .GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First());

        // Several shortcuts for the same program (VLC, "VLC skinned", "Reset VLC…"; PowerShell and PowerShell (x86))
        // collapse into the main one. Programs that host many different tools are never merged.
        var merged = unique
            .GroupBy(a => a.ExePath != null && !SharedHosts.Contains(Path.GetFileName(a.ExePath))
                ? "exe:" + Path.GetFileName(a.ExePath).ToLowerInvariant()
                : "id:" + a.Aumid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(MainScore).ThenBy(a => a.Name.Length).First());

        return merged.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Lower is "more main": a plain shortcut (no arguments) to the 64-bit, unskinned program.</summary>
    private static int MainScore(AppEntry a)
    {
        int score = 0;
        if (a.Aumid.StartsWith("Microsoft.AutoGenerated", StringComparison.OrdinalIgnoreCase)) score += 4; // has arguments
        if (Variant.IsMatch(a.Name)) score += 2;
        return score;
    }

    /// <summary>Helper shortcuts that aren't apps.</summary>
    private static readonly System.Text.RegularExpressions.Regex HelperName = new(
        @"^(uninstall|reset)\b|\buninstall\b|release notes|documentation|\bmanuals?\b|\bwebsite\b|\bfaqs?\b|read ?me|\blicen[cs]e\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Name hints of a secondary variant.</summary>
    private static readonly System.Text.RegularExpressions.Regex Variant = new(
        @"\(x86\)|32-bit|\bskinned\b|\bsafe mode\b|\bdebug\b|\bprofiler\b|\(admin\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Programs that launch many unrelated tools or web apps: never merge by these.</summary>
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mmc.exe", "rundll32.exe", "explorer.exe", "control.exe", "cmd.exe",
        "wscript.exe", "cscript.exe", "chrome_proxy.exe", "msedge_proxy.exe", "msedge.exe", "chrome.exe",
        "brave.exe", "firefox.exe", "java.exe", "javaw.exe", "dotnet.exe", "wsl.exe", "conhost.exe",
    };

    /// <summary>URL → icon file, from the .url shortcuts in both Start Menu folders.</summary>
    private static Dictionary<string, string> UrlShortcutIcons()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.url", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    string url = null, icon = null;
                    foreach (var line in File.ReadLines(f))
                    {
                        if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) url = line[4..].Trim();
                        else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) icon = line[9..].Trim();
                    }
                    if (url != null && icon != null && File.Exists(icon)) map[url] = icon;
                }
                catch { }
            }
        }
        return map;
    }

    /// <summary>The icon to show for a launch target (handles link-style apps).</summary>
    public string IconFor(string target)
    {
        if (target == null) return null;
        var entry = _apps.FirstOrDefault(a => a.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
        return entry?.IconSource ?? target;
    }

    public AppEntry FindByAumid(string aumid) =>
        _apps.FirstOrDefault(a => a.Aumid.Equals(aumid, StringComparison.OrdinalIgnoreCase));

    public AppEntry FindByExe(string exe) =>
        _apps.FirstOrDefault(a => a.ExePath != null && a.ExePath.Equals(exe, StringComparison.OrdinalIgnoreCase));

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Turns "{KnownFolderGuid}\sub\app.exe" or "C:\...\app.exe" into an absolute exe path (or null).</summary>
    public static string ResolveAppsFolderPath(string parsingName)
    {
        if (string.IsNullOrEmpty(parsingName) || parsingName.Contains('!')) return null;
        try
        {
            if (parsingName.StartsWith("{") && parsingName.Length > 38 && parsingName[37] == '}')
            {
                if (!Guid.TryParse(parsingName[1..37], out var g)) return null;
                if (SHGetKnownFolderPath(g, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
                string root = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                string full = Path.Combine(root, parsingName[39..]);
                return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            if (Path.IsPathRooted(parsingName) && parsingName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(parsingName);
        }
        catch { }
        return null;
    }
}
