using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PearUI.Core;

/// <summary>One icon found on macOSicons.com.</summary>
internal sealed record MacIconHit(string Name, string PngUrl, string Credit, string CreditUrl, string Category);

/// <summary>
/// Optional macOS-style app icons from the community library at macOSicons.com. It uses the person's own free API key
/// (nothing is shared or bundled), keeps only the icons they pick (on their PC), and always falls back to the app's own icon.
/// </summary>
internal static class MacIcons
{
    public const string SiteUrl = "https://macosicons.com";
    public const int FreeMonthlyLimit = 50;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string Folder = Path.Combine(SettingsStore.Folder, "macicons");

    /// <summary>Raised after a pick changed (or was reset), so the Dock and others show the new icons.</summary>
    public static event Action IconsChanged;
    public static void RaiseChanged() => IconsChanged?.Invoke();

    /// <summary>Once, when this version first runs: icons found automatically by an earlier build are dropped, and "Big Sur style" becomes the icon style.</summary>
    public static void Migrate()
    {
        var s = SettingsStore.Current;
        if (s.IconStylesMigrated) return;
        s.IconStylesMigrated = true;
        s.MacIconOverrides = new();
        if (s.BigSurIcons) s.IconStyle = IconStyles.BigSur;
        s.BigSurIcons = false;
        SettingsStore.Save(raiseEvent: false);
    }

    public static bool Ready => SettingsStore.Current.MacIconsEnabled && !string.IsNullOrWhiteSpace(SettingsStore.Current.MacIconsApiKey);

    /// <summary>The picked icon file for an app's icon source, or null to use the app's own icon.</summary>
    public static string OverrideFor(string source)
    {
        if (string.IsNullOrEmpty(source) || !SettingsStore.Current.MacIconsEnabled) return null;
        if (!IconStyles.TryGet(SettingsStore.Current.MacIconOverrides, source, out var file)) return null;
        return File.Exists(file) ? file : null;
    }

    /// <summary>Searches made this month (kept on this PC; the free plan allows 50).</summary>
    public static int UsedThisMonth()
    {
        var s = SettingsStore.Current;
        return s.MacIconsMonth == DateTime.Now.ToString("yyyy-MM") ? s.MacIconsSearches : 0;
    }

    public static async Task<List<MacIconHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        var s = SettingsStore.Current;
        if (string.IsNullOrWhiteSpace(s.MacIconsApiKey)) throw new InvalidOperationException("Add your macOSicons API key in Settings first.");
        if (UsedThisMonth() >= FreeMonthlyLimit) throw new InvalidOperationException($"You have used {FreeMonthlyLimit} searches this month (the free plan's limit).");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.macosicons.com/api/v1/search");
        req.Headers.Add("x-api-key", s.MacIconsApiKey.Trim());
        req.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);

        // Every attempt counts against the monthly allowance.
        string month = DateTime.Now.ToString("yyyy-MM");
        s.MacIconsSearches = (s.MacIconsMonth == month ? s.MacIconsSearches : 0) + 1;
        s.MacIconsMonth = month;
        SettingsStore.Save(raiseEvent: false);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("macOSicons did not accept the API key.");
        if ((int)resp.StatusCode == 429) throw new InvalidOperationException("macOSicons says you have used up this month's searches.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<MacIconHit>();
        if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return list;
        foreach (var h in hits.EnumerateArray())
        {
            string Str(string n) => h.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string png = Str("lowResPngUrl");
            if (!Uri.TryCreate(png, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) continue;
            string by = !string.IsNullOrWhiteSpace(Str("designedBy")) ? Str("designedBy") : (h.TryGetProperty("hideUploader", out var hide) && hide.ValueKind == JsonValueKind.True ? "" : Str("usersName"));
            string byUrl = !string.IsNullOrWhiteSpace(Str("designedByUrl")) ? Str("designedByUrl") : Str("uploadedBy");
            list.Add(new MacIconHit(Str("appName"), png, by, byUrl, Str("category")));
            if (list.Count >= 24) break;
        }
        return list;
    }

    /// <summary>Downloads one icon image (thumbnails and the chosen icon share this). Cached on disk for 30 days.</summary>
    public static async Task<string> DownloadAsync(MacIconHit hit, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(hit.PngUrl)))[..16] + ".png";
        string file = Path.Combine(Folder, name);
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(30)) return file;
        byte[] bytes = await Http.GetByteArrayAsync(hit.PngUrl, ct);
        if (bytes.Length < 100 || bytes.Length > 8_000_000) throw new InvalidDataException("Not a valid icon.");
        await File.WriteAllBytesAsync(file, bytes, ct);
        return file;
    }


    /// <summary>Uses the downloaded icon for the app, or the app's own icon again when <paramref name="file"/> is null.</summary>
    public static void Apply(string source, string file)
    {
        var s = SettingsStore.Current;
        s.MacIconOverrides ??= new();
        string key = IconStyles.Key(source);
        s.MacIconOverrides.Remove(source);
        if (file == null) s.MacIconOverrides.Remove(key); else s.MacIconOverrides[key] = file;
        PearUI.Native.ShellIcons.InvalidateAll();
        SettingsStore.Save(raiseEvent: false);
        IconsChanged?.Invoke();
    }

    /// <summary>Deletes picked icons older than 30 days that no app uses any more.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var used = new HashSet<string>(SettingsStore.Current.MacIconOverrides?.Values ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(Folder))
                if (!used.Contains(f) && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(30)) File.Delete(f);
        }
        catch { }
    }
}
