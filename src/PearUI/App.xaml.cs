using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PearUI.ControlCenter;
using PearUI.Core;
using PearUI.Dock;
using PearUI.Launcher;
using PearUI.MenuBar;
using PearUI.Native;
using PearUI.Settings;
using PearUI.Themes;
using PearUI.WindowControls;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PearUI;

public partial class App : Application
{
    public static App Instance => (App)Current;

    private const string MutexName = "PearUI.SingleInstance.v1";
    private const string QuitEventName = "PearUI.Quit.v1";
    private const string ShowEventName = "PearUI.ShowSettings.v1";

    private Mutex _mutex;
    private EventWaitHandle _quitEvent, _showEvent;
    private HotkeyManager _hotkeys;
    private int _launcherHotkeyId;
    private Forms.NotifyIcon _tray;

    private readonly List<MenuBarWindow> _menuBars = new();
    private DockWindow _dock;
    private LauncherWindow _launcher;
    private ControlCenterWindow _controlCenter;
    private SettingsWindow _settings;
    private TrafficLightsWindow _trafficLights;
    private AllApps.AllAppsWindow _allApps;
    private Shell.HotCorners _hotCorners;
    private WindowFitter _windowFitter;
    private WindowTranslucency _translucency;
    private Shell.StartMenuBlocker _startBlocker;
    private WindowControls.CaptionButtonHider _captionHider;
    private int _allAppsHotkeyId;
    private bool _glassApplied;

    private AppSettings _applied;
    private DispatcherTimer _displayDebounce;
    private bool _quitting;

    public bool LauncherHotkeyOk { get; private set; } = true;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    protected override void OnStartup(StartupEventArgs e)
    {
        Motion.Init();   // animations at the monitor's refresh rate, not 60 fps
        base.OnStartup(e);
        // Started via "dotnet PearUI.dll": detach from (and thereby close) the host's console window.
        FreeConsole();
        // Used by install.ps1: save the pear logo as an .ico for the Start Menu shortcut, then exit.
        int iconArg = Array.FindIndex(e.Args, a => a.Equals("--write-icon", StringComparison.OrdinalIgnoreCase));
        if (iconArg >= 0 && iconArg + 1 < e.Args.Length)
        {
            try { WritePngIco(e.Args[iconArg + 1], new[] { 256, 128, 96, 64, 48, 40, 32, 24, 20, 16 }); }
            catch { }
            Shutdown();
            return;
        }

        // Used by install.ps1: macOS-style Windows Terminal + Command Prompt as default, then exit.
        if (e.Args.Any(a => a.Equals("--setup-terminal", StringComparison.OrdinalIgnoreCase)))
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pearui-terminal-setup.txt"), TerminalSetup.Apply()); } catch { }
            Shutdown();
            return;
        }

        // Used by the uninstaller: make sure the Windows taskbar is visible again, then exit.
        if (e.Args.Any(a => a.Equals("--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            try { TaskbarController.RestoreIfCrashed(); } catch { }
            Shutdown();
            return;
        }

        // "info" command of PearUI Terminal: print the system summary and exit.
        if (e.Args.Any(a => a.Equals("--info", StringComparison.OrdinalIgnoreCase)))
        {
            try { SystemInfo.Print(); } catch { }
            Shutdown();
            return;
        }

        // Elevated helper: apply service changes chosen in Settings → Performance, then exit.
        int si = Array.FindIndex(e.Args, a => a.Equals("--services", StringComparison.OrdinalIgnoreCase));
        int di = Array.FindIndex(e.Args, a => a.Equals("--debloat", StringComparison.OrdinalIgnoreCase));
        if (si >= 0 || di >= 0)
        {
            if (si >= 0 && si + 1 < e.Args.Length) { try { OptionalServices.ApplyFromArgs(e.Args[si + 1]); } catch { } }
            if (di >= 0 && di + 1 < e.Args.Length) { try { Debloat.ApplyFromArgs(e.Args[di + 1]); } catch { } }
            Shutdown();
            return;
        }

        bool quitRequested = e.Args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Another instance is running: forward the request and exit.
            try { EventWaitHandle.OpenExisting(quitRequested ? QuitEventName : ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        if (quitRequested) { Shutdown(); return; }

        // Get the shell up first while everything else is still loading at sign-in.
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High; } catch { }

        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => Dispatcher.BeginInvoke(Quit), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, -1, false);

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log(ex.ExceptionObject as Exception); Cleanup(); };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log(ex.Exception); ex.SetObserved(); };
        SessionEnding += (_, _) => Cleanup();

