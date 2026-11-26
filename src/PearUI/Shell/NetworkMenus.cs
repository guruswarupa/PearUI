using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using PearUI.Core;
using PearUI.Native;

namespace PearUI.Shell;

/// <summary>
/// Right-click menus for the Wi‑Fi and Bluetooth tiles: pick a Wi‑Fi network (asking for the password when
/// a new secured network needs one) or connect / disconnect a paired Bluetooth device.
/// Left-click on the tiles still just turns the radio on or off.
/// </summary>
public static class NetworkMenus
{
    // ===================== Wi‑Fi =====================
    private sealed record WifiNet(string Ssid, int Bars, bool Secured, bool Connected, WiFiAvailableNetwork Raw);

    private static async Task<WiFiAdapter> GetWifiAdapterAsync()
    {
        try
        {
            if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return null;
            return (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Start a scan so the list is fresh by the time the menu is opened.</summary>
    public static async Task PrescanWifiAsync()
    {
        try { var a = await GetWifiAdapterAsync(); if (a != null) await a.ScanAsync(); } catch { }
    }

    public static async Task ShowWifiMenuAsync(int x, int y)
    {
        var entries = new List<GlassMenu.Entry>();
        try
        {
            if (await SystemStatus.GetRadioStateAsync(RadioKind.WiFi) != true)
            {
                entries.Add(new GlassMenu.Entry("Wi‑Fi is turned off", Enabled: false));
            }
            else
            {
                var adapter = await GetWifiAdapterAsync();
                if (adapter == null) entries.Add(new GlassMenu.Entry("No Wi‑Fi adapter found", Enabled: false));
                else
                {
                    string connected = null;
                    try { connected = (await adapter.NetworkAdapter.GetConnectedProfileAsync())?.WlanConnectionProfileDetails?.GetConnectedSsid(); }
                    catch { }

                    var nets = adapter.NetworkReport.AvailableNetworks
                        .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                        .GroupBy(n => n.Ssid)
                        .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                        .Select(n => new WifiNet(n.Ssid, n.SignalBars,
                            n.SecuritySettings.NetworkAuthenticationType != NetworkAuthenticationType.Open80211,
                            n.Ssid == connected, n))
                        .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Bars).ThenBy(n => n.Ssid)
                        .Take(18).ToList();

                    if (nets.Count == 0) entries.Add(new GlassMenu.Entry("No networks found", Enabled: false));
                    foreach (var n in nets)
                    {
                        var net = n;
                        entries.Add(new GlassMenu.Entry(net.Ssid, Gesture: (net.Secured ? "🔒 " : "") + SignalGlyph(net.Bars),
                            Action: net.Connected ? () => adapter.Disconnect() : async () => await ConnectWifiAsync(adapter, net),
                            Checked: net.Connected));
                    }
                    if (connected != null)
                    {
                        entries.Add(null);
                        entries.Add(new GlassMenu.Entry("Disconnect from “" + connected + "”", Action: () => adapter.Disconnect()));
                    }
                }
            }
        }
        catch { entries.Add(new GlassMenu.Entry("Couldn't list networks", Enabled: false)); }

        entries.Add(null);
        entries.Add(new GlassMenu.Entry("Wi‑Fi Settings…", Action: () => Launch.Settings("network-wifi")));
        new GlassMenu(entries).ShowAt(x, y);
    }

    private static string SignalGlyph(int bars) => new string("▂▄▆█".Take(Math.Clamp(bars, 1, 4)).ToArray());

    private static async Task ConnectWifiAsync(WiFiAdapter adapter, WifiNet net)
    {
        try
        {
            // A saved network (or an open one) connects without asking for anything.
            var result = await adapter.ConnectAsync(net.Raw, WiFiReconnectionKind.Automatic);
            if (result.ConnectionStatus == WiFiConnectionStatus.Success) return;

            if (net.Secured && result.ConnectionStatus is WiFiConnectionStatus.InvalidCredential or WiFiConnectionStatus.UnspecifiedFailure)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    string password = await PasswordPrompt.AskAsync(net.Ssid, attempt > 0);
                    if (password == null) return; // cancelled
                    result = await adapter.ConnectAsync(net.Raw, WiFiReconnectionKind.Automatic,
                        new PasswordCredential { Password = password });
                    if (result.ConnectionStatus == WiFiConnectionStatus.Success) return;
                    if (result.ConnectionStatus != WiFiConnectionStatus.InvalidCredential) break;
                }
            }
            MessageBox.Show($"Couldn't connect to “{net.Ssid}” ({result.ConnectionStatus}).", "PearUI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't connect to “{net.Ssid}”.\n\n{ex.Message}", "PearUI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===================== Bluetooth =====================
    private sealed record BtDevice(string Id, string Name, bool Connected);

    private static async Task<List<BtDevice>> ListBluetoothAsync()
    {
        var list = new List<BtDevice>();
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            new[] { "System.Devices.Aep.IsConnected" });
        foreach (var d in infos)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) continue;
            bool connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var v) && v is bool b && b;
            list.Add(new BtDevice(d.Id, d.Name, connected));
        }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    public static async Task ShowBluetoothMenuAsync(int x, int y)
    {
        var entries = new List<GlassMenu.Entry>();
        try
        {
            if (await SystemStatus.GetRadioStateAsync(RadioKind.Bluetooth) != true)
                entries.Add(new GlassMenu.Entry("Bluetooth is turned off", Enabled: false));
            else
            {
                var devices = await ListBluetoothAsync();
                if (devices.Count == 0) entries.Add(new GlassMenu.Entry("No paired devices", Enabled: false));
                foreach (var d in devices)
                {
                    var dev = d;
                    entries.Add(new GlassMenu.Entry(dev.Name, Gesture: dev.Connected ? "Connected" : "Connect",
                        Action: async () => await SetBluetoothConnectedAsync(dev, !dev.Connected), Checked: dev.Connected));
                }
            }
        }
        catch { entries.Add(new GlassMenu.Entry("Couldn't list devices", Enabled: false)); }

        entries.Add(null);
        entries.Add(new GlassMenu.Entry("Bluetooth Settings…", Action: () => Launch.Settings("bluetooth")));
        new GlassMenu(entries).ShowAt(x, y);
    }

