using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IoPath = System.IO.Path;
using PearUI.Core;
using PearUI.Shell;
using PearUI.Themes;

namespace PearUI.Settings;

/// <summary>
/// "About This PC", styled after macOS's About This Mac: a small frosted-glass window with your wallpaper in a circle,
/// the system's name in large type and the essentials beneath, and tabs for the full detail of every part of the computer.
/// </summary>
internal sealed class AboutThisPcWindow : OverlayWindow
{
    private static AboutThisPcWindow _current;

    public static void ShowWindow()
    {
        if (_current != null) { _current.Activate(); return; }
        _current = new AboutThisPcWindow();
        _current.Show();
        _current.Activate();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint flags);

    private PcInfo _info;
    private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal };
    private readonly Border _content = new();
    private readonly List<(Border Pill, TextBlock Label, string Name, Func<UIElement> Build)> _tabs = new();
    private string _selected = "Overview";

    private static bool Dark => ThemeManager.IsDark;
    private static Brush Soft(double darkAlpha, double lightAlpha) =>
        new SolidColorBrush(Dark ? Color.FromArgb((byte)(255 * darkAlpha), 255, 255, 255) : Color.FromArgb((byte)(255 * lightAlpha), 0, 0, 0));

    private AboutThisPcWindow() : base(SurfaceKind.Popup, noActivate: false)
    {
        Title = "About This PC";
        FallbackCornerRadius = 12;
        Topmost = false;        // an ordinary window, not an overlay
        ShowInTaskbar = true;   // so it can be minimized and found again
        ToolWindow = false;
        Width = 900;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // top row: traffic lights on the left, tabs centered
        var top = new Grid { Height = 48 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var controls = WindowButtons();
        Grid.SetColumn(controls, 2);
        var segmented = new Border { Child = _tabBar, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center, Background = Soft(0.07, 0.05) };
        Grid.SetColumn(segmented, 1);
        top.Children.Add(controls);
        top.Children.Add(segmented);

        _content.Margin = new Thickness(0, 0, 0, 0);
        var root = new Grid { Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_content, 1);
        root.Children.Add(top);
        root.Children.Add(_content);
        Content = root;

        // drag the window by any empty part of it
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is not DependencyObject source || IsInteractive(source)) return;
            try { DragMove(); } catch { }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Left) Step(-1);   // arrow keys move between the tabs
            else if (e.Key == Key.Right) Step(1);
            else return;
            e.Handled = true;
        };
        Closed += (_, _) => _current = null;

        _content.Child = new TextBlock { Text = "Reading system information…", Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Loaded += async (_, _) =>
        {
            try { _info = await Task.Run(PcInfo.Gather); }
            catch { return; }
            Populate();
        };
    }

    // ---------- window controls (Windows style, top right) ----------

    private UIElement WindowButtons()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(CaptionButton("", "Minimize", () => WindowState = WindowState.Minimized, false, out _));
        panel.Children.Add(CaptionButton("", "Close", Close, true, out _));
        return panel;
    }

    private static Border CaptionButton(string glyph, string tip, Action click, bool close, out TextBlock icon)
    {
        icon = new TextBlock { Text = glyph, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        var glyphBlock = icon;
        var b = new Border { Width = 46, Height = 32, Background = Brushes.Transparent, Child = icon, ToolTip = tip, Tag = "nodrag" };
        if (glyph == "")
        {
            // The minimize line is drawn as a shape: it is a hairline that some icon fonts render too faintly.
            var line = new Rectangle { Width = 10, Height = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, SnapsToDevicePixels = true };
            line.SetResourceReference(Shape.FillProperty, "Brush.Text");
            b.Child = line;
        }
        var hover = close ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : Soft(0.12, 0.08);
        b.MouseEnter += (_, _) => { b.Background = hover; if (close) glyphBlock.Foreground = Brushes.White; };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; if (close) glyphBlock.ClearValue(TextBlock.ForegroundProperty); };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    /// <summary>True when the click landed on something clickable (a tab, a button, text you can select), which must not start a window drag.</summary>
    private static bool IsInteractive(DependencyObject d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && (fe.Tag as string == "nodrag" || fe is TextBox || fe is Button || fe is System.Windows.Controls.Primitives.ScrollBar)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ---------- tabs ----------
    private void Populate()
    {
        var p = _info;
        _tabs.Clear();
        _tabBar.Children.Clear();
        AddTab("Overview", Overview);
        AddTab("Processor", Processor);
        AddTab("Memory", Memory);
        AddTab("Graphics", Graphics);
        AddTab("Storage", Storage);
        if (p.HasBattery) AddTab("Battery", Battery);
        AddTab("Network", Network);
        Select(_selected);
    }

    private void AddTab(string name, Func<UIElement> build)
    {
        var label = new TextBlock { Text = name, FontSize = 12.5, Margin = new Thickness(12, 4, 12, 5) };
        var pill = new Border { Child = label, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Tag = "nodrag" };
        pill.MouseLeftButtonUp += (_, _) => Select(name);
        _tabs.Add((pill, label, name, build));
        _tabBar.Children.Add(pill);
    }

    private void Step(int direction)
    {
        int index = _tabs.FindIndex(t => t.Name == _selected);
        if (index < 0 || _tabs.Count == 0) return;
        Select(_tabs[Math.Clamp(index + direction, 0, _tabs.Count - 1)].Name);
    }

    private void Select(string name)
    {
        _selected = name;
        foreach (var (pill, label, n, build) in _tabs)
        {
            bool on = n == name;
            pill.Background = on ? (Dark ? Soft(0.18, 0) : new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255))) : Brushes.Transparent;
            pill.Effect = on && !Dark ? new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0.5, Opacity = 0.18 } : null;
            label.Opacity = on ? 1 : 0.72;
            label.FontWeight = on ? FontWeights.Medium : FontWeights.Normal;
            if (on) _content.Child = build();
        }
    }

    // ---------- building blocks ----------
    private static Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var c in children) stack.Children.Add(c);
        return new Border { Child = stack, CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 12, 18, 12), Margin = new Thickness(0, 0, 0, 12), Background = Soft(0.07, 0.045) };
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.5, Margin = new Thickness(4, 6, 0, 8) };

    private static UIElement Row(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Border { Height = 0 };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Top };
        var v = new TextBox
        {
            Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTabStop = false, VerticalAlignment = VerticalAlignment.Top,
        };
        v.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        return grid;
    }

    private static UIElement Meter(double fraction, string left, string right)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Soft(0.14, 0.10) };
        var fill = new Border { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, fraction > 0.9 ? "Brush.Text" : "Brush.Accent");
        var bar = new Grid { Margin = new Thickness(0, 6, 0, 4) };
        bar.Children.Add(track);
        bar.Children.Add(fill);
        bar.SizeChanged += (_, _) => fill.Width = Math.Max(0, bar.ActualWidth * fraction);

        var captions = new Grid();
        captions.Children.Add(new TextBlock { Text = left, FontSize = 12, Opacity = 0.7 });
        captions.Children.Add(new TextBlock { Text = right, FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right });
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        stack.Children.Add(bar);
        stack.Children.Add(captions);
        return stack;
    }

    private static Border PillButton(string text, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(14, 4, 14, 5), HorizontalAlignment = HorizontalAlignment.Center };
        var b = new Border
        {
            Child = label, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand, Tag = "nodrag",
            Background = Dark ? Soft(0.14, 0) : new SolidColorBrush(Color.FromArgb(0xE0, 255, 255, 255)),
            BorderThickness = new Thickness(1), BorderBrush = Soft(0.10, 0.12),
        };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    // ---------- Overview (the About This Mac look) ----------
    private UIElement Overview()
    {
        var p = _info;

        var grid = new Grid { Margin = new Thickness(34, 8, 34, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = WallpaperCircle();
        circle.VerticalAlignment = VerticalAlignment.Center;
        circle.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(circle);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string os = p.OsName.Replace(" Pro", "").Replace(" Home", "").Replace(" Enterprise", "").Replace(" Education", "").Trim();
        right.Children.Add(new TextBlock { Text = os, FontSize = 34, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        right.Children.Add(new TextBlock { Text = $"Version {p.OsVersion}".Trim() + (p.OsBuild.Length > 0 ? $"  (build {p.OsBuild})" : ""), FontSize = 12.5, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 14) });

        string gpu = string.Join(" + ", p.Gpus.Select(g => Tidy(g.Name)));
        var facts = new Grid();
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });   // the part's picture
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void Fact(string label, string value, FrameworkElement icon = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            facts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var l = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var v = new TextBlock { Text = value, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 };
            Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(l, 1); Grid.SetColumn(v, 2);
            facts.Children.Add(l); facts.Children.Add(v);
            if (icon != null)
            {
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                icon.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(icon, row);
                facts.Children.Add(icon);
            }
            row++;
        }
        var firstDisk = p.Disks.FirstOrDefault();
        Fact("Model", (p.Model + (p.Manufacturer.Length > 0 ? $" ({p.Manufacturer})" : "")).Trim());
        Fact("Processor", $"{Tidy(p.CpuName)}" + (p.CpuCores > 0 ? $" · {p.CpuCores} cores, {p.CpuThreads} threads" : ""), HardwareIcons.Cpu(22));
        Fact("Memory", p.RamSummary, HardwareIcons.Ram(22));
        Fact("Startup Disk", StartupDisk(), firstDisk != null ? DiskIcon(firstDisk, 22) : HardwareIcons.Ssd(22));
        Fact("Graphics", gpu, HardwareIcons.Gpu(22));
        Fact("Wi-Fi", WifiText(), p.WifiAdapter?.Length > 0 || p.WifiOn != null ? HardwareIcons.Wifi(22, p.WifiOn != false) : null);
        Fact("Bluetooth", BluetoothText(), p.BluetoothAdapter != null ? HardwareIcons.Bluetooth(22, p.BluetoothOn != false) : null);
        if (p.HasBattery) Fact("Battery", $"{p.BatteryPercent}%" + (IsCharging() ? " · charging" : "") + (p.BatteryDesignMwh > 0 && p.BatteryFullMwh > 0 ? $" · health {(double)p.BatteryFullMwh / p.BatteryDesignMwh:P0}" : ""), HardwareIcons.Battery(22, p.BatteryPercent, IsCharging()));
        Fact("Serial Number", p.Serial);
        right.Children.Add(facts);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(PillButton("System Report…", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msinfo32.exe") { UseShellExecute = true }); } catch { } }));
        buttons.Children.Add(PillButton("Software Update…", () => Core.Launch.Settings("windowsupdate")));
        buttons.Children.Add(PillButton("Copy Details", () => { try { Clipboard.SetText(ToText(p)); } catch { } }));
        right.Children.Add(buttons);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var footer = new TextBlock
        {
            Text = "Windows is a trademark of the Microsoft group of companies. PearUI is an independent project.",
            FontSize = 10.5, Opacity = 0.4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14), VerticalAlignment = VerticalAlignment.Bottom,
        };
        // The block keeps its proportions and scales with the window, so it looks as good full screen as it does small.
        // Natural size, centered in the window (the window can't be resized, so nothing needs scaling).
        grid.Margin = new Thickness(0, 0, 0, 24);
        grid.HorizontalAlignment = HorizontalAlignment.Center;
        grid.VerticalAlignment = VerticalAlignment.Center;
        var holder = new Grid();
        holder.Children.Add(grid);
        holder.Children.Add(footer);
        return holder;
    }

    private string WifiText()
    {
        var p = _info;
        string adapter = Tidy(System.Text.RegularExpressions.Regex.Replace(p.WifiAdapter ?? "", @"^Wi-Fi \(|\)$", ""));
        if (adapter.Length == 0 && p.WifiOn == null) return "";
        string state = p.WifiOn == false ? "Off" : p.WifiNetwork?.Length > 0 ? $"Connected to {p.WifiNetwork}" : "On";
        return adapter.Length > 0 ? $"{adapter} · {state}" : state;
    }

    private string BluetoothText()
    {
        var p = _info;
        if (p.BluetoothAdapter == null) return "";
        string state = p.BluetoothOn == false ? "Off" : p.BluetoothOn == true ? "On" : "";
        return (Tidy(p.BluetoothAdapter) + (state.Length > 0 ? " · " + state : "")).Trim();
    }

    private static string Tidy(string name) => name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace("  ", " ").Trim();

    private string StartupDisk()
    {
        try
        {
            string drive = IoPath.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";
            var v = _info.Volumes.FirstOrDefault(x => x.Letter.Equals(drive, StringComparison.OrdinalIgnoreCase));
            string label = string.IsNullOrWhiteSpace(v?.Label) ? "Local Disk" : v.Label;
            return $"{label} ({drive})" + (v != null ? $" · {PcInfo.Bytes(v.Bytes)}" : "");
        }
        catch { return ""; }
    }

    /// <summary>The desktop wallpaper in a circle with a white rim, like the picture in About This Mac.</summary>
    private static FrameworkElement WallpaperCircle()
    {
        const double size = 190;
        ImageSource image = null;
        try
        {
            var sb = new StringBuilder(520);
            string path = SystemParametersInfo(0x0073 /*SPI_GETDESKWALLPAPER*/, (uint)sb.Capacity, sb, 0) ? sb.ToString() : "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                path = IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 640;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
        }
        catch { }

        var inner = new Ellipse { Width = size - 8, Height = size - 8, Margin = new Thickness(4) };
        if (image != null) inner.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        else inner.SetResourceReference(Shape.FillProperty, "Brush.Accent");
        var rim = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromArgb(0xF2, 255, 255, 255)), Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.28, Direction = 270 } };
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(rim);
        grid.Children.Add(inner);
        return grid;
    }

    private static string ToText(PcInfo p)
    {
        var sb = new StringBuilder();
        void Line(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"{label}: {value}"); }
        sb.AppendLine(p.DeviceName);
        Line("Model", (p.Manufacturer + " " + p.Model).Trim());
        Line("Serial number", p.Serial);
        Line("Windows", $"{p.OsName} {p.OsVersion} (build {p.OsBuild}, {p.OsArch})");
        Line("Installed on", p.InstallDate);
        Line("Up for", p.UptimeText);
        sb.AppendLine();
        Line("Processor", $"{p.CpuName} ({p.CpuCores} cores, {p.CpuThreads} threads)");
        Line("Memory", p.RamSummary);
        for (int i = 0; i < p.Modules.Count; i++)
        {
            var m = p.Modules[i];
            Line($"  Slot {(m.Slot.Length > 0 ? m.Slot : (i + 1).ToString())}", $"{PcInfo.Bytes(m.Bytes)} {m.Type}-{m.SpeedMhz} {m.FormFactor} {m.Maker} {m.Part}".Trim());
        }
        if (p.RamSlots > p.Modules.Count) Line("  Empty slots", (p.RamSlots - p.Modules.Count).ToString());
        sb.AppendLine();
        foreach (var g in p.Gpus) Line("Graphics", $"{g.Name} {PcInfo.Bytes(g.VramBytes)} (driver {g.Driver})".Trim());
        foreach (var d in p.Displays) Line("Display", $"{d.Name} {d.Resolution} {(d.RefreshHz > 1 ? d.RefreshHz + " Hz" : "")}".Trim());
        sb.AppendLine();
        foreach (var d in p.Disks) Line("Drive", $"{d.Name}, {PcInfo.Bytes(d.Bytes)} {d.Kind} {d.Bus}".Trim());
        foreach (var v in p.Volumes) Line("Volume", $"{v.Letter} {v.Label}: {PcInfo.Bytes(v.Bytes - v.FreeBytes)} used of {PcInfo.Bytes(v.Bytes)}");
        if (p.HasBattery) Line("Battery", $"{p.BatteryPercent}%" + (p.BatteryDesignMwh > 0 && p.BatteryFullMwh > 0 ? $", health {(double)p.BatteryFullMwh / p.BatteryDesignMwh:P0}" : ""));
        foreach (var n in p.Network) Line("Network", $"{n.Kind} {n.Ipv4} ({n.Mac})");
        return sb.ToString();
    }

    // ---------- the other tabs ----------
    /// <summary>A picture of the part on the left, its name and a short description beside it.</summary>
    private static UIElement Header(FrameworkElement icon, string title, string subtitle)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle)) texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.65, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(texts, 1);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        return grid;
    }

    // ---------- page layout: every page fills the window ----------
    /// <summary>Stacks the parts of a page; parts marked "fill" share all the room that is left, so the page uses the whole window.</summary>
    private static UIElement Page(params (UIElement Part, bool Fill)[] parts)
    {
        var grid = new Grid { Margin = new Thickness(30, 4, 30, 18) };
        for (int i = 0; i < parts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = parts[i].Fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(parts[i].Part, i);
            grid.Children.Add(parts[i].Part);
        }
        return grid;
    }

    /// <summary>Cards side by side, equally wide, each as tall as the row.</summary>
    private static UIElement CardRow(IEnumerable<UIElement> cards)
    {
        var list = cards.ToList();
        int columns = Math.Clamp(list.Count, 1, 4);
        var grid = new UniformGrid { Columns = columns, Margin = new Thickness(-6, 0, -6, 0) };
        foreach (var c in list)
        {
            if (c is FrameworkElement fe) { fe.Margin = new Thickness(6, 0, 6, 12); fe.VerticalAlignment = VerticalAlignment.Stretch; }
            grid.Children.Add(c);
        }
        return grid;
    }

    /// <summary>A tall card: a big picture on top, the name beneath it, then a few centered lines.</summary>
    private static Border TallCard(FrameworkElement icon, string title, string subtitle, params string[] lines)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle)) stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.65, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        foreach (var l in lines.Where(x => !string.IsNullOrWhiteSpace(x)))
            stack.Children.Add(new TextBlock { Text = l, FontSize = 12, Opacity = 0.78, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        return new Border { Child = stack, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Background = Soft(0.07, 0.045) };
    }

    /// <summary>A large number with a small caption, like a statistic.</summary>
    private static Border Stat(string value, string caption)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock { Text = caption, FontSize = 12, Opacity = 0.62, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        return new Border { Child = stack, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 16, 10, 16), Background = Soft(0.07, 0.045) };
    }

    private static UIElement Spacer(double height) => new Border { Height = height };

    private UIElement Processor()
    {
        var p = _info;
        var stats = new List<UIElement>();
        if (p.CpuCores > 0) stats.Add(Stat(p.CpuCores.ToString(), "Cores"));
        if (p.CpuThreads > 0) stats.Add(Stat(p.CpuThreads.ToString(), "Threads"));
        if (p.CpuBaseMhz > 0) stats.Add(Stat($"{p.CpuBaseMhz / 1000.0:0.0} GHz", "Base speed"));
        long cacheKb = p.CpuL2Kb + p.CpuL3Kb;
        if (cacheKb > 0) stats.Add(Stat(PcInfo.Bytes(cacheKb * 1024L).Replace(".0", ""), "Cache"));

        // the chip and its name on the left, the details on the right
        var chip = TallCard(HardwareIcons.Cpu(150), Tidy(p.CpuName), p.CpuMaker);
        var details = Card(
            Heading("Details"),
            Row("Manufacturer", p.CpuMaker),
            Row("Socket", p.CpuSocket),
            Row("Architecture", p.CpuArch),
            Row("L2 cache", p.CpuL2Kb > 0 ? PcInfo.Bytes(p.CpuL2Kb * 1024L).Replace(".0", "") : ""),
            Row("L3 cache", p.CpuL3Kb > 0 ? PcInfo.Bytes(p.CpuL3Kb * 1024L).Replace(".0", "") : ""),
            Row("Virtualization", p.CpuVirtualization == null ? "" : p.CpuVirtualization == true ? "Enabled" : "Disabled in firmware"));
        details.Margin = new Thickness(0, 0, 0, 12);
        details.VerticalAlignment = VerticalAlignment.Stretch;
        chip.Margin = new Thickness(0, 0, 12, 12);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(details, 1);
        body.Children.Add(chip);
        body.Children.Add(details);
        return Page((CardRow(stats), false), (body, true));
    }

    private UIElement Memory()
    {
        var p = _info;
        long used = Math.Max(0, p.RamTotal - p.RamAvailable);
        string type = p.Modules.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "";
        int speed = p.Modules.Select(m => m.ConfiguredMhz > 0 ? m.ConfiguredMhz : m.SpeedMhz).FirstOrDefault();
        var summary = Card(
            Header(HardwareIcons.Ram(56), $"{PcInfo.Bytes(p.RamInstalled)} {type}".Trim(), string.Join(" · ", new[] { speed > 0 ? $"{speed} MHz" : "", p.RamSlots > 0 ? $"{p.Modules.Count} of {p.RamSlots} slots in use" : "", p.RamMaxCapacity > 0 ? $"up to {PcInfo.Bytes(p.RamMaxCapacity)} supported" : "" }.Where(x => x.Length > 0))),
            Meter(p.RamTotal > 0 ? (double)used / p.RamTotal : 0, $"{PcInfo.Bytes(used)} in use", $"{PcInfo.Bytes(p.RamAvailable)} available"));

        int slots = Math.Max(p.RamSlots, p.Modules.Count);
        var cards = new List<UIElement>();
        for (int i = 0; i < slots; i++) cards.Add(i < p.Modules.Count ? ModuleCard(p.Modules[i], i + 1) : EmptySlot(i + 1));
        UIElement row = cards.Count > 0 ? CardRow(cards) : Card(new TextBlock { Text = "Windows didn't report the memory slots on this PC.", Opacity = 0.6 });
        return Page((summary, false), (Heading("Memory slots"), false), (row, true));
    }

    private static Border ModuleCard(PcInfo.Module m, int index)
    {
        string name = m.Slot.Length > 0 ? m.Slot : "Slot " + index;
        string headline = $"{PcInfo.Bytes(m.Bytes)} {m.Type}".Trim() + (m.SpeedMhz > 0 ? $"-{m.SpeedMhz}" : "");
        string speed = m.ConfiguredMhz > 0 && m.ConfiguredMhz != m.SpeedMhz ? $"{m.SpeedMhz} MHz, running at {m.ConfiguredMhz} MHz" : m.SpeedMhz > 0 ? $"{m.SpeedMhz} MHz" : "";
        return TallCard(HardwareIcons.Ram(120), headline, name + (m.FormFactor.Length > 0 ? " · " + m.FormFactor : ""),
            string.Join(" · ", new[] { m.Maker, m.Part }.Where(x => !string.IsNullOrWhiteSpace(x))), speed, m.Serial.Length > 0 ? "Serial " + m.Serial : "");
    }

    private static Border EmptySlot(int index)
    {
        var icon = HardwareIcons.Ram(120);
        icon.Opacity = 0.3;
        return TallCard(icon, "Empty slot", $"Slot {index}");
    }

    private UIElement Graphics()
    {
        var p = _info;
        var gpus = new List<UIElement>();
        foreach (var g in p.Gpus)
        {
            bool shared = g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                          g.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase);
            gpus.Add(TallCard(HardwareIcons.Gpu(130), Tidy(g.Name), shared ? "Integrated · shares system memory" : "Dedicated graphics",
                shared ? "" : "Video memory " + PcInfo.Bytes(g.VramBytes),
                g.Driver.Length > 0 ? $"Driver {g.Driver}" : "", g.DriverDate));
        }
        UIElement gpuRow = gpus.Count > 0 ? CardRow(gpus) : Card(new TextBlock { Text = "No graphics adapter was reported.", Opacity = 0.6 });

        var displays = CardRow(p.Displays.Select(d => (UIElement)Card(
            Header(HardwareIcons.Monitor(54), d.Name, d.Primary ? "Main display" : ""),
            new TextBlock { Text = d.Resolution + (d.RefreshHz > 1 ? $" · {d.RefreshHz} Hz" : ""), FontSize = 13, Opacity = 0.85, Margin = new Thickness(0, 2, 0, 0) })));
        return Page((Heading("Graphics"), false), (gpuRow, true), (Heading("Displays"), false), (displays, false));
    }

    private static FrameworkElement DiskIcon(PcInfo.Disk d, double size) =>
        d.Kind.Contains("Hard", StringComparison.OrdinalIgnoreCase) ? HardwareIcons.HardDisk(size) : HardwareIcons.Ssd(size, d.Bus == "NVMe");

    private UIElement Storage()
    {
        var p = _info;
        var drives = p.Disks.Select(d => (UIElement)TallCard(DiskIcon(d, 120), d.Name,
            $"{(d.Kind + (d.Bus.Length > 0 ? " · " + d.Bus : "")).Trim()} · {PcInfo.Bytes(d.Bytes)}".Trim(' ', '·'),
            d.Health.Length > 0 ? "Health: " + d.Health : "", d.Serial.Length > 0 ? "Serial " + d.Serial : "")).ToList();
        UIElement driveRow = drives.Count > 0 ? CardRow(drives) : Card(new TextBlock { Text = "Windows didn't report the physical drives.", Opacity = 0.6 });

        var volumes = CardRow(p.Volumes.Select(v =>
        {
            long used = v.Bytes - v.FreeBytes;
            return (UIElement)Card(
                new TextBlock { Text = $"{v.Letter}  {v.Label}".Trim() + (v.FileSystem.Length > 0 ? "  ·  " + v.FileSystem : ""), FontWeight = FontWeights.SemiBold, FontSize = 13.5 },
                Meter(v.Bytes > 0 ? (double)used / v.Bytes : 0, $"{PcInfo.Bytes(used)} used of {PcInfo.Bytes(v.Bytes)}", $"{PcInfo.Bytes(v.FreeBytes)} free"));
        }));
        return Page((Heading("Drives"), false), (driveRow, true), (Heading("Volumes"), false), (volumes, false));
    }

    private UIElement Battery()
    {
        var p = _info;
        double health = p.BatteryDesignMwh > 0 && p.BatteryFullMwh > 0 ? (double)p.BatteryFullMwh / p.BatteryDesignMwh : 0;
        bool charging = IsCharging();

        var left = TallCard(HardwareIcons.Battery(170, p.BatteryPercent, charging), p.BatteryPercent + "%", charging ? "Charging" : "On battery", p.BatteryName);
        left.Margin = new Thickness(0, 0, 12, 12);

        var stats = new List<UIElement>();
        if (health > 0) stats.Add(Stat($"{health:P0}", "Health"));
        if (p.BatteryCycles > 0) stats.Add(Stat(p.BatteryCycles.ToString(), "Charge cycles"));
        var details = Card(
            Heading("Capacity"),
            Row("Design capacity", p.BatteryDesignMwh > 0 ? $"{p.BatteryDesignMwh / 1000.0:0.0} Wh" : ""),
            Row("Full charge capacity", p.BatteryFullMwh > 0 ? $"{p.BatteryFullMwh / 1000.0:0.0} Wh" : ""),
            Row("Health", health > 0 ? $"{health:P0} of its original capacity" : ""));
        details.Margin = new Thickness(0, 0, 0, 12);
        details.VerticalAlignment = VerticalAlignment.Stretch;

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (stats.Count > 0) { var row = CardRow(stats); Grid.SetRow(row, 0); right.Children.Add(row); }
        Grid.SetRow(details, 1);
        right.Children.Add(details);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);
        return Page((body, true));
    }

    private static bool IsCharging()
    {
        try { var b = SystemStatus.GetBattery(); return b.HasBattery && (b.Charging || b.PluggedIn); }
        catch { return false; }
    }

    private UIElement Network()
    {
        var p = _info;
        var cards = new List<UIElement>();
        if (p.WifiOn != null || p.WifiAdapter?.Length > 0)
            cards.Add(TallCard(HardwareIcons.Wifi(130, p.WifiOn != false), "Wi-Fi", p.WifiOn == false ? "Off" : p.WifiNetwork?.Length > 0 ? $"Connected to {p.WifiNetwork}" : "On",
                Tidy(System.Text.RegularExpressions.Regex.Replace(p.WifiAdapter ?? "", @"^Wi-Fi \(|\)$", ""))));
        if (p.BluetoothAdapter != null)
            cards.Add(TallCard(HardwareIcons.Bluetooth(130, p.BluetoothOn != false), "Bluetooth", p.BluetoothOn == false ? "Off" : p.BluetoothOn == true ? "On" : "", Tidy(p.BluetoothAdapter)));
        foreach (var n in p.Network.Where(x => x.Kind != "Wi-Fi"))
        {
            string speed = n.SpeedBps > 0 ? (n.SpeedBps >= 1_000_000_000 ? $"{n.SpeedBps / 1_000_000_000.0:0.#} Gbps" : $"{n.SpeedBps / 1_000_000} Mbps") : "";
            cards.Add(TallCard(HardwareIcons.Monitor(110), n.Kind + " connection", Tidy(n.Name), n.Ipv4.Length > 0 ? "IP address " + n.Ipv4 : "", n.Mac.Length > 0 ? "MAC address " + n.Mac : "", speed.Length > 0 ? "Link speed " + speed : ""));
        }

        // Wi-Fi details (address, speed) under the cards
        var wifi = p.Network.FirstOrDefault(n => n.Kind == "Wi-Fi");
        UIElement details = wifi == null ? new Border { Height = 0 } : Card(
            Row("IP address", wifi.Ipv4),
            Row("MAC address", wifi.Mac),
            Row("Link speed", wifi.SpeedBps > 0 ? (wifi.SpeedBps >= 1_000_000_000 ? $"{wifi.SpeedBps / 1_000_000_000.0:0.#} Gbps" : $"{wifi.SpeedBps / 1_000_000} Mbps") : ""));
        UIElement row = cards.Count > 0 ? CardRow(cards) : Card(new TextBlock { Text = "No active network connection.", Opacity = 0.6 });
        return Page((row, true), (details, false));
    }
}
