using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PearUI.Core;
using PearUI.Native;
using PearUI.Themes;

namespace PearUI.Settings;

/// <summary>Tells you a newer PearUI is available and updates it in one click: download, check, install, restart.</summary>
internal sealed class UpdateWindow : Window
{
    private static UpdateWindow _current;

    public static void ShowFor(ReleaseInfo release)
    {
        if (_current != null) { _current.Activate(); return; }
        _current = new UpdateWindow(release);
        _current.Show();
        _current.Activate();
    }

    private readonly ReleaseInfo _release;
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Height = 6, Margin = new Thickness(0, 8, 0, 0), Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _update = new() { Padding = new Thickness(22, 7, 22, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _later = new() { Content = "Later", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _skip = new() { Content = "Skip This Version", Padding = new Thickness(14, 7, 14, 7) };
    private bool _busy;

    private UpdateWindow(ReleaseInfo release)
    {
        _release = release;
        Title = "PearUI Update";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        this.SetResourceReference(BackgroundProperty, "Brush.Window");
        this.SetResourceReference(ForegroundProperty, "Brush.Text");
        Shell.PearLogo.ApplyWindowIcon(this);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = $"PearUI {release.Version.ToString(3)} is available", FontSize = 17, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"You have {Updater.Current.ToString(3)}.", Opacity = 0.65, Margin = new Thickness(0, 3, 0, 12) });

        string notes = Tidy(release.Notes);
        if (notes.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = "What's new", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var scroll = new ScrollViewer { MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 12.5 } };
            panel.Children.Add(scroll);
        }

        panel.Children.Add(_progress);
        panel.Children.Add(_status);

        _update.Content = Updater.CanSelfUpdate ? "Update Now" : "Open Download Page";
        _update.IsDefault = true;
        var buttons = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_later);
        right.Children.Add(_update);
        Grid.SetColumn(right, 1);
        buttons.Children.Add(_skip);
        buttons.Children.Add(right);
        panel.Children.Add(buttons);
        Content = panel;

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
        SourceInitialized += (_, _) => Backdrop.SetDarkFrame(new WindowInteropHelper(this).Handle, ThemeManager.IsDark);
        Closing += (_, e) => { if (_busy) e.Cancel = true; };   // not while it is downloading or installing
        Closed += (_, _) => _current = null;
    }

    /// <summary>Release notes are Markdown: keep them readable as plain text and short.</summary>
    private static string Tidy(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l, @"^#+\s*", ""))
            .Select(l => Regex.Replace(l, @"^\s*[-*]\s+", "• "))
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
                _progress.Value = p;
                _status.Text = $"Downloading… {p:P0}";
            }));

            _progress.IsIndeterminate = true;
            _status.Text = "Installing. PearUI will restart in a moment…";
            await Task.Delay(400);
            Updater.RunInstaller(installer);
            App.Instance.Quit();   // the installer takes over and starts the new version
        }
        catch (Exception ex)
        {
            _busy = false;
            _progress.IsIndeterminate = false;
            _progress.Visibility = Visibility.Collapsed;
            _status.Text = "The update didn't work: " + ex.Message + " You can try again, or download it from the releases page.";
            _update.Content = "Try Again";
            _update.IsEnabled = _later.IsEnabled = _skip.IsEnabled = true;
        }
    }
}
