using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PearUI.Core;

/// <summary>One wallpaper in the online collection.</summary>
public sealed record WallpaperItem(string File, string Title, string Group, int Order);

/// <summary>The wallpaper that was set before PearUI changed it, so it can be put back.</summary>
public sealed record WallpaperSnapshot(string Path, string Style, string Tile);

/// <summary>
/// Optional "set a macOS wallpaper" feature: lists Apple's default wallpapers from a public GitHub collection
/// (LAYTAT/macOS-Wallpapers, Cheetah to Tahoe), shows small previews, downloads the chosen full-size image and sets it
/// as the desktop wallpaper. Nothing is sent except the requests for the lists and images themselves.
/// </summary>
public static class WallpaperCatalog
{
    private const string Repo = "LAYTAT/macOS-Wallpapers", Branch = "main";

    private static readonly HttpClient Http = CreateClient();
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearUI", "wallpapers");
    private static string ThumbDir => Path.Combine(Dir, "thumbs");

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PearUI", "1")); // GitHub requires a user agent; no personal details
        return c;
    }

    // ---------- the list ----------
    public static async Task<List<WallpaperItem>> ListAsync(CancellationToken ct)
    {
        string indexFile = Path.Combine(Dir, "index.json");
        string json;
        try
        {
            json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/git/trees/{Branch}", ct);
            try { Directory.CreateDirectory(Dir); File.WriteAllText(indexFile, json); } catch { }
        }
        catch when (!ct.IsCancellationRequested && File.Exists(indexFile))
        {
            json = File.ReadAllText(indexFile); // offline: the list from last time
        }

        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => e.GetProperty("path").GetString())
            .ToList();
        return Build(names);
    }

    private static readonly Dictionary<int, string> Releases = new()
    {
        [26] = "Tahoe", [15] = "Sequoia", [14] = "Sonoma", [13] = "Ventura", [12] = "Monterey", [11] = "Big Sur",
    };

    private static List<WallpaperItem> Build(List<string> names)
    {
        var images = names.Where(n => n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                   || n.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                   || n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                          .Where(n => !n.Contains("Server", StringComparison.OrdinalIgnoreCase))
                          .ToList();
        string Stem(string n) => Path.GetFileNameWithoutExtension(n);
        string NoSize(string n) => Regex.Replace(Stem(n), @"[-_ ]?6K$", "", RegexOptions.IgnoreCase);

        // Where a smaller version exists, skip the huge 6K file (a 6K PNG can be 50 MB).
        var plain = images.Where(n => !Regex.IsMatch(Stem(n), @"6K$", RegexOptions.IgnoreCase)).Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = images.Where(n => !Regex.IsMatch(Stem(n), @"6K$", RegexOptions.IgnoreCase) || !plain.Contains(NoSize(n))).ToList();

        var items = chosen.Select(Describe).OrderBy(i => i.Order).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return items;
    }

    private static WallpaperItem Describe(string file)
    {
        string stem = Regex.Replace(Path.GetFileNameWithoutExtension(file), @"[-_ ]?6K$", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(stem, @"^(\d+)[-_](.*)$");
        if (m.Success)
        {
            int v = int.Parse(m.Groups[1].Value);
            string rest = m.Groups[2].Value.Replace('-', ' ').Replace('_', ' ').Trim();
            if (v == 10)
            {
                string t = file.StartsWith("10-0_10.1", StringComparison.OrdinalIgnoreCase) ? "Mac OS X 10.0 and 10.1" : "OS X 10." + rest;
                return new WallpaperItem(file, t, "Classic Mac OS X", 3000 + MinorOf(rest));
            }
            if (Releases.TryGetValue(v, out string name))
            {
                rest = Regex.Replace(rest, @"^0\s+", "");                       // "0 Day" -> "Day"
                string title = rest.Contains(name, StringComparison.OrdinalIgnoreCase) ? rest : (name + " " + rest).Trim();
                return new WallpaperItem(file, title, "macOS " + name, 1000 - v * 10);
            }
        }
        return new WallpaperItem(file, stem.Replace('-', ' ').Replace('_', ' '), "Landscapes, flowers and more", 2000);
    }

    private static int MinorOf(string rest) => int.TryParse(Regex.Match(rest, @"^\d+").Value, out int n) ? 100 - n : 0;

    // ---------- previews ----------
    public static string RawUrl(string file) => $"https://raw.githubusercontent.com/{Repo}/{Branch}/{Uri.EscapeDataString(file)}";

    private static string Safe(string file) => Regex.Replace(file, @"[^A-Za-z0-9._-]", "_");

    /// <summary>A small preview (cached on disk). Full-size files are 5-50 MB, so a resizing proxy makes the list. Null when it could not be fetched.</summary>
    public static async Task<string> ThumbAsync(string file, CancellationToken ct)
    {
        string path = Path.Combine(ThumbDir, Safe(file) + ".jpg");
        if (File.Exists(path) && new FileInfo(path).Length > 500) return path;

        string url = "https://wsrv.nl/?url=" + Uri.EscapeDataString(RawUrl(file)) + "&w=420&h=270&fit=cover&q=72&output=jpg";
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
    public static async Task<string> DownloadAsync(string file, IProgress<double> progress, CancellationToken ct)
    {
        string path = Path.Combine(Dir, Safe(file));
        if (File.Exists(path) && new FileInfo(path).Length > 100_000) { progress?.Report(1); return path; }

        Directory.CreateDirectory(Dir);
        string tmp = path + ".part";
        using var resp = await Http.GetAsync(RawUrl(file), HttpCompletionOption.ResponseHeadersRead, ct);
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
