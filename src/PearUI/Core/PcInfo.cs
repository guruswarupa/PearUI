using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PearUI.Core;

/// <summary>Everything "About This PC" shows, read once from Windows (WMI, the registry and .NET). Slow: call from a background thread.</summary>
public sealed class PcInfo
{
    public sealed record Module(string Slot, string Bank, long Bytes, int SpeedMhz, int ConfiguredMhz, string Type, string FormFactor, string Maker, string Part, string Serial);
    public sealed record Gpu(string Name, long VramBytes, string Driver, string DriverDate, string Processor);
    public sealed record Disk(string Name, string Kind, string Bus, long Bytes, string Health, string Serial);
    public sealed record Volume(string Letter, string Label, string FileSystem, long Bytes, long FreeBytes);
    public sealed record Display(string Name, string Resolution, int RefreshHz, bool Primary);
    public sealed record NetAdapter(string Name, string Kind, string Mac, string Ipv4, long SpeedBps);

    // overview
    public string DeviceName, Manufacturer, Model, Serial, BiosVersion, BiosDate, Board, Domain, User;
    public string OsName, OsVersion, OsBuild, OsArch, InstallDate;
    public TimeSpan Uptime;

    // processor
    public string CpuName, CpuMaker, CpuSocket, CpuArch;
    public int CpuCores, CpuThreads, CpuBaseMhz, CpuL2Kb, CpuL3Kb;
    public bool? CpuVirtualization;

    // memory
    public long RamTotal, RamAvailable, RamMaxCapacity;
    public int RamSlots;
    public readonly List<Module> Modules = new();

    // everything else
    public readonly List<Gpu> Gpus = new();
    public readonly List<Disk> Disks = new();
    public readonly List<Volume> Volumes = new();
    public readonly List<Display> Displays = new();
    public readonly List<NetAdapter> Network = new();

    /// <summary>Capacity of the installed memory modules (a round number, unlike the slightly smaller amount Windows can use).</summary>
    public long RamInstalled => Modules.Count > 0 ? Modules.Sum(m => m.Bytes) : RamTotal;

    // wireless
    public string WifiAdapter, WifiNetwork, BluetoothAdapter;
    public bool? WifiOn, BluetoothOn;

    // battery
    public bool HasBattery;
    public int BatteryPercent, BatteryCycles;
    public long BatteryDesignMwh, BatteryFullMwh;
    public string BatteryName;