        TaskbarController.RestoreIfCrashed();
        SettingsStore.Load();
        ThemeManager.Initialize();
        WindowTracker.Instance.Start();
        _ = AppCatalog.Instance.LoadAsync();
        AppCatalog.Instance.Loaded += () => Dispatcher.BeginInvoke(() => WindowTracker.Instance.Refresh());
        // Warm every icon in the background so the Dock, All Apps and search show them instantly.
        ShellIcons.Prefetch(SettingsStore.Current.PinnedApps.Select(p => AppCatalog.Instance.IconFor(p.Target)), ShellIcons.PixelsFor(SettingsStore.Current.DockMagnifiedSize * 1.25));
        AppCatalog.Instance.Loaded += () => ShellIcons.Prefetch(AppCatalog.Instance.Apps.Select(a => a.IconSource), ShellIcons.PixelsFor(96));

        _hotkeys = new HotkeyManager();
        _glassApplied = Backdrop.BlurAvailable;
        CreatePopups();
        CreateTray();
        _hotCorners = new Shell.HotCorners();
        _windowFitter = new WindowFitter();
        _translucency = new WindowTranslucency();
        _captionHider = new WindowControls.CaptionButtonHider();

        SettingsStore.Changed += () => Dispatcher.BeginInvoke(() => ApplySettings(false));
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(OnDisplaysChanged);
        // Transparency effects toggled in Windows (or by battery saver): rebuild surfaces in the right mode.
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(CheckGlassMode);
        ApplySettings(true);
        if (SettingsStore.Current.StageManager) StageManager.StageManager.Instance.SetEnabled(true);

