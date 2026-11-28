using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PearUI.Core;

/// <summary>A newer PearUI release found on GitHub.</summary>
internal sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, string AssetName, string AssetUrl, long AssetSize, string Sha256);

/// <summary>
/// Over-the-air updates: asks GitHub whether a newer release exists, downloads its installer, checks it, and runs it quietly
/// so PearUI restarts on the new version. Only the project's own GitHub releases are trusted.
/// </summary>
internal static class Updater
{
    private const string Repo = "guruswarupa/PearUI";
    private static readonly HttpClient Http = CreateClient();

    // For testing the update flow against a local server. Not used in normal operation.
    private static string ApiUrl => Environment.GetEnvironmentVariable("PEARUI_UPDATE_API") ?? $"https://api.github.com/repos/{Repo}/releases/latest";
    private static bool IsTestServer => Environment.GetEnvironmentVariable("PEARUI_UPDATE_API") != null;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PearUI-Updater", "1"));   // GitHub requires a user agent; no personal details
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>The version of the running app (major.minor.build).</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>True when this copy runs from the installed location, so an update can replace it. A copy built from source can't.</summary>
    public static bool CanSelfUpdate
    {
        get
        {
            try
            {
                string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearUI", "app");
                return Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').Equals(installed, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// <summary>The newest release if it is newer than this app, otherwise null. Throws when GitHub can't be reached.</summary>
    public static async Task<ReleaseInfo> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(ApiUrl, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no release published yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"\d+\.\d+(\.\d+)?");
        if (!m.Success || !Version.TryParse(m.Value, out var version)) return null;
        if (version.Build < 0) version = new Version(version.Major, version.Minor, 0);
        if (version <= Current) return null;

        // The Windows installer among the release's files.
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("PearUI-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            // The changelog lists every version between yours and the newest, in plain words; the release's own text is the fallback.
            string fromChangelog = await WhatsNewAsync(version, ct);
            if (!string.IsNullOrWhiteSpace(fromChangelog)) notes = fromChangelog;
            return new ReleaseInfo(
                version, tag,
                notes,
                root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : $"https://github.com/{Repo}/releases/latest",
                name, asset.GetProperty("browser_download_url").GetString() ?? "", asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "");
        }
        return null;   // a release without an installer is not an update
    }

    /// <summary>
    /// The changelog entries of every version newer than this one, up to <paramref name="latest"/>, read from the project's
    /// CHANGELOG.md on GitHub. Returns an empty string when it can't be read (offline, a test server, or no matching entries).
    /// </summary>
    private static async Task<string> WhatsNewAsync(Version latest, CancellationToken ct)
    {
        if (IsTestServer) return "";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            string md = await Http.GetStringAsync($"https://raw.githubusercontent.com/{Repo}/main/CHANGELOG.md", cts.Token);

            var sb = new System.Text.StringBuilder();
            bool include = false;
            bool sawNewest = false;
            foreach (var raw in md.Replace("", "").Split('
'))
                var h = System.Text.RegularExpressions.Regex.Match(raw, @"^##\s*\[(\d+\.\d+(?:\.\d+)?)\]");
                if (raw.StartsWith("## "))
                {
                    include = h.Success && Version.TryParse(h.Groups[1].Value, out var v) &&
                              new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) is var nv && nv > Current && nv <= latest;
                    if (include) { sawNewest = true; sb.AppendLine().AppendLine("Version " + h.Groups[1].Value); }
                    continue;
                }
                if (include) sb.AppendLine(raw);
            }
            string text = sb.ToString().Trim();
            if (sawNewest) return text;

            // The newest version has no entry yet (tagged before the heading was renamed): what is under [Unreleased] is that release.
            var unreleased = new System.Text.StringBuilder();
            bool inUnreleased = false;
            foreach (var raw in md.Replace("", "").Split('
'))
            {
                if (raw.StartsWith("## ")) { inUnreleased = raw.Contains("[Unreleased]", StringComparison.OrdinalIgnoreCase); continue; }
                if (inUnreleased) unreleased.AppendLine(raw);
            }
            string extra = unreleased.ToString().Trim();
            if (extra.Length == 0) return text;
            return ("Version " + latest.ToString(3) + Environment.NewLine + extra + (text.Length > 0 ? Environment.NewLine + Environment.NewLine + text : "")).Trim();
        }
        catch { return ""; }
    }

    /// <summary>Downloads the installer to a temporary folder and checks its size and checksum. Returns its path.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct = default)
    {
        if (!IsTestServer && !IsTrustedHost(new Uri(release.AssetUrl).Host)) throw new InvalidOperationException("The update is not hosted on GitHub.");

        string dir = Path.Combine(Path.GetTempPath(), "PearUI-update");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Path.GetFileName(release.AssetName));
        string part = file + ".part";

        using (var resp = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            string finalHost = resp.RequestMessage?.RequestUri?.Host ?? "";
            if (!IsTestServer && !IsTrustedHost(finalHost)) throw new InvalidOperationException("The update was redirected away from GitHub.");

            long total = resp.Content.Headers.ContentLength ?? release.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buffer = new byte[81920];
            long done = 0; int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        // The file must be the size GitHub reported and match its checksum when one is published.
        var info = new FileInfo(part);
        if (release.AssetSize > 0 && info.Length != release.AssetSize) { TryDelete(part); throw new InvalidDataException("The download is incomplete."); }
        if (!string.IsNullOrEmpty(release.Sha256))
        {
            string actual;
            await using (var fs = File.OpenRead(part)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) { TryDelete(part); throw new InvalidDataException("The download does not match its checksum."); }
        }

        File.Move(part, file, true);
        return file;
    }

    private static bool IsTrustedHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the installer quietly and hands over to it. The caller must quit PearUI right after. The installer keeps your
    /// settings and your earlier choices (it does not set up the Terminal or Linux tools again), then starts PearUI.
    /// </summary>
    public static void RunInstaller(string installer)
    {
        var psi = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! };
        psi.ArgumentList.Add("/VERYSILENT");
        psi.ArgumentList.Add("/SUPPRESSMSGBOXES");
        psi.ArgumentList.Add("/NORESTART");
        psi.ArgumentList.Add("/TASKS=");        // no optional extras again: only the program files are replaced
        psi.ArgumentList.Add("/restart=1");     // start PearUI again when finished
        Process.Start(psi);
    }

    /// <summary>Removes installers left over from earlier updates.</summary>
    public static void CleanUp()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "PearUI-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
