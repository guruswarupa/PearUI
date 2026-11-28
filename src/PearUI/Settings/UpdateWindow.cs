using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PearUI.Core;
using PearUI.Native;
using PearUI.Shell;
using PearUI.Themes;

namespace PearUI.Settings;

/// <summary>
/// Tells you a newer PearUI is available and updates it in one click: download, check, install, restart.
/// Frosted glass like Settings and About This PC.
/// </summary>
internal sealed class UpdateWindow : OverlayWindow
{
    private static UpdateWindow _current;

    public static void ShowFor(ReleaseInfo release)
    {
        if (_current != null) { _current.Activate(); return; }
        _current = new UpdateWindow(release);
        _current.Show();
        _current.Activate();
    }

    /// <summary>Frosted glass that stays readable in the light theme.</summary>
    protected override Color GlassTint => ThemeManager.ReadableGlassTint;

    private readonly ReleaseInfo _release;
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Height = 6, Margin = new Thickness(0, 12, 0, 0), Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _update = new() { Padding = new Thickness(22, 7, 22, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _later = new() { Content = "Later", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _skip = new() { Content = "Skip This Version", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 7, 14, 7) };
    private bool _busy, _installing;

    private UpdateWindow(ReleaseInfo release) : base(SurfaceKind.Popup, noActivate: false)
    {
        _release = release;
        Title = "PearUI Update";
        FallbackCornerRadius = 12;
        Topmost = true;          // an update offer should not hide behind other windows
        ShowInTaskbar = true;
        ToolWindow = false;
        Width = 540;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.SetResourceReference(ForegroundProperty, "Brush.Text");
        Shell.PearLogo.ApplyWindowIcon(this);

        var root = new Grid { Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // what's new
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // progress, status, buttons

        // ---- header: the pear, the version and what you have now ----
        var header = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 34, 24, 16) };
        var logo = Logo(76);
        if (logo != null) header.Children.Add(new Image { Source = logo, Width = 76, Height = 76, Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Center });
        header.Children.Add(new TextBlock { Text = $"PearUI {release.Version.ToString(3)} is available", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        header.Children.Add(new TextBlock { Text = $"You have {Updater.Current.ToString(3)}.", Opacity = 0.62, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(header);

        // ---- what's new, in a soft glass card ----
        string notes = Tidy(release.Notes);
        if (notes.Length > 0)
        {
            var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 6, 12), Margin = new Thickness(28, 0, 28, 0) };
            card.SetResourceReference(Border.BackgroundProperty, "Brush.GlassCard");
            var inner = new Grid();
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            inner.Children.Add(new TextBlock { Text = "What's new", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 10, 0),
                Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, Opacity = 0.88, FontSize = 12.5, LineHeight = 19 },
            };
            Grid.SetRow(scroll, 1);
            inner.Children.Add(scroll);
            card.Child = inner;
            Grid.SetRow(card, 1);
            root.Children.Add(card);
        }

        // ---- progress, messages and the buttons ----
        var bottom = new StackPanel { Margin = new Thickness(28, 6, 28, 24) };
        bottom.Children.Add(_progress);
        bottom.Children.Add(_status);

        _update.Content = Updater.CanSelfUpdate ? "Update Now" : "Open Download Page";
        _update.IsDefault = true;
        var buttons = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_later);
        right.Children.Add(_update);
        Grid.SetColumn(right, 1);
        buttons.Children.Add(_skip);
        buttons.Children.Add(right);
        bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        // ---- Windows-style close button in the corner (not while installing) ----
        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        Shell.GlassChrome.AddCloseButton(windowButtons, () => { if (!_busy) Close(); });
        Grid.SetRowSpan(windowButtons, 3);
        root.Children.Add(windowButtons);
        Shell.GlassChrome.EnableDrag(this, root);
        Content = root;

        if (TryFindResource("AccentButton") is Style accent) _update.Style = accent;
        if (TryFindResource("FlatButton") is Style flat) { _later.Style = flat; _skip.Style = flat; }

        _update.Click += async (_, _) => await UpdateNow();
        _later.Click += (_, _) => Close();
        _skip.Click += (_, _) =>
        {
            SettingsStore.Current.SkippedUpdate = _release.Tag;
            SettingsStore.Save(raiseEvent: false);
            Close();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) { Close(); e.Handled = true; } };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };   // not while it is downloading or installing
        Closed += (_, _) => _current = null;
    }

    /// <summary>The PearUI icon for the header (null if it can't be drawn).</summary>
    private static BitmapSource Logo(int size)
    {
        try
        {
            using var bmp = Shell.PearLogo.CreateColorBitmap(size * 2);
            IntPtr hbmp = bmp.GetHbitmap(System.Drawing.Color.Transparent);
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    /// <summary>Release notes are Markdown: keep them readable as plain text and short.</summary>
    private static string Tidy(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l, @"^#+\s*", ""))
            .Select(l => Regex.Replace(l, @"^(\s*)[-*]\s+", m => m.Groups[1].Value.Length >= 2 ? "    – " : "• "))
            .Select(l => Regex.Replace(l, @"\*\*|__|`", ""))
            .Select(l => Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1"))
            .Where(l => !l.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase))
            .ToList();
        string text = string.Join("\n", lines).Trim();
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Length > 4000 ? text[..4000].TrimEnd() + "…" : text;
    }

    private async Task UpdateNow()
    {
        if (!Updater.CanSelfUpdate)
        {
            // A copy built from source can't replace itself: the download page is the way.
            Core.Launch.Open(_release.PageUrl);
            Close();
            return;
        }

        _busy = true;
        _update.IsEnabled = _later.IsEnabled = _skip.IsEnabled = false;
        _progress.Visibility = _status.Visibility = Visibility.Visible;
        _status.Text = "Downloading…";
        try
        {
            string installer = await Updater.DownloadAsync(_release, new Progress<double>(p =>
            {
                if (_installing) return;   // a late progress report must not overwrite the next message
                _progress.Value = p;
                _status.Text = $"Downloading… {p:P0}";
            }));

            _installing = true;
            _progress.IsIndeterminate = true;
            _status.Text = "Installing. PearUI will restart in a moment…";
            await Task.Delay(400);
            Updater.RunInstaller(installer);
            App.Instance.Quit();   // the installer takes over and starts the new version
        }
        catch (Exception ex)
        {
            _busy = false;
            _installing = false;
            _progress.IsIndeterminate = false;
            _progress.Visibility = Visibility.Collapsed;
            _status.Text = "The update didn't work: " + ex.Message + " You can try again, or download it from the releases page.";
            _update.Content = "Try Again";
            _update.IsEnabled = _later.IsEnabled = _skip.IsEnabled = true;
        }
    }
}
