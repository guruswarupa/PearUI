using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ical.Net;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using Ical.Net.DataTypes;

namespace PearUI.Core;

public record ICalFeed(string Name, string Url);

/// <summary>
/// Calendars added by pasting an iCal link (Google Calendar's "Secret address in iCal format", Outlook, iCloud, ...).
/// The links are secrets, so they are stored encrypted for this Windows user. Events are read-only.
/// </summary>
public static class ICalFeeds
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static string FilePath => Path.Combine(SettingsStore.Folder, "calendars.dat");

    private static List<ICalFeed> _feeds;
    private record Cached(DateTime At, Calendar Cal);
    private static readonly Dictionary<string, Cached> Cache = new();

    public static IReadOnlyList<ICalFeed> Feeds { get { Load(); return _feeds; } }
    public static bool Any => Feeds.Count > 0;

    private static void Load()
    {
        if (_feeds != null) return;
        _feeds = new();
        try
        {
            if (!File.Exists(FilePath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            _feeds = JsonSerializer.Deserialize<List<ICalFeed>>(Encoding.UTF8.GetString(plain)) ?? new();
        }
        catch { _feeds = new(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_feeds)), null, DataProtectionScope.CurrentUser));
        }
        catch { }
    }

    /// <summary>webcal:// is just https:// for calendars.</summary>
    public static string Normalize(string url)
    {
        url = (url ?? "").Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        return url;
    }

    public static bool LooksLikeFeed(string text)
    {
        var u = Normalize(text);
        return Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !u.Contains(' ') && !u.Contains('\n');
    }

    /// <summary>Downloads and validates the link, then saves it. Returns an error message, or null on success.</summary>
    public static async Task<string> AddAsync(string url)
    {
        url = Normalize(url);
        if (!LooksLikeFeed(url)) return "That doesn't look like a calendar link. It should start with https:// or webcal://.";
        Load();
        if (_feeds.Any(f => f.Url == url)) return "That calendar is already added.";
        try
        {
            var cal = await DownloadAsync(url);
            string name = cal.Properties.Get<string>("X-WR-CALNAME");
            if (string.IsNullOrWhiteSpace(name)) name = new Uri(url).Host;
            _feeds.Add(new ICalFeed(name.Trim(), url));
            Cache[url] = new Cached(DateTime.Now, cal);
            Save();
            return null;
        }
        catch (Exception ex)
        {
            return "Couldn't read that calendar (" + ex.Message.Trim() + "). Check the link and try again.";
        }
    }

    /// <summary>Forget downloaded copies so the next read fetches every calendar again.</summary>
    public static void Refresh() => Cache.Clear();

    public static void Remove(ICalFeed feed)
    {
        Load();
        _feeds.RemoveAll(f => f.Url == feed.Url);
        Cache.Remove(feed.Url);
        Save();
    }

    private static async Task<Calendar> DownloadAsync(string url)
    {
        string text = await Http.GetStringAsync(url);
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("not an iCal feed");
        return await Task.Run(() => Calendar.Load(text));
    }

    private static async Task<Calendar> GetCalendarAsync(ICalFeed f)
    {
        if (Cache.TryGetValue(f.Url, out var c) && DateTime.Now - c.At < TimeSpan.FromMinutes(10)) return c.Cal;
        var cal = await DownloadAsync(f.Url);
        Cache[f.Url] = new Cached(DateTime.Now, cal);
        return cal;
    }

    private static DateTime Local(CalDateTime d) =>
        d.TzId == null ? d.Value : d.AsUtc.ToLocalTime(); // floating and all-day values are already local wall-clock

    public static async Task<CalendarResult> GetEventsAsync(DateTime from, DateTime to)
    {
        var feeds = Feeds.ToList();
        string error = null;
        var tasks = feeds.Select(async f =>
        {
            var found = new List<CalendarEvent>();
            try
            {
                var cal = await GetCalendarAsync(f);
                await Task.Run(() =>
                {
                    foreach (var o in cal.GetOccurrences(new CalDateTime(DateTime.SpecifyKind(from, DateTimeKind.Unspecified))).TakeWhile(o => Local(o.Period.StartTime) < to))
                    {
                        if (o.Source is not IcalEvent ev) continue;
                        if (ev.Status == "CANCELLED") continue;
                        var start = Local(o.Period.StartTime);
                        var end = o.Period.EndTime == null ? start : Local(o.Period.EndTime);
                        if (end < from) continue;
                        bool allDay = !o.Period.StartTime.HasTime;
                        found.Add(new CalendarEvent(string.IsNullOrWhiteSpace(ev.Summary) ? "(No title)" : ev.Summary.Trim(),
                            start, end, allDay, ev.Location, f.Name));
                    }
                });
            }
            catch (Exception ex) { error ??= "Couldn't update \"" + f.Name + "\" (" + ex.Message.Trim() + ")."; }
            return found;
        });
        var all = (await Task.WhenAll(tasks)).SelectMany(x => x).OrderBy(e => e.Start).ToList();
        return new CalendarResult(all, error);
    }
}
