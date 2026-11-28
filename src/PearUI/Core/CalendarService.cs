using Windows.ApplicationModel.Appointments;

namespace PearUI.Core;

public record CalendarEvent(string Subject, DateTime Start, DateTime End, bool AllDay, string Location, string Calendar)
{
    /// <summary>True when the event touches the given calendar day (multi-day events show on every day they span).</summary>
    public bool Covers(DateTime day)
    {
        var d0 = day.Date;
        var d1 = d0.AddDays(1);
        if (End <= Start) return Start >= d0 && Start < d1;
        return Start < d1 && End > d0;
    }
}

public record CalendarResult(List<CalendarEvent> Events, string Error);

/// <summary>Reads appointments from the calendars synced into Windows (Outlook, Microsoft, Google, iCloud, ...), read-only.</summary>
public static class CalendarService
{
    /// <summary>Calendars added by iCal link, plus whatever calendars Windows has.</summary>
    public static async Task<CalendarResult> GetEventsAsync(DateTime from, DateTime to)
    {
        var windowsTask = GetWindowsEventsAsync(from, to);
        // Windows' calendar store can hang for desktop apps: don't let it hold up the popup.
        var timed = await Task.WhenAny(windowsTask, Task.Delay(TimeSpan.FromSeconds(6)));
        var winFast = timed == windowsTask ? await windowsTask : new CalendarResult(new(), null);
        var feeds = ICalFeeds.Any ? await ICalFeeds.GetEventsAsync(from, to) : new CalendarResult(new(), null);
        var win = winFast; // Windows' own error is ignored: the popup offers to add a calendar link instead
        var all = feeds.Events.Concat(win.Events).OrderBy(e => e.Start).ToList();
        return new CalendarResult(all, feeds.Error);
    }

    private static async Task<CalendarResult> GetWindowsEventsAsync(DateTime from, DateTime to)
    {
        try
        {
            var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            if (store == null)
                return new CalendarResult(new(), "Calendar access is turned off. Allow desktop apps to read your calendar in Windows Settings.");

            var options = new FindAppointmentsOptions { MaxCount = 300, IncludeHidden = false };
            options.FetchProperties.Add(AppointmentProperties.Subject);
            options.FetchProperties.Add(AppointmentProperties.Location);
            options.FetchProperties.Add(AppointmentProperties.StartTime);
            options.FetchProperties.Add(AppointmentProperties.Duration);
            options.FetchProperties.Add(AppointmentProperties.AllDay);

            var list = await store.FindAppointmentsAsync(new DateTimeOffset(from), to - from, options);

            var names = new Dictionary<string, string>();
            try { foreach (var c in await store.FindAppointmentCalendarsAsync()) names[c.LocalId] = c.DisplayName; } catch { }

            var events = new List<CalendarEvent>();
            foreach (var a in list)
            {
                var start = a.StartTime.LocalDateTime;
                var end = start + a.Duration;
                names.TryGetValue(a.CalendarId ?? "", out var cal);
                events.Add(new CalendarEvent(string.IsNullOrWhiteSpace(a.Subject) ? "(No title)" : a.Subject.Trim(),
                    start, end, a.AllDay, a.Location, cal));
            }
            events.Sort((x, y) => x.Start.CompareTo(y.Start));
            return new CalendarResult(events, null);
        }
        catch (Exception ex)
        {
            return new CalendarResult(new(), "Couldn't read your calendar (" + ex.Message.Trim() + ").");
        }
    }
}
