using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PearUI.Core;

/// <summary>
/// The <c>info</c> command of PearUI Terminal (run as "PearUI.exe --info"): a pear logo next to the
/// essentials of this PC, drawn with colours in the style of fastfetch and neofetch.
/// Everything is read from the registry and the runtime, so it appears instantly.
/// </summary>
public static class SystemInfo
{
    private const string Esc = "\u001b[";
    private static string Rgb(int r, int g, int b) => $"{Esc}38;2;{r};{g};{b}m";
    private static readonly string Reset = Esc + "0m";
    // The terminal can be light or dark, and the colours have to read well on both: pale tones on a dark
    // background, deeper ones on a light background.
    private static readonly bool Light = DetectLight();
    private static readonly string Label = Light ? Rgb(34, 120, 50) : Rgb(126, 214, 106);
    private static readonly string Dim = Light ? Rgb(100, 100, 112) : Rgb(140, 140, 150);
    private static readonly string Accent = Light ? Rgb(52, 112, 22) : Rgb(190, 240, 120);

    /// <summary>Is the terminal light? Follows PearUI's own theme setting, or Windows' when that is left on "System".</summary>
    private static bool DetectLight()
    {
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PearUI", "settings.json");
            if (File.Exists(file))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Theme", out var t))
                {
                    if (t.GetString() == "Light") return true;
                    if (t.GetString() == "Dark") return false;
                }
            }
        }
        catch { }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") as int? != 0;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleOutputCP(uint cp);

    public static void Print()
    {
        Stream stdout;
        try { stdout = Console.OpenStandardOutput(); }
        catch { return; }
        if (stdout == Stream.Null) { AttachConsole(-1); stdout = Console.OpenStandardOutput(); }

        int width = 100;
        try { if (!Console.IsOutputRedirected) width = Console.WindowWidth; } catch { }

        var info = Gather();
        // The pear is drawn as wide as the console allows (up to 44 characters), leaving room for the details.
        int logoCols = Math.Min(44, width - 70);
        bool showLogo = logoCols >= 16;
        var logo = showLogo ? Logo(logoCols) : new List<(string Text, int Width)>();

        var sb = new StringBuilder();
        sb.Append('\n');
        int rows = Math.Max(info.Count, logo.Count);
        int logoWidth = logo.Count > 0 ? logo.Max(l => l.Width) : 0;
        for (int i = 0; i < rows; i++)
        {
            sb.Append("  ");
            if (showLogo)
            {
                if (i < logo.Count) { sb.Append(logo[i].Text); sb.Append(new string(' ', logoWidth - logo[i].Width)); }
                else sb.Append(new string(' ', logoWidth));
                sb.Append("   ");
            }
            if (i < info.Count) sb.Append(info[i]);
            sb.Append(Reset).Append('\n');
        }
        sb.Append('\n');

        // Command Prompt's console uses an old code page by default; switch it to UTF-8 while the blocks are printed.
        uint oldPage = GetConsoleOutputCP();
        if (oldPage != 0) SetConsoleOutputCP(65001);
        var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
        if (oldPage != 0) SetConsoleOutputCP(oldPage);
    }

    // ================= logo =================
    // The pear from the app icon, drawn with quadrant block characters: every character cell shows 2 x 2 pixels,
    // coloured with two colours (character and background), so curves are as smooth as a terminal can make them.
    private static readonly char[] Quadrants = { ' ', '▘', '▝', '▀', '▖', '▌', '▞', '▛', '▗', '▚', '▐', '▜', '▄', '▙', '▟', '█' };

    /// <param name="cols">Width in character cells; the height follows from the shape of a cell, so the pear is not squashed.</param>
    private static List<(string Text, int Width)> Logo(int cols)
    {
        int rows = (int)Math.Round(cols * 8 / 19.0);   // a cell is about 8 x 19 pixels
        int w = cols * 2, h = rows * 2;
        var rowsOut = new List<(string, int)>();
        using var bmp = Shell.PearLogo.CreateGlyphBitmap(w, h, Light);

        // Soft edges are blended against the terminal's background, so they look smooth on dark and light themes.
        bool light = Light;
        int bgR = light ? 247 : 28, bgG = light ? 247 : 30, bgB = light ? 248 : 36;

        // r, g, b of one pixel; null when it is (nearly) transparent
        (int R, int G, int B)? Px(int x, int y)
        {
            var c = bmp.GetPixel(x, y);
            if (c.A < 28) return null;
            double a = c.A / 255.0;
            return ((int)(c.R * a + bgR * (1 - a)), (int)(c.G * a + bgG * (1 - a)), (int)(c.B * a + bgB * (1 - a)));
        }

        for (int cy = 0; cy < rows; cy++)
        {
            var sb = new StringBuilder();
            for (int cx = 0; cx < cols; cx++)
            {
                // bit 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right
                var p = new (int R, int G, int B)?[] { Px(cx * 2, cy * 2), Px(cx * 2 + 1, cy * 2), Px(cx * 2, cy * 2 + 1), Px(cx * 2 + 1, cy * 2 + 1) };
                int solid = 0;
                for (int i = 0; i < 4; i++) if (p[i] != null) solid |= 1 << i;

                if (solid == 0) { sb.Append(Esc).Append("0m "); continue; }

                if (solid != 15)
                {
                    // part of the cell is empty: the visible pixels share one colour, the rest stays transparent
                    var (r, g, b) = Mean(p, solid);
                    sb.Append($"{Esc}0;38;2;{r};{g};{b}m{Quadrants[solid]}");
                    continue;
                }

                // a full cell: pick the split into two colours that loses the least detail
                int best = 15; double bestCost = double.MaxValue;
                for (int mask = 1; mask < 15; mask++)
                {
                    double cost = Cost(p, mask) + Cost(p, 15 ^ mask);
                    if (cost < bestCost) { bestCost = cost; best = mask; }
                }
                double whole = Cost(p, 15);
                if (whole <= bestCost + 12) best = 15;   // nearly one colour anyway

                var fg = Mean(p, best);
                if (best == 15) sb.Append($"{Esc}0;38;2;{fg.R};{fg.G};{fg.B}m{Quadrants[15]}");
                else
                {
                    var bg = Mean(p, 15 ^ best);
                    sb.Append($"{Esc}38;2;{fg.R};{fg.G};{fg.B};48;2;{bg.R};{bg.G};{bg.B}m{Quadrants[best]}");
                }
            }
            sb.Append(Reset);
            rowsOut.Add((sb.ToString(), cols));
        }
        return rowsOut;
    }

    private static (int R, int G, int B) Mean((int R, int G, int B)?[] p, int mask)
    {
        int n = 0, r = 0, g = 0, b = 0;
        for (int i = 0; i < 4; i++)
            if ((mask & (1 << i)) != 0 && p[i] is { } c) { n++; r += c.R; g += c.G; b += c.B; }
        return n == 0 ? (0, 0, 0) : (r / n, g / n, b / n);
    }

    /// <summary>How far the pixels in <paramref name="mask"/> are from their own average colour.</summary>
    private static double Cost((int R, int G, int B)?[] p, int mask)
    {
        var m = Mean(p, mask);
        double sum = 0;
        for (int i = 0; i < 4; i++)
            if ((mask & (1 << i)) != 0 && p[i] is { } c) sum += Math.Pow(c.R - m.R, 2) + Math.Pow(c.G - m.G, 2) + Math.Pow(c.B - m.B, 2);
        return sum;
    }

    // ================= details =================
    private static List<string> Gather()
    {
        var lines = new List<string>();
        void Add(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lines.Add($"{Label}{label,-10}{Reset}{value}");
        }

        string user = Environment.UserName, host = Environment.MachineName;
        lines.Add($"{Accent}\u001b[1m{user}{Reset}{Dim}@{Reset}{Accent}\u001b[1m{host}{Reset}");
        lines.Add(Dim + new string('─', user.Length + host.Length + 1) + Reset);

        Add("OS", Safe(OsName));
        Add("Host", Safe(HostModel));
        Add("Kernel", $"Windows NT {Environment.OSVersion.Version}");
        Add("Uptime", Uptime());
        Add("Apps", Safe(AppCount));
        Add("Shell", Safe(ShellName));
        Add("Display", Safe(Displays));
        Add("Desktop", Safe(() => "PearUI " + (typeof(SystemInfo).Assembly.GetName().Version?.ToString(3) ?? "")));
        Add("Theme", Light ? "Light" : "Dark");
        Add("Terminal", Environment.GetEnvironmentVariable("TERM_PROGRAM") == "PearUI" ? "PearUI Terminal" : Safe(() => Environment.GetEnvironmentVariable("WT_SESSION") != null ? "Windows Terminal" : "Console"));
        Add("CPU", Safe(Cpu));
        Add("GPU", Safe(Gpu));
        Add("Memory", Safe(Memory));
        Add("Disk", Safe(Disk));
        Add("Battery", Safe(Battery));
        Add("Network", Safe(LocalIp));

        lines.Add("");
        lines.Add(Swatch(40) );
        lines.Add(Swatch(100));
        return lines;
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch { return null; }
    }

    private static string Swatch(int baseCode)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++) sb.Append($"{Esc}{baseCode + i}m   ");
        return sb.Append(Reset).ToString();
    }

    private static string Bar(double fraction, int cells = 10, bool fullIsGood = false)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        int filled = (int)Math.Round(fraction * cells);
        double load = fullIsGood ? 1 - fraction : fraction;   // for a battery, a low charge is the warning
        string color = Light
            ? (load < 0.6 ? Rgb(40, 150, 64) : load < 0.85 ? Rgb(190, 140, 10) : Rgb(204, 56, 46))
            : (load < 0.6 ? Rgb(110, 214, 110) : load < 0.85 ? Rgb(236, 200, 80) : Rgb(236, 100, 90));
        string empty = Light ? Rgb(206, 208, 216) : Rgb(70, 70, 78);
        return color + new string('█', filled) + empty + new string('█', cells - filled) + Reset;
    }

    private static string OsName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = k?.GetValue("ProductName") as string ?? "Windows";
        string display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string;
        string build = k?.GetValue("CurrentBuild") as string;
        // Windows 11 still reports "Windows 10" in ProductName; the build number tells them apart.
        if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
        return $"{product} {display} {Dim}(build {build}){Reset} {arch}".Replace("  ", " ");
    }

    private static string HostModel()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string maker = (k?.GetValue("SystemManufacturer") as string)?.Trim();
        string model = (k?.GetValue("SystemProductName") as string)?.Trim();
        if (string.IsNullOrEmpty(model)) return null;
        bool junk = model.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || model.Contains("System Product", StringComparison.OrdinalIgnoreCase);
        return junk ? null : (maker != null && !model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? maker + " " + model : model);
    }

    private static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var parts = new List<string>();
        if (t.Days > 0) parts.Add($"{t.Days} day{(t.Days == 1 ? "" : "s")}");
        if (t.Hours > 0) parts.Add($"{t.Hours} hour{(t.Hours == 1 ? "" : "s")}");
        parts.Add($"{t.Minutes} min{(t.Minutes == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }

    private static string AppCount()
    {
        int n = 0;
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            using var k = hive.OpenSubKey(path);
            if (k == null) continue;
            foreach (var name in k.GetSubKeyNames())
            {
                using var s = k.OpenSubKey(name);
                if (s?.GetValue("DisplayName") != null && s.GetValue("SystemComponent") as int? != 1) n++;
            }
        }
        return n > 0 ? $"{n} installed" : null;
    }

    private static string ShellName()
    {
        // Walk up from this process: skip the batch file's own cmd.exe and report the shell the user is in.
        var chain = new List<string>();
        int pid = Environment.ProcessId;
        for (int i = 0; i < 4; i++)
        {
            pid = ParentOf(pid);
            if (pid <= 0) break;
            try { chain.Add(Process.GetProcessById(pid).ProcessName.ToLowerInvariant()); } catch { break; }
        }
        string[] shells = { "powershell", "pwsh", "cmd", "bash", "zsh", "fish", "sh", "dash", "ksh", "nu", "wsl" };
        // "info.cmd" runs inside its own cmd.exe: when that is the immediate parent, the shell the user is in is the next one up.
        // The batch file (cmd) or script (sh, which may show up twice) that runs "info" is not the shell the user is in: look further up.
        int skip = chain.Count == 0 ? 0 : chain[0] == "cmd" ? 1 : chain[0] == "sh" ? (chain.Count > 2 && chain[1] == "sh" ? 2 : 1) : 0;
        string pick = chain.Skip(skip).FirstOrDefault(n => shells.Contains(n)) ?? chain.FirstOrDefault();
        return pick switch
        {
            "powershell" => "Windows PowerShell",
            "pwsh" => "PowerShell",
            "cmd" => "Command Prompt",
            "bash" => "Bash",
            "zsh" => "Zsh",
            "fish" => "Fish",
            "nu" => "Nushell",
            "wsl" => "WSL",
            null => null,
            _ => pick,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION info, int size, out int ret);

    private static int ParentOf(int pid)
    {
        using var p = Process.GetProcessById(pid);
        var pbi = new PROCESS_BASIC_INFORMATION();
        return NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0 ? (int)pbi.InheritedFromUniqueProcessId : 0;
    }

    private static string Displays()
    {
        var parts = System.Windows.Forms.Screen.AllScreens.Select(s => $"{s.Bounds.Width}x{s.Bounds.Height}");
        return string.Join(", ", parts);
    }

    private static string Cpu()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        string name = (k?.GetValue("ProcessorNameString") as string)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM)\)|\bCPU\b|\s{2,}", " ").Replace("  ", " ").Trim();
        return $"{name} {Dim}({Environment.ProcessorCount} threads){Reset}";
    }

    private static string Gpu()
    {
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Registry.LocalMachine.OpenSubKey(cls);
        if (root == null) return null;
        var names = new List<string>();
        foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4 && char.IsDigit(n[0])))
        {
            using var k = root.OpenSubKey(sub);
            string d = k?.GetValue("DriverDesc") as string;
            if (!string.IsNullOrWhiteSpace(d) && !d.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !d.Contains("Remote", StringComparison.OrdinalIgnoreCase)) names.Add(d.Trim());
        }
        return names.Count == 0 ? null : string.Join(", ", names.Distinct());
    }

    private static string Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        double total = m.ullTotalPhys, used = m.ullTotalPhys - m.ullAvailPhys;
        return $"{used / 1073741824.0:0.0} / {total / 1073741824.0:0.0} GiB  {Bar(used / total)} {Dim}{used / total:P0}{Reset}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    private static string Disk()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory);
        var d = new DriveInfo(root);
        double total = d.TotalSize, used = d.TotalSize - d.AvailableFreeSpace;
        return $"{used / 1073741824.0:0} / {total / 1073741824.0:0} GiB ({root.TrimEnd('\\')})  {Bar(used / total)} {Dim}{used / total:P0}{Reset}";
    }

    private static string Battery()
    {
        var p = System.Windows.Forms.SystemInformation.PowerStatus;
        if (p.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) return null;
        double pct = Math.Clamp(p.BatteryLifePercent, 0, 1);
        string state = p.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "charging" : "on battery";
        return $"{pct:P0} {Bar(pct, fullIsGood: true)} {Dim}{state}{Reset}";
    }

    private static string LocalIp()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
            var ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip != null && ni.GetIPProperties().GatewayAddresses.Count > 0) return $"{ip.Address} {Dim}({ni.NetworkInterfaceType.ToString().Replace("Wireless80211", "Wi-Fi")}){Reset}";
        }
        return null;
    }
}
