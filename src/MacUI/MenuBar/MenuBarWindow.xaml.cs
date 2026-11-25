using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MacUI.Core;
using MacUI.Native;
using MacUI.Shell;
using MacUI.Themes;
using Forms = System.Windows.Forms;
using K = MacUI.Shell.Keys;

namespace MacUI.MenuBar;

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
        _appBar.Attach();

        WindowTracker.Instance.ForegroundChanged += OnForegroundChanged;
        OnForegroundChanged();
        UpdateClock();
        UpdateStatus();
        _clockTimer.Start();
        _statusTimer.Start();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        WindowTracker.Instance.ForegroundChanged -= OnForegroundChanged;
        _clockTimer.Stop();
        _statusTimer.Stop();
        _appBar?.Dispose();
    }

    private void CheckFullScreen()
    {
        bool full = WindowTracker.Instance.IsFullScreenOn(MonitorRect);
        if (full && IsVisible) Hide();
        else if (!full && !IsVisible) { Show(); _appBar?.UpdatePosition(); }
    }

    // ===================== foreground app =====================
    private void OnForegroundChanged()
    {
        var app = WindowTracker.Instance.ForegroundApp;
        AppNameText.Text = app?.Name ?? "Desktop";
        MenuTitles.Visibility = app != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== clock & status =====================
    private void UpdateClock()
    {
        var s = SettingsStore.Current;
        var now = DateTime.Now;
        string time = s.Clock24Hour
            ? now.ToString(s.ClockShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture)
            : now.ToString(s.ClockShowSeconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.CurrentCulture);
        ClockText.Text = s.ClockShowDate ? now.ToString("ddd d MMM", CultureInfo.CurrentCulture) + "   " + time : time;
    }

    private async void UpdateStatus()
    {
        var s = SettingsStore.Current;

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

        // Volume
        float v = AudioVolume.GetVolume();
        bool mute = AudioVolume.GetMute();
        VolumeButton.Visibility = v < 0 ? Visibility.Collapsed : Visibility.Visible;
        VolumeGlyph.Text = mute ? "" : v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
        VolumeButton.ToolTip = mute ? "Muted" : $"Volume {(int)Math.Round(v * 100)}%";

        // Now playing
        var media = await SystemStatus.GetMediaAsync();
        if (media != null && !string.IsNullOrWhiteSpace(media.Title))
        {
            MediaButton.Visibility = Visibility.Visible;
            MediaGlyph.Text = media.Playing ? "" : "";
            MediaText.Text = string.IsNullOrWhiteSpace(media.Artist) ? media.Title : $"{media.Title} — {media.Artist}";
        }
        else MediaButton.Visibility = Visibility.Collapsed;
    }

    // ===================== status item clicks =====================
    private void Clock_Click(object sender, RoutedEventArgs e) => Win32.PressKeys(Win32.VK_LWIN, (byte)'N'); // Notification center
    private void Battery_Click(object sender, RoutedEventArgs e) => Launch.Settings("batterysaver");
    private async void Wifi_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var p = WifiButton.PointToScreen(new Point(0, ActualHeight));
        await NetworkMenus.ShowWifiMenuAsync((int)p.X - 2, (int)p.Y + 2);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => App.Instance.ToggleLauncher();
    private void ControlCenter_Click(object sender, RoutedEventArgs e) => App.Instance.ToggleControlCenter(_screen);

    private async void Media_Click(object sender, RoutedEventArgs e)
    {
        await SystemStatus.MediaPlayPauseAsync();
        await Task.Delay(300);
        UpdateStatus();
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
                Item("Mission Control", MacUI.Shell.Features.MissionControl, "Win+Tab"),
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
