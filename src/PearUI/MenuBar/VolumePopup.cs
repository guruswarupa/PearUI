using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PearUI.Native;
using PearUI.Shell;

namespace PearUI.MenuBar;

/// <summary>Sound popup: a volume slider and a mute button.</summary>
public sealed class VolumePopup : BarPopup
{
    private readonly PillSlider _slider = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _muteGlyph = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private readonly StackPanel _apps = new();

    public VolumePopup() : base(320)
    {
        FallbackCornerRadius = 14;
        var title = new TextBlock { Text = "Sound", FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };

        _muteGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        var mute = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(10, 0, 0, 0),
            Cursor = Cursors.Hand, Child = _muteGlyph, ToolTip = "Mute",
        };
        mute.SetResourceReference(Border.BackgroundProperty, "Brush.Track");
        mute.MouseLeftButtonUp += (_, _) => { AudioVolume.SetMute(!AudioVolume.GetMute()); Refresh(); };

        _slider.UserChanged += (_, v) =>
        {
            AudioVolume.SetMute(false);
            AudioVolume.SetVolume((float)(v / 100.0));
            UpdateGlyphs(v, false);
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_slider);
        Grid.SetColumn(mute, 1);
        row.Children.Add(mute);

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        root.Children.Add(title);
        root.Children.Add(row);
        root.Children.Add(_apps);
        Content = root;
        Refresh();
        BuildApps();
    }

    /// <summary>The volume mixer: one slider per app that has an audio session.</summary>
    private void BuildApps()
    {
        _apps.Children.Clear();
        var sessions = AppAudio.GetSessions();
        _apps.Children.Add(new TextBlock { Text = "Apps", FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 16, 0, 8) });
        if (sessions.Count == 0)
        {
            _apps.Children.Add(new TextBlock { Text = "No apps are playing sound", Opacity = 0.5, FontSize = 12 });
            return;
        }
        foreach (var s in sessions)
        {
            var session = s;
            var icon = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            if (session.ExePath != null) ShellIcons.LoadInto(icon, session.ExePath, 48);
            var name = new TextBlock { Text = session.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) };
            var slider = new PillSlider { Value = session.Muted ? 0 : Math.Round(session.Volume * 100), Glyph = "" };
            slider.UserChanged += (_, v) => session.Volume = (float)(v / 100.0);

            var col = new StackPanel();
            col.Children.Add(name);
            col.Children.Add(slider);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(icon);
            Grid.SetColumn(col, 1);
            row.Children.Add(col);
            _apps.Children.Add(row);
        }
    }

    private void Refresh()
    {
        float v = AudioVolume.GetVolume();
        _slider.IsEnabled = v >= 0;
        double pct = v < 0 ? 0 : Math.Round(v * 100);
        _slider.Value = pct;
        UpdateGlyphs(pct, AudioVolume.GetMute());
    }

    private void UpdateGlyphs(double pct, bool muted)
    {
        _slider.Glyph = muted || pct <= 0.5 ? "\uE74F" : pct < 34 ? "\uE992" : pct < 67 ? "\uE993" : "\uE995";
        _muteGlyph.Text = muted ? "\uE74F" : "\uE767";
    }
}
