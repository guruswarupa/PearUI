using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MacUI.Core;
using MacUI.MenuBar;
using MacUI.Native;
using MacUI.Themes;
using Windows.Devices.Radios;
using Forms = System.Windows.Forms;

namespace MacUI.ControlCenter;

/// <summary>Drop-down panel with Wi‑Fi, Bluetooth, dark mode, brightness, volume and media controls.</summary>
public partial class ControlCenterWindow : Shell.OverlayWindow
{
    private bool _updating;
    private DateTime _shownAt;
    private DateTime _hiddenAt;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };

    public ControlCenterWindow() : base(Shell.SurfaceKind.Popup, noActivate: false)
    {
        FallbackCornerRadius = 16;
        InitializeComponent();
        Deactivated += (_, _) => { if ((DateTime.Now - _shownAt).TotalMilliseconds > 150) HidePanel(); };
        _refresh.Tick += async (_, _) => await RefreshAsync(lightweight: true);
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

    private void ShowPanel(Forms.Screen screen)
    {
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
    }

    public void HidePanel()
    {
        _refresh.Stop();
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
                BluetoothState.Text = bt == null ? "Unavailable" : bt == true ? "On" : "Off";

                int? brightness = SystemStatus.GetBrightness();
                BrightnessSlider.IsEnabled = brightness != null; // external monitors: no brightness control
                BrightnessSlider.Opacity = brightness == null ? 0.4 : 1;
                if (brightness != null) BrightnessSlider.Value = brightness.Value;
            }

            WifiName.Text = net.Connected ? (net.IsWifi ? net.Name ?? "Connected" : "Ethernet: " + (net.Name ?? "Connected"))
                : WifiToggle.IsChecked == true ? "Not connected" : "Off";

            bool stage = StageManager.StageManager.Instance.Enabled;
            StageToggle.IsChecked = stage;

            DarkToggle.IsChecked = ThemeManager.IsDark; // what PearUI shows, not only what Windows is set to

            float v = AudioVolume.GetVolume();
            bool mute = AudioVolume.GetMute();
            VolumeSlider.IsEnabled = v >= 0;
            if (v >= 0 && !VolumeSlider.IsMouseCaptureWithin) VolumeSlider.Value = Math.Round(v * 100);
            MuteGlyph.Text = mute ? "" : "";
            VolumeSlider.Glyph = VolumeIcon(v * 100, mute);

            var media = await SystemStatus.GetMediaAsync();
            bool hasMedia = media != null && !string.IsNullOrWhiteSpace(media.Title);
            MediaTitle.Text = hasMedia ? media.Title : "Not Playing";
            MediaArtist.Text = hasMedia ? media.Artist : "";
            PlayGlyph.Text = hasMedia && media.Playing ? "" : "";
        }
        finally { _updating = false; }
    }

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
