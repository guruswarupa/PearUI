using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PearUI.Core;
using PearUI.Native;
using PearUI.Themes;

namespace PearUI.Settings;

/// <summary>Pick a macOS-style icon for one app from macOSicons.com. The app's own icon is always one click away.</summary>
internal sealed class MacIconPickerWindow : Window
{
    private readonly string _source;
    private readonly Action _applied;
    private readonly TextBox _query = new() { Padding = new Thickness(8, 6, 8, 6), MinWidth = 280 };
    private readonly Button _search = new() { Content = "Search", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly WrapPanel _grid = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6) };
    private CancellationTokenSource _cts;

    public static void ShowFor(string appName, string iconSource, Action applied)
    {
        var w = new MacIconPickerWindow(appName, iconSource, applied);
        w.Show();
        w.Activate();
    }

    private MacIconPickerWindow(string appName, string source, Action applied)
    {
        _source = source;
        _applied = applied;
        Title = "macOS Icon – " + appName;
        Width = 640; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        this.SetResourceReference(BackgroundProperty, "Brush.Window");
        this.SetResourceReference(ForegroundProperty, "Brush.Text");
        Shell.PearLogo.ApplyWindowIcon(this);

        var root = new Grid { Margin = new Thickness(20, 16, 20, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock { Text = "Choose an icon for " + appName, FontSize = 16, FontWeight = FontWeights.SemiBold });

        var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_search, System.Windows.Controls.Dock.Right);
        bar.Children.Add(_search);
        _query.Text = MacName(appName);
        _status.Text = "Check the name, then press Search.";
        bar.Children.Add(_query);
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_status);
        Grid.SetRow(top, 1);
        root.Children.Add(top);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var original = new Button { Content = "Use the App's Own Icon", Padding = new Thickness(14, 6, 14, 6) };
        DockPanel.SetDock(original, System.Windows.Controls.Dock.Right);
        footer.Children.Add(original);
        var credit = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Opacity = 0.6 };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("macOSicons.com")) { NavigateUri = new Uri(MacIcons.SiteUrl) };
        link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { } };
        credit.Inlines.Add("Icons from ");
        credit.Inlines.Add(link);
        credit.Inlines.Add(" and its artists. For personal use.  ");
        footer.Children.Add(credit);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        if (TryFindResource("AccentButton") is Style accent) _search.Style = accent;
        if (TryFindResource("FlatButton") is Style flat) original.Style = flat;

        _search.IsDefault = true;
        _search.Click += async (_, _) => await RunSearch();
        original.Click += (_, _) => { IconStyles.SetFor(_source, IconStyles.Original); _applied?.Invoke(); Close(); };
        SourceInitialized += (_, _) => Backdrop.SetDarkFrame(new WindowInteropHelper(this).Handle, ThemeManager.IsDark);
        // Nothing is searched until you press Search (each search counts against the monthly allowance), so you can edit the name first.
        Loaded += (_, _) => { _query.Focus(); _query.SelectAll(); };
        Closed += (_, _) => _cts?.Cancel();
    }

    // macOS calls these apps something else, and that is the name the icon library knows.
    private static string MacName(string windowsName)
    {
        string n = (windowsName ?? "").Trim();
        return n.ToLowerInvariant() switch
        {
            "recycle bin" => "Trash",
            "file explorer" or "explorer" => "Finder",
            "settings" => "System Settings",
            "task manager" => "Activity Monitor",
            "notepad" => "TextEdit",
            "calculator" => "Calculator",
            "command prompt" or "windows terminal" or "windows powershell" => "Terminal",
            "all apps" => "Launchpad",
            "microsoft edge" => "Microsoft Edge",
            "photos" => "Photos",
            "camera" => "Photo Booth",
            "snipping tool" => "Screenshot",
            "microsoft store" => "App Store",
            "downloads" => "Downloads",
            _ => n
        };
    }

    private async Task RunSearch()
    {
        string q = _query.Text.Trim();
        if (q.Length == 0) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _grid.Children.Clear();
        _status.Text = "Searching…";
        try
        {
            var hits = await MacIcons.SearchAsync(q, ct);
            if (ct.IsCancellationRequested) return;
            _status.Text = hits.Count == 0 ? "No icons found. Try a shorter name." : $"{hits.Count} icons. Click one to use it.  ({MacIcons.UsedThisMonth()} of {MacIcons.FreeMonthlyLimit} free searches used this month)";
            foreach (var hit in hits) _ = AddTile(hit, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task AddTile(MacIconHit hit, CancellationToken ct)
    {
        var img = new Image { Width = 72, Height = 72, Stretch = Stretch.Uniform };
        var name = new TextBlock { Text = hit.Name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var by = new TextBlock { Text = string.IsNullOrWhiteSpace(hit.Credit) ? "" : "by " + hit.Credit, FontSize = 10, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        var stack = new StackPanel { Width = 96, Margin = new Thickness(2), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = hit.Name + (by.Text.Length > 0 ? "  " + by.Text : "") };
        stack.Children.Add(img); stack.Children.Add(name); stack.Children.Add(by);
        stack.Margin = new Thickness(4, 6, 4, 6);
        _grid.Children.Add(stack);
        try
        {
            string file = await MacIcons.DownloadAsync(hit, ct);
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.UriSource = new Uri(file); bmp.DecodePixelWidth = 160; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
            img.Source = bmp;
            stack.MouseLeftButtonUp += (_, _) => { MacIcons.Apply(_source, file); _applied?.Invoke(); Close(); };
            stack.MouseEnter += (_, _) => stack.Opacity = 0.75;
            stack.MouseLeave += (_, _) => stack.Opacity = 1;
        }
        catch { stack.Visibility = Visibility.Collapsed; }
    }
}