    // ---------- reading ----------
    private static List<ManagementBaseObject> Query(string query, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query));
            foreach (var o in s.Get()) list.Add(o);
        }
        catch { }
        return list;
    }

    private static string Str(ManagementBaseObject o, string name) => (o?[name] as string)?.Trim() ?? "";
    private static long Long(ManagementBaseObject o, string name) { try { return Convert.ToInt64(o?[name] ?? 0); } catch { return 0; } }
    private static int Int(ManagementBaseObject o, string name) => (int)Long(o, name);

    private static DateTime? Wmi(ManagementBaseObject o, string name)
    {
        try { return o?[name] is string s && s.Length >= 14 ? ManagementDateTimeConverter.ToDateTime(s) : null; } catch { return null; }
    }

    public static PcInfo Gather()
    {
        var p = new PcInfo();
        p.ReadSystem();
        p.ReadProcessor();
        p.ReadMemory();
        p.ReadGraphics();
        p.ReadStorage();
        p.ReadDisplays();
        p.ReadBattery();
        p.ReadNetwork();
        p.ReadWireless();
        return p;
    }

    private void ReadSystem()
    {
        var cs = Query("SELECT Name, Manufacturer, Model, Domain, UserName, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        DeviceName = Str(cs, "Name");
        if (DeviceName.Length == 0) DeviceName = Environment.MachineName;
        Manufacturer = Str(cs, "Manufacturer");
        Model = Str(cs, "Model");
        Domain = Str(cs, "Domain");
        User = Str(cs, "UserName");
        if (User.Length == 0) User = Environment.UserName;
        RamTotal = Long(cs, "TotalPhysicalMemory");

        var os = Query("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, FreePhysicalMemory FROM Win32_OperatingSystem").FirstOrDefault();
        OsName = Str(os, "Caption").Replace("Microsoft ", "");
        OsArch = Str(os, "OSArchitecture");
        var installed = Wmi(os, "InstallDate");
        InstallDate = installed?.ToString("d MMMM yyyy") ?? "";
        var boot = Wmi(os, "LastBootUpTime");
        Uptime = boot != null ? DateTime.Now - boot.Value : TimeSpan.FromMilliseconds(Environment.TickCount64);
        RamAvailable = Long(os, "FreePhysicalMemory") * 1024;

        string display = "", ubr = "";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
            ubr = k?.GetValue("UBR")?.ToString() ?? "";
        }
        catch { }
        OsVersion = display;
        string build = Str(os, "BuildNumber");
        OsBuild = ubr.Length > 0 ? $"{build}.{ubr}" : build;

        var bios = Query("SELECT SMBIOSBIOSVersion, Manufacturer, ReleaseDate, SerialNumber FROM Win32_BIOS").FirstOrDefault();
        BiosVersion = (Str(bios, "Manufacturer") + " " + Str(bios, "SMBIOSBIOSVersion")).Trim();
        BiosDate = Wmi(bios, "ReleaseDate")?.ToString("d MMM yyyy") ?? "";
        Serial = Str(bios, "SerialNumber");

        var board = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        Board = (Str(board, "Manufacturer") + " " + Str(board, "Product")).Trim();
    }

    private void ReadProcessor()
    {
        var cpus = Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, Architecture, VirtualizationFirmwareEnabled FROM Win32_Processor");
        var c = cpus.FirstOrDefault();
        if (c == null) return;
        CpuName = Regex(Str(c, "Name"));
        CpuMaker = Str(c, "Manufacturer");
        CpuCores = cpus.Sum(x => Int(x, "NumberOfCores"));
        CpuThreads = cpus.Sum(x => Int(x, "NumberOfLogicalProcessors"));
        CpuBaseMhz = Int(c, "MaxClockSpeed");
        // Windows reports the speed it is running at right now; the processor's name carries the rated base speed ("@ 2.40GHz").
        var rated = System.Text.RegularExpressions.Regex.Match(CpuName, @"@\s*([\d.]+)\s*GHz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (rated.Success && double.TryParse(rated.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ghz)) CpuBaseMhz = (int)Math.Round(ghz * 1000);
        CpuL2Kb = Int(c, "L2CacheSize");
        CpuL3Kb = Int(c, "L3CacheSize");
        CpuSocket = Str(c, "SocketDesignation");
        CpuArch = Int(c, "Architecture") switch { 0 => "x86", 5 => "ARM", 9 => "x64", 12 => "ARM64", _ => "" };
        try { if (c["VirtualizationFirmwareEnabled"] is bool b) CpuVirtualization = b; } catch { }
    }

    private static string Regex(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    private static string MemType(int smbios, int speed) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5",
        27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5",
        _ => speed >= 4800 ? "DDR5" : speed >= 2133 ? "DDR4" : "",
    };

    private void ReadMemory()
    {
        foreach (var m in Query("SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SerialNumber, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
        {
            int speed = Int(m, "Speed"), configured = Int(m, "ConfiguredClockSpeed");
            string form = Int(m, "FormFactor") switch { 8 => "DIMM", 12 => "SODIMM", 13 => "SRIMM", 0 or 2 or 6 => "Soldered / other", _ => "Other" };
            Modules.Add(new Module(Str(m, "DeviceLocator"), Str(m, "BankLabel"), Long(m, "Capacity"), speed, configured,
                MemType(Int(m, "SMBIOSMemoryType"), Math.Max(speed, configured)), form, CleanMaker(Str(m, "Manufacturer")), Str(m, "PartNumber"), Str(m, "SerialNumber")));
        }
        var array = Query("SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray").FirstOrDefault();
        if (array != null)
        {
            RamSlots = Int(array, "MemoryDevices");
            long max = Long(array, "MaxCapacityEx");            // kilobytes (the older field overflows at 2 TB)
            if (max == 0) max = Long(array, "MaxCapacity");
            RamMaxCapacity = max * 1024;
        }
        if (RamSlots < Modules.Count) RamSlots = Modules.Count;
    }

    /// <summary>Memory makers often report a JEDEC id (such as 80CE for Samsung) or a placeholder instead of a name.</summary>
    private static string CleanMaker(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        if (s is "Unknown" or "Undefined" or "0000" or "FFFF" or "Manufacturer" or "Not Specified") return "";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9A-Fa-f]{4,}$"))
        {
            return s[..4].ToUpperInvariant() switch
            {
                "80CE" => "Samsung", "802C" => "Micron", "80AD" => "SK hynix", "859B" => "Crucial", "04CD" => "G.Skill",
                "029E" => "Corsair", "0198" => "Kingston", "02CB" => "ADATA", "04EF" => "Team Group", "0194" => "Smart Modular",
                "014F" => "Transcend", "0143" => "Ramaxel", "8551" => "Qimonda", "7F7F" or "0000" => "",
                var id => "JEDEC " + id,
            };
        }
        return s;
    }

    private void ReadGraphics()
    {
        // Win32_VideoController.AdapterRAM stops at 4 GB; the registry holds the real dedicated memory.
        var vram = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!int.TryParse(name, out _)) continue;
                using var k = cls.OpenSubKey(name);
                string desc = k?.GetValue("DriverDesc") as string;
                var raw = k?.GetValue("HardwareInformation.qwMemorySize");
                if (desc == null || raw == null) continue;
                long bytes = raw is byte[] b && b.Length >= 8 ? BitConverter.ToInt64(b, 0) : Convert.ToInt64(raw);
                vram[desc] = bytes;
            }
        }
        catch { }

        foreach (var g in Query("SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoProcessor FROM Win32_VideoController"))
        {
            string name = Str(g, "Name");
            if (name.Length == 0 || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && Gpus.Count > 0) continue;
            long bytes = vram.TryGetValue(name, out var v) ? v : Long(g, "AdapterRAM");
            Gpus.Add(new Gpu(name, bytes, Str(g, "DriverVersion"), Wmi(g, "DriverDate")?.ToString("d MMM yyyy") ?? "", Str(g, "VideoProcessor")));
        }
    }

    private void ReadStorage()
    {
        foreach (var d in Query("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus, SerialNumber FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            string kind = Int(d, "MediaType") switch { 3 => "Hard disk", 4 => "SSD", 5 => "Storage class memory", _ => "Disk" };
            string bus = Int(d, "BusType") switch { 1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 11 => "SATA", 17 => "NVMe", 12 => "SD", _ => "" };
            if (bus == "NVMe" && kind == "Disk") kind = "SSD";
            string health = Int(d, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "" };
            Disks.Add(new Disk(Str(d, "FriendlyName"), kind, bus, Long(d, "Size"), health, Str(d, "SerialNumber")));
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                Volumes.Add(new Volume(drive.Name.TrimEnd('\\'), drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch { }
        }
    }

    private void ReadDisplays()
    {
        var names = new List<string>();
        foreach (var m in Query("SELECT ManufacturerName, UserFriendlyName FROM WmiMonitorID", @"root\wmi"))
        {
            try
            {
                string Decode(string field) => m[field] is ushort[] u ? new string(u.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
                string model = Decode("UserFriendlyName"), maker = Decode("ManufacturerName");
                names.Add(model.Length > 0 ? model : maker);
            }
            catch { names.Add(""); }
        }

        int i = 0;
        foreach (var screen in Forms.Screen.AllScreens)
        {
            string name = i < names.Count && names[i].Length > 0 ? names[i] : "Display " + (i + 1);
            Displays.Add(new Display(name, $"{screen.Bounds.Width} × {screen.Bounds.Height}", RefreshRate(screen.DeviceName), screen.Primary));
            i++;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

    private static int RefreshRate(string device)
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettings(device, -1, ref dm) ? (int)dm.dmDisplayFrequency : 0;
        }
        catch { return 0; }
    }

    private void ReadBattery()
    {
        var b = Query("SELECT Name, EstimatedChargeRemaining FROM Win32_Battery").FirstOrDefault();
        if (b == null) return;
        HasBattery = true;
        BatteryName = Str(b, "Name");
        BatteryPercent = Int(b, "EstimatedChargeRemaining");
        BatteryDesignMwh = Query("SELECT DesignedCapacity FROM BatteryStaticData", @"root\wmi").Select(x => Long(x, "DesignedCapacity")).FirstOrDefault();
        BatteryFullMwh = Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi").Select(x => Long(x, "FullChargedCapacity")).FirstOrDefault();
        BatteryCycles = (int)Query("SELECT CycleCount FROM BatteryCycleCount", @"root\wmi").Select(x => Long(x, "CycleCount")).FirstOrDefault();
    }

    private void ReadWireless()
    {
        try
        {
            WifiAdapter = Network.FirstOrDefault(n => n.Kind == "Wi-Fi")?.Name ?? "";
            var net = SystemStatus.GetNetwork();
            if (net.Connected && net.IsWifi) WifiNetwork = net.Name ?? "";
            WifiOn = SystemStatus.GetRadioStateAsync(Windows.Devices.Radios.RadioKind.WiFi).GetAwaiter().GetResult();
            BluetoothOn = SystemStatus.GetRadioStateAsync(Windows.Devices.Radios.RadioKind.Bluetooth).GetAwaiter().GetResult();
        }
        catch { }

        // The Bluetooth adapter's own name (not the many helper entries Windows lists under Bluetooth).
        foreach (var e in Query("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'"))
        {
            string name = Str(e, "Name");
            if (name.Length == 0) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, "Enumerator|RFCOMM|Protocol|Generic|Service|Device$|Gateway|Profile|Transport|LE ", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            if (name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) { BluetoothAdapter = name; break; }
        }
        if (BluetoothAdapter == null && BluetoothOn != null) BluetoothAdapter = "Bluetooth";
    }

    private void ReadNetwork()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string desc = n.Description;
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || desc.Contains("VPN", StringComparison.OrdinalIgnoreCase)) continue;
                string ip = n.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                string mac = string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2")));
                string kind = n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : n.NetworkInterfaceType is NetworkInterfaceType.Ethernet ? "Ethernet" : n.NetworkInterfaceType.ToString();
                Network.Add(new NetAdapter(n.Name + " (" + desc + ")", kind, mac, ip, n.Speed));
            }
        }
        catch { }
    }

    // ---------- helpers for showing it ----------
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1073741824.0;
        if (gb >= 1000) return $"{gb / 1024:0.##} TB";
        if (gb >= 1) return gb >= 100 ? $"{gb:0} GB" : $"{gb:0.#} GB";
        return $"{bytes / 1048576.0:0} MB";
    }

    public string UptimeText => Uptime.TotalDays >= 1 ? $"{(int)Uptime.TotalDays} d {Uptime.Hours} h {Uptime.Minutes} min" : $"{Uptime.Hours} h {Uptime.Minutes} min";

    public string RamSummary
    {
        get
        {
            string type = Modules.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "";
            int speed = Modules.Select(m => m.ConfiguredMhz > 0 ? m.ConfiguredMhz : m.SpeedMhz).FirstOrDefault();
            return $"{Bytes(RamInstalled)} {type}{(speed > 0 ? " " + speed + " MHz" : "")}".Trim();
        }
    }
}
