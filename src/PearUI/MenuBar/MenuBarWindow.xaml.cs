using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Native;
using PearUI.Shell;
using PearUI.Themes;
using Forms = System.Windows.Forms;
using K = PearUI.Shell.Keys;

namespace PearUI.MenuBar;

/// <summary>The top menu bar (one per display).</summary>
public partial class MenuBarWindow : OverlayWindow
{
    public const double BarHeight = 28;

    private readonly Forms.Screen _screen;
    private AppBar _appBar;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _statusTimer;
    private GlassMenu _openMenu;
    private Button _openButton;
    private IntPtr _menuTarget;
    private readonly List<Button> _titleButtons = new();

    public Forms.Screen Screen => _screen;

    public MenuBarWindow(Forms.Screen screen) : base(SurfaceKind.Bar, noActivate: true)
    {
        _screen = screen;
        InitializeComponent();
        Height = BarHeight;
        LogoPath.Data = PearLogo.CreateGeometry();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        BuildMenuTitles();
        LogoButton.MouseEnter += Title_MouseEnter;
        AppNameButton.MouseEnter += Title_MouseEnter;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private Win32.RECT MonitorRect => new(_screen.Bounds.Left, _screen.Bounds.Top, _screen.Bounds.Right, _screen.Bounds.Bottom);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _appBar = new AppBar(this, Win32.ABE_TOP, () => MonitorRect,
            () => (int)Math.Ceiling(BarHeight * Win32.GetScaleForScreen(_screen)));
        _appBar.FullScreenChanged += _ => CheckFullScreen();
        if (SettingsStore.Current.MenuBarAutoHide)
        {
            // Auto-hide: don't reserve screen space, and place the window through the slide position below.
            _appBar.ReserveSpace = false;
            _appBar.PositionWindow = rc => Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, rc.Left,
                rc.Top - (int)Math.Round(rc.Height * (1 - _slide)), rc.Width, rc.Height, Win32.SWP_NOACTIVATE);
            _slide = 1;
            _edgeTimer.Tick += (_, _) => EdgeTick();
            _edgeTimer.Start();
        }
        _appBar.Attach();
        ClipboardHistory.Instance.Start();

