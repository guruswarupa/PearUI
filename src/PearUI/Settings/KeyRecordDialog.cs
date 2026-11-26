using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PearUI.Native;
using PearUI.Shell;
using PearUI.Themes;

namespace PearUI.Settings;

/// <summary>A small dialog that records one key combination (used for the terminal's own shortcuts).</summary>
internal sealed class KeyRecordDialog : Window
{
    private readonly TextBox _keys = new() { IsReadOnly = true, Padding = new Thickness(9, 6, 9, 6), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
    private readonly Button _record = new() { Content = "Record", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x44, 0x3A)), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Func<string, string> _validate;
    private bool _recording;

    public string Result { get; private set; }

    /// <param name="validate">Returns a message when the keys are not acceptable, or null.</param>
    public KeyRecordDialog(string title, string current, Func<string, string> validate)
    {
        _validate = validate;
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.SetResourceReference(BackgroundProperty, "Brush.Window");
        this.SetResourceReference(ForegroundProperty, "Brush.Text");
        Shell.PearLogo.ApplyWindowIcon(this);

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_record, 1);
        row.Children.Add(_keys);
        row.Children.Add(_record);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "Press Record, then the keys you want. Esc cancels.", FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(_error);

        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(22, 7, 22, 7), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 7, 18, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;

        foreach (var b in new[] { _record, ok, cancel })
            if (TryFindResource("FlatButton") is Style flat) b.Style = flat;

        _keys.Text = current;
        _record.Click += (_, _) => StartRecording();
        ok.Click += (_, _) => Save();
        SourceInitialized += (_, _) => Backdrop.SetDarkFrame(new WindowInteropHelper(this).Handle, ThemeManager.IsDark);
        Closed += (_, _) => StopRecording();
        Deactivated += (_, _) => StopRecording();
    }

    private void StartRecording()
    {
        _recording = true;
        _record.Content = "Press keys…";
        _error.Visibility = Visibility.Collapsed;
        StartMenuBlocker.Capture = spec =>
        {
            StopRecording();
            if (spec.Equals("Esc", StringComparison.OrdinalIgnoreCase) || spec.Equals("Escape", StringComparison.OrdinalIgnoreCase)) return;
            _keys.Text = spec;
        };
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        StartMenuBlocker.Capture = null;
        _record.Content = "Record";
    }

    private void Save()
    {
        StopRecording();
        string keys = _keys.Text.Trim();
        string problem = !Shell.Shortcuts.TryParse(keys, out var mods, out int vk) ? "Record the keys first." : _validate?.Invoke(Shell.Shortcuts.Format(mods, vk));
        if (problem != null)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            return;
        }
        Result = Shell.Shortcuts.Format(mods, vk);
        DialogResult = true;
    }
}
