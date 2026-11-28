using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PearUI.Core;

namespace PearUI.MenuBar;

/// <summary>Month calendar with the upcoming events from the calendars synced into Windows.</summary>
public sealed class CalendarPopup : BarPopup
{
    private const int UpcomingDays = 14;

    private readonly TextBlock _monthTitle = new() { FontSize = 17, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    private readonly StackPanel _events = new();
    private readonly TextBlock _listTitle = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 6) };

    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime? _selected; // null = show the upcoming list
    private List<CalendarEvent> _all = new();
    private string _error;
    private bool _loaded;
    private int _loadId;

    public CalendarPopup() : base(340)
    {
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        // Header: month + navigation
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_monthTitle);
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(NavButton("", () => Navigate(-1)));
        nav.Children.Add(TextButton("Today", GoToday));
        nav.Children.Add(NavButton("", () => Navigate(1)));
        Grid.SetColumn(nav, 1);
        head.Children.Add(nav);
        root.Children.Add(head);

        // Weekday names
        var names = new UniformGrid { Columns = 7 };
        var fdow = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var d = (DayOfWeek)(((int)fdow + i) % 7);
            var t = new TextBlock
            {
                Text = CultureInfo.CurrentCulture.DateTimeFormat.GetShortestDayName(d), FontSize = 11, Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
            };
            names.Children.Add(t);
        }
        root.Children.Add(names);
        root.Children.Add(_days);

        // Events
        var sep = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10), Opacity = 0.6 };
        sep.SetResourceReference(Border.BackgroundProperty, "Brush.Separator");
        root.Children.Add(sep);
        // Always-visible header: list title on the left, "Add calendar" on the right (the list below can be long or still loading).
        _listTitle.Margin = new Thickness(0);
        _listTitle.VerticalAlignment = VerticalAlignment.Center;
        var listHead = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        listHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        listHead.Children.Add(_listTitle);
        var addCal = TextButton("+ Add calendar", () => { if (_adding) { _adding = false; BuildEvents(); } else StartAdding(); });
        var addEvent = TextButton("+ Event", () => { if (_addingEvent) { _addingEvent = false; BuildEvents(); } else StartAddingEvent(); });
        var refresh = NavButton("", () =>
        {
            ICalFeeds.Refresh();
            _loaded = false;
            BuildEvents(); // shows "Loading…" until the calendars have been downloaded again
            _ = LoadAsync();
        });
        refresh.ToolTip = "Refresh calendars";
        var headButtons = new StackPanel { Orientation = Orientation.Horizontal };
        headButtons.Children.Add(refresh);
        headButtons.Children.Add(addEvent);
        headButtons.Children.Add(addCal);
        Grid.SetColumn(headButtons, 1);
        listHead.Children.Add(headButtons);
        root.Children.Add(listHead);
        var scroll = new ScrollViewer { Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _events };
        root.Children.Add(scroll);

        Content = root;
        BuildGrid();
        BuildEvents();
    }

    protected override void OnShown() => _ = LoadAsync();

    // ---------- data ----------
    private DateTime GridStart()
    {
        var fdow = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int back = ((int)_month.DayOfWeek - fdow + 7) % 7;
        return _month.AddDays(-back);
    }

    private async Task LoadAsync()
    {
        int id = ++_loadId;
        var from = GridStart();
        if (DateTime.Today < from) from = DateTime.Today;
        var to = GridStart().AddDays(42);
        var upcomingEnd = DateTime.Today.AddDays(UpcomingDays);
        if (upcomingEnd > to) to = upcomingEnd;

        var res = await CalendarService.GetEventsAsync(from, to);
        if (id != _loadId) return;
        _all = res.Events;
        _error = res.Error;
        _loaded = true;
        BuildGrid();
        BuildEvents();
    }

    private void Navigate(int months)
    {
        _month = _month.AddMonths(months);
        _selected = null;
        BuildGrid();
        BuildEvents();
        _ = LoadAsync();
    }

    private void GoToday()
    {
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _selected = null;
        BuildGrid();
        BuildEvents();
        _ = LoadAsync();
    }

    // ---------- month grid ----------
    private void BuildGrid()
    {
        _monthTitle.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _days.Children.Clear();
        var start = GridStart();
        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            bool today = day == DateTime.Today;
            bool sel = _selected == day;
            bool inMonth = day.Month == _month.Month;
            bool hasEvents = _all.Any(e => e.Covers(day));

            var num = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.CurrentCulture), FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                Opacity = inMonth ? 1 : 0.35,
            };
            if (today) num.Foreground = Brushes.White;

            var circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = num };
            if (today) circle.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            else if (sel) circle.SetResourceReference(Border.BackgroundProperty, "Brush.TileIcon");

            var dot = new Border
            {
                Width = 4, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0),
                Visibility = hasEvents ? Visibility.Visible : Visibility.Hidden, Opacity = inMonth ? 0.8 : 0.4,
            };
            dot.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");

            var cell = new Grid { Height = 34, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            circle.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(circle);
            Grid.SetRow(dot, 1);
            cell.Children.Add(dot);
            var captured = day;
            cell.MouseLeftButtonUp += (_, _) =>
            {
                _selected = _selected == captured ? null : captured;
                if (captured.Month != _month.Month) { _month = new DateTime(captured.Year, captured.Month, 1); _ = LoadAsync(); }
                BuildGrid();
                BuildEvents();
            };
            _days.Children.Add(cell);
        }
    }

    // ---------- event list ----------
    private void BuildEvents()
    {
        _events.Children.Clear();

        if (_addingEvent)
        {
            _listTitle.Text = "NEW EVENT";
            _events.Children.Add(EventForm());
            return;
        }

        if (_adding)
        {
            _listTitle.Text = "ADD A CALENDAR";
            _events.Children.Add(AddPanel());
            return;
        }

        if (_selected is DateTime sel)
        {
            _listTitle.Text = sel == DateTime.Today ? "TODAY" : sel.ToString("dddd, d MMMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
            var items = _all.Where(e => e.Covers(sel)).ToList();
            if (!Report(items.Count == 0 ? "No events" : null)) { foreach (var e in items) _events.Children.Add(EventRow(e)); AddAccountRow(); }
            return;
        }

        _listTitle.Text = "UPCOMING";
        var now = DateTime.Now;
        var end = DateTime.Today.AddDays(UpcomingDays);
        var upcoming = _all.Where(e => e.End > now && e.Start < end).ToList();
        if (Report(upcoming.Count == 0 ? "No upcoming events in the next " + UpcomingDays + " days" : null)) return;

        DateTime? lastDay = null;
        foreach (var e in upcoming)
        {
            var day = e.Start < DateTime.Today ? DateTime.Today : e.Start.Date; // already-running multi-day events sit under today
            if (lastDay != day)
            {
                lastDay = day;
                string label = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(1) ? "Tomorrow"
                    : day.ToString("dddd, d MMM", CultureInfo.CurrentCulture);
                _events.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            }
            _events.Children.Add(EventRow(e));
        }
        AddAccountRow();
    }

    /// <summary>Shows a status line instead of events: loading, an error, or an empty message. Returns true when it did.</summary>
    private bool Report(string empty)
    {
        if (!_loaded)
        {
            _events.Children.Add(new TextBlock { Text = "Loading…", FontSize = 13, Opacity = 0.6 });
            return true;
        }
        if (_error != null)
        {
            _events.Children.Add(new TextBlock { Text = _error, FontSize = 12.5, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            _events.Children.Add(new Border { Height = 8 });
        }
        if (empty != null)
        {
            _events.Children.Add(new TextBlock { Text = empty, FontSize = 13, Opacity = 0.6 });
            AddAccountRow();
            return true;
        }
        if (_error != null) return true;
        return false;
    }

    private bool _adding;
    private string _addMessage;
    private string _linkDraft = "";

    /// <summary>Add-a-calendar prompt when none is linked; the linked calendars (removable) and an "Add calendar" link otherwise.</summary>
    private void AddAccountRow()
    {
        _events.Children.Add(new Border { Height = 10 });
        if (_adding) { _events.Children.Add(AddPanel()); return; }

        if (!ICalFeeds.Any)
        {
            _events.Children.Add(new TextBlock
            {
                Text = "Use “+ Add calendar” above to connect your Google Calendar.", FontSize = 12.5, Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var f in ICalFeeds.Feeds.ToList())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = f.Name, FontSize = 11.5, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var remove = TextButton("Remove", () =>
            {
                ICalFeeds.Remove(f);
                _all = new(); _error = null; _selected = null;
                BuildGrid(); BuildEvents();
                _ = LoadAsync();
            });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _events.Children.Add(row);
        }
    }

    // ---------- new event ----------
    // Google's secret iCal link is read-only, so a new event is drafted here and finished in Google Calendar
    // (its "create event" page opens pre-filled; one click on Save there adds it).
    private bool _addingEvent;
    private string _evTitle = "", _evLocation = "", _evStart = "", _evEnd = "", _evMsg;
    private DateTime _evDate = DateTime.Today;
    private bool _evAllDay;

    private void StartAddingEvent()
    {
        _adding = false;
        _addingEvent = true;
        _evMsg = null;
        _evTitle = ""; _evLocation = "";
        _evDate = _selected ?? DateTime.Today;
        var next = DateTime.Now.AddHours(1);
        var t = new DateTime(next.Year, next.Month, next.Day, next.Hour, 0, 0);
        _evStart = FormatTime(t);
        _evEnd = FormatTime(t.AddHours(1));
        _evAllDay = false;
        BuildEvents();
    }

    private static TextBox Field(string text, Action<string> changed, double width = double.NaN)
    {
        var box = new TextBox
        {
            Text = text, FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, Width = width,
        };
        box.SetResourceReference(Control.BackgroundProperty, "Brush.TileIcon");
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Brush.Text");
        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    private UIElement EventForm()
    {
        var panel = new StackPanel();

        var title = Field(_evTitle, v => _evTitle = v);
        title.Margin = new Thickness(0, 0, 0, 6);
        title.ToolTip = "Title";
        panel.Children.Add(Label("Title"));
        panel.Children.Add(title);

        // Date with previous/next day
        var dateRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dateText = new TextBlock
        {
            Text = _evDate.ToString("ddd, d MMMM yyyy", CultureInfo.CurrentCulture), FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var prev = NavButton("", () => { _evDate = _evDate.AddDays(-1); dateText.Text = _evDate.ToString("ddd, d MMMM yyyy", CultureInfo.CurrentCulture); });
        var next = NavButton("", () => { _evDate = _evDate.AddDays(1); dateText.Text = _evDate.ToString("ddd, d MMMM yyyy", CultureInfo.CurrentCulture); });
        dateRow.Children.Add(prev);
        Grid.SetColumn(dateText, 1); dateRow.Children.Add(dateText);
        Grid.SetColumn(next, 2); dateRow.Children.Add(next);
        panel.Children.Add(Label("Date"));
        panel.Children.Add(dateRow);

        // Times
        var times = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var start = Field(_evStart, v => _evStart = v);
        var end = Field(_evEnd, v => _evEnd = v);
        Grid.SetColumn(end, 2);
        times.Children.Add(start);
        times.Children.Add(end);
        var allDay = new CheckBox { Content = "All day", IsChecked = _evAllDay, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        allDay.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        allDay.Checked += (_, _) => { _evAllDay = true; times.Visibility = Visibility.Collapsed; timesLabel.Visibility = Visibility.Collapsed; };
        allDay.Unchecked += (_, _) => { _evAllDay = false; times.Visibility = Visibility.Visible; timesLabel.Visibility = Visibility.Visible; };
        panel.Children.Add(allDay);
        timesLabel = Label("Starts · Ends");
        panel.Children.Add(timesLabel);
        panel.Children.Add(times);
        if (_evAllDay) { times.Visibility = Visibility.Collapsed; timesLabel.Visibility = Visibility.Collapsed; }

        var loc = Field(_evLocation, v => _evLocation = v);
        loc.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(Label("Location (optional)"));
        panel.Children.Add(loc);

        if (_evMsg != null)
            panel.Children.Add(new TextBlock { Text = _evMsg, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var create = AccentButton("Add to Google Calendar", () => { CreateEvent(); return Task.CompletedTask; });
        var cancel = TextButton("Cancel", () => { _addingEvent = false; BuildEvents(); });
        cancel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(cancel, 1);
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        return panel;
    }

    private FrameworkElement timesLabel;

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 0, 0, 3) };

    private void CreateEvent()
    {
        if (string.IsNullOrWhiteSpace(_evTitle)) { _evMsg = "Give the event a title."; BuildEvents(); return; }

        string dates;
        if (_evAllDay)
            dates = _evDate.ToString("yyyyMMdd") + "/" + _evDate.AddDays(1).ToString("yyyyMMdd"); // end is exclusive
        else
        {
            if (!DateTime.TryParse(_evStart, CultureInfo.CurrentCulture, out var s) || !DateTime.TryParse(_evEnd, CultureInfo.CurrentCulture, out var e))
            { _evMsg = "Couldn't read the times. Use something like 9:30 AM or 14:00."; BuildEvents(); return; }
            var from = _evDate.Date + s.TimeOfDay;
            var to = _evDate.Date + e.TimeOfDay;
            if (to <= from) to = to.AddDays(1) > from && e.TimeOfDay < s.TimeOfDay ? to.AddDays(1) : from.AddHours(1);
            dates = from.ToString("yyyyMMdd'T'HHmmss") + "/" + to.ToString("yyyyMMdd'T'HHmmss");
        }

        string url = "https://calendar.google.com/calendar/render?action=TEMPLATE&text=" + Uri.EscapeDataString(_evTitle.Trim()) +
                     "&dates=" + dates;
        if (!string.IsNullOrWhiteSpace(_evLocation)) url += "&location=" + Uri.EscapeDataString(_evLocation.Trim());
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana)) url += "&ctz=" + Uri.EscapeDataString(iana);

        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        _addingEvent = false;
        CloseMenu();
    }

    private void StartAdding()
    {
        _addingEvent = false;
        _adding = true;
        _addMessage = null;
        try
        {
            string clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            _linkDraft = ICalFeeds.LooksLikeFeed(clip) ? clip : "";
        }
        catch { _linkDraft = ""; }
        BuildEvents();
    }

    private UIElement AddPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Add a calendar", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

        var steps = new TextBlock
        {
            Text = "1. Open Google Calendar settings and pick your calendar." + Environment.NewLine +
                   "2. Under “Integrate calendar”, copy the “Secret address in iCal format”." + Environment.NewLine +
                   "3. Paste it below.",
            FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(steps);

        var open = TextButton("Open Google Calendar settings", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://calendar.google.com/calendar/u/0/r/settings") { UseShellExecute = true }); } catch { }
        });
        open.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(open);

        var box = new TextBox
        {
            Text = _linkDraft, FontSize = 12, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(Control.BackgroundProperty, "Brush.TileIcon");
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Brush.Text");
        box.TextChanged += (_, _) => _linkDraft = box.Text;
        panel.Children.Add(box);

        if (_addMessage != null)
            panel.Children.Add(new TextBlock { Text = _addMessage, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var add = AccentButton("Add", async () =>
        {
            _addMessage = "Checking the link…";
            BuildEvents();
            string err = await ICalFeeds.AddAsync(_linkDraft);
            if (err != null) { _addMessage = err; BuildEvents(); return; }
            _adding = false; _addMessage = null; _linkDraft = "";
            _loaded = false;
            BuildEvents();
            await LoadAsync();
        });
        var cancel = TextButton("Cancel", () => { _adding = false; BuildEvents(); });
        Grid.SetColumn(cancel, 1);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(add);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        return panel;
    }

    private UIElement EventRow(CalendarEvent e)
    {
        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 10, 1) };
        bar.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");

        string time = e.AllDay ? "All day"
            : FormatTime(e.Start) + " – " + FormatTime(e.End);
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = e.Subject, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = time, FontSize = 11.5, Opacity = 0.65 });
        if (!string.IsNullOrWhiteSpace(e.Location))
            texts.Children.Add(new TextBlock { Text = e.Location.Trim(), FontSize = 11.5, Opacity = 0.5, TextTrimming = TextTrimming.CharacterEllipsis });

        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(bar);
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        return g;
    }

    private static string FormatTime(DateTime t) =>
        SettingsStore.Current.Clock24Hour ? t.ToString("HH:mm", CultureInfo.CurrentCulture) : t.ToString("h:mm tt", CultureInfo.CurrentCulture);

    // ---------- small buttons ----------
    private static Button NavButton(string glyph, Action click)
    {
        var tb = new TextBlock { Text = glyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        var b = Flat(tb, 28);
        b.Click += (_, _) => click();
        return b;
    }

    private static Button TextButton(string text, Action click)
    {
        var b = Flat(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 0);
        b.Padding = new Thickness(10, 0, 10, 0);
        b.Click += (_, _) => click();
        return b;
    }

    private static Button AccentButton(string text, Func<Task> click)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };

        // Full-width accent pill: the button's own template paints the background, so it fills whatever column it is in.
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88, "Bd"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7, "Bd"));
        template.Triggers.Add(pressed);

        var b = new Button
        {
            Content = t, Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Focusable = false, Template = template,
        };
        b.Click += async (_, _) => { try { await click(); } catch { } };
        return b;
    }

    private static Button Flat(UIElement content, double width)
    {
        var b = new Button
        {
            Content = content, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Focusable = false, Template = FlatTemplate(),
        };
        if (width > 0) b.Width = width;
        b.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        return b;
    }

    private static ControlTemplate FlatTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.Name = "Bd";
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)), "Bd"));
        t.Triggers.Add(hover);
        return t;
    }
}
