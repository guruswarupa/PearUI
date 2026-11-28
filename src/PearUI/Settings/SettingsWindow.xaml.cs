using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Native;
using PearUI.Themes;

namespace PearUI.Settings;

public partial class SettingsWindow : Shell.OverlayWindow
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

    public SettingsWindow() : base(Shell.SurfaceKind.Popup, noActivate: false)
    {
        InitializeComponent();
        Topmost = false;          // an ordinary window, not an overlay
        ShowInTaskbar = true;
        ToolWindow = false;
        FallbackCornerRadius = 12;
        Shell.GlassChrome.AddCaptionButtons(CaptionButtons, this);
        Shell.GlassChrome.EnableDrag(this, TopBar);
        ShowWallpaperInHotCorners();
        PreviewKeyDown += OnPageKey;
        SizeChanged += (_, _) => FitWallpaper();
        Shell.PearLogo.ApplyWindowIcon(this);
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SettingsStore.Save(); };
        VersionText.Text = "PearUI " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "");
        AccentBox.ItemsSource = ThemeManager.AccentNames.ToList();
        PinnedList.ItemsSource = _pins;

        LoadValues();
        WireEvents();
        BuildServices();
        BuildTerminalCommands();
        BuildExplorerTweaks();
        BuildShortcuts();
        BuildTerminalKeys();
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
        ShakeToFindCursor.IsChecked = S.ShakeToFindCursor;
        QuickLookBox.IsChecked = S.QuickLook;
        MacContextMenuBox.IsChecked = S.MacContextMenu;
        ReplaceStartMenu.IsChecked = S.ReplaceStartMenu;
        AutoCheckUpdates.IsChecked = S.AutoCheckUpdates;
        UpdateStatus.Text = S.LastUpdateCheck > DateTime.MinValue ? "Last checked " + S.LastUpdateCheck.ToLocalTime().ToString("g") : "";
        ThemeBox.SelectedIndex = S.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        AccentBox.SelectedItem = ThemeManager.AccentNames.Contains(S.Accent) ? S.Accent : "System";
        UseBlur.IsChecked = S.UseBlur;
        TerminalOpacity.Value = S.TerminalOpacity;
        FillTerminalShells();

        MenuBarEnabled.IsChecked = S.MenuBarEnabled;
        MenuBarAllDisplays.IsChecked = S.MenuBarAllDisplays;
        MenuBarAutoHide.IsChecked = S.MenuBarAutoHide;
        ClockShowDate.IsChecked = S.ClockShowDate;
        ClockShowSeconds.IsChecked = S.ClockShowSeconds;
        Clock24Hour.IsChecked = S.Clock24Hour;
        ShowBatteryPercent.IsChecked = S.ShowBatteryPercent;
        ClipboardHistoryBox.IsChecked = S.ClipboardHistory;

        DockEnabled.IsChecked = S.DockEnabled;
        DockIconSize.Value = S.DockIconSize;
        DockMagnification.IsChecked = S.DockMagnification;
        DockMagnifiedSize.Value = S.DockMagnifiedSize;
        DockAutoHide.IsChecked = S.DockAutoHide;
        DockShowRunningApps.IsChecked = S.DockShowRunningApps;
        WindowNumberShortcuts.IsChecked = S.WindowNumberShortcuts;
        VirtualDesktopShortcuts.IsChecked = S.VirtualDesktopShortcuts;

        LauncherEnabled.IsChecked = S.LauncherEnabled;
        HotkeyBox.Text = S.LauncherHotkey;
        foreach (ComboBoxItem i in HotkeyBox.Items) if ((string)i.Content == S.LauncherHotkey) HotkeyBox.SelectedItem = i;
        LauncherSearchFiles.IsChecked = S.LauncherSearchFiles;


        StageManagerEnabled.IsChecked = PearUI.StageManager.StageManager.Instance.Enabled;
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
        Bool(ShakeToFindCursor, v => S.ShakeToFindCursor = v);
        Bool(QuickLookBox, v => S.QuickLook = v);
        Bool(MacContextMenuBox, v => S.MacContextMenu = v);
        Bool(ReplaceStartMenu, v => S.ReplaceStartMenu = v);
        Bool(AutoCheckUpdates, v => S.AutoCheckUpdates = v);
        Bool(UseBlur, v => S.UseBlur = v);
        TerminalShellBox.SelectionChanged += (_, _) =>
        {
            if (_loading || TerminalShellBox.SelectedItem is not ComboBoxItem item) return;
            S.TerminalShell = item.Tag as string ?? "";
            Changed();
        };
        TerminalOpacity.ValueChanged += (_, e) => { if (_loading) return; S.TerminalOpacity = (int)Math.Round(e.NewValue); Changed(); };
        Bool(MenuBarEnabled, v => S.MenuBarEnabled = v);
        Bool(MenuBarAllDisplays, v => S.MenuBarAllDisplays = v);
        Bool(MenuBarAutoHide, v => S.MenuBarAutoHide = v);
        Bool(ClockShowDate, v => S.ClockShowDate = v);
        Bool(ClockShowSeconds, v => S.ClockShowSeconds = v);
        Bool(Clock24Hour, v => S.Clock24Hour = v);
        Bool(ShowBatteryPercent, v => S.ShowBatteryPercent = v);
        Bool(ClipboardHistoryBox, v => S.ClipboardHistory = v);
        Bool(DockEnabled, v => S.DockEnabled = v);
        Bool(DockMagnification, v => S.DockMagnification = v);
        Bool(DockAutoHide, v => S.DockAutoHide = v);
        Bool(DockShowRunningApps, v => S.DockShowRunningApps = v);
        Bool(WindowNumberShortcuts, v => S.WindowNumberShortcuts = v);
        Bool(VirtualDesktopShortcuts, v => S.VirtualDesktopShortcuts = v);
        Bool(LauncherEnabled, v => S.LauncherEnabled = v);
        Bool(LauncherSearchFiles, v => S.LauncherSearchFiles = v);
        Bool(DockShowAllApps, v => S.DockShowAllApps = v);
        StageManagerEnabled.Click += (_, _) => PearUI.StageManager.StageManager.Instance.SetEnabled(StageManagerEnabled.IsChecked == true);
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

    /// <summary>The shells found on this PC, so the terminal's starting shell can be chosen.</summary>
    private void FillTerminalShells()
    {
        TerminalShellBox.Items.Clear();
        TerminalShellBox.Items.Add(new ComboBoxItem { Content = "Automatic (Bash if installed)", Tag = "" });
        foreach (var shell in Terminal.ShellCatalog.All())
            TerminalShellBox.Items.Add(new ComboBoxItem { Content = shell.Name, Tag = shell.Id, ToolTip = shell.Exe });
        TerminalShellBox.SelectedIndex = 0;
        foreach (ComboBoxItem i in TerminalShellBox.Items) if ((i.Tag as string ?? "") == (S.TerminalShell ?? "")) TerminalShellBox.SelectedItem = i;
    }

    // ---------- File Explorer tweaks ----------
    private void BuildExplorerTweaks()
    {
        ExplorerTweaksList.Children.Clear();
        foreach (var tweak in ExplorerTweaks.All)
        {
            var t = tweak;
            AddOption(ExplorerTweaksList, t.Title, t.Description, ExplorerTweaks.IsApplied(t), out var box);
            box.Click += (_, _) => ExplorerTweaks.Set(t, box.IsChecked == true);
        }
    }

    // ---------- terminal commands (wg, update) ----------
    private void BuildTerminalCommands()
    {
        TerminalCommandsList.Children.Clear();
        void Row(string command, string runs, string text, bool first)
        {
            if (!first) TerminalCommandsList.Children.Add(new Separator());
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cmd = new TextBlock { Text = command, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
            var what = new TextBlock { Text = runs, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 16, 0) };
            var desc = new TextBlock { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(what, 1);
            Grid.SetColumn(desc, 2);
            grid.Children.Add(cmd);
            grid.Children.Add(what);
            grid.Children.Add(desc);
            TerminalCommandsList.Children.Add(grid);
        }

        Row("update", "Windows Update", "Install Windows updates quietly in the background (asks for administrator rights)", true);
        Row("update all", "Windows Update", "Install everything: regular and optional updates (preview updates, drivers). \"update optional\" does the same", false);
        Row("info", "PearUI", "Show a summary of this PC: system, CPU, memory, disks and more", false);
        foreach (var c in Core.TerminalCommands.WgCommands)
            Row("wg " + c.Alias, "winget " + c.WingetArgs, c.Description, false);
    }

    // ---------- keyboard shortcuts ----------
    private void BuildShortcuts()
    {
        ShortcutsList.Children.Clear();
        var list = S.Shortcuts ??= Shell.Shortcuts.Defaults();
        if (list.Count == 0)
            ShortcutsList.Children.Add(new TextBlock { Text = "No shortcuts yet. Add one to get started.", Style = (Style)FindResource("Hint"), Margin = new Thickness(2, 6, 2, 6) });

        foreach (var b in list.ToList())
        {
            if (ShortcutsList.Children.Count > 0) ShortcutsList.Children.Add(new Separator());
            var action = Shell.ShortcutActions.Find(b.Action);

            var keys = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), MinWidth = 120, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = b.Keys, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center },
            };
            keys.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");

            string what = action == null ? "Unknown action" : action.Title;
            if (action != null && action.Arg != Shell.ArgKind.None && !string.IsNullOrWhiteSpace(b.Arg)) what += ": " + b.Arg;
            var label = new TextBlock { Text = what, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 12, 0), ToolTip = what };
            Grid.SetColumn(label, 1);

            var on = new CheckBox { IsChecked = b.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Turn this shortcut on or off", Margin = new Thickness(0, 0, 8, 0) };
            on.Click += (_, _) => { b.Enabled = on.IsChecked == true; Changed(); };
            Grid.SetColumn(on, 2);

            var edit = new Button { Content = "Edit", Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0, 0, 6, 0) };
            edit.Click += (_, _) => EditShortcut(b);
            Grid.SetColumn(edit, 3);
            var remove = new Button { Content = "Remove", Style = (Style)FindResource("FlatButton") };
            remove.Click += (_, _) => { S.Shortcuts.Remove(b); Changed(); BuildShortcuts(); };
            Grid.SetColumn(remove, 4);

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(keys); row.Children.Add(label); row.Children.Add(on); row.Children.Add(edit); row.Children.Add(remove);
            ShortcutsList.Children.Add(row);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        UpdateStatus.Text = await App.Instance.CheckForUpdates(manual: true);
        CheckUpdatesButton.IsEnabled = true;
    }

    // ---------- support and contributors ----------
    private bool _supportLoaded;

    private void SupportLink_Click(object sender, RoutedEventArgs e)
    {
        string url = sender == StarButton ? ProjectInfo.RepoUrl
            : sender == IssuesButton ? ProjectInfo.RepoUrl + "/issues/new/choose"
            : sender == ContributeButton ? ProjectInfo.RepoUrl + "/blob/main/CONTRIBUTING.md"
            : sender == AllContributorsButton ? ProjectInfo.RepoUrl + "/graphs/contributors"
            : (sender as FrameworkElement)?.Tag as string;
        if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) Core.Launch.Open(u.AbsoluteUri);
    }

    // ---------- more apps by the developer ----------
    private bool _otherAppsShown;

    /// <summary>Shows the built-in list at once, then swaps in the newer list from the repository if there is one.</summary>
    private async Task LoadOtherAppsAsync()
    {
        if (_otherAppsShown) return;
        _otherAppsShown = true;
        FillOtherApps(ProjectInfo.BuiltInApps());
        try
        {
            var latest = await ProjectInfo.GetOtherAppsAsync();
            if (latest.Count > 0) FillOtherApps(latest);
        }
        catch { }
    }

    private void FillOtherApps(List<OtherApp> apps)
    {
        OtherAppsPanel.Children.Clear();
        foreach (var app in apps) OtherAppsPanel.Children.Add(MakeAppCard(app));
    }

    private FrameworkElement MakeAppCard(OtherApp app)
    {
        // icon: a rounded tile with the app's picture (a glyph until it loads, or if it can't)
        var photo = new ImageBrush { Stretch = Stretch.Uniform };
        var tile = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Background = photo, ClipToBounds = true };
        var fallback = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        var frame = new Grid { Width = 52, Height = 52, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var plate = new Border { CornerRadius = new CornerRadius(12) };
        plate.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");
        frame.Children.Add(plate);
        frame.Children.Add(fallback);
        frame.Children.Add(tile);
        _ = LoadAppIconAsync(app.Icon, photo, fallback);

        var name = new TextBlock { Text = app.Name, FontSize = 14, FontWeight = FontWeights.SemiBold };
        var platforms = new TextBlock { Text = app.Platforms, Style = (Style)FindResource("Hint"), FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
        var tagline = new TextBlock { Text = app.Tagline, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
        var links = new WrapPanel();
        foreach (var link in app.Links)
        {
            var b = new Button { Content = link.Title, Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0, 0, 6, 6), Tag = link.Url, FontSize = 12 };
            b.Click += SupportLink_Click;
            links.Children.Add(b);
        }

        var text = new StackPanel();
        text.Children.Add(name); text.Children.Add(platforms); text.Children.Add(tagline); text.Children.Add(links);
        var row = new Grid { Margin = new Thickness(6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(frame);
        row.Children.Add(text);

        var card = new Border { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(8), CornerRadius = new CornerRadius(12), Child = row };
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Separator");
        card.BorderThickness = new Thickness(1);
        return card;
    }

    private static async Task LoadAppIconAsync(string url, ImageBrush target, UIElement fallback)
    {
        if (string.IsNullOrEmpty(url)) return;
        var bytes = await ProjectInfo.GetImageAsync(url);
        if (bytes == null) return;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 160;   // the pictures are up to 1024 px: keep only what is needed
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            target.ImageSource = img;
            fallback.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void CopyUpi_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ProjectInfo.UpiId); UpiCopied.Text = "Copied"; }
        catch { UpiCopied.Text = "Couldn't copy"; }
    }

    /// <summary>The UPI QR code is drawn here on this PC, so no outside service sees it.</summary>
    private void ShowUpiQr()
    {
        UpiIdText.Text = ProjectInfo.UpiId;
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(ProjectInfo.UpiPayload, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(10, new byte[] { 0, 0, 0, 255 }, new byte[] { 255, 255, 255, 255 }, drawQuietZones: true);
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(png);
            img.EndInit();
            img.Freeze();
            UpiQr.Source = img;
        }
        catch { }
    }

    /// <summary>Fills the Support page the first time it is opened: the funding links and the contributors, both from GitHub.</summary>
    private async Task LoadSupportAsync()
    {
        if (_supportLoaded) return;
        _supportLoaded = true;
        ShowUpiQr();
        SupportStatus.Text = "Loading…";
        ContributorsStatus.Text = "Loading…";

        try
        {
            var links = await ProjectInfo.GetFundingAsync();
            SupportLinks.Children.Clear();
            bool first = true;
            foreach (var link in links)
            {
                var b = new Button { Content = first ? "♥  " + link.Title : link.Title, Margin = new Thickness(0, 0, 8, 8), Tag = link.Url };
                b.Style = (Style)FindResource(first ? "AccentButton" : "FlatButton");
                b.Click += SupportLink_Click;
                SupportLinks.Children.Add(b);
                first = false;
            }
            SupportStatus.Text = "";
        }
        catch { SupportStatus.Text = "Couldn't load the support links. Check your connection and reopen this page."; _supportLoaded = false; }

        try
        {
            var people = await ProjectInfo.GetContributorsAsync();
            ContributorsPanel.Children.Clear();
            foreach (var c in people) ContributorsPanel.Children.Add(MakeContributorTile(c));
            ContributorsStatus.Text = people.Count == 0 ? "No contributors to show yet. Be the first!" : "";
        }
        catch { ContributorsStatus.Text = "Couldn't load the contributors. Check your connection and reopen this page."; _supportLoaded = false; }
    }

    private FrameworkElement MakeContributorTile(Contributor c)
    {
        var photo = new ImageBrush { Stretch = Stretch.UniformToFill };
        var avatar = new System.Windows.Shapes.Ellipse { Width = 56, Height = 56, Fill = photo, HorizontalAlignment = HorizontalAlignment.Center };
        avatar.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Separator");
        avatar.StrokeThickness = 1;

        var name = new TextBlock { Text = c.Login, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
        var count = new TextBlock { Text = c.Contributions == 1 ? "1 contribution" : c.Contributions + " contributions", Style = (Style)FindResource("Hint"), TextAlignment = TextAlignment.Center, FontSize = 11 };

        var stack = new StackPanel();
        stack.Children.Add(avatar); stack.Children.Add(name); stack.Children.Add(count);
        var tile = new Border { Width = 132, Padding = new Thickness(6, 10, 6, 10), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = stack, ToolTip = c.ProfileUrl };
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (Uri.TryCreate(c.ProfileUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) Core.Launch.Open(u.AbsoluteUri);
        };

        _ = LoadAvatarAsync(c, photo);
        return tile;
    }

    private static async Task LoadAvatarAsync(Contributor c, ImageBrush target)
    {
        var bytes = await ProjectInfo.GetAvatarAsync(c);
        if (bytes == null) return;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            target.ImageSource = img;
        }
        catch { }
    }

    // ---------- terminal shortcuts ----------
    private void BuildTerminalKeys()
    {
        TerminalKeysList.Children.Clear();
        foreach (var def in Terminal.TerminalKeys.All)
        {
            if (TerminalKeysList.Children.Count > 0) TerminalKeysList.Children.Add(new Separator());
            string id = def.Id;
            string keys = Terminal.TerminalKeys.Get(id);

            var label = new TextBlock { Text = def.Title, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), MinWidth = 130, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = keys, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center },
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");
            Grid.SetColumn(chip, 1);
            var change = new Button { Content = "Change…", Style = (Style)FindResource("FlatButton") };
            change.Click += (_, _) => ChangeTerminalKey(def);
            Grid.SetColumn(change, 2);

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label); row.Children.Add(chip); row.Children.Add(change);
            TerminalKeysList.Children.Add(row);
        }
    }

    private void ChangeTerminalKey(Terminal.TerminalKeys.Def def)
    {
        var dialog = new KeyRecordDialog(def.Title, Terminal.TerminalKeys.Get(def.Id), keys =>
        {
            if (!Shell.Shortcuts.IsSafe(keys)) return "Add Ctrl, Alt, Shift or the Windows key (function keys can stand alone).";
            var other = Terminal.TerminalKeys.Conflict(def.Id, keys);
            return other == null ? null : $"\"{other.Title}\" already uses these keys.";
        }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Terminal.TerminalKeys.Set(def.Id, dialog.Result);
        Changed();
        BuildTerminalKeys();
    }

    private void ResetTerminalKeys_Click(object sender, RoutedEventArgs e)
    {
        Terminal.TerminalKeys.ResetAll();
        Changed();
        BuildTerminalKeys();
    }

    private void AddShortcut_Click(object sender, RoutedEventArgs e) => EditShortcut(null);

    private void EditShortcut(Shell.ShortcutBinding existing)
    {
        var dialog = new ShortcutDialog(existing, S.Shortcuts.Where(x => x != existing)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (existing == null) S.Shortcuts.Add(dialog.Result);
        else { existing.Keys = dialog.Result.Keys; existing.Action = dialog.Result.Action; existing.Arg = dialog.Result.Arg; existing.Enabled = true; }
        Changed();
        BuildShortcuts();
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace all your shortcuts with the defaults?", "PearUI", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        S.Shortcuts = Shell.Shortcuts.Defaults();
        Changed();
        BuildShortcuts();
    }

    // ---------- optional services ----------
    private readonly List<(OptionalServices.Def Def, CheckBox Box)> _serviceBoxes = new();

    private readonly List<(Debloat.Tweak Tweak, CheckBox Box)> _tweakBoxes = new();

    private void AddOption(Panel list, string title, string desc, bool isChecked, out CheckBox box)
    {
        if (list.Children.Count > 0) list.Children.Add(new Separator());
        box = new CheckBox { Content = title, IsChecked = isChecked };
        list.Children.Add(box);
        list.Children.Add(new TextBlock { Text = desc, Style = (Style)FindResource("Hint"), Margin = new Thickness(26, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
    }

    private void BuildServices()
    {
        ServicesList.Children.Clear();
        DebloatList.Children.Clear();
        _serviceBoxes.Clear();
        _tweakBoxes.Clear();
        foreach (var d in OptionalServices.All)
        {
            if (!OptionalServices.Exists(d.Name)) continue;
            AddOption(ServicesList, d.Title, d.Description, OptionalServices.IsDisabled(d.Name), out var box);
            _serviceBoxes.Add((d, box));
        }
        foreach (var t in Debloat.All)
        {
            if (!Debloat.IsRelevant(t)) continue;
            AddOption(DebloatList, t.Title, t.Description, Debloat.IsApplied(t), out var box);
            _tweakBoxes.Add((t, box));
        }
    }

    private void ApplyServices_Click(object sender, RoutedEventArgs e)
    {
        var changes = _serviceBoxes
            .Where(x => (x.Box.IsChecked == true) != OptionalServices.IsDisabled(x.Def.Name))
            .Select(x => (x.Def.Name, x.Box.IsChecked == true)).ToList();
        var tweaks = _tweakBoxes
            .Where(x => (x.Box.IsChecked == true) != Debloat.IsApplied(x.Tweak))
            .Select(x => (x.Tweak, Apply: x.Box.IsChecked == true)).ToList();
        if (changes.Count == 0 && tweaks.Count == 0) { ServicesStatus.Text = "Nothing to change."; return; }

        // Current-user registry changes need no permission; the rest goes to one elevated helper.
        foreach (var (t, apply) in tweaks) Debloat.ApplyUserPart(t, apply);
        var admin = tweaks.Where(x => x.Tweak.NeedsAdmin).Select(x => (x.Tweak.Id, x.Apply)).ToList();
        if (changes.Count == 0 && admin.Count == 0) { BuildServices(); ServicesStatus.Text = "Done."; return; }

        ServicesStatus.Text = "Waiting for administrator permission…";
        ApplyServices.IsEnabled = false;
        Task.Run(() => OptionalServices.ApplyElevated(changes, admin)).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            ApplyServices.IsEnabled = true;
            BuildServices();
            ServicesStatus.Text = t.Result ? "Done. Some changes need a restart or sign-out." : "Cancelled. Administrator changes were not applied.";
        }));
    }

    private Shell.WallpaperPicker _wallpaperPicker;

    /// <summary>The wallpaper grid takes all the height the window has left.</summary>
    private void FitWallpaper()
    {
        if (_wallpaperPicker != null) _wallpaperPicker.ScrollHeight = ActualHeight - 330;
    }

    /// <summary>Up and Down (and Page Up / Page Down) scroll the page, unless the focused control needs the arrows itself.</summary>
    private void OnPageKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.PageUp or Key.PageDown)) return;
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is ComboBox or TextBox or Slider or PasswordBox or ComboBoxItem) return;
        if (focused is ListBoxItem && !Nav.IsKeyboardFocusWithin) return;   // the pinned apps list uses the arrows to choose
        int direction = e.Key is Key.Up or Key.PageUp ? -1 : 1;
        bool page = e.Key is Key.PageUp or Key.PageDown;
        if (PageWallpaper.Visibility == Visibility.Visible && _wallpaperPicker != null)
        {
            _wallpaperPicker.ScrollBy(direction, page);   // the wallpaper grid scrolls inside the page
            e.Handled = true;
            return;
        }
        double step = e.Key is Key.PageUp or Key.PageDown ? PageScroll.ViewportHeight * 0.9 : 64;
        if (e.Key is Key.Up or Key.PageUp) step = -step;
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset + step);
        e.Handled = true;
    }

    /// <summary>The hot corners picture is your own desktop wallpaper, like a small screen.</summary>
    private void ShowWallpaperInHotCorners()
    {
        var image = Shell.DesktopWallpaper.Load(480);
        if (image != null) HotScreen.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        else HotScreen.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string tag = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "General";
        foreach (var (name, page) in new (string, FrameworkElement)[]
                 {
                     ("General", PageGeneral), ("MenuBar", PageMenuBar), ("Dock", PageDock), ("Desktop", PageDesktop), ("Wallpaper", PageWallpaper),
                     ("Launcher", PageLauncher), ("Shortcuts", PageShortcuts), ("Performance", PagePerformance), ("Support", PageSupport), ("About", PageAbout),
                 })
            page.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Wallpaper" && WallpaperHost.Child == null)
        {
            WallpaperHost.Child = _wallpaperPicker = new Shell.WallpaperPicker(420);
            FitWallpaper();
        }
        if (tag == "Support") _ = LoadSupportAsync();
        if (tag == "About") _ = LoadOtherAppsAsync();
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
    private void Privacy_Click(object sender, RoutedEventArgs e) => Launch.Open(ProjectInfo.RepoUrl + "/blob/main/PRIVACY.md");

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Instance.Quit();
}