    private static async Task SetBluetoothConnectedAsync(BtDevice dev, bool connect)
    {
        bool ok = false;
        try
        {
            using var bt = await BluetoothDevice.FromIdAsync(dev.Id);
            if (bt != null) ok = await Task.Run(() => BluetoothNative.SetAudioServices(bt.BluetoothAddress, connect));
        }
        catch { }
        if (!ok)
            MessageBox.Show(connect
                    ? $"Couldn't connect to “{dev.Name}”. Make sure it's turned on and in range."
                    : $"Couldn't disconnect “{dev.Name}”.",
                "PearUI", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

/// <summary>
/// Connects or disconnects a paired Bluetooth audio device by enabling / disabling its audio services
/// (the same mechanism Windows' own Bluetooth panel uses).
/// </summary>
internal static class BluetoothNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams { public int Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { public ushort Y, M, DW, D, H, Min, S, Ms; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [DllImport("bthprops.cpl")] private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out IntPtr radio);
    [DllImport("bthprops.cpl")] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("bthprops.cpl")] private static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref DeviceInfo info);
    [DllImport("bthprops.cpl")] private static extern uint BluetoothSetServiceState(IntPtr radio, ref DeviceInfo info, ref Guid service, uint flags);

    private static readonly Guid[] AudioServices =
    {
        new("0000110B-0000-1000-8000-00805F9B34FB"), // A2DP sink (headphones, speakers)
        new("0000111E-0000-1000-8000-00805F9B34FB"), // hands-free
        new("00001108-0000-1000-8000-00805F9B34FB"), // headset
    };

    public static bool SetAudioServices(ulong address, bool enable)
    {
        var p = new FindRadioParams { Size = Marshal.SizeOf<FindRadioParams>() };
        IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
        if (find == IntPtr.Zero) return false;
        try
        {
            var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>(), Address = address };
            if (BluetoothGetDeviceInfo(radio, ref info) != 0) return false;

            bool any = false;
            foreach (var g in AudioServices)
            {
                var guid = g;
                // Toggling off then on makes Windows re-establish the link when connecting.
                if (enable) BluetoothSetServiceState(radio, ref info, ref guid, 0);
                if (BluetoothSetServiceState(radio, ref info, ref guid, enable ? 1u : 0u) == 0) any = true;
            }
            return any;
        }
        finally { Win32.CloseHandle(radio); BluetoothFindRadioClose(find); }
    }
}

/// <summary>Small glass window asking for a Wi‑Fi password.</summary>
internal sealed class PasswordPrompt : OverlayWindow
{
    private readonly TaskCompletionSource<string> _tcs = new();
    private readonly PasswordBox _box = new();

    private PasswordPrompt(string ssid, bool wrong) : base(SurfaceKind.Popup, noActivate: false)
    {
        Title = "PearUI Wi‑Fi Password";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        FallbackCornerRadius = 14;

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        panel.Children.Add(new TextBlock { Text = $"Enter the password for “{ssid}”", FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock
        {
            Text = wrong ? "That password didn't work. Try again." : "The Wi‑Fi network is protected.",
            FontSize = 12, Margin = new Thickness(0, 4, 0, 12), Opacity = wrong ? 1 : 0.6,
        };
        if (wrong) hint.Foreground = System.Windows.Media.Brushes.IndianRed;
        panel.Children.Add(hint);

        _box.Padding = new Thickness(8, 6, 8, 6);
        _box.FontSize = 14;
        _box.BorderThickness = new Thickness(0);
        _box.SetResourceReference(Control.BackgroundProperty, "Brush.Field");
        _box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        _box.SetResourceReference(PasswordBox.CaretBrushProperty, "Brush.Text");
        panel.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("FlatButton"), Margin = new Thickness(0, 0, 8, 0) };
        var join = new Button { Content = "Join", Style = (Style)Application.Current.FindResource("AccentButton"), IsDefault = true };
        cancel.Click += (_, _) => Finish(null);
        join.Click += (_, _) => Finish(_box.Password.Length > 0 ? _box.Password : null);
        buttons.Children.Add(cancel);
        buttons.Children.Add(join);
        panel.Children.Add(buttons);
        Content = panel;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(null);
            else if (e.Key == Key.Enter) Finish(_box.Password.Length > 0 ? _box.Password : null);
        };
        Deactivated += (_, _) => Finish(null);
        Closed += (_, _) => _tcs.TrySetResult(null);
    }

    private void Finish(string value)
    {
        _tcs.TrySetResult(value);
        if (IsVisible) Close();
    }

    /// <summary>Returns the entered password, or null if cancelled.</summary>
    public static Task<string> AskAsync(string ssid, bool wrong)
    {
        var w = new PasswordPrompt(ssid, wrong);
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        w.Show();
        double s = w.DpiScale;
        Win32.SetWindowPos(w.Handle, Win32.HWND_TOPMOST,
            screen.Bounds.Left + (screen.Bounds.Width - (int)(w.ActualWidth * s)) / 2,
            screen.Bounds.Top + (int)(screen.Bounds.Height * 0.25), 0, 0, Win32.SWP_NOSIZE);
        w.Activate();
        Win32.ActivateWindow(w.Handle);
        w._box.Focus();
        return w._tcs.Task;
    }
}
