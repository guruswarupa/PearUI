using System.ComponentModel;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using MacUI.Core;
using MacUI.Native;

namespace MacUI.Launcher;

public sealed class SearchResult : INotifyPropertyChanged
{
    private ImageSource _icon;
    public string Title { get; init; }
    public string Subtitle { get; init; }
    public string Category { get; init; }
    public string Glyph { get; init; }
    public Action Execute { get; init; }
    public string RevealPath { get; init; }
    public double Score { get; init; }
    public string IconSource { get; init; }

    public ImageSource Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler PropertyChanged;
}

/// <summary>Search sources for the launcher: apps, calculator, settings pages, commands, files, web.</summary>
public static class SearchProviders
{
    private static readonly (string Name, string Uri, string Keywords)[] SettingsPages =
    {
        ("Wi-Fi", "network-wifi", "wireless internet network"),
        ("Bluetooth & devices", "bluetooth", "devices mouse headphones"),
        ("Display", "display", "brightness resolution screen monitor scale night light"),
        ("Sound", "sound", "volume audio speakers microphone"),
        ("Notifications", "notifications", "focus do not disturb"),
        ("Power & battery", "batterysaver", "energy sleep"),
        ("Installed apps", "appsfeatures", "uninstall programs"),
        ("Default apps", "defaultapps", "browser"),
        ("Background", "personalization-background", "wallpaper desktop"),
        ("Colors", "colors", "dark mode light accent theme"),
        ("Themes", "themes", "personalization"),
        ("Network & internet", "network", "ethernet vpn proxy"),
        ("Windows Update", "windowsupdate", "updates"),
        ("Privacy & security", "privacy", "permissions"),
        ("Accounts", "accounts", "user sign-in password"),
        ("Date & time", "dateandtime", "clock timezone"),
        ("Language & region", "regionlanguage", "keyboard"),
        ("Mouse", "mousetouchpad", "pointer cursor"),
        ("Keyboard", "typing", "typing"),
        ("Storage", "storagesense", "disk space"),
        ("Printers & scanners", "printers", "printer"),
        ("Multitasking", "multitasking", "snap desktops"),
        ("About this PC", "about", "system info specs"),
    };

    private static (string Name, string Glyph, string Keywords, Action Run)[] Commands => new (string, string, string, Action)[]
    {
        ("PearUI Settings", "", "preferences dock menu bar", () => App.Instance.ShowSettings()),
        ("Lock Screen", "", "lock", Launch.Lock),
        ("Sleep", "", "suspend", Launch.Sleep),
        ("Restart", "", "reboot", Launch.Restart),
        ("Shut Down", "", "power off shutdown", Launch.ShutDown),
        ("Log Out", "", "sign out logout", Launch.LogOut),
        ("Empty Recycle Bin", "", "trash empty", () => Win32.SHEmptyRecycleBin(IntPtr.Zero, null, 0)),
        ("Task Manager", "", "force quit processes", () => Launch.Open("taskmgr.exe")),
        ("Toggle Dark Mode", "", "appearance theme light", () => SystemStatus.SetSystemDark(!SystemStatus.IsSystemDark())),
        ("Quit PearUI", "", "exit close", () => App.Instance.Quit()),
    };

