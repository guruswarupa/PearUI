using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PearUI.Native;
using PearUI.Shell;
using PearUI.Themes;

namespace PearUI.Settings;

/// <summary>Adds or edits one keyboard shortcut: record the keys, pick what they do, and give the action its program or command.</summary>
internal sealed class ShortcutDialog : Window
{
    private readonly TextBox _keys = new() { IsReadOnly = true, Padding = new Thickness(9, 6, 9, 6), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Text = "" };
    private readonly Button _record = new() { Content = "Record", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _action = new() { Padding = new Thickness(9, 6, 9, 6) };
    private readonly TextBlock _argLabel = new() { Margin = new Thickness(0, 14, 0, 4) };
    private readonly TextBox _arg = new() { Padding = new Thickness(9, 6, 9, 6) };
    private readonly Button _browse = new() { Content = "Browse…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x44, 0x3A)), Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Grid _argRow = new();
    private readonly IEnumerable<ShortcutBinding> _others;
    private bool _recording;

    public ShortcutBinding Result { get; private set; }

    public ShortcutDialog(ShortcutBinding existing, IEnumerable<ShortcutBinding> others)
    {
        _others = others;
        Title = existing == null ? "New Shortcut" : "Edit Shortcut";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.SetResourceReference(BackgroundProperty, "Brush.Window");
        this.SetResourceReference(ForegroundProperty, "Brush.Text");
        Shell.PearLogo.ApplyWindowIcon(this);

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

        // keys
        panel.Children.Add(new TextBlock { Text = "Keys", Margin = new Thickness(0, 0, 0, 4) });
        var keysRow = new Grid();
        keysRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keysRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_record, 1);
        keysRow.Children.Add(_keys);
        keysRow.Children.Add(_record);
        panel.Children.Add(keysRow);
        panel.Children.Add(new TextBlock { Text = "Press Record, then the keys you want. Esc cancels.", FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });

        // action
        panel.Children.Add(new TextBlock { Text = "Does this", Margin = new Thickness(0, 14, 0, 4) });
        foreach (var a in ShortcutActions.All) _action.Items.Add($"{a.Group}  ·  {a.Title}");
        panel.Children.Add(_action);

        // argument
        panel.Children.Add(_argLabel);
        _argRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _argRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_browse, 1);
        _argRow.Children.Add(_arg);
        _argRow.Children.Add(_browse);
        panel.Children.Add(_argRow);
        panel.Children.Add(_error);

        // buttons
        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(22, 7, 22, 7), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 7, 18, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;

        foreach (var b in new[] { _record, _browse, ok, cancel })
            if (TryFindResource("FlatButton") is Style flat) b.Style = flat;

        if (existing != null)
        {
            _keys.Text = existing.Keys;
            _action.SelectedIndex = Math.Max(0, IndexOf(existing.Action));
            _arg.Text = existing.Arg ?? "";
        }
        else _action.SelectedIndex = 0;
        UpdateArgRow();

        _action.SelectionChanged += (_, _) => UpdateArgRow();
        _record.Click += (_, _) => StartRecording();
        _browse.Click += (_, _) => Browse();
        ok.Click += (_, _) => Save();
        SourceInitialized += (_, _) => Backdrop.SetDarkFrame(new WindowInteropHelper(this).Handle, ThemeManager.IsDark);
        Closed += (_, _) => StopRecording();
        Deactivated += (_, _) => StopRecording();
    }

    private static int IndexOf(string id)
    {
        for (int i = 0; i < ShortcutActions.All.Count; i++) if (ShortcutActions.All[i].Id == id) return i;
        return -1;
    }

    private ShortcutAction Selected => _action.SelectedIndex >= 0 ? ShortcutActions.All[_action.SelectedIndex] : null;

    private void UpdateArgRow()
    {
        var kind = Selected?.Arg ?? ArgKind.None;
        var show = kind == ArgKind.None ? Visibility.Collapsed : Visibility.Visible;
        _argLabel.Visibility = show;
        _argRow.Visibility = show;
        _argLabel.Text = ShortcutActions.ArgLabel(kind);
        _browse.Visibility = kind == ArgKind.Command ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Browse()
    {
        var d = new OpenFileDialog { Title = "Choose a program or file", CheckFileExists = true };
        if (d.ShowDialog(this) == true) _arg.Text = d.FileName;
    }

    // ---------- recording ----------
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

    // ---------- saving ----------
    private void Save()
    {
        StopRecording();
        string keys = _keys.Text.Trim();
        var action = Selected;

        string problem = null;
        if (keys.Length == 0 || !Shell.Shortcuts.TryParse(keys, out _, out _)) problem = "Record the keys for this shortcut first.";
        else if (!Shell.Shortcuts.IsSafe(keys)) problem = "Add Ctrl, Alt, Shift or the Windows key, so normal typing never triggers it (function keys can stand alone).";
        else if (_others.Any(o => string.Equals(Normalize(o.Keys), Normalize(keys), StringComparison.OrdinalIgnoreCase))) problem = "Another shortcut already uses these keys.";
        else if (action == null) problem = "Choose what the shortcut should do.";
        else if (action.Arg != ArgKind.None && string.IsNullOrWhiteSpace(_arg.Text)) problem = ShortcutActions.ArgLabel(action.Arg) + " is needed.";

        if (problem != null)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            return;
        }

        Result = new ShortcutBinding { Keys = Normalize(keys), Action = action.Id, Arg = action.Arg == ArgKind.None ? "" : _arg.Text.Trim(), Enabled = true };
        DialogResult = true;
    }

    private static string Normalize(string spec) =>
        Shell.Shortcuts.TryParse(spec, out var mods, out int vk) ? Shell.Shortcuts.Format(mods, vk) : spec;
}
