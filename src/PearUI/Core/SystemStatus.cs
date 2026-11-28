using System.Management;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Media.Control;
using Windows.Networking.Connectivity;
using Forms = System.Windows.Forms;

namespace PearUI.Core;

public record BatteryInfo(bool HasBattery, int Percent, bool Charging, bool PluggedIn);
public record NetworkInfo(bool Connected, bool IsWifi, string Name);
public record MediaInfo(string Title, string Artist, bool Playing, string AppId);
public record MediaDetail(string Title, string Artist, string Album, bool Playing, TimeSpan Position, TimeSpan Duration,
    bool? Shuffle, System.Windows.Media.Imaging.BitmapImage Art, bool CanPrevious, bool CanNext, bool CanSeek);

/// <summary>Read and change system state: battery, network, radios, brightness, media, dark mode.</summary>
public static class SystemStatus
{
    // ---------- battery ----------
    public static BatteryInfo GetBattery()
    {
        var ps = Forms.SystemInformation.PowerStatus;
        bool has = ps.BatteryChargeStatus != Forms.BatteryChargeStatus.NoSystemBattery
                   && ps.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;
        int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
        bool plugged = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
        bool charging = (ps.BatteryChargeStatus & Forms.BatteryChargeStatus.Charging) != 0;
        return new BatteryInfo(has, Math.Clamp(pct, 0, 100), charging, plugged);
    }