        WindowTracker.Instance.ForegroundChanged += OnForegroundChanged;
        WindowTracker.Instance.ForegroundChanged += ReassertOnTop;
        OnForegroundChanged();
        UpdateClock();
        UpdateStatus();
        _clockTimer.Start();
        _statusTimer.Start();
        SystemStatus.MediaChanged += OnMediaChanged;
        _ = SystemStatus.InitMediaAsync();
        BackgroundAppService.Instance.Changed += OnBackgroundChanged;
        BackgroundAppService.Instance.Start();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        WindowTracker.Instance.ForegroundChanged -= OnForegroundChanged;
        WindowTracker.Instance.ForegroundChanged -= ReassertOnTop;
        _clockTimer.Stop();
        _statusTimer.Stop();
        _edgeTimer.Stop();
        CompositionTarget.Rendering -= OnSlideFrame;
        SystemStatus.MediaChanged -= OnMediaChanged;
        BackgroundAppService.Instance.Changed -= OnBackgroundChanged;
        _appBar?.Dispose();
    }

    /// <summary>
    /// "Show desktop" (Win+D) and similar make Windows treat the desktop as a full-screen window and push app bars to the
    /// bottom of the stacking order, which hides the bar behind the wallpaper. Put it back on top (a few times, because
    /// Windows does this a moment after the notification).
    /// </summary>
    private void ReassertOnTop()
    {
        foreach (int delay in new[] { 0, 80, 300, 800 })
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay)) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!IsVisible || _autoHidden) return;
                Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            };
            t.Start();
        }
    }

    private void CheckFullScreen()
    {
        ReassertOnTop();
        bool full = WindowTracker.Instance.IsFullScreenOn(MonitorRect);
        if (full && IsVisible) Hide();
        else if (!full && !IsVisible && !_autoHidden) { Show(); _appBar?.UpdatePosition(); }
    }

    // ===================== auto-hide =====================
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _slide = 1;            // 1 = fully shown, 0 = fully tucked above the screen
    private bool _autoHidden;             // true while hidden by auto-hide
    private DateTime _lastNeeded = DateTime.Now, _edgeSince = DateTime.MaxValue;
    private double _slideFrom, _slideTo;
    private DateTime _slideStart;
    private bool _sliding;

    private void EdgeTick()
    {
        var mon = _screen.Bounds;
        Win32.GetCursorPos(out var p);
        bool onScreen = p.X >= mon.Left && p.X < mon.Right && p.Y >= mon.Top && p.Y < mon.Bottom;
        int barPx = (int)Math.Ceiling(BarHeight * Win32.GetScaleForScreen(_screen));
        bool atEdge = onScreen && p.Y <= mon.Top + 1;
        bool overBar = onScreen && p.Y < mon.Top + barPx + 4;
        bool busy = _openMenu != null || _popup != null || App.Instance.ControlCenterOpen || Mouse.LeftButton == MouseButtonState.Pressed;

        if (_autoHidden)
        {
            // A short touch of the top edge brings it back (not just brushing past it).
            if (atEdge) { if (_edgeSince == DateTime.MaxValue) _edgeSince = DateTime.Now; }
            else _edgeSince = DateTime.MaxValue;
            if (_edgeSince != DateTime.MaxValue && (DateTime.Now - _edgeSince).TotalMilliseconds > 120 && !GameFullScreen())
                SetAutoHidden(false);
        }
        else
        {
            if (overBar || busy) _lastNeeded = DateTime.Now;
            else if ((DateTime.Now - _lastNeeded).TotalMilliseconds > 700) SetAutoHidden(true);
        }
    }

    /// <summary>
    /// With the menu bar not reserving space, a maximized app covers the whole monitor, which the usual full-screen
    /// check would mistake for a game. Only a frameless window the size of the monitor counts here.
    /// </summary>
    private bool GameFullScreen()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Win32.GetShellWindow()) return false;
        long style = Win32.GetWindowLongPtr(fg, Win32.GWL_STYLE).ToInt64();
        if ((style & 0x00C00000L) != 0 || (style & 0x00040000L) != 0) return false; // title bar or resize frame: a normal window
        Win32.GetWindowRect(fg, out var r);
        var m = MonitorRect;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private void SetAutoHidden(bool hide)
    {
        if (_autoHidden == hide) return;
        _autoHidden = hide;
        _edgeSince = DateTime.MaxValue;
        _lastNeeded = DateTime.Now;
        if (!hide && !IsVisible) Show();
        _slideFrom = _slide;
        _slideTo = hide ? 0 : 1;
        _slideStart = DateTime.Now;
        if (!_sliding) { _sliding = true; CompositionTarget.Rendering += OnSlideFrame; }
    }

    private void OnSlideFrame(object sender, EventArgs e)
    {
        double ms = _slideTo == 0 ? 260 : 200;
        double t = Math.Clamp((DateTime.Now - _slideStart).TotalMilliseconds / ms, 0, 1);
        double eased = _slideTo == 0 ? t * t * t : 1 - Math.Pow(1 - t, 3); // ease in to hide, ease out to show
        _slide = _slideFrom + (_slideTo - _slideFrom) * eased;
        _appBar?.UpdatePosition();
        if (t >= 1)
        {
            _sliding = false;
            CompositionTarget.Rendering -= OnSlideFrame;
            if (_autoHidden) Hide(); // fully tucked away: nothing left to catch clicks at the top edge
        }
    }

    // ===================== foreground app =====================
    private void OnForegroundChanged()
    {
        var app = WindowTracker.Instance.ForegroundApp;
        AppNameText.Text = app?.Name ?? "Desktop";
        MenuTitles.Visibility = app != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== clock & status =====================
    private string _clockTemplate;
    private string _widestDigit;

    private void UpdateClock()
    {
        var s = SettingsStore.Current;
        var now = DateTime.Now;
        string time = s.Clock24Hour
            ? now.ToString(s.ClockShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture)
            : now.ToString(s.ClockShowSeconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.CurrentCulture);
        ClockText.Text = s.ClockShowDate ? now.ToString("ddd d MMM", CultureInfo.CurrentCulture) + "   " + time : time;
        ReserveClockWidth(s, now);
    }

    /// <summary>
    /// Digits differ in width, so a ticking clock would nudge every item to its left on each change. Reserve the
    /// width of the widest possible reading (two-digit day and hour, widest digit everywhere) and right-align the text.
    /// </summary>
    private void ReserveClockWidth(AppSettings s, DateTime now)
    {
        string time = s.Clock24Hour
            ? now.ToString(s.ClockShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture)
            : now.ToString(s.ClockShowSeconds ? "hh:mm:ss tt" : "hh:mm tt", CultureInfo.CurrentCulture);
        string text = s.ClockShowDate ? now.ToString("ddd dd MMM", CultureInfo.CurrentCulture) + "   " + time : time;

        _widestDigit ??= WidestDigit();
        string template = System.Text.RegularExpressions.Regex.Replace(text, "[0-9]", _widestDigit);
        if (template == _clockTemplate) return;
        _clockTemplate = template;

        var ft = new FormattedText(template, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(ClockText.FontFamily, ClockText.FontStyle, ClockText.FontWeight, ClockText.FontStretch),
            ClockText.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        ClockText.TextAlignment = TextAlignment.Right;
        ClockText.MinWidth = Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
    }

    private string WidestDigit()
    {
        string best = "0";
        double bestWidth = 0;
        for (char c = '0'; c <= '9'; c++)
        {
            var ft = new FormattedText(new string(c, 8), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(ClockText.FontFamily, ClockText.FontStyle, ClockText.FontWeight, ClockText.FontStretch),
                ClockText.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (ft.WidthIncludingTrailingWhitespace > bestWidth) { bestWidth = ft.WidthIncludingTrailingWhitespace; best = c.ToString(); }
        }
        return best;
    }

    private async void UpdateStatus()
    {
        var s = SettingsStore.Current;
        ClipboardButton.Visibility = s.ClipboardHistory ? Visibility.Visible : Visibility.Collapsed;

        // Battery
        var bat = SystemStatus.GetBattery();
        BatteryButton.Visibility = bat.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (bat.HasBattery)
        {
            int idx = Math.Clamp(bat.Percent / 10, 0, 10);
            BatteryGlyph.Text = ((char)((bat.Charging || bat.PluggedIn ? 0xEBAB : 0xEBA0) + idx)).ToString();
            BatteryPercent.Text = s.ShowBatteryPercent ? bat.Percent + "%" : "";
            BatteryPercent.Visibility = s.ShowBatteryPercent ? Visibility.Visible : Visibility.Collapsed;
            BatteryButton.ToolTip = $"Battery {bat.Percent}%" + (bat.Charging ? " (charging)" : bat.PluggedIn ? " (plugged in)" : "");
        }

        // Network
        var net = SystemStatus.GetNetwork();
        WifiGlyph.Text = net.Connected && !net.IsWifi ? "" : "";
        WifiGlyph.Opacity = net.Connected ? 1 : 0.35;
        WifiButton.ToolTip = net.Connected ? (net.Name ?? "Connected") : "Not connected";

        // Bluetooth (only shown while the radio is on)
        BluetoothButton.Visibility = await SystemStatus.GetRadioStateAsync(Windows.Devices.Radios.RadioKind.Bluetooth) == true
            ? Visibility.Visible : Visibility.Collapsed;

        // Volume
        float v = AudioVolume.GetVolume();
        bool mute = AudioVolume.GetMute();
        VolumeButton.Visibility = v < 0 ? Visibility.Collapsed : Visibility.Visible;
        VolumeGlyph.Text = mute ? "" : v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
        VolumeButton.ToolTip = mute ? "Muted" : $"Volume {(int)Math.Round(v * 100)}%";

        await UpdateMediaAsync();
    }

    /// <summary>Now-playing item; also runs the instant a track or play state changes.</summary>
    private async Task UpdateMediaAsync()
    {
        var media = await SystemStatus.GetMediaAsync();
        if (media != null && !string.IsNullOrWhiteSpace(media.Title))
        {
            MediaButton.Visibility = Visibility.Visible;
            MediaGlyph.Text = media.Playing ? "" : "";
            MediaText.Text = string.IsNullOrWhiteSpace(media.Artist) ? media.Title : $"{media.Title} — {media.Artist}";
        }
        else MediaButton.Visibility = Visibility.Collapsed;
    }

    private void OnMediaChanged() => Dispatcher.BeginInvoke(async () => { try { await UpdateMediaAsync(); } catch { } });

    // ===================== status item clicks =====================
    private void Clock_Click(object sender, RoutedEventArgs e) => Win32.PressKeys(Win32.VK_LWIN, (byte)'N'); // Notification center
    private void Battery_Click(object sender, RoutedEventArgs e) => Launch.Settings("batterysaver");
    private async void Wifi_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await ShowWifiAsync();
    }

    private async void Wifi_Click(object sender, RoutedEventArgs e) => await ShowWifiAsync();

    private async Task ShowWifiAsync()
    {
        _popup?.CloseMenu();
        var p = WifiButton.PointToScreen(new Point(0, ActualHeight));
        await NetworkMenus.ShowWifiMenuAsync((int)p.X - 2, (int)p.Y + 2);
    }

    private async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        _popup?.CloseMenu();
        var p = BluetoothButton.PointToScreen(new Point(0, ActualHeight));
        await NetworkMenus.ShowBluetoothMenuAsync((int)p.X - 2, (int)p.Y + 2);
    }

    private void Volume_Click(object sender, RoutedEventArgs e) => ToggleBarPopup(VolumeButton, () => new VolumePopup());

    private void Search_Click(object sender, RoutedEventArgs e) => App.Instance.ToggleLauncher();
    private void ControlCenter_Click(object sender, RoutedEventArgs e) => App.Instance.ToggleControlCenter(_screen);

    private BarPopup _popup;
    private Button _popupOwner;

    private void ToggleBarPopup(Button owner, Func<BarPopup> create)
    {
        if (_popup != null)
        {
            bool same = _popupOwner == owner;
            _popup.CloseMenu();
            if (same) return;
        }
        if (_openMenu != null) _openMenu.CloseMenu();

        var popup = create();
        popup.PopupClosed += () =>
        {
            owner.Tag = null;
            if (_popup == popup) { _popup = null; _popupOwner = null; }
            UpdateStatus();
        };
        owner.Tag = "Open";
        _popup = popup;
        _popupOwner = owner;
        var pt = owner.PointToScreen(new Point(0, ActualHeight));
        popup.ShowAt((int)pt.X - 2, (int)pt.Y + 4);
    }

    // ---------- background apps: the arrow button opens the flyout; its tooltip shows how many there are ----------
    private void OnBackgroundChanged() => Dispatcher.BeginInvoke(() =>
    {
        int n = BackgroundAppService.Instance.Apps.Count;
        BackgroundButton.ToolTip = n == 0 ? "Background apps" : $"Background apps ({n})";
    });

    private void Background_Click(object sender, RoutedEventArgs e) => ToggleBarPopup(BackgroundButton, () => new BackgroundAppsPopup());

    private void Media_Click(object sender, RoutedEventArgs e) => ToggleBarPopup(MediaButton, () => new MediaPopup());

    private void Clipboard_Click(object sender, RoutedEventArgs e)
    {
        var target = WindowTracker.Instance.ForegroundWindow; // where "paste" should go
        ToggleBarPopup(ClipboardButton, () => new ClipboardPopup(target));
    }

    private void Volume_Wheel(object sender, MouseWheelEventArgs e)
    {
        float v = AudioVolume.GetVolume();
        if (v < 0) return;
        AudioVolume.SetVolume(v + (e.Delta > 0 ? 0.05f : -0.05f));
        UpdateStatus();
    }

    // ===================== menus =====================
    private static readonly string[] Titles = { "File", "Edit", "View", "Window", "Help" };

    private void BuildMenuTitles()
    {
        foreach (var title in Titles)
        {
            var b = new Button
            {
                Style = (Style)FindResource("BarButton"),
                Content = new TextBlock { Text = title, FontSize = 13 },
                Tag = null,
            };
            b.Click += (_, _) => OpenMenu(b, BuildMenu(title));
            b.MouseEnter += Title_MouseEnter;
            b.DataContext = title;
            _titleButtons.Add(b);
            MenuTitles.Items.Add(b);
        }
    }

    private void Logo_Click(object sender, RoutedEventArgs e) => OpenMenu(LogoButton, BuildLogoMenu());
    private void AppMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(AppNameButton, BuildAppMenu());

    private void OpenMenu(Button owner, List<GlassMenu.Entry> entries)
    {
        if (_openMenu != null)
        {
            bool same = _openButton == owner;
            _openMenu.CloseMenu();
            if (same) return;
        }

        _menuTarget = WindowTracker.Instance.ForegroundWindow;
        var menu = new GlassMenu(entries);
        menu.MenuClosed += () =>
        {
            owner.Tag = null;
            if (_openMenu == menu) { _openMenu = null; _openButton = null; }
        };
        owner.Tag = "Open";
        _openMenu = menu;
        _openButton = owner;
        var pt = owner.PointToScreen(new Point(0, ActualHeight)); // device pixels, just below the bar
        menu.ShowAt((int)pt.X - 2, (int)pt.Y + 2);
    }

    /// <summary>macOS behaviour: while a menu is open, sliding over another title opens that menu.</summary>
    private void Title_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_openMenu == null || sender is not Button b || b == _openButton) return;
        var target = _menuTarget;
        if (b == LogoButton) OpenMenu(b, BuildLogoMenu());
        else if (b == AppNameButton) OpenMenu(b, BuildAppMenu());
        else OpenMenu(b, BuildMenu((string)b.DataContext));
        _menuTarget = target;
    }

    private static GlassMenu.Entry Item(string header, Action action, string gesture = null, bool enabled = true, bool isChecked = false) =>
        new(header, gesture, action, enabled, isChecked);

    /// <summary>null entries become separators.</summary>
    private static List<GlassMenu.Entry> Menu(params GlassMenu.Entry[] items) => items.ToList();

    private List<GlassMenu.Entry> BuildLogoMenu() => Menu(
        Item("About This PC", () => Launch.Settings("about")),
        null,
        Item("System Settings…", () => Launch.Settings()),
        Item("All Apps", () => App.Instance.ToggleAllApps(), SettingsStore.Current.AllAppsHotkey),
        Item("Microsoft Store…", () => Launch.Open("ms-windows-store:")),
        Item("Terminal", () => Launch.Terminal(), "Win+T"),
        null,
        Item("Recent Files", () => Launch.Open(@"shell:recent")),
        null,
        Item("Force Quit…", () => Launch.Open("taskmgr.exe"), "Ctrl+Shift+Esc"),
        null,
        Item("Sleep", Launch.Sleep),
        Item("Restart…", Launch.Restart),
        Item("Shut Down…", Launch.ShutDown),
        null,
        Item("Lock Screen", Launch.Lock, "Win+L"),
        Item($"Log Out {Environment.UserName}…", Launch.LogOut),
        null,
        Item("Welcome to PearUI…", () => App.Instance.ShowOnboarding()),
        Item("PearUI Settings…", () => App.Instance.ShowSettings()),
        Item("Check for Updates…", async () => Shell.ShortcutToast.Show("", await App.Instance.CheckForUpdates(manual: true))),
        Item("Quit PearUI", () => App.Instance.Quit()));

    private List<GlassMenu.Entry> BuildAppMenu()
    {
        var app = WindowTracker.Instance.ForegroundApp;
        if (app == null)
        {
            return Menu(
                Item("New File Explorer Window", () => Launch.Open("explorer.exe")),
                Item("Change Wallpaper…", () => Launch.Settings("personalization-background")),
                null,
                Item("Show Desktop", () => Win32.PressKeys(Win32.VK_LWIN, (byte)'D'), "Win+D"),
                null,
                Item("PearUI Settings…", () => App.Instance.ShowSettings()));
        }

        // Grab the full window list for this app (ForegroundApp only describes the app).
        var running = WindowTracker.Instance.Apps.FirstOrDefault(a => a.Key == app.Key) ?? app;
        return Menu(
            Item($"About {app.Name}", () => { if (app.ExePath != null) Launch.RevealInExplorer(app.ExePath); }, enabled: app.ExePath != null),
            null,
            Item("Keep in Dock", () => App.Instance.PinApp(running), isChecked: App.Instance.IsPinned(running)),
            null,
            Item($"Hide {app.Name}", () => { foreach (var w in running.Windows) WindowTracker.Minimize(w.Hwnd); }, "Win+↓"),
            Item("Hide Others", () => HideOthers(app.Key)),
            Item("Show All", ShowAll),
            null,
            Item($"Quit {app.Name}", () => WindowTracker.QuitApp(running), "Alt+F4"));
    }

    private List<GlassMenu.Entry> BuildMenu(string title)
    {
        IntPtr t() => _menuTarget;
        const byte C = K.Ctrl, S = K.Shift;
        return title switch
        {
            "File" => Menu(
                Item("New Window", () => K.SendTo(t(), C, (byte)'N'), "Ctrl+N"),
                Item("New Tab", () => K.SendTo(t(), C, (byte)'T'), "Ctrl+T"),
                Item("Open…", () => K.SendTo(t(), C, (byte)'O'), "Ctrl+O"),
                null,
                Item("Save", () => K.SendTo(t(), C, (byte)'S'), "Ctrl+S"),
                Item("Save As…", () => K.SendTo(t(), C, S, (byte)'S'), "Ctrl+Shift+S"),
                null,
                Item("Print…", () => K.SendTo(t(), C, (byte)'P'), "Ctrl+P"),
                null,
                Item("Close Window", () => WindowTracker.Close(t()), "Ctrl+W")),
            "Edit" => Menu(
                Item("Undo", () => K.SendTo(t(), C, (byte)'Z'), "Ctrl+Z"),
                Item("Redo", () => K.SendTo(t(), C, (byte)'Y'), "Ctrl+Y"),
                null,
                Item("Cut", () => K.SendTo(t(), C, (byte)'X'), "Ctrl+X"),
                Item("Copy", () => K.SendTo(t(), C, (byte)'C'), "Ctrl+C"),
                Item("Paste", () => K.SendTo(t(), C, (byte)'V'), "Ctrl+V"),
                Item("Select All", () => K.SendTo(t(), C, (byte)'A'), "Ctrl+A"),
                null,
                Item("Find…", () => K.SendTo(t(), C, (byte)'F'), "Ctrl+F"),
                null,
                Item("Emoji & Symbols", () => K.SendTo(t(), K.Win, 0xBE /*VK_OEM_PERIOD*/), "Win+.")),
            "View" => Menu(
                Item("Enter Full Screen", () => K.SendTo(t(), 0x7A /*F11*/), "F11"),
                null,
                Item("Zoom In", () => K.SendTo(t(), C, 0xBB /*VK_OEM_PLUS*/), "Ctrl++"),
                Item("Zoom Out", () => K.SendTo(t(), C, 0xBD /*VK_OEM_MINUS*/), "Ctrl+-"),
                Item("Actual Size", () => K.SendTo(t(), C, (byte)'0'), "Ctrl+0"),
                null,
                Item("Refresh", () => K.SendTo(t(), 0x74 /*F5*/), "F5")),
            "Window" => Menu(
                Item("Minimize", () => WindowTracker.Minimize(t()), "Win+↓"),
                Item("Zoom", () => WindowTracker.ToggleZoom(t()), "Win+↑"),
                null,
                Item("Tile Window to Left of Screen", () => K.SendTo(t(), K.Win, 0x25 /*Left*/), "Win+←"),
                Item("Tile Window to Right of Screen", () => K.SendTo(t(), K.Win, 0x27 /*Right*/), "Win+→"),
                Item("Snap Layouts…", () => K.SendTo(t(), K.Win, (byte)'Z'), "Win+Z"),
                null,
                Item("Mission Control", PearUI.Shell.Features.MissionControl, "Win+Tab"),
                Item("Stage Manager", () => StageManager.StageManager.Instance.Toggle(), isChecked: StageManager.StageManager.Instance.Enabled),
                Item("All Apps", () => App.Instance.ToggleAllApps(), SettingsStore.Current.AllAppsHotkey),
                null,
                Item("Bring All to Front", () =>
                {
                    var app = WindowTracker.Instance.ForegroundApp;
                    var running = app == null ? null : WindowTracker.Instance.Apps.FirstOrDefault(a => a.Key == app.Key);
                    if (running != null) foreach (var w in running.Windows.AsEnumerable().Reverse()) Win32.ActivateWindow(w.Hwnd);
                })),
            _ => Menu(
                Item("Search…", () => App.Instance.ToggleLauncher(), SettingsStore.Current.LauncherHotkey),
                null,
                Item($"{WindowTracker.Instance.ForegroundApp?.Name ?? "App"} Help", () => K.SendTo(t(), 0x70 /*F1*/), "F1"),
                Item("Windows Tips", () => Launch.Open("ms-get-started:")),
                Item("PearUI Settings…", () => App.Instance.ShowSettings())),
        };
    }

    private static void HideOthers(string keepKey)
    {
        foreach (var a in WindowTracker.Instance.Apps.Where(a => a.Key != keepKey))
            foreach (var w in a.Windows) WindowTracker.Minimize(w.Hwnd);
    }

    private static void ShowAll()
    {
        foreach (var a in WindowTracker.Instance.Apps)
            foreach (var w in a.Windows.Where(w => w.Minimized)) Win32.ShowWindowAsync(w.Hwnd, Win32.SW_SHOWNOACTIVATE);
    }
}
