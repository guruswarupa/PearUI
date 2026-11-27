using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using PearUI.Native;

namespace PearUI.Core;

/// <summary>
/// A program running in the background (closed to the tray, or a tray-only helper): no window on screen.
/// <see cref="Target"/> is how to open it again; it is null for programs that are not in the Start menu list.
/// </summary>
public sealed record BackgroundApp(string Name, string Target, string IconSource, string ExePath, string Family);

/// <summary>Finds programs that are running without a window, and stops them when the user asks.</summary>
public static class BackgroundAppManager
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows' own tray helpers that live in the Windows folder but that the user would expect in the list.</summary>
    private static readonly HashSet<string> SystemAllow = new(StringComparer.OrdinalIgnoreCase) { "SecurityHealthSystray.exe" };

    private static readonly HashSet<string> NeverList = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dotnet.exe", "PearUI.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe",
        "OpenConsole.exe", "git.exe", "bash.exe", "node.exe", "python.exe", "pythonw.exe", "claude.exe",
    };

    /// <summary>
    /// First, installed apps (the Start menu list) that are running with no window, which can be opened again. Then every
    /// other program of the current user that runs without a window, such as the Windows Security tray icon. Call off the UI thread.
    /// </summary>
    public static List<BackgroundApp> Find(WindowTracker.ProcessSnapshot snap, IReadOnlyList<RunningApp> running)
    {
        var result = FindInstalled(snap, running);
        try { result.AddRange(FindOther(result, running)); } catch { }
        return result;
    }

    private static List<BackgroundApp> FindInstalled(WindowTracker.ProcessSnapshot snap, IReadOnlyList<RunningApp> running)
    {
        var result = new List<BackgroundApp>();
        if (snap == null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string own = Environment.ProcessPath;

        foreach (var a in AppCatalog.Instance.Apps)
        {
            try
            {
                if (a.Aumid.Contains("://")) continue;
                string exe = a.ExePath ?? AppCatalog.ResolveAppsFolderPath(a.Aumid);
                if (exe != null)
                {
                    string file = Path.GetFileName(exe);
                    if (exe.Equals(own, StringComparison.OrdinalIgnoreCase) || NeverList.Contains(file)) continue;
                }

                int bang = a.Aumid.IndexOf('!');
                string family = bang > 0 ? a.Aumid[..bang] : null;
                bool alive = (exe != null && snap.Paths.Contains(exe)) || (family != null && snap.Families.Contains(family));
                if (!alive) continue;

                var probe = new PinnedApp { Name = a.Name, Target = a.Target, ExePath = exe };
                if (running.Any(r => WindowTracker.Matches(probe, r))) continue;       // it has a window: it is a normal running app

                if (!seen.Add(exe ?? family ?? a.Aumid)) continue;
                result.Add(new BackgroundApp(a.Name, a.Target, a.IconSource, exe, family));
            }
            catch { }
        }
        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<BackgroundApp> FindOther(List<BackgroundApp> listed, IReadOnlyList<RunningApp> running)
    {
        var found = new Dictionary<string, BackgroundApp>(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        string own = Environment.ProcessPath;

        // Programs that have a window own everything in their folder (their helpers, updaters, crash handlers).
        var windowedExes = running.Select(r => r.ExePath).Where(p => p != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // (The Windows folder is shared by many unrelated programs, so a window there does not claim its neighbours.)
        var windowedDirs = windowedExes.Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d) && !(d + "\\").StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windowedFamilies = windowedExes.Select(WindowTracker.PackageFamilyFromPath).Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listedExes = listed.Select(l => l.ExePath).Where(p => p != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listedFamilies = listed.Select(l => l.Family).Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                string path = Win32.GetProcessPath((uint)p.Id);
                if (path == null || found.ContainsKey(path)) continue;
                string file = Path.GetFileName(path);

                if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && !SystemAllow.Contains(file)) continue;   // Windows itself
                if (path.Equals(own, StringComparison.OrdinalIgnoreCase) || NeverList.Contains(file)) continue;
                if (Regex.IsMatch(file, @"crash|helper|broker|webview|setup|install", RegexOptions.IgnoreCase)) continue;
                if (path.Contains(@"\Git\usr\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Git\mingw", StringComparison.OrdinalIgnoreCase)) continue; // Git's command-line tools
                if (windowedExes.Contains(path) || listedExes.Contains(path)) continue;

                string dir = Path.GetDirectoryName(path);
                if (windowedDirs.Any(d => dir.Equals(d, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)
                                          || d.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) && dir.Length > 12)) continue;

                string family = WindowTracker.PackageFamilyFromPath(path);
                if (family != null && (windowedFamilies.Contains(family) || listedFamilies.Contains(family))) continue;

                found[path] = new BackgroundApp(Describe(path), null, path, path, family);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The name the program calls itself ("Windows Security notification icon"), or its file name.</summary>
    private static string Describe(string path)
    {
        lock (Descriptions)
            if (Descriptions.TryGetValue(path, out var cached)) return cached;
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(v.FileDescription)) name = v.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(v.ProductName)) name = v.ProductName.Trim();
        }
        catch { }
        lock (Descriptions) Descriptions[path] = name;
        return name;
    }

    /// <summary>
    /// What clicking the program's tray icon would do: bring its window back. Windows Security opens the Windows Security app;
    /// an app from the Start menu opens through its Start menu entry; anything else is started again (most tray programs
    /// are single-instance and show their window when started a second time).
    /// </summary>
    public static void Open(BackgroundApp app)
    {
        try
        {
            if (app.Target != null) { Launch.Open(app.Target); return; }
            string exe = app.ExePath;
            if (exe == null) return;

            if (Path.GetFileName(exe).Equals("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase)) { Launch.Open("windowsdefender:"); return; }

            // The same program's Start menu entry (e.g. the tray helper of an app installed in the same folder).
            string dir = Path.GetDirectoryName(exe);
            var sibling = AppCatalog.Instance.Apps.FirstOrDefault(a =>
            {
                string e = a.ExePath ?? AppCatalog.ResolveAppsFolderPath(a.Aumid);
                return e != null && dir != null && string.Equals(Path.GetDirectoryName(e), dir, StringComparison.OrdinalIgnoreCase);
            });
            if (sibling != null) { Launch.Open(sibling.Target); return; }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch { }
    }

    /// <summary>Stops every process of the program (and the processes it started). Returns how many were stopped.</summary>
    public static int Quit(BackgroundApp app)
    {
        int stopped = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                string path = Win32.GetProcessPath((uint)p.Id);
                if (path == null) continue;
                bool match = (app.ExePath != null && path.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase))
                          || (app.Family != null && string.Equals(WindowTracker.PackageFamilyFromPath(path), app.Family, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
                p.Kill(entireProcessTree: true);
                stopped++;
            }
            catch { /* already gone, or protected: nothing more to do */ }
            finally { p.Dispose(); }
        }
        return stopped;
    }
}