    // ---------- network ----------
    public static NetworkInfo GetNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return new NetworkInfo(false, false, null);
            var level = profile.GetNetworkConnectivityLevel();
            bool connected = level is NetworkConnectivityLevel.InternetAccess or NetworkConnectivityLevel.ConstrainedInternetAccess
                or NetworkConnectivityLevel.LocalAccess;
            bool wifi = profile.IsWlanConnectionProfile;
            string name = wifi ? profile.WlanConnectionProfileDetails?.GetConnectedSsid() : profile.ProfileName;
            return new NetworkInfo(connected, wifi, string.IsNullOrEmpty(name) ? profile.ProfileName : name);
        }
        catch { return new NetworkInfo(false, false, null); }
    }

    // ---------- radios (Wi-Fi / Bluetooth) ----------
    public static async Task<Radio> GetRadioAsync(RadioKind kind)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return null;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch { return null; }
    }

    public static async Task<bool?> GetRadioStateAsync(RadioKind kind)
    {
        var r = await GetRadioAsync(kind);
        return r == null ? null : r.State == RadioState.On;
    }

    public static async Task SetRadioStateAsync(RadioKind kind, bool on)
    {
        var r = await GetRadioAsync(kind);
        if (r != null) await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
    }

    // ---------- brightness (internal displays only, via WMI) ----------
    public static int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void SetBrightness(int percent)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
        }
        catch { }
    }

    // ---------- media ----------
    private static GlobalSystemMediaTransportControlsSessionManager _mediaManager;

    /// <summary>Raised (on a background thread) when the track, play state or active player changes.</summary>
    public static event Action MediaChanged;
    private static readonly HashSet<GlobalSystemMediaTransportControlsSession> _hookedSessions = new();
    private static System.Threading.Timer _mediaDebounce;

    private static void RaiseMediaChanged()
    {
        // A track change fires several events in a row: coalesce them into one update.
        lock (_hookedSessions)
        {
            _mediaDebounce?.Dispose();
            _mediaDebounce = new System.Threading.Timer(_ => { try { MediaChanged?.Invoke(); } catch { } }, null, 80, System.Threading.Timeout.Infinite);
        }
    }

    private static void HookSessions()
    {
        try
        {
            var current = _mediaManager.GetSessions().ToList();
            lock (_hookedSessions)
            {
                _hookedSessions.RemoveWhere(s => !current.Contains(s));
                foreach (var s in current)
                    if (_hookedSessions.Add(s))
                    {
                        s.MediaPropertiesChanged += (_, _) => RaiseMediaChanged();
                        s.PlaybackInfoChanged += (_, _) => RaiseMediaChanged();
                    }
            }
        }
        catch { }
    }

    /// <summary>Starts listening for media changes (call once at startup; the first use also does it).</summary>
    public static async Task InitMediaAsync()
    {
        if (_mediaManager != null) return;
        try
        {
            var m = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_mediaManager != null) return;
            _mediaManager = m;
            m.CurrentSessionChanged += (_, _) => { HookSessions(); RaiseMediaChanged(); };
            m.SessionsChanged += (_, _) => { HookSessions(); RaiseMediaChanged(); };
            HookSessions();
            RaiseMediaChanged();
        }
        catch { }
    }

    /// <summary>The player that is actually playing (the "current" session can be an idle one).</summary>
    private static async Task<GlobalSystemMediaTransportControlsSession> GetMediaSessionAsync()
    {
        try
        {
            await InitMediaAsync();
            if (_mediaManager == null) return null;
            var cur = _mediaManager.GetCurrentSession();
            if (cur?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return cur;
            var playing = _mediaManager.GetSessions().FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            return playing ?? cur ?? _mediaManager.GetSessions().FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Last resort when a player does not answer the session API: the keyboard media keys.</summary>
    private static void SendMediaKey(byte vk)
    {
        Native.Win32.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Native.Win32.keybd_event(vk, 0, (uint)Native.Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static async Task<MediaInfo> GetMediaAsync()
    {
        var s = await GetMediaSessionAsync();
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var playback = s.GetPlaybackInfo();
            return new MediaInfo(props?.Title, props?.Artist,
                playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                s.SourceAppUserModelId);
        }
        catch { return null; }
    }

    /// <summary>Full now-playing details for the media popup (cover art, album, position).</summary>
    public static async Task<MediaDetail> GetMediaDetailAsync()
    {
        var s = await GetMediaSessionAsync();
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var playback = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var pos = tl?.Position ?? TimeSpan.Zero;
            if (playing && tl != null) pos += DateTimeOffset.Now - tl.LastUpdatedTime;
            var dur = tl == null ? TimeSpan.Zero : tl.EndTime - tl.StartTime;
            if (dur > TimeSpan.Zero && pos > dur) pos = dur;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

            System.Windows.Media.Imaging.BitmapImage art = null;
            try
            {
                if (props?.Thumbnail != null)
                {
                    using var rs = await props.Thumbnail.OpenReadAsync();
                    using var ms = new System.IO.MemoryStream();
                    await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(rs).CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    art = bmp;
                }
            }
            catch { }

            var c = playback?.Controls;
            return new MediaDetail(props?.Title, props?.Artist, props?.AlbumTitle, playing, pos, dur,
                playback?.IsShuffleActive, art, c?.IsPreviousEnabled ?? true, c?.IsNextEnabled ?? true,
                c?.IsPlaybackPositionEnabled ?? false);
        }
        catch { return null; }
    }

    public static async Task MediaSeekAsync(TimeSpan position) { var s = await GetMediaSessionAsync(); if (s != null) await s.TryChangePlaybackPositionAsync(position.Ticks); }
    public static async Task MediaShuffleAsync(bool on) { var s = await GetMediaSessionAsync(); if (s != null) await s.TryChangeShuffleActiveAsync(on); }

    public static async Task MediaPlayPauseAsync()
    {
        bool ok = false;
        var s = await GetMediaSessionAsync();
        if (s != null)
        {
            try
            {
                bool playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                ok = playing ? await s.TryPauseAsync() : await s.TryPlayAsync();
                if (!ok) ok = await s.TryTogglePlayPauseAsync();
            }
            catch { }
        }
        if (!ok) SendMediaKey(Native.Win32.VK_MEDIA_PLAY_PAUSE);
    }

    public static async Task MediaNextAsync()
    {
        bool ok = false;
        var s = await GetMediaSessionAsync();
        if (s != null) try { ok = await s.TrySkipNextAsync(); } catch { }
        if (!ok) SendMediaKey(Native.Win32.VK_MEDIA_NEXT_TRACK);
    }

    public static async Task MediaPreviousAsync()
    {
        bool ok = false;
        var s = await GetMediaSessionAsync();
        if (s != null) try { ok = await s.TrySkipPreviousAsync(); } catch { }
        if (!ok) SendMediaKey(Native.Win32.VK_MEDIA_PREV_TRACK);
    }

    // ---------- dark mode ----------
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsSystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Switches Windows itself (apps + system UI) between light and dark.</summary>
    // ---------- Do Not Disturb (Windows' "Notifications" master switch: toast banners and sounds) ----------
    private const string PushKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    public static bool GetDoNotDisturb()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PushKey);
            return k?.GetValue("ToastEnabled") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void SetDoNotDisturb(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PushKey);
            k.SetValue("ToastEnabled", on ? 0 : 1, RegistryValueKind.DWord);
            Native.Win32.SendMessageTimeout(new IntPtr(0xFFFF), Native.Win32.WM_SETTINGCHANGE, IntPtr.Zero,
                "WindowsNotifications", 0x0002 /*SMTO_ABORTIFHUNG*/, 200, out _);
        }
        catch { }
    }

    public static void SetSystemDark(bool dark)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            Native.Win32.SendMessageTimeout(new IntPtr(0xFFFF), Native.Win32.WM_SETTINGCHANGE, IntPtr.Zero,
                "ImmersiveColorSet", 0x0002 /*SMTO_ABORTIFHUNG*/, 200, out _);
        }
        catch { }
    }

    /// <summary>The Windows accent color (DWM), or null.</summary>
    public static System.Windows.Media.Color? GetSystemAccent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
            {
                byte r = (byte)(abgr & 0xFF), g = (byte)((abgr >> 8) & 0xFF), b = (byte)((abgr >> 16) & 0xFF);
                return System.Windows.Media.Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return null;
    }
}