    /// <summary>0 = no match; higher = better.</summary>
    public static double Score(string text, string query, string keywords = null)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return 0;
        var ci = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions o = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        if (ci.Compare(text, query, o) == 0) return 120;
        if (ci.IsPrefix(text, query, o)) return 100 - Math.Min(text.Length, 30) * 0.3;
        foreach (var word in text.Split(' ', '-', '_', '.', '(', ')'))
            if (word.Length > 0 && ci.IsPrefix(word, query, o)) return 80;
        // Initials: "vsc" -> "Visual Studio Code"
        var initials = new string(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());
        if (query.Length >= 2 && ci.IsPrefix(initials, query, o)) return 75;
        if (ci.IndexOf(text, query, o) >= 0) return 60;
        if (keywords != null && ci.IndexOf(keywords, query, o) >= 0) return 45;
        // Subsequence (fuzzy)
        int qi = 0;
        string t = text.ToLowerInvariant(), q = query.ToLowerInvariant();
        foreach (char c in t) if (qi < q.Length && c == q[qi]) qi++;
        return qi == q.Length && q.Length >= 3 ? 25 : 0;
    }

    public static List<SearchResult> Instant(string query)
    {
        var results = new List<SearchResult>();
        query = query.Trim();
        if (query.Length == 0) return results;

        // Calculator
        var calc = TryCalculate(query);
        if (calc != null)
        {
            string value = calc;
            results.Add(new SearchResult
            {
                Title = "= " + value, Subtitle = query + "   (Enter to copy)", Category = "Calculator", Glyph = "", Score = 200,
                Execute = () => Clipboard.SetText(value),
            });
        }

        // Apps
        foreach (var app in AppCatalog.Instance.Apps)
        {
            double s = Score(app.Name, query);
            if (s <= 0) continue;
            var entry = app;
            results.Add(new SearchResult
            {
                Title = app.Name, Category = "Application", Score = s + 10, IconSource = app.IconSource,
                Subtitle = app.ExePath,
                RevealPath = app.ExePath,
                Execute = () => Launch.Open(entry.Target),
            });
        }

        // MacUI commands
        foreach (var c in Commands)
        {
            double s = Score(c.Name, query, c.Keywords);
            if (s > 0) results.Add(new SearchResult { Title = c.Name, Category = "Command", Glyph = c.Glyph, Score = s, Execute = c.Run });
        }

        // Settings pages
        foreach (var p in SettingsPages)
        {
            double s = Score(p.Name, query, p.Keywords);
            string uri = p.Uri;
            if (s > 0) results.Add(new SearchResult
            {
                Title = p.Name, Subtitle = "Settings", Category = "System Settings", Glyph = "", Score = s - 5,
                Execute = () => Launch.Settings(uri),
            });
        }

        var top = results.OrderByDescending(r => r.Score).Take(12).ToList();

        // Web search fallback is always last.
        string q = query;
        top.Add(new SearchResult
        {
            Title = $"Search the web for “{query}”", Category = "Web", Glyph = "", Score = 1,
            Execute = () => Launch.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(q)),
        });
        return top;
    }

    private static readonly Regex MathChars = new(@"^[\d\s\.\,\+\-\*/\(\)%\^x×÷]+$", RegexOptions.Compiled);

    public static string TryCalculate(string q)
    {
        if (!MathChars.IsMatch(q) || !q.Any(c => "+-*/%^x×÷".Contains(c)) || !q.Any(char.IsDigit)) return null;
        string expr = q.Replace('×', '*').Replace('x', '*').Replace('÷', '/').Replace(",", "");
        try
        {
            expr = ExpandPower(expr);
            var result = new DataTable().Compute(expr, null);
            if (result == null || result is DBNull) return null;
            double d = Convert.ToDouble(result, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            return Math.Round(d, 10).ToString("#,0.##########", CultureInfo.CurrentCulture);
        }
        catch { return null; }
    }

    /// <summary>DataTable.Compute has no '^'; evaluate simple "a^b" pairs up front.</summary>
    private static string ExpandPower(string expr)
    {
        var rx = new Regex(@"(\d+(?:\.\d+)?)\s*\^\s*(\d+(?:\.\d+)?)");
        while (rx.IsMatch(expr))
            expr = rx.Replace(expr, m => Math.Pow(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture), 1);
        return expr;
    }

    /// <summary>Queries the Windows Search index for files in the user's profile.</summary>
    public static List<SearchResult> Files(string query, CancellationToken ct)
    {
        var list = new List<SearchResult>();
        query = query.Trim();
        if (query.Length < 2) return list;
        string term = new string(query.Where(c => c != '"' && c != '\'' && c != '*').ToArray()).Trim();
        if (term.Length < 2) return list;

        string scope = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        string sql = "SELECT TOP 8 System.ItemPathDisplay, System.ItemNameDisplay, System.ItemTypeText FROM SYSTEMINDEX " +
                     $"WHERE SCOPE='file:{scope.Replace("'", "''")}' AND CONTAINS(System.FileName, '\"{term}*\"') " +
                     "ORDER BY System.DateModified DESC";
        try
        {
            using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            conn.Open();
            using var cmd = new OleDbCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            while (reader != null && reader.Read())
            {
                if (ct.IsCancellationRequested) break;
                string path = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (path == null) continue;
                string name = reader.IsDBNull(1) ? Path.GetFileName(path) : reader.GetString(1);
                string type = reader.IsDBNull(2) ? "File" : reader.GetString(2);
                list.Add(new SearchResult
                {
                    Title = name, Subtitle = path, Category = type, IconSource = path, RevealPath = path, Score = 5,
                    Execute = () => Launch.Open(path),
                });
            }
        }
        catch { /* Windows Search not available */ }
        return list;
    }
}
