using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PearUI.Core;

/// <summary>One wallpaper in the online collection.</summary>
public sealed record WallpaperItem(string File, string Title, string Group, int Order, string Category, string Repo, string Branch, string Path);

/// <summary>The wallpaper that was set before PearUI changed it, so it can be put back.</summary>
public sealed record WallpaperSnapshot(string Path, string Style, string Tile);

/// <summary>
/// Optional "set a wallpaper" feature: lists the default wallpapers of macOS, Windows (XP to 11) and Linux distributions
/// (Ubuntu, Fedora, Pop!_OS, elementary OS, KDE Plasma) from public GitHub collections, shows small previews, downloads
/// the chosen full-size image and sets it as the desktop (and lock screen) wallpaper. Nothing is sent except the
/// requests for the lists and images themselves.
/// </summary>
public static class WallpaperCatalog
{
    private static readonly HttpClient Http = CreateClient();
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearUI", "wallpapers");
    private static string ThumbDir => Path.Combine(Dir, "thumbs");

    /// <summary>
    /// Connects to a server's addresses one after another, 0.6 s apart, and keeps the first that answers. The default is to
    /// wait for each address to time out (about 21 s) before trying the next, which made the first download look frozen
    /// when one of GitHub's addresses is not reachable from the user's network.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addrs = (await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct))
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
        int port = ctx.DnsEndPoint.Port;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        static async Task<Socket> One(IPAddress a, int port, CancellationToken token)
        {
            var s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(new IPEndPoint(a, port), token); return s; }
            catch { s.Dispose(); throw; }
        }

        var pending = new List<Task<Socket>>();
        int next = 0;
        Socket won = null;
        Exception last = null;
        while (won == null && (next < addrs.Count || pending.Count > 0))
        {
            if (next < addrs.Count) pending.Add(One(addrs[next++], port, cts.Token));
            var wait = Task.Delay(next < addrs.Count ? 600 : 12000, ct);
            var done = await Task.WhenAny(pending.Cast<Task>().Append(wait));
            if (done == wait)
            {
                if (next >= addrs.Count) break;       // nobody answered in time
                continue;                              // start the next address while the others keep trying
            }
            var t = (Task<Socket>)done;
            pending.Remove(t);
            if (t.IsCompletedSuccessfully) won = t.Result; else last = t.Exception?.GetBaseException();
        }
        cts.Cancel();
        foreach (var t in pending) _ = t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); });
        if (won == null) throw last ?? new IOException("Could not connect to " + ctx.DnsEndPoint.Host);
        return new NetworkStream(won, ownsSocket: true);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { ConnectCallback = ConnectAsync }) { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PearUI", "1")); // GitHub requires a user agent; no personal details
        return c;
    }

    // ---------- the list ----------
    private sealed record IndexFile(DateTime Time, List<WallpaperItem> Items);

    private static string IndexPath => Path.Combine(Dir, "items.json");

    /// <summary>Every wallpaper from every source. The list is remembered for a day (GitHub allows only 60 anonymous requests an hour).</summary>
    public static async Task<List<WallpaperItem>> ListAsync(CancellationToken ct)
    {
        IndexFile cached = null;
        try { if (File.Exists(IndexPath)) cached = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(IndexPath)); } catch { }
        if (cached?.Items is { Count: > 0 } && DateTime.UtcNow - cached.Time < TimeSpan.FromHours(24)) return cached.Items;

        var sources = new Func<CancellationToken, Task<List<WallpaperItem>>>[] { MacAsync, WindowsAsync, UbuntuAsync, FedoraAsync, PopAsync, ElementaryAsync, KdeAsync };
        var results = await Task.WhenAll(sources.Select(async s =>
        {
            try { return await s(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }   // one source being down must not hide the others
        }));
        ct.ThrowIfCancellationRequested();

        var all = results.Where(r => r != null).SelectMany(r => r).ToList();
        if (all.Count == 0 || results.Any(r => r == null))
        {
            if (cached?.Items is { Count: > 0 }) return cached.Items;   // offline or rate limited: last complete list
            if (all.Count == 0) throw new IOException("No wallpaper source could be reached.");
            return all;
        }
        try { Directory.CreateDirectory(Dir); File.WriteAllText(IndexPath, JsonSerializer.Serialize(new IndexFile(DateTime.UtcNow, all))); } catch { }
        return all;
    }

    private static async Task<List<(string Path, long Size)>> TreeAsync(string repo, string branch, CancellationToken ct)
    {
        string json = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/git/trees/{branch}?recursive=1", ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, long)>();
        foreach (var e in doc.RootElement.GetProperty("tree").EnumerateArray())
            if (e.GetProperty("type").GetString() == "blob")
                list.Add((e.GetProperty("path").GetString(), e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
        return list;
    }

    private static bool IsImage(string name) =>
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static string Natural(string s) => Regex.Replace(s, @"\d+", m => m.Value.PadLeft(6, '0'));
    private static string Spaced(string s) => s.Replace('-', ' ').Replace('_', ' ').Trim();

    // ---- macOS ----
    private const string MacRepo = "LAYTAT/macOS-Wallpapers", MacBranch = "main";

    private static readonly Dictionary<int, string> Releases = new()
    {
        [26] = "Tahoe", [15] = "Sequoia", [14] = "Sonoma", [13] = "Ventura", [12] = "Monterey", [11] = "Big Sur",
    };

    private static async Task<List<WallpaperItem>> MacAsync(CancellationToken ct)
    {
        var names = (await TreeAsync(MacRepo, MacBranch, ct)).Select(t => t.Path).Where(p => !p.Contains('/')).ToList();
        var images = names.Where(IsImage).Where(n => !n.Contains("Server", StringComparison.OrdinalIgnoreCase)).ToList();
        string Stem(string n) => Path.GetFileNameWithoutExtension(n);
        string NoSize(string n) => Regex.Replace(Stem(n), @"[-_ ]?6K$", "", RegexOptions.IgnoreCase);

        // Where a smaller version exists, skip the huge 6K file (a 6K PNG can be 50 MB).
        var plain = images.Where(n => !Regex.IsMatch(Stem(n), @"6K$", RegexOptions.IgnoreCase)).Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = images.Where(n => !Regex.IsMatch(Stem(n), @"6K$", RegexOptions.IgnoreCase) || !plain.Contains(NoSize(n)));

        return chosen.Select(DescribeMac).OrderBy(i => i.Order).ThenBy(i => Natural(i.Title), StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static WallpaperItem DescribeMac(string file)
    {
        WallpaperItem Make(string title, string group, int order) => new(file, title, group, order, "macOS", MacRepo, MacBranch, file);
        string stem = Regex.Replace(Path.GetFileNameWithoutExtension(file), @"[-_ ]?6K$", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(stem, @"^(\d+)[-_](.*)$");
        if (m.Success)
        {
            int v = int.Parse(m.Groups[1].Value);
            string rest = Spaced(m.Groups[2].Value);
            if (v == 10)
            {
                string t = file.StartsWith("10-0_10.1", StringComparison.OrdinalIgnoreCase) ? "Mac OS X 10.0 and 10.1" : "OS X 10." + rest;
                return Make(t, "Classic Mac OS X", 3000 + MinorOf(rest));
            }
            if (Releases.TryGetValue(v, out string name))
            {
                rest = Regex.Replace(rest, @"^0\s+", "");                       // "0 Day" -> "Day"
                string title = rest.Contains(name, StringComparison.OrdinalIgnoreCase) ? rest : (name + " " + rest).Trim();
                return Make(title, "macOS " + name, 1000 - v * 10);
            }
        }
        return Make(Spaced(stem), "Landscapes, flowers and more", 2000);
    }

    private static int MinorOf(string rest) => int.TryParse(Regex.Match(rest, @"^\d+").Value, out int n) ? 100 - n : 0;

    // ---- Windows (XP to 11) ----
    private const string WinRepo = "bronzemelon/All-Windows-Wallpapers", WinBranch = "main";

    private static async Task<List<WallpaperItem>> WindowsAsync(CancellationToken ct)
    {
        // The repository is several GB, so list one folder per Windows version instead of the whole tree.
        string[] versions = { "Windows 11", "Windows 10", "Windows 8.1", "Windows 8", "Windows 7", "Windows Vista", "Windows XP" };
        var parts = await Task.WhenAll(versions.Select(async (dir, rank) =>
        {
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{WinRepo}/contents/{Uri.EscapeDataString(dir)}?ref={WinBranch}", ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<WallpaperItem>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.GetProperty("type").GetString() != "file") continue;
                string name = e.GetProperty("name").GetString();
                string stem = Path.GetFileNameWithoutExtension(name);
                if (!IsImage(name) || Regex.IsMatch(stem, @"Server|Starter|Picture Password|_\d+x\d+", RegexOptions.IgnoreCase)) continue;
                var img = Regex.Match(stem, @"^img(\d+)", RegexOptions.IgnoreCase);
                string title = img.Success ? $"Wallpaper {img.Groups[1].Value}" : Spaced(Regex.Replace(stem, @"\s*\(.*\)$", ""));
                items.Add(new WallpaperItem("win-" + dir + "-" + name, title, dir, 4000 + rank, "Windows", WinRepo, WinBranch, dir + "/" + name));
            }
            return items.OrderBy(i => Natural(i.Title), StringComparer.OrdinalIgnoreCase).ToList();
        }));
        return parts.SelectMany(p => p).ToList();
    }

    // ---- Linux ----
    private static readonly (string Code, string Version, string Name)[] UbuntuReleases =
    {
        ("resolute", "26.04", "Resolute Raccoon"), ("questing", "25.10", "Questing Quokka"), ("plucky", "25.04", "Plucky Puffin"),
        ("oracular", "24.10", "Oracular Oriole"), ("noble", "24.04", "Noble Numbat"), ("jammy", "22.04", "Jammy Jellyfish"),
        ("focal", "20.04", "Focal Fossa"), ("bionic", "18.04", "Bionic Beaver"),
    };

    private static async Task<List<WallpaperItem>> UbuntuAsync(CancellationToken ct)
    {
        const string repo = "azzamsa/ubuntu-wallpapers", branch = "master";
        var tree = await TreeAsync(repo, branch, ct);
        var items = new List<WallpaperItem>();
        for (int rank = 0; rank < UbuntuReleases.Length; rank++)
        {
            var (code, version, name) = UbuntuReleases[rank];
            foreach (var (path, _) in tree.Where(t => t.Path.StartsWith($"curated/{code}/") && IsImage(t.Path)))
            {
                string file = path.Split('/')[^1];
                string title = Spaced(Regex.Replace(Path.GetFileNameWithoutExtension(file), @"_by_.*$", "", RegexOptions.IgnoreCase));
                items.Add(new WallpaperItem("ubuntu-" + code + "-" + file, title, $"Ubuntu {version} {name}", 5000 + rank, "Linux", repo, branch, path));
            }
        }
        return items.OrderBy(i => i.Order).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<WallpaperItem>> FedoraAsync(CancellationToken ct)
    {
        const string repo = "r1w1s1/fedora-wallpapers", branch = "master";
        var items = new List<WallpaperItem>();
        foreach (var (path, _) in await TreeAsync(repo, branch, ct))
        {
            var m = Regex.Match(path, @"^wallpapers/fedora/f(\d+)(?:-\d+)?(?:-([a-z]+))?\.png$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int v = int.Parse(m.Groups[1].Value);
            string part = m.Groups[2].Success ? " " + char.ToUpper(m.Groups[2].Value[0]) + m.Groups[2].Value[1..] : "";
            items.Add(new WallpaperItem("fedora-" + path.Split('/')[^1], $"Fedora {v}{part}", "Fedora", 6000 - v, "Linux", repo, branch, path));
        }
        return items.OrderBy(i => i.Order).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string TitleCase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpper(w[0]) + w[1..]));

    private static async Task<List<WallpaperItem>> PopAsync(CancellationToken ct)
    {
        const string repo = "pop-os/wallpapers", branch = "master";
        var items = new List<WallpaperItem>();
        foreach (var (path, _) in await TreeAsync(repo, branch, ct))
        {
            if (!path.StartsWith("original/") || !IsImage(path)) continue;
            string stem = Path.GetFileNameWithoutExtension(path);
            string title = TitleCase(Spaced(Regex.Replace(stem, @"(-\d+)?(-edit)?$", "")));
            items.Add(new WallpaperItem("pop-" + path.Split('/')[^1], title, "Pop!_OS", 6100, "Linux", repo, branch, path));
        }
        return items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<WallpaperItem>> ElementaryAsync(CancellationToken ct)
    {
        const string repo = "elementary/wallpapers", branch = "main";
        var items = new List<WallpaperItem>();
        foreach (var (path, _) in await TreeAsync(repo, branch, ct))
        {
            if (!path.StartsWith("backgrounds/") || !IsImage(path)) continue;
            items.Add(new WallpaperItem("elementary-" + path.Split('/')[^1], Spaced(Path.GetFileNameWithoutExtension(path)), "elementary OS", 6200, "Linux", repo, branch, path));
        }
        return items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<WallpaperItem>> KdeAsync(CancellationToken ct)
    {
        const string repo = "KDE/plasma-workspace-wallpapers", branch = "master";
        var items = new List<WallpaperItem>();
        // Every KDE wallpaper is a folder with the picture in many sizes: take the biggest one up to 4K.
        foreach (var folder in (await TreeAsync(repo, branch, ct)).GroupBy(t => t.Path.Split('/')[0]))
        {
            string best = null;
            long bestArea = 0;
            foreach (var (path, _) in folder)
            {
                var m = Regex.Match(path, @"/contents/images/(\d+)x(\d+)\.(jpg|jpeg|png)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                long w = long.Parse(m.Groups[1].Value), h = long.Parse(m.Groups[2].Value);
                if (w > 3840 || w < h) continue;                       // not the portrait or 5K+ variants
                if (w * h > bestArea) { bestArea = w * h; best = path; }
            }
            if (best == null) continue;
            string title = Regex.Replace(Spaced(folder.Key), @"(?<=[a-z])(?=[A-Z])", " ");
            items.Add(new WallpaperItem("kde-" + folder.Key + Path.GetExtension(best), TitleCase(title), "KDE Plasma", 6300, "Linux", repo, branch, best));
        }
        return items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- previews ----------
    public static string RawUrl(WallpaperItem item) =>
        $"https://raw.githubusercontent.com/{item.Repo}/{item.Branch}/{string.Join("/", item.Path.Split('/').Select(Uri.EscapeDataString))}";

    private static string Safe(string file) => Regex.Replace(file, @"[^A-Za-z0-9._-]", "_");

    /// <summary>A small preview (cached on disk). Full-size files are 1-50 MB, so a resizing proxy makes the list. Null when it could not be fetched.</summary>
    public static async Task<string> ThumbAsync(WallpaperItem item, CancellationToken ct)
    {
        string path = Path.Combine(ThumbDir, Safe(item.File) + ".jpg");
        if (File.Exists(path) && new FileInfo(path).Length > 500) return path;

        string url = "https://wsrv.nl/?url=" + Uri.EscapeDataString(RawUrl(item)) + "&w=420&h=270&fit=cover&q=72&output=jpg";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                if (bytes.Length < 500) continue;
                Directory.CreateDirectory(ThumbDir);
                await File.WriteAllBytesAsync(path, bytes, ct);
                return path;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { try { await Task.Delay(700, ct); } catch (OperationCanceledException) { throw; } }
        }
        return null;
    }

    // ---------- download & set ----------
    public static async Task<string> DownloadAsync(WallpaperItem item, IProgress<double> progress, CancellationToken ct)
    {
        string path = Path.Combine(Dir, Safe(item.File));
        if (File.Exists(path) && new FileInfo(path).Length > 100_000) { progress?.Report(1); return path; }

        Directory.CreateDirectory(Dir);
        string tmp = path + ".part";
        using var resp = await Http.GetAsync(RawUrl(item), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0, read = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        File.Move(tmp, path, true);
        return path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SetWallpaperInfo(uint action, uint param, string value, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    private static extern bool GetWallpaperInfo(uint action, uint param, StringBuilder value, uint winIni);

    private const uint SPI_SETDESKWALLPAPER = 0x0014, SPI_GETDESKWALLPAPER = 0x0073, UPDATE_AND_BROADCAST = 0x01 | 0x02;

    public static WallpaperSnapshot TakeSnapshot()
    {
        try
        {
            var sb = new StringBuilder(520);
            GetWallpaperInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return new WallpaperSnapshot(sb.ToString(), k?.GetValue("WallpaperStyle") as string ?? "10", k?.GetValue("TileWallpaper") as string ?? "0");
        }
        catch { return new WallpaperSnapshot("", "10", "0"); }
    }

    /// <summary>Sets the desktop wallpaper (scaled to fill the screen).</summary>
    public static bool Apply(string path, string style = "10", string tile = "0")
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            k.SetValue("WallpaperStyle", style, RegistryValueKind.String);
            k.SetValue("TileWallpaper", tile, RegistryValueKind.String);
        }
        catch { }
        return SetWallpaperInfo(SPI_SETDESKWALLPAPER, 0, path, UPDATE_AND_BROADCAST);
    }

    /// <summary>Sets the lock screen picture (Windows 10/11, "Picture" lock screen mode). False when Windows does not allow it.</summary>
    public static async Task<bool> ApplyLockScreenAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            try
            {
                // The classic call: works for desktop apps on current Windows 11 builds, where the newer one below answers "false".
                await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
                return true;
            }
            catch
            {
                if (!Windows.System.UserProfile.UserProfilePersonalizationSettings.IsSupported()) return false;
                return await Windows.System.UserProfile.UserProfilePersonalizationSettings.Current.TrySetLockScreenImageAsync(file);
            }
        }
        catch { return false; }
    }

    public static bool Restore(WallpaperSnapshot s) => s != null && Apply(s.Path ?? "", s.Style, s.Tile);
}
