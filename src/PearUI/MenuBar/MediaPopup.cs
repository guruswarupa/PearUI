using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PearUI.Core;

namespace PearUI.MenuBar;

/// <summary>Compact now-playing card: cover art, title, seek bar and transport controls (no playlist).</summary>
public sealed class MediaPopup : BarPopup
{
    private readonly Image _art = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _artGlyph = new() { Text = "", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8 };
    private readonly TextBlock _album = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.5 };
    private readonly TextBlock _pos = new() { FontSize = 11, Opacity = 0.6 };
    private readonly TextBlock _dur = new() { FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _bar = new() { Height = 16, Background = Brushes.Transparent };
    private readonly TextBlock _playGlyph = new() { FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _shuffle, _prev, _next;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private TimeSpan _position, _duration;
    private DateTime _stamp = DateTime.Now;
    private bool _playing, _canSeek, _dragging, _shuffleOn;
    private int _polls;

    public MediaPopup() : base(340)
    {
        _bar.Cursor = Cursors.Hand;
        var track = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.SliderTrack");
        _fill.Height = 5;
        _fill.VerticalAlignment = VerticalAlignment.Center;
        _fill.SetResourceReference(Border.BackgroundProperty, "Brush.Text");
        _bar.Children.Add(track);
        _bar.Children.Add(_fill);
        _bar.SizeChanged += (_, _) => UpdateBar();
        _bar.MouseLeftButtonDown += (_, e) => { if (!_canSeek || _duration <= TimeSpan.Zero) return; _dragging = true; _bar.CaptureMouse(); SeekPreview(e); };
        _bar.MouseMove += (_, e) => { if (_dragging) SeekPreview(e); };
        _bar.MouseLeftButtonUp += async (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            _bar.ReleaseMouseCapture();
            SeekPreview(e);
            await SystemStatus.MediaSeekAsync(_position);
        };

        // Cover art
        var artBox = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 14, 0) };
        artBox.SetResourceReference(Border.BackgroundProperty, "Brush.Track");
        var artGrid = new Grid();
        artGrid.Children.Add(_artGlyph);
        artGrid.Children.Add(_art);
        artBox.Child = artGrid;
        _artGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_title);
        texts.Children.Add(_artist);
        texts.Children.Add(_album);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(artBox);
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);

        var times = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        times.Children.Add(_pos);
        times.Children.Add(_dur);

        _shuffle = IconButton("", 15, async () => { _shuffleOn = !_shuffleOn; UpdateShuffle(); await SystemStatus.MediaShuffleAsync(_shuffleOn); });
        _prev = IconButton("", 20, async () => { await SystemStatus.MediaPreviousAsync(); await Task.Delay(350); await PollAsync(); });
        var play = IconButton(null, 0, async () => { await SystemStatus.MediaPlayPauseAsync(); await Task.Delay(250); await PollAsync(); });
        _next = IconButton("", 20, async () => { await SystemStatus.MediaNextAsync(); await Task.Delay(350); await PollAsync(); });
        _playGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        var playCircle = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Child = _playGlyph, IsHitTestVisible = false };
        playCircle.SetResourceReference(Border.BackgroundProperty, "Brush.Track");
        play.Content = playCircle;
        play.Width = 52;

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in new[] { _shuffle, _prev, play, _next }) controls.Children.Add(b);

        var root = new StackPanel { Margin = new Thickness(18, 18, 18, 14) };
        root.Children.Add(head);
        root.Children.Add(new Border { Height = 14 });
        root.Children.Add(_bar);
        root.Children.Add(times);
        root.Children.Add(controls);
        Content = root;

        _tick.Tick += (_, _) => Tick();
        Closed += (_, _) => _tick.Stop();
        ApplyDetail(null);
    }

    private Button IconButton(string glyph, double size, Func<Task> click)
    {
        var tb = new TextBlock { Text = glyph, FontSize = size > 0 ? size : 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        var b = new Button
        {
            Content = glyph == null ? null : tb, Width = 46, Height = 52, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false, Template = FlatTemplate(),
        };
        b.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        b.Click += async (_, _) => { try { await click(); } catch { } };
        return b;
    }

    private static ControlTemplate FlatTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        t.VisualTree = cp;
        return t;
    }

    protected override async void OnShown()
    {
        await PollAsync();
        _tick.Start();
    }

    private async Task PollAsync()
    {
        try
        {
            var d = await SystemStatus.GetMediaDetailAsync();
            if (IsLoaded || IsVisible) ApplyDetail(d);
        }
        catch { }
    }

    private void ApplyDetail(MediaDetail d)
    {
        bool has = d != null && !string.IsNullOrWhiteSpace(d.Title);
        _title.Text = has ? d.Title : "Not Playing";
        _artist.Text = has ? d.Artist ?? "" : "";
        _artist.Visibility = string.IsNullOrWhiteSpace(_artist.Text) ? Visibility.Collapsed : Visibility.Visible;
        _album.Text = has ? d.Album ?? "" : "";
        _album.Visibility = string.IsNullOrWhiteSpace(_album.Text) ? Visibility.Collapsed : Visibility.Visible;
        _art.Source = has ? d.Art : null;
        _artGlyph.Visibility = _art.Source == null ? Visibility.Visible : Visibility.Collapsed;
        _playing = has && d.Playing;
        _playGlyph.Text = _playing ? "" : "";
        _canSeek = has && d.CanSeek;
        if (!_dragging) { _position = has ? d.Position : TimeSpan.Zero; _stamp = DateTime.Now; }
        _duration = has ? d.Duration : TimeSpan.Zero;
        _shuffleOn = has && d.Shuffle == true;
        _shuffle.IsEnabled = has && d.Shuffle != null;
        _prev.IsEnabled = has && d.CanPrevious;
        _next.IsEnabled = has && d.CanNext;
        foreach (var b in new[] { _shuffle, _prev, _next }) b.Opacity = b.IsEnabled ? 1 : 0.3;
        UpdateShuffle();
        UpdateBar();
    }

    private void UpdateShuffle() => _shuffle.Opacity = !_shuffle.IsEnabled ? 0.3 : _shuffleOn ? 1 : 0.5;

    private void Tick()
    {
        if (_playing && !_dragging)
        {
            _position += DateTime.Now - _stamp;
            if (_duration > TimeSpan.Zero && _position > _duration) _position = _duration;
        }
        _stamp = DateTime.Now;
        UpdateBar();
        if (++_polls % 6 == 0 && !_dragging) _ = PollAsync(); // pick up track changes made elsewhere
    }

    private void SeekPreview(MouseEventArgs e)
    {
        double w = _bar.ActualWidth;
        if (w <= 0) return;
        double f = Math.Clamp(e.GetPosition(_bar).X / w, 0, 1);
        _position = TimeSpan.FromTicks((long)(_duration.Ticks * f));
        UpdateBar();
    }

    private void UpdateBar()
    {
        double w = _bar.ActualWidth;
        double f = _duration > TimeSpan.Zero ? Math.Clamp(_position.TotalSeconds / _duration.TotalSeconds, 0, 1) : 0;
        _fill.Width = Math.Max(0, w * f);
        _pos.Text = Format(_position);
        _dur.Text = _duration > TimeSpan.Zero ? Format(_duration) : "";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