        // First start: greet the user once the menu bar and Dock are up.
        if (!SettingsStore.Current.OnboardingDone)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            t.Tick += (_, _) => { t.Stop(); if (!SettingsStore.Current.OnboardingDone) ShowOnboarding(); };
            t.Start();
        }

        StartUpdateChecks();

        // "PearUI.exe --terminal": also open the Terminal once everything is up.
        if (e.Args.Any(a => a.Equals("--terminal", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.BeginInvoke(() => Launch.Terminal(), DispatcherPriority.ApplicationIdle);
    }

    private void CreatePopups()
    {
        _launcher?.Close();
        _controlCenter?.Close();
        _allApps?.Close();
        _launcher = new LauncherWindow();
        _controlCenter = new ControlCenterWindow();
        _allApps = new AllApps.AllAppsWindow();
    }

    /// <summary>Glass windows and see-through fallback windows are different window types: recreate on change.</summary>
    private void CheckGlassMode()
    {
        bool glass = Backdrop.BlurAvailable;
        if (glass == _glassApplied || _quitting) return;
        _glassApplied = glass;
        ThemeManager.Apply();
        CreatePopups();
        RebuildMenuBars();
        RebuildDock();
        if (StageManager.StageManager.Instance.Enabled)
        {
            StageManager.StageManager.Instance.SetEnabled(false);
            StageManager.StageManager.Instance.SetEnabled(true);
        }
    }

    // ===================== settings =====================
    private void ApplySettings(bool initial)
    {
        var s = SettingsStore.Current;
        var a = _applied;
        bool changed(Func<AppSettings, object> f) => initial || !Equals(f(a), f(s));

        ThemeManager.Apply();
        if (!initial && changed(x => x.UseBlur)) { CheckGlassMode(); a = Clone(s); }

        if (changed(x => x.StageManager)) StageManager.StageManager.Instance.SetEnabled(s.StageManager);

        if (!initial && (changed(x => x.TranslucentWindows) || changed(x => x.WindowOpacity))) _translucency?.Apply();

        if (changed(x => x.ReplaceStartMenu))
        {
            _startBlocker?.Dispose();
            // Always on: it also serves the user's own shortcuts. The flag only says whether the Windows key opens All Apps.
            _startBlocker = new Shell.StartMenuBlocker(s.ReplaceStartMenu);
        }
        Shell.Shortcuts.Reload();

        if (changed(x => x.StartWithWindows)) StartupManager.Apply(s.StartWithWindows);

        if (changed(x => x.HideTaskbar))
        {
            if (s.HideTaskbar) TaskbarController.Hide(); else TaskbarController.Show();
        }

        if (changed(x => x.MenuBarEnabled) || changed(x => x.MenuBarAllDisplays)) RebuildMenuBars();

        if (changed(x => x.DockEnabled) || changed(x => x.DockIconSize) || changed(x => x.DockMagnification)
            || changed(x => x.DockMagnifiedSize) || changed(x => x.DockAutoHide))
            RebuildDock();
        else
            _dock?.RebuildItems(); // pins / running-apps / All Apps item

        if (changed(x => x.LauncherEnabled) || changed(x => x.LauncherHotkey)) RegisterLauncherHotkey();
        if (changed(x => x.AllAppsHotkey))
        {
            if (_allAppsHotkeyId != 0) _hotkeys.Unregister(_allAppsHotkeyId);
            _allAppsHotkeyId = 0;
            AllAppsHotkeyOk = _hotkeys.Register(s.AllAppsHotkey, ToggleAllApps, out _allAppsHotkeyId);
            if (!AllAppsHotkeyOk) _allAppsHotkeyId = 0;
        }

        if (!initial && changed(x => x.HideWindowsCaptionButtons)) _captionHider?.Apply();

        if (changed(x => x.WindowControlsEnabled))
        {
            _trafficLights?.Close();
            _trafficLights = null;
            if (s.WindowControlsEnabled)
            {
                _trafficLights = new TrafficLightsWindow();
                _trafficLights.Show();
            }
        }

        _applied = Clone(s);
    }

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s));

    private void RebuildMenuBars()
    {
        foreach (var m in _menuBars) m.Close();
        _menuBars.Clear();
        if (!SettingsStore.Current.MenuBarEnabled) return;

        var screens = SettingsStore.Current.MenuBarAllDisplays ? Forms.Screen.AllScreens : new[] { Forms.Screen.PrimaryScreen };
        foreach (var screen in screens)
        {
            var bar = new MenuBarWindow(screen);
            bar.Show();
            _menuBars.Add(bar);
        }
    }

    private void RebuildDock()
    {
        _dock?.Close();
        _dock = null;
        if (!SettingsStore.Current.DockEnabled) return;
        _dock = new DockWindow(Forms.Screen.PrimaryScreen);
        _dock.ShowDock();
    }

    private void RegisterLauncherHotkey()
    {
        if (_launcherHotkeyId != 0) _hotkeys.Unregister(_launcherHotkeyId);
        _launcherHotkeyId = 0;
        LauncherHotkeyOk = true;
        if (!SettingsStore.Current.LauncherEnabled) return;
        LauncherHotkeyOk = _hotkeys.Register(SettingsStore.Current.LauncherHotkey, ToggleLauncher, out _launcherHotkeyId);
        if (!LauncherHotkeyOk) _launcherHotkeyId = 0;
    }

    private void OnDisplaysChanged()
    {
        _displayDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _displayDebounce.Stop();
        _displayDebounce.Tick -= DisplayTick;
        _displayDebounce.Tick += DisplayTick;
        _displayDebounce.Start();
    }

    private void DisplayTick(object sender, EventArgs e)
    {
        _displayDebounce.Stop();
        RebuildMenuBars();
        RebuildDock();
    }

    // ===================== public API for windows =====================
    public void ToggleLauncher()
    {
        if (SettingsStore.Current.LauncherEnabled) _launcher.Toggle();
    }

    public void ToggleControlCenter(Forms.Screen screen) => _controlCenter.Toggle(screen);

    public void ToggleAllApps() => _allApps.Toggle();

    // ===================== updates =====================
    private DispatcherTimer _updateTimer;
    private string _promptedVersion;

    /// <summary>Starts the background checks: shortly after start-up, then a few times a day.</summary>
    private void StartUpdateChecks()
    {
        Updater.CleanUp();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdates(manual: false);
        };
        _updateTimer.Start();
    }

    /// <summary>
    /// Looks for a newer release and, if there is one, offers to install it. Automatic checks stay quiet when
    /// there is nothing new, when you skipped that version, or while a full-screen app is in front.
    /// Returns a short sentence describing the result.
    /// </summary>
    public async Task<string> CheckForUpdates(bool manual)
    {
        var s = SettingsStore.Current;
        if (!manual && !s.AutoCheckUpdates) return "";
        try
        {
            var release = await Updater.CheckAsync();
            s.LastUpdateCheck = DateTime.UtcNow;
            SettingsStore.Save(raiseEvent: false);
            if (release == null) return $"PearUI is up to date ({Updater.Current.ToString(3)}).";

            if (!manual)
            {
                if (s.SkippedUpdate == release.Tag || _promptedVersion == release.Tag) return "";
                if (FullScreenGuard.IsFullScreenAppActive()) { if (_updateTimer != null) _updateTimer.Interval = TimeSpan.FromMinutes(10); return ""; }
            }
            _promptedVersion = release.Tag;
            Settings.UpdateWindow.ShowFor(release);
            return $"PearUI {release.Version.ToString(3)} is available.";
        }
        catch
        {
            return manual ? "Couldn't reach GitHub. Check your internet connection and try again." : "";
        }
    }

    private Onboarding.OnboardingWindow _onboarding;

    /// <summary>The welcome tour (shown automatically on the first start; also from the pear menu).</summary>
    public void ShowOnboarding()
    {
        if (_onboarding != null) { _onboarding.Activate(); return; }
        _onboarding = new Onboarding.OnboardingWindow();
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Open();
    }

    public bool AllAppsHotkeyOk { get; private set; } = true;

    public void ShowSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    public bool IsPinned(RunningApp app) =>
        app != null && SettingsStore.Current.PinnedApps.Any(p => WindowTracker.Matches(p, app));

    public void PinApp(RunningApp app)
    {
        if (app == null) return;
        var pins = SettingsStore.Current.PinnedApps;
        var existing = pins.FirstOrDefault(p => WindowTracker.Matches(p, app));
        if (existing != null) pins.Remove(existing); // toggle
        else
        {
            pins.Add(new PinnedApp
            {
                Name = app.Name,
                Target = app.Aumid != null ? @"shell:AppsFolder\" + app.Aumid : app.ExePath,
                ExePath = app.ExePath,
            });
        }
        SettingsStore.Save();
        _settings?.ReloadPins();
    }

    public void Unpin(PinnedApp pin)
    {
        SettingsStore.Current.PinnedApps.Remove(pin);
        SettingsStore.Save();
        _settings?.ReloadPins();
    }

    // ===================== tray =====================
    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Search…", null, (_, _) => ToggleLauncher());
        menu.Items.Add("PearUI Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit PearUI", null, (_, _) => Dispatcher.BeginInvoke(Quit));

        _tray = new Forms.NotifyIcon
        {
            Text = "PearUI",
            Icon = CreateTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowSettings(); };
    }

    /// <summary>
    /// Writes a multi-size .ico with PNG-compressed, fully transparent images.
    /// (System.Drawing.Icon.Save drops the alpha channel and gives a black box.)
    /// </summary>
    private static void WritePngIco(string path, int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            using var bmp = Shell.PearLogo.CreateColorBitmap(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
            return (Size: s, Data: ms.ToArray());
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count); // ICONDIR
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)                                   // ICONDIRENTRY
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
    }

    private static Drawing.Icon CreateTrayIcon() => Shell.PearLogo.CreateIcon(32);

    // ===================== shutdown =====================
    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        try { TaskbarController.Show(); } catch { }
        try { _hotCorners?.Dispose(); } catch { }
        try { _windowFitter?.Dispose(); } catch { }
        try { _translucency?.Dispose(); } catch { } // every window gets its solid look back
        try { _startBlocker?.Dispose(); } catch { } // Windows key opens Start again
        try { _captionHider?.Dispose(); } catch { } // gives every app its Windows buttons back
        try
        {
            // Hand back windows Stage Manager tucked away (keeps the saved preference).
            if (StageManager.StageManager.Instance.Enabled)
            {
                StageManager.StageManager.Instance.SetEnabled(false);
                SettingsStore.Current.StageManager = true;
                SettingsStore.Save(raiseEvent: false);
            }
        }
        catch { }
        try
        {
            foreach (var m in _menuBars) m.Close(); // unregisters the AppBars, releasing reserved screen space
            _menuBars.Clear();
            _dock?.Close();
            _trafficLights?.Close();
        }
        catch { }
        try { _hotkeys?.Dispose(); } catch { }
        try { WindowTracker.Instance.Dispose(); } catch { }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    private static void Log(Exception ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(Path.Combine(SettingsStore.Folder, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { }
    }
}
