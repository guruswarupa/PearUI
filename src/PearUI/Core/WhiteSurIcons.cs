using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace PearUI.Core;

/// <summary>
/// Big Sur style icons from the open-source WhiteSur icon theme (github.com/vinceliuice/WhiteSur-icon-theme, GPL-3.0).
/// Needs no account or key. Icons are downloaded only for apps that have a match, drawn to pictures and kept on this PC.
/// </summary>
internal static class WhiteSurIcons
{
    public const string RepoUrl = "https://github.com/vinceliuice/WhiteSur-icon-theme";
    private const string IndexApi = "https://api.github.com/repos/vinceliuice/WhiteSur-icon-theme/git/trees/master:src/apps/scalable";
    private const string RawBase = "https://raw.githubusercontent.com/vinceliuice/WhiteSur-icon-theme/master/src/apps/scalable/";
    private static readonly string Folder = Path.Combine(SettingsStore.Folder, "whitesur");
    private static readonly HttpClient Http = CreateClient();
    private static int _running;
    private static readonly object Gate = new();
    private static Dictionary<string, string> _map;   // app icon source -> picture on disk

    // Windows names that WhiteSur knows under a Linux name.
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["fileexplorer"] = "file-manager", ["explorer"] = "file-manager",
        ["settings"] = "preferences-system", ["taskmanager"] = "utilities-system-monitor",
        ["camera"] = "accessories-camera", ["charactermap"] = "accessories-character-map", ["charmap"] = "accessories-character-map",
        ["notepad"] = "text-editor", ["windowsterminal"] = "terminal", ["commandprompt"] = "terminal", ["windowspowershell"] = "terminal",
        ["powershell"] = "terminal", ["wt"] = "terminal", ["cmd"] = "terminal", ["regedit"] = "regedit", ["registryeditor"] = "regedit",
        ["microsoftedge"] = "microsoft-edge", ["msedge"] = "microsoft-edge", ["code"] = "visual-studio-code",
        ["calculator"] = "calc", ["snippingtool"] = "accessories-screenshot", ["photos"] = "shotwell", ["diskcleanup"] = "accessories-system-cleaner",
        ["defragmentandoptimizedrives"] = "gnome-disks", ["onscreenkeyboard"] = "onboard", ["remotedesktopconnection"] = "remmina",
        ["performancemonitor"] = "utilities-system-monitor", ["resourcemonitor"] = "utilities-system-monitor", ["controlpanel"] = "preferences-system",
        ["gitbash"] = "git", ["gitcmd"] = "git", ["gitgui"] = "git", ["gitfaqsfrequentlyaskedquestions"] = "git", ["gitreleasenotes"] = "git",
        ["documentation"] = "help", ["getstarted"] = "help", ["systeminformation"] = "utilities-system-monitor",
        ["word"] = "ms-word", ["excel"] = "ms-excel", ["powerpoint"] = "ms-powerpoint", ["outlook"] = "ms-outlook", ["onenote"] = "ms-onenote",
    };
    private static readonly HashSet<string> Generic = new() { "windows", "microsoft", "google", "visual", "open", "new", "system", "android", "git", "power", "task", "file" };

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PearUI-Icons/1");
        return c;
    }

    /// <summary>WhiteSur pictures for the Dock's own items: source -> path inside the theme (places and apps folders).</summary>
    private static Dictionary<string, string> Specials() => new(StringComparer.OrdinalIgnoreCase)
    {
        [IconStyles.BinSource] = "places/scalable/user-trash.svg",
        [IconStyles.BinFullKey] = "places/scalable/user-trash-full.svg",
        [IconStyles.DownloadsSource] = "places/scalable/folder-download.svg",
        [IconStyles.AllAppsKey] = "apps/scalable/view-app-grid.svg",
    };
    private const string ThemeBase = "https://raw.githubusercontent.com/vinceliuice/WhiteSur-icon-theme/master/src/";

    private static async Task<bool> EnsureSpecialAsync(string source, string path, CancellationToken ct)
    {
        try
        {
            if (FileFor(source) != null) return true;
            Directory.CreateDirectory(Folder);
            string png = Path.Combine(Folder, Path.GetFileNameWithoutExtension(path) + ".png");
            if (!File.Exists(png))
            {
                byte[] svg = await Http.GetByteArrayAsync(ThemeBase + path, ct);
                string made = await Application.Current.Dispatcher.InvokeAsync(() => RenderPng(svg, png)).Task;
                if (made == null) return false;
            }
            lock (Gate) Map()[IconStyles.Key(source)] = png;
            SaveMap();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s ?? "") if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string MapFile => Path.Combine(Folder, "map.json");

    private static Dictionary<string, string> Map()
    {
        lock (Gate)
        {
            if (_map != null) return _map;
            try { _map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MapFile)) ?? new(); }
            catch { _map = new(); }
            return _map;
        }
    }

    private static void SaveMap()
    {
        lock (Gate)
        {
            try { Directory.CreateDirectory(Folder); File.WriteAllText(MapFile, JsonSerializer.Serialize(Map())); } catch { }
        }
    }

    /// <summary>The WhiteSur picture for an app's icon source when one was fetched, otherwise null (never blocks).</summary>
    public static string FileFor(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        lock (Gate) return IconStyles.TryGet(Map(), source, out var f) && File.Exists(f) ? f : null;
    }

    /// <summary>Moves entries saved under an older kind of key to the current one.</summary>
    public static void Rekey()
    {
        lock (Gate)
        {
            var m = Map();
            var n = new Dictionary<string, string>();
            bool changed = false;
            foreach (var kv in m)
            {
                string k = kv.Key.StartsWith("special:") ? kv.Key : IconStyles.Key(kv.Key);
                if (k != kv.Key) changed = true;
                n[k] = kv.Value;
            }
            if (!changed) return;
            _map = n;
        }
        SaveMap();
    }

    public static void Forget()
    {
        lock (Gate) { _map = new(); }
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
    }

    private static async Task<Dictionary<string, string>> LoadIndexAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        string file = Path.Combine(Folder, "index.txt");
        string[] names;
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(14)) names = File.ReadAllLines(file);
        else
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(IndexApi, ct));
            names = doc.RootElement.GetProperty("tree").EnumerateArray()
                .Select(e => e.GetProperty("path").GetString() ?? "")
                .Where(p => p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToArray();
            await File.WriteAllLinesAsync(file, names, ct);
        }
        var map = new Dictionary<string, string>();
        foreach (var n in names)
        {
            string stem = n[..^4];
            if (stem.Contains("symbolic", StringComparison.OrdinalIgnoreCase) || stem.Contains('.')) continue;   // reverse-DNS ids never match a Windows name
            map.TryAdd(Norm(stem), n);
        }
        return map;
    }

    private static string Match(Dictionary<string, string> index, string name, string exePath)
    {
        var tries = new List<string>();
        void Add(string s) { string n = Norm(s); if (n.Length >= 3) tries.Add(n); }
        Add(name);
        if (!string.IsNullOrEmpty(exePath)) Add(Path.GetFileNameWithoutExtension(exePath));
        foreach (var t in tries.ToList())
            foreach (var pre in new[] { "microsoft", "google", "adobe", "apple" })
                if (t.StartsWith(pre) && t.Length > pre.Length + 2) tries.Add(t[pre.Length..]);
        foreach (var t in tries.ToList()) { string bare = t.TrimEnd('0','1','2','3','4','5','6','7','8','9'); if (bare.Length >= 3 && bare != t) tries.Add(bare); }
        foreach (var t in tries) if (index.TryGetValue(t, out var f)) return f;
        foreach (var t in tries) if (Aliases.TryGetValue(t, out var stem) && index.TryGetValue(Norm(stem), out var g)) return g;

        // "Brave Browser" -> brave: try the name without filler words, then its first word (when it is a distinctive one).
        string[] filler = { "browser", "app", "desktop", "client", "for", "windows", "the", "launcher", "studio" };
        var words = System.Text.RegularExpressions.Regex.Split(name ?? "", @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToList();
        var kept = words.Where(w => !filler.Contains(w.ToLowerInvariant())).ToList();
        if (kept.Count > 0 && kept.Count < words.Count && index.TryGetValue(Norm(string.Concat(kept)), out var h)) return h;
        if (words.Count > 1 && words[0].Length >= 4 && !Generic.Contains(words[0].ToLowerInvariant()) && index.TryGetValue(Norm(words[0]), out var first)) return first;
        return null;
    }

    private static string RenderPng(byte[] svg, string pngPath)
    {
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true, OptimizePath = true };
        using var reader = new FileSvgReader(settings);
        var drawing = reader.Read(new MemoryStream(svg));
        if (drawing == null) return null;
        var b = drawing.Bounds;
        if (b.Width < 1 || b.Height < 1) return null;
        const int S = 256;
        double scale = S / Math.Max(b.Width, b.Height);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform((S - b.Width * scale) / 2 - b.X * scale, (S - b.Height * scale) / 2 - b.Y * scale));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(drawing);
        }
        var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(pngPath)) enc.Save(fs);
        return pngPath;
    }

    /// <summary>Matches, downloads and draws one app's icon. Null when WhiteSur has none for it.</summary>
    private static async Task<string> FetchAsync(Dictionary<string, string> index, string name, string exe, CancellationToken ct)
    {
        string svgName = Match(index, name, exe);
        if (svgName == null) return null;
        string png = Path.Combine(Folder, Path.GetFileNameWithoutExtension(svgName) + ".png");
        if (File.Exists(png)) return png;
        byte[] svg = await Http.GetByteArrayAsync(RawBase + Uri.EscapeDataString(svgName), ct);
        return await Application.Current.Dispatcher.InvokeAsync(() => RenderPng(svg, png)).Task;
    }

    /// <summary>For one app (from its right-click menu). True when WhiteSur has an icon for it.</summary>
    public static async Task<bool> EnsureOneAsync(string name, string exe, string source, CancellationToken ct = default)
    {
        if (FileFor(source) != null) return true;
        var specials = Specials();
        if (source != null && specials.TryGetValue(source, out var special))
        {
            bool ok = await EnsureSpecialAsync(source, special, ct);
            if (source == IconStyles.BinSource) ok &= await EnsureSpecialAsync(IconStyles.BinFullKey, specials[IconStyles.BinFullKey], ct);
            return ok;
        }
        try
        {
            var index = await LoadIndexAsync(ct);
            string png = await FetchAsync(index, name, exe, ct);
            if (png == null) return false;
            lock (Gate) Map()[IconStyles.Key(source)] = png;
            SaveMap();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>Finds WhiteSur icons for every app that has one (Dock apps first). Returns how many were found and how many apps were looked at.</summary>
    public static async Task<(int found, int total)> BuildAllAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return (0, 0);
        try
        {
            var s = SettingsStore.Current;
            progress?.Report("Reading the WhiteSur icon list…");
            var index = await LoadIndexAsync(ct);
            var targets = new List<(string name, string exe, string source)>();
            foreach (var p in s.PinnedApps ?? new()) targets.Add((p.Name, p.ExePath, AppCatalog.Instance.IconFor(p.Target)));
            foreach (var a in AppCatalog.Instance.Apps) targets.Add((a.Name, a.ExePath, AppCatalog.Instance.IconFor(a.Target)));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int found = 0, total = 0;
            foreach (var (name, exe, source) in targets)
            {
                if (string.IsNullOrEmpty(source) || !seen.Add(IconStyles.Key(source))) continue;
                total++;
                if (FileFor(source) != null) { found++; continue; }
                try
                {
                    progress?.Report($"Adding {name}… ({found} so far)");
                    string png = await FetchAsync(index, name, exe, ct);
                    if (png == null) continue;
                    lock (Gate) Map()[IconStyles.Key(source)] = png;
                    found++;
                }
                catch (OperationCanceledException) { throw; }
                catch { }   // this one icon failed: the app keeps its own
            }
            foreach (var kv in Specials())
                try { await EnsureSpecialAsync(kv.Key, kv.Value, ct); } catch (OperationCanceledException) { throw; } catch { }
            return (found, total);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
