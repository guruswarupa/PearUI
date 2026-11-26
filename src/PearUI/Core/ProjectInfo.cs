using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PearUI.Core;

internal sealed record FundingLink(string Title, string Url);
internal sealed record Contributor(string Login, string ProfileUrl, string AvatarUrl, int Contributions);
internal sealed record AppLink(string Title, string Url);
internal sealed record OtherApp(string Name, string Tagline, string Platforms, string Icon, List<AppLink> Links);

/// <summary>
/// What the Support page of Settings shows: the ways to fund the project and the people who contribute to it.
/// Both come from the project's GitHub repository (so they stay current without a new release),
/// are cached for a while, and have built-in fallbacks when offline.
/// </summary>
internal static class ProjectInfo
{
    public const string Owner = "guruswarupa";
    public const string Repo = "PearUI";
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";

    /// <summary>UPI (India): the maintainer's payment address, and what a UPI app reads from the QR code (it lets the payer choose the amount).</summary>
    public const string UpiId = "msgswarupa@oksbi";
    public static string UpiPayload => $"upi://pay?pa={UpiId}&pn={Uri.EscapeDataString("Guru Swarupa")}&cu=INR";

    private static readonly HttpClient Http = CreateClient();
    private static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PearUI", "cache");

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PearUI", "1"));   // GitHub requires a user agent; no personal details
        return c;
    }

    // ---------- funding ----------
    /// <summary>Where people can chip in, read from the repository's .github/FUNDING.yml (the file GitHub's Sponsor button uses).</summary>
    public static async Task<List<FundingLink>> GetFundingAsync()
    {
        string yaml = await FetchCachedAsync("funding.yml", $"https://raw.githubusercontent.com/{Owner}/{Repo}/main/.github/FUNDING.yml", TimeSpan.FromHours(12));
        var links = yaml == null ? new List<FundingLink>() : ParseFunding(yaml);
        // Until the repository lists something (or when offline), use the maintainer's own pages.
        if (links.Count == 0)
        {
            links.Add(new FundingLink("GitHub Sponsors", $"https://github.com/sponsors/{Owner}"));
            links.Add(new FundingLink("Buy Me a Coffee", $"https://buymeacoffee.com/{Owner}"));
        }
        return links;
    }

    private static readonly (string Key, string Title, string Template)[] Platforms =
    {
        ("github", "GitHub Sponsors", "https://github.com/sponsors/{0}"),
        ("buy_me_a_coffee", "Buy Me a Coffee", "https://buymeacoffee.com/{0}"),
        ("ko_fi", "Ko-fi", "https://ko-fi.com/{0}"),
        ("patreon", "Patreon", "https://www.patreon.com/{0}"),
        ("open_collective", "Open Collective", "https://opencollective.com/{0}"),
        ("liberapay", "Liberapay", "https://liberapay.com/{0}"),
        ("polar", "Polar", "https://polar.sh/{0}"),
        ("issuehunt", "IssueHunt", "https://issuehunt.io/r/{0}"),
    };

    /// <summary>Reads the simple "platform: name" and "custom: [links]" lines of a FUNDING.yml.</summary>
    internal static List<FundingLink> ParseFunding(string yaml)
    {
        var links = new List<FundingLink>();
        foreach (var raw in yaml.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();
            int hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) value = value[..hash].Trim();
            var items = value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(v => v.Trim('"', '\'')).Where(v => v.Length > 0).ToList();

            if (key == "custom")
            {
                foreach (var url in items)
                    if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                        links.Add(new FundingLink(u.Host.StartsWith("www.") ? u.Host[4..] : u.Host, u.AbsoluteUri));
                continue;
            }
            var platform = Platforms.FirstOrDefault(p => p.Key == key);
            if (platform.Key == null) continue;
            foreach (var name in items)
                if (Regex.IsMatch(name, @"^[A-Za-z0-9_.\-/]+$"))
                    links.Add(new FundingLink(platform.Title, string.Format(platform.Template, name)));
        }
        return links;
    }

    // ---------- the maintainer's other apps ----------
    /// <summary>Other apps by the same developer. The list lives in the repository's .github/apps.json (so it can grow without a release); a copy is built in for offline use.</summary>
    public static async Task<List<OtherApp>> GetOtherAppsAsync()
    {
        string json = await FetchCachedAsync("apps.json", $"https://raw.githubusercontent.com/{Owner}/{Repo}/HEAD/.github/apps.json", TimeSpan.FromHours(12));
        var apps = json == null ? null : ParseApps(json);
        return apps is { Count: > 0 } ? apps : BuiltInApps();
    }

    public static List<OtherApp> BuiltInApps()
    {
        try
        {
            using var s = typeof(ProjectInfo).Assembly.GetManifestResourceStream("PearUI.apps.json");
            using var r = new StreamReader(s!);
            return ParseApps(r.ReadToEnd()) ?? new();
        }
        catch { return new(); }
    }

    internal static List<OtherApp> ParseApps(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<List<OtherApp>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var list = new List<OtherApp>();
            foreach (var a in raw ?? new())
            {
                if (string.IsNullOrWhiteSpace(a.Name)) continue;
                // Only secure web links are kept, and pictures only from GitHub.
                var links = (a.Links ?? new()).Where(l => IsHttps(l.Url) && !string.IsNullOrWhiteSpace(l.Title)).ToList();
                string icon = IsTrustedImageHost(a.Icon) ? a.Icon : "";
                list.Add(a with { Links = links, Icon = icon, Tagline = a.Tagline ?? "", Platforms = a.Platforms ?? "" });
            }
            return list;
        }
        catch { return null; }
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static bool IsTrustedImageHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "github.com" || u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>A picture from the web (an app icon), cached on disk for a week.</summary>
    public static async Task<byte[]> GetImageAsync(string url)
    {
        if (!IsTrustedImageHost(url)) return null;
        string file = Path.Combine(CacheDir, "images", Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".img");
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7)) return await File.ReadAllBytesAsync(file);
            byte[] bytes = await Http.GetByteArrayAsync(url);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, bytes);
            return bytes;
        }
        catch { return File.Exists(file) ? await File.ReadAllBytesAsync(file) : null; }
    }

    // ---------- contributors ----------
    /// <summary>The people who have contributed code, most active first (bots left out). Falls back to the last saved list when offline.</summary>
    public static async Task<List<Contributor>> GetContributorsAsync()
    {
        var all = new List<Contributor>();
        for (int page = 1; page <= 5; page++)
        {
            string json = await FetchCachedAsync($"contributors-{page}.json", $"https://api.github.com/repos/{Owner}/{Repo}/contributors?per_page=100&page={page}", TimeSpan.FromHours(6));
            if (json == null) break;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) break;
            int count = 0;
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                count++;
                if (c.TryGetProperty("type", out var type) && type.GetString() == "Bot") continue;
                all.Add(new Contributor(
                    c.GetProperty("login").GetString() ?? "",
                    c.GetProperty("html_url").GetString() ?? "",
                    c.GetProperty("avatar_url").GetString() ?? "",
                    c.TryGetProperty("contributions", out var n) ? n.GetInt32() : 0));
            }
            if (count < 100) break;
        }
        return all.Where(c => c.Login.Length > 0).OrderByDescending(c => c.Contributions).ToList();
    }

    /// <summary>The picture bytes of a contributor's avatar (small), cached on disk.</summary>
    public static async Task<byte[]> GetAvatarAsync(Contributor c)
    {
        try
        {
            string dir = Path.Combine(CacheDir, "avatars");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Regex.Replace(c.Login, @"[^A-Za-z0-9_\-]", "_") + ".img");
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7)) return await File.ReadAllBytesAsync(file);

            string url = c.AvatarUrl + (c.AvatarUrl.Contains('?') ? "&" : "?") + "s=128";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] bytes = await Http.GetByteArrayAsync(u);
            await File.WriteAllBytesAsync(file, bytes);
            return bytes;
        }
        catch
        {
            try { return await File.ReadAllBytesAsync(Path.Combine(CacheDir, "avatars", Regex.Replace(c.Login, @"[^A-Za-z0-9_\-]", "_") + ".img")); }
            catch { return null; }
        }
    }

    // ---------- fetching with a disk cache ----------
    /// <summary>Downloads a text file, reusing a saved copy for <paramref name="maxAge"/>; offline, any saved copy is used. Null when there is nothing.</summary>
    private static async Task<string> FetchCachedAsync(string cacheName, string url, TimeSpan maxAge)
    {
        string file = Path.Combine(CacheDir, cacheName);
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < maxAge) return await File.ReadAllTextAsync(file);
            using var resp = await Http.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { try { File.Delete(file); } catch { } return null; }
            resp.EnsureSuccessStatusCode();
            string text = await resp.Content.ReadAsStringAsync();
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(file, text);
            return text;
        }
        catch
        {
            try { return File.Exists(file) ? await File.ReadAllTextAsync(file) : null; } catch { return null; }
        }
    }
}
