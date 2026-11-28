using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.MenuBar;
using PearUI.Native;
using PearUI.Themes;
using Windows.Devices.Radios;
using Forms = System.Windows.Forms;

namespace PearUI.ControlCenter;

/// <summary>Drop-down panel with Wi‑Fi, Bluetooth, dark mode, brightness, volume and media controls.</summary>
public partial class ControlCenterWindow : Shell.OverlayWindow
{
    private bool _updating;
    private DateTime _shownAt;
    private DateTime _hiddenAt;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    // Sliders follow the keyboard volume/brightness keys, so poll them quickly while the panel is open.
    private readonly DispatcherTimer _sync = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _syncing;

    public ControlCenterWindow() : base(Shell.SurfaceKind.Transparent, noActivate: false) // no outer panel: tiles float on the desktop
    {
        FallbackCornerRadius = 16;
        InitializeComponent();
        ApplyTileBrushes();
        ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(ApplyTileBrushes); // follow dark/light switches live
        Deactivated += (_, _) => { if ((DateTime.Now - _shownAt).TotalMilliseconds > 150) HidePanel(); };
        _refresh.Tick += async (_, _) => await RefreshAsync(lightweight: true);
        _sync.Tick += async (_, _) => await SyncSlidersAsync();
        SystemStatus.MediaChanged += () => Dispatcher.BeginInvoke(async () => { if (IsVisible) await RefreshAsync(lightweight: true); });
    }

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        StageManager.StageManager.Instance.Toggle();
        StageToggle.IsChecked = StageManager.StageManager.Instance.Enabled;
    }

    public void Toggle(Forms.Screen screen)
    {
        if (IsVisible) { HidePanel(); return; }
        // Clicking the menu bar icon deactivates us first; don't immediately reopen.
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;
        ShowPanel(screen);
    }

    // The panel has no frosted backdrop of its own, so the tiles need to be solid enough to read on any wallpaper.
    private void ApplyTileBrushes()
    {
        bool dark = ThemeManager.IsDark;
        static System.Windows.Media.SolidColorBrush B(byte a, byte r, byte g, byte b) =>
            new(System.Windows.Media.Color.FromArgb(a, r, g, b));
        Resources["Brush.Tile"] = dark ? B(0xE6, 0x2A, 0x2A, 0x2E) : B(0xEB, 0xFF, 0xFF, 0xFF);
        Resources["Brush.TileIcon"] = dark ? B(0x4D, 0xFF, 0xFF, 0xFF) : B(0x26, 0x00, 0x00, 0x00);
        Resources["Brush.SliderTrack"] = dark ? B(0x59, 0xFF, 0xFF, 0xFF) : B(0x33, 0x00, 0x00, 0x00);
        Resources["Brush.Separator"] = dark ? B(0x80, 0xFF, 0xFF, 0xFF) : B(0x55, 0x00, 0x00, 0x00);
    }

    private void ShowPanel(Forms.Screen screen)
    {
        ApplyTileBrushes();
        double s = Win32.GetScaleForScreen(screen);
        _shownAt = DateTime.Now;
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        int w = (int)Math.Ceiling(Width * s);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST,
            screen.Bounds.Right - w - (int)(8 * s),
            screen.Bounds.Top + (int)((MenuBarWindow.BarHeight + 6) * s), 0, 0, Win32.SWP_NOSIZE);
        Activate();
        Win32.SetForegroundWindow(hwnd);
        _ = RefreshAsync(lightweight: false);
        _ = Shell.NetworkMenus.PrescanWifiAsync(); // fresh network list for a right-click
        _refresh.Start();
        _sync.Start();
    }

    public void HidePanel()
    {
        _refresh.Stop();
        _sync.Stop();
        _hiddenAt = DateTime.Now;
        Hide();
    }

    private async Task RefreshAsync(bool lightweight)
    {
        _updating = true;
        try
        {
            var net = SystemStatus.GetNetwork();
            if (!lightweight)
            {
                var wifi = await SystemStatus.GetRadioStateAsync(RadioKind.WiFi);
                WifiToggle.IsEnabled = wifi != null;
                WifiToggle.IsChecked = wifi == true;

                var bt = await SystemStatus.GetRadioStateAsync(RadioKind.Bluetooth);
                BluetoothToggle.IsEnabled = bt != null;
                BluetoothToggle.IsChecked = bt == true;
                await SyncBrightnessAsync(first: true);
            }

            WifiName.Text = net.Connected ? (net.IsWifi ? net.Name ?? "Connected" : "Ethernet: " + (net.Name ?? "Connected"))
                : WifiToggle.IsChecked == true ? "Not connected" : "Off";

            bool stage = StageManager.StageManager.Instance.Enabled;
            StageToggle.IsChecked = stage;

            FocusToggle.IsChecked = SystemStatus.GetDoNotDisturb();
            DarkToggle.IsChecked = ThemeManager.IsDark; // what PearUI shows, not only what Windows is set to

            SyncVolume();

            var media = await SystemStatus.GetMediaDetailAsync();
            bool hasMedia = media != null && !string.IsNullOrWhiteSpace(media.Title);
            MediaTitle.Text = hasMedia ? media.Title : "Not Playing";
            MediaArtist.Text = hasMedia ? media.Artist : "";
            MediaArt.ImageSource = hasMedia ? media.Art : null;
            PlayGlyph.Text = hasMedia && media.Playing ? "" : "";
        }
        finally { _updating = false; }
    }


    // ---------- live slider sync ----------
    private async Task SyncSlidersAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            SyncVolume();
            await SyncBrightnessAsync(first: false);
        }
        finally { _syncing = false; }
    }

    private void SyncVolume()
    {
        if (VolumeSlider.IsMouseCaptured) return;
        float v = AudioVolume.GetVolume();
        bool mute = AudioVolume.GetMute();
        VolumeSlider.IsEnabled = v >= 0;
        if (v >= 0) VolumeSlider.Value = Math.Round(v * 100);
        MuteGlyph.Text = mute ? "\uE74F" : "\uE767";
        VolumeSlider.Glyph = VolumeIcon(Math.Max(v, 0) * 100, mute);
    }

    private async Task SyncBrightnessAsync(bool first)
    {
        if (BrightnessSlider.IsMouseCaptured) return;
        int? b = await Task.Run(SystemStatus.GetBrightness); // WMI is slow: keep it off the UI thread
        if (BrightnessSlider.IsMouseCaptured) return;
        BrightnessSlider.IsEnabled = b != null; // external monitors: no brightness control
        if (b != null) { BrightnessSlider.Value = b.Value; _lastBrightness = b.Value; }
    }

    // ---------- shortcuts row ----------
    private static void Launch(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
    private void Calc_Click(object sender, RoutedEventArgs e) { HidePanel(); Launch("calc.exe"); }
    private void Clock_Click(object sender, RoutedEventArgs e) { HidePanel(); Launch("ms-clock:"); }
    private void Snip_Click(object sender, RoutedEventArgs e) { HidePanel(); Launch("ms-screenclip:"); }
    private void Settings_Click(object sender, RoutedEventArgs e) { HidePanel(); Launch("ms-settings:"); }
    private void Focus_Click(object sender, RoutedEventArgs e) => SystemStatus.SetDoNotDisturb(FocusToggle.IsChecked == true);

    // ---------- toggles ----------
    private async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        await SystemStatus.SetRadioStateAsync(RadioKind.WiFi, WifiToggle.IsChecked == true);
        await Task.Delay(400);
        await RefreshAsync(false);
    }

    private async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        await SystemStatus.SetRadioStateAsync(RadioKind.Bluetooth, BluetoothToggle.IsChecked == true);
        await Task.Delay(400);
        await RefreshAsync(false);
    }

    // Right-click: choose a Wi‑Fi network / Bluetooth device (the panel closes as the menu takes focus).
    private async void Wifi_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var p = WifiToggle.PointToScreen(new Point(0, WifiToggle.ActualHeight));
        await Shell.NetworkMenus.ShowWifiMenuAsync((int)p.X, (int)p.Y);
    }

    private async void Bluetooth_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var p = BluetoothToggle.PointToScreen(new Point(0, BluetoothToggle.ActualHeight));
        await Shell.NetworkMenus.ShowBluetoothMenuAsync((int)p.X, (int)p.Y);
    }

    private void Dark_Click(object sender, RoutedEventArgs e)
    {
        bool dark = DarkToggle.IsChecked == true;
        SystemStatus.SetSystemDark(dark);
        // The button switches the whole desktop's appearance: PearUI has to follow too, even if its own
        // theme had been pinned to Light or Dark (for example by the welcome tour).
        if (SettingsStore.Current.Theme != "System")
        {
            SettingsStore.Current.Theme = "System";
            SettingsStore.Save();
        }
        ThemeManager.Apply();
    }

    // ---------- sliders ----------
    private int _lastBrightness = -1;

    private void Brightness_Changed(object sender, double value)
    {
        int v = (int)Math.Round(value);
        if (v == _lastBrightness) return;
        _lastBrightness = v;
        Task.Run(() => SystemStatus.SetBrightness(v)); // WMI is slow: keep dragging smooth
    }

    private static string VolumeIcon(double v, bool mute) =>
        mute || v < 1 ? "" : v < 34 ? "" : v < 67 ? "" : "";

    private void Volume_Changed(object sender, double value)
    {
        AudioVolume.SetVolume((float)(value / 100.0));
        MuteGlyph.Text = "";
        VolumeSlider.Glyph = VolumeIcon(value, false);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        bool mute = !AudioVolume.GetMute();
        AudioVolume.SetMute(mute);
        MuteGlyph.Text = mute ? "" : "";
        VolumeSlider.Glyph = VolumeIcon(VolumeSlider.Value, mute);
    }

    // ---------- media ----------
    private async void PlayPause_Click(object sender, RoutedEventArgs e) { await SystemStatus.MediaPlayPauseAsync(); await Task.Delay(250); await RefreshAsync(true); }
    private async void Next_Click(object sender, RoutedEventArgs e) { await SystemStatus.MediaNextAsync(); await Task.Delay(400); await RefreshAsync(true); }
    private async void Prev_Click(object sender, RoutedEventArgs e) { await SystemStatus.MediaPreviousAsync(); await Task.Delay(400); await RefreshAsync(true); }

    // ---------- shortcuts ----------
    private void Cast_Click(object sender, RoutedEventArgs e) { CastToggle.IsChecked = false; HidePanel(); Win32.PressKeys(Win32.VK_LWIN, (byte)'K'); }
}
