using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacUI.Core;
using MacUI.Native;
using MacUI.Themes;

namespace MacUI.Settings;

public partial class SettingsWindow : Window
{
    public sealed class PinRow : INotifyPropertyChanged
    {
        private ImageSource _icon;
        public PinnedApp Pin { get; init; }
        public string Name => Pin.Name;
        public ImageSource Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    private readonly ObservableCollection<PinRow> _pins = new();
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SettingsStore.Save(); };
        VersionText.Text = "PearUI " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "");
        AccentBox.ItemsSource = ThemeManager.AccentNames.ToList();
        PinnedList.ItemsSource = _pins;

        LoadValues();
        WireEvents();
        Nav.SelectedIndex = 0;

        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

        AppCatalog.Instance.Loaded += OnCatalogLoaded;
        Closed += (_, _) => AppCatalog.Instance.Loaded -= OnCatalogLoaded;
        FillAllApps();
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(ApplyTitleBarTheme);
    private void OnCatalogLoaded() => Dispatcher.BeginInvoke(FillAllApps);

    private void FillAllApps() => AllApps.ItemsSource = AppCatalog.Instance.Apps.ToList();

    private void ApplyTitleBarTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, sizeof(int));
    }

    private static AppSettings S => SettingsStore.Current;

    private void LoadValues()
    {
        _loading = true;
        StartWithWindows.IsChecked = S.StartWithWindows;
        HideTaskbar.IsChecked = S.HideTaskbar;
        ReplaceStartMenu.IsChecked = S.ReplaceStartMenu;
        ThemeBox.SelectedIndex = S.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        AccentBox.SelectedItem = ThemeManager.AccentNames.Contains(S.Accent) ? S.Accent : "System";
        UseBlur.IsChecked = S.UseBlur;
        TranslucentWindows.IsChecked = S.TranslucentWindows;
        WindowOpacity.Value = S.WindowOpacity;

        MenuBarEnabled.IsChecked = S.MenuBarEnabled;
        MenuBarAllDisplays.IsChecked = S.MenuBarAllDisplays;
        ClockShowDate.IsChecked = S.ClockShowDate;
        ClockShowSeconds.IsChecked = S.ClockShowSeconds;
        Clock24Hour.IsChecked = S.Clock24Hour;
        ShowBatteryPercent.IsChecked = S.ShowBatteryPercent;

        DockEnabled.IsChecked = S.DockEnabled;
        DockIconSize.Value = S.DockIconSize;
        DockMagnification.IsChecked = S.DockMagnification;
        DockMagnifiedSize.Value = S.DockMagnifiedSize;
        DockAutoHide.IsChecked = S.DockAutoHide;
        DockShowRunningApps.IsChecked = S.DockShowRunningApps;

        LauncherEnabled.IsChecked = S.LauncherEnabled;
        HotkeyBox.Text = S.LauncherHotkey;
        foreach (ComboBoxItem i in HotkeyBox.Items) if ((string)i.Content == S.LauncherHotkey) HotkeyBox.SelectedItem = i;
        LauncherSearchFiles.IsChecked = S.LauncherSearchFiles;


        StageManagerEnabled.IsChecked = MacUI.StageManager.StageManager.Instance.Enabled;
        DockShowAllApps.IsChecked = S.DockShowAllApps;
        foreach (ComboBoxItem i in AllAppsHotkeyBox.Items) if ((string)i.Content == S.AllAppsHotkey) AllAppsHotkeyBox.SelectedItem = i;
        foreach (var (box, value) in new[] { (HotTL, S.HotCornerTopLeft), (HotTR, S.HotCornerTopRight), (HotBL, S.HotCornerBottomLeft), (HotBR, S.HotCornerBottomRight) })
        {
            box.ItemsSource = Shell.Features.HotCornerActions;
            box.SelectedItem = Shell.Features.HotCornerActions.Contains(value) ? value : "None";
        }
        TransparencyWarning.Visibility = Backdrop.SystemTransparencyEnabled ? Visibility.Collapsed : Visibility.Visible;

        ReloadPins();
        _loading = false;
    }

    private void EnableTransparency_Click(object sender, RoutedEventArgs e)
    {
        Backdrop.SetSystemTransparency(true);
        TransparencyWarning.Visibility = Visibility.Collapsed;
    }

    private void WireEvents()
    {
        void Bool(ToggleButton box, Action<bool> set) => box.Click += (_, _) => { set(box.IsChecked == true); Changed(); };

        Bool(StartWithWindows, v => S.StartWithWindows = v);
        Bool(HideTaskbar, v => S.HideTaskbar = v);
        Bool(ReplaceStartMenu, v => S.ReplaceStartMenu = v);
        Bool(UseBlur, v => S.UseBlur = v);
        Bool(TranslucentWindows, v => S.TranslucentWindows = v);
        WindowOpacity.ValueChanged += (_, e) => { if (_loading) return; S.WindowOpacity = (int)Math.Round(e.NewValue); Changed(); };
        Bool(MenuBarEnabled, v => S.MenuBarEnabled = v);
        Bool(MenuBarAllDisplays, v => S.MenuBarAllDisplays = v);
        Bool(ClockShowDate, v => S.ClockShowDate = v);
        Bool(ClockShowSeconds, v => S.ClockShowSeconds = v);
        Bool(Clock24Hour, v => S.Clock24Hour = v);
        Bool(ShowBatteryPercent, v => S.ShowBatteryPercent = v);
        Bool(DockEnabled, v => S.DockEnabled = v);
        Bool(DockMagnification, v => S.DockMagnification = v);
        Bool(DockAutoHide, v => S.DockAutoHide = v);
        Bool(DockShowRunningApps, v => S.DockShowRunningApps = v);
        Bool(LauncherEnabled, v => S.LauncherEnabled = v);
        Bool(LauncherSearchFiles, v => S.LauncherSearchFiles = v);
        Bool(DockShowAllApps, v => S.DockShowAllApps = v);
        StageManagerEnabled.Click += (_, _) => MacUI.StageManager.StageManager.Instance.SetEnabled(StageManagerEnabled.IsChecked == true);
        AllAppsHotkeyBox.SelectionChanged += (_, _) =>
        {
            if (_loading || AllAppsHotkeyBox.SelectedItem is not ComboBoxItem item) return;
            S.AllAppsHotkey = (string)item.Content;
            Changed();
        };
        HotTL.SelectionChanged += (_, _) => { if (!_loading) { S.HotCornerTopLeft = HotTL.SelectedItem as string; Changed(); } };
        HotTR.SelectionChanged += (_, _) => { if (!_loading) { S.HotCornerTopRight = HotTR.SelectedItem as string; Changed(); } };
        HotBL.SelectionChanged += (_, _) => { if (!_loading) { S.HotCornerBottomLeft = HotBL.SelectedItem as string; Changed(); } };
        HotBR.SelectionChanged += (_, _) => { if (!_loading) { S.HotCornerBottomRight = HotBR.SelectedItem as string; Changed(); } };

        ThemeBox.SelectionChanged += (_, _) => { if (_loading) return; S.Theme = ((ComboBoxItem)ThemeBox.SelectedItem).Content as string; Changed(); };
        AccentBox.SelectionChanged += (_, _) => { if (_loading) return; S.Accent = AccentBox.SelectedItem as string ?? "System"; Changed(); };
        HotkeyBox.SelectionChanged += (_, _) =>
        {
            if (_loading || HotkeyBox.SelectedItem is not ComboBoxItem item) return;
            S.LauncherHotkey = (string)item.Content;
            Changed();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                HotkeyError.Visibility = App.Instance.LauncherHotkeyOk ? Visibility.Collapsed : Visibility.Visible);
        };
        DockIconSize.ValueChanged += (_, e) => { if (_loading) return; S.DockIconSize = e.NewValue; Changed(); };
        DockMagnifiedSize.ValueChanged += (_, e) => { if (_loading) return; S.DockMagnifiedSize = e.NewValue; Changed(); };
    }

    private void Changed()
    {
        if (_loading) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string tag = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "General";
        foreach (var (name, page) in new (string, FrameworkElement)[]
                 {
                     ("General", PageGeneral), ("MenuBar", PageMenuBar), ("Dock", PageDock), ("Desktop", PageDesktop),
                     ("Launcher", PageLauncher), ("About", PageAbout),
                 })
            page.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- pinned apps ----------
    public void ReloadPins()
    {
        var selected = (PinnedList.SelectedItem as PinRow)?.Pin;
        _pins.Clear();
        foreach (var p in S.PinnedApps)
        {
            var row = new PinRow { Pin = p };
            _pins.Add(row);
            ShellIcons.GetAsync(AppCatalog.Instance.IconFor(p.Target), 48).ContinueWith(t => row.Icon = t.Result,
                TaskScheduler.FromCurrentSynchronizationContext());
            if (p == selected) PinnedList.SelectedItem = row;
        }
    }

    private void PinsChanged()
    {
        SettingsStore.Save();
        ReloadPins();
    }

    private void MovePin(int delta)
    {
        if (PinnedList.SelectedItem is not PinRow row) return;
        int i = S.PinnedApps.IndexOf(row.Pin), j = i + delta;
        if (i < 0 || j < 0 || j >= S.PinnedApps.Count) return;
        (S.PinnedApps[i], S.PinnedApps[j]) = (S.PinnedApps[j], S.PinnedApps[i]);
        PinsChanged();
    }

    private void PinUp_Click(object sender, RoutedEventArgs e) => MovePin(-1);
    private void PinDown_Click(object sender, RoutedEventArgs e) => MovePin(1);

    private void PinRemove_Click(object sender, RoutedEventArgs e)
    {
        if (PinnedList.SelectedItem is not PinRow row) return;
        S.PinnedApps.Remove(row.Pin);
        PinsChanged();
    }

    private void PinBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add a program to the Dock",
            Filter = "Programs and shortcuts|*.exe;*.lnk;*.appref-ms;*.bat;*.cmd|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
        }
        catch { }
        S.PinnedApps.Add(new PinnedApp
        {
            Name = name, Target = path,
            ExePath = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null,
        });
        PinsChanged();
    }

    private void PinAdd_Click(object sender, RoutedEventArgs e)
    {
        var entry = AllApps.SelectedItem as AppEntry
                    ?? AppCatalog.Instance.Apps.FirstOrDefault(a => a.Name.Equals(AllApps.Text, StringComparison.CurrentCultureIgnoreCase));
        if (entry == null) return;
        if (S.PinnedApps.Any(p => p.Target.Equals(entry.Target, StringComparison.OrdinalIgnoreCase))) return;
        S.PinnedApps.Add(new PinnedApp { Name = entry.Name, Target = entry.Target, ExePath = entry.ExePath });
        PinsChanged();
    }

    private void PinReset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace the apps in the Dock with the default set?", "Reset Dock",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        S.PinnedApps = null;
        SettingsStore.Save(raiseEvent: false);
        SettingsStore.Load(); // recreates the defaults
        SettingsStore.Save();
        ReloadPins();
    }

    // ---------- about ----------
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.Folder);
        Launch.Open(SettingsStore.Folder);
    }

    private void Tour_Click(object sender, RoutedEventArgs e) => App.Instance.ShowOnboarding();

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Instance.Quit();
}
