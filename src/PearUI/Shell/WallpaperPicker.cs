using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PearUI.Core;

namespace PearUI.Shell;

/// <summary>
/// A scrolling grid of macOS, Windows and Linux wallpapers (fetched from the internet, see <see cref="WallpaperCatalog"/>): click one to
/// download and set it. Used by the welcome tour and by PearUI Settings.
/// </summary>
public sealed class WallpaperPicker : StackPanel
{
    // Shared by every picker, so reopening one (or the other) is instant and "restore" always goes back to the real original.
    private static readonly Dictionary<string, ImageSource> ThumbCache = new();
    private static List<WallpaperItem> _items;
    private static WallpaperSnapshot _original;
    private static string _chosen;
    private static readonly SemaphoreSlim ThumbGate = new(4);
    private static bool _alsoLockScreen = true;
    private static string _category = "macOS";
    private static readonly string[] Categories = { "macOS", "Windows", "Linux" };

    private sealed class TileState
    {
        public WallpaperItem Item;
        public Grid Tile;
        public Border Thumb, Ring, Progress, Check;
        public TextBlock ProgressText;
        public bool Started;
    }

    private readonly List<TileState> _tiles = new();
    private readonly TextBlock _status = new() { FontSize = 12.5, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 8), Text = "Loading wallpapers…" };
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _list = new();
    private readonly List<(Border Pill, TextBlock Text, string Name)> _chips = new();
    private readonly Button _revert;
    private readonly CancellationTokenSource _thumbCts = new();
    private CancellationTokenSource _applyCts;

    public WallpaperPicker(double scrollHeight)
    {
        _original ??= WallpaperCatalog.TakeSnapshot();
        // macOS | Windows | Linux
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6) };
        var chipHost = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Child = chipRow, HorizontalAlignment = HorizontalAlignment.Center };
        chipHost.SetResourceReference(Border.BackgroundProperty, "Brush.Field");
        foreach (string name in Categories)
        {
            var text = new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center };
            var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 5, 18, 6), MinWidth = 86, Child = text, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            string cat = name;
            pill.MouseLeftButtonUp += (_, _) => { if (_category == cat) return; _category = cat; UpdateChips(); Populate(); };
            _chips.Add((pill, text, name));
            chipRow.Children.Add(pill);
        }
        Children.Add(chipHost);
        UpdateChips();

        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        Children.Add(_status);

        _scroll = new ScrollViewer
        {
            Height = scrollHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _list,
        };
        _scroll.ScrollChanged += (_, _) => LoadVisibleThumbs();
        Children.Add(_scroll);

        var lockBox = new CheckBox { Content = "Also use it as the lock screen picture", IsChecked = _alsoLockScreen, Margin = new Thickness(2, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        lockBox.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        lockBox.Click += (_, _) => _alsoLockScreen = lockBox.IsChecked == true;
        Children.Add(lockBox);

        _revert = new Button
        {
            Content = "Keep my original wallpaper", Style = (Style)Application.Current.FindResource("FlatButton"), Background = Brushes.Transparent,
            Padding = new Thickness(14, 7, 14, 8), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            Visibility = _chosen == null ? Visibility.Collapsed : Visibility.Visible,
        };
        _revert.Click += (_, _) =>
        {
            WallpaperCatalog.Restore(_original);
            _chosen = null;
            RefreshTiles();
            _status.Text = "Back to your original wallpaper.";
        };
        Children.Add(_revert);

        Unloaded += (_, _) => _thumbCts.Cancel();   // previews stop; a running download still finishes and applies
        _ = FillAsync();
    }

    private async Task FillAsync()
    {
        try
        {
            _items ??= await WallpaperCatalog.ListAsync(_thumbCts.Token);
            _status.Text = _chosen == null ? "Click a wallpaper to set it." : "Wallpaper set. Pick another, or keep this one.";

            Populate();
        }
        catch (OperationCanceledException) { }
        catch { _status.Text = "Couldn't reach the internet. You can try again later from PearUI Settings."; }
    }

    private void UpdateChips()
    {
        foreach (var (pill, text, name) in _chips)
        {
            bool on = name == _category;
            if (on) pill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent"); else pill.Background = Brushes.Transparent;
            if (on) text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText"); else text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        }
    }

    /// <summary>Shows the wallpapers of the selected category, grouped (macOS release, Windows version, Linux distribution).</summary>
    private void Populate()
    {
        if (_items == null) return;
        _list.Children.Clear();
        _tiles.Clear();
        foreach (var group in _items.Where(i => i.Category == _category).GroupBy(i => i.Group))
        {
            var header = new TextBlock { Text = group.Key, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 8) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
            _list.Children.Add(header);
            var wrap = new WrapPanel();
            foreach (var item in group) wrap.Children.Add(MakeTile(item));
            _list.Children.Add(wrap);
        }
        _scroll.ScrollToTop();
        RefreshTiles();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LoadVisibleThumbs));
    }

    private UIElement MakeTile(WallpaperItem item)
    {
        var st = new TileState { Item = item };
        st.Thumb = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x3F, 0x4B), Color.FromRgb(0x23, 0x26, 0x2E), 60),
        };
        var shade = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, Height = 42, IsHitTestVisible = false, CornerRadius = new CornerRadius(0, 0, 10, 10),
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xC0, 0, 0, 0), 90),
        };
        var label = new TextBlock
        {
            Text = item.Title, FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(9, 0, 9, 6), IsHitTestVisible = false,
        };
        st.Ring = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2.5), BorderBrush = Brushes.Transparent, IsHitTestVisible = false };
        st.ProgressText = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        st.Progress = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), Visibility = Visibility.Collapsed, Child = st.ProgressText, IsHitTestVisible = false };
        var tick = new TextBlock { Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };
        tick.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        st.Check = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 7, 0), Child = tick, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        st.Check.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");

        var scale = new ScaleTransform(1, 1);
        st.Tile = new Grid { Width = 156, Height = 100, Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, ToolTip = item.Title, RenderTransform = scale, RenderTransformOrigin = new Point(0.5, 0.5), Background = Brushes.Transparent };
        foreach (var e in new UIElement[] { st.Thumb, shade, label, st.Progress, st.Ring, st.Check }) st.Tile.Children.Add(e);
        void Zoom(double to, int ms)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
        }
        st.Tile.MouseEnter += (_, _) => Zoom(1.04, 140);
        st.Tile.MouseLeave += (_, _) => Zoom(1, 180);
        st.Tile.MouseLeftButtonUp += async (_, _) => await ChooseAsync(st);

        if (ThumbCache.TryGetValue(item.File, out var cached)) { st.Thumb.Background = new ImageBrush(cached) { Stretch = Stretch.UniformToFill }; st.Started = true; }
        _tiles.Add(st);
        return st.Tile;
    }

    /// <summary>Only the previews you can see (plus a screenful below) are fetched; the rest wait until you scroll.</summary>
    private void LoadVisibleThumbs()
    {
        foreach (var t in _tiles)
        {
            if (t.Started || !t.Tile.IsLoaded) continue;
            double y;
            try { y = t.Tile.TranslatePoint(new Point(0, 0), _scroll).Y; } catch { continue; }
            if (y > -150 && y < _scroll.ViewportHeight + 320) { t.Started = true; _ = LoadThumbAsync(t); }
        }
    }

    private async Task LoadThumbAsync(TileState t)
    {
        try
        {
            string path;
            await ThumbGate.WaitAsync(_thumbCts.Token);
            try { path = await WallpaperCatalog.ThumbAsync(t.Item, _thumbCts.Token); }
            finally { ThumbGate.Release(); }
            if (path == null) { t.Started = false; return; }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 320;
            bmp.EndInit();
            bmp.Freeze();
            ThumbCache[t.Item.File] = bmp;
            t.Thumb.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            t.Thumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        }
        catch (OperationCanceledException) { t.Started = false; }
        catch { t.Started = false; }
    }

    private void RefreshTiles()
    {
        foreach (var t in _tiles)
        {
            bool on = t.Item.File == _chosen;
            if (on) t.Ring.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent"); else t.Ring.BorderBrush = Brushes.Transparent;
            t.Check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        _revert.Visibility = _chosen == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ChooseAsync(TileState t)
    {
        _applyCts?.Cancel();
        var cts = _applyCts = new CancellationTokenSource();
        foreach (var other in _tiles) other.Progress.Visibility = Visibility.Collapsed;
        t.Progress.Visibility = Visibility.Visible;
        t.ProgressText.Text = "…";
        _status.Text = $"Downloading “{t.Item.Title}”…";
        try
        {
            var progress = new Progress<double>(p => t.ProgressText.Text = $"{(int)(p * 100)}%");
            string path = await WallpaperCatalog.DownloadAsync(t.Item, progress, cts.Token);
            if (!WallpaperCatalog.Apply(path)) throw new InvalidOperationException("Windows refused the wallpaper.");
            _chosen = t.Item.File;
            RefreshTiles();
            string done = $"Wallpaper set to “{t.Item.Title}”";
            if (_alsoLockScreen)
            {
                _status.Text = done + "…";
                bool lockOk = await WallpaperCatalog.ApplyLockScreenAsync(path);
                done += lockOk ? " and the lock screen." : ". The lock screen could not be changed (it may be set to Windows Spotlight).";
            }
            else done += ".";
            _status.Text = done;
        }
        catch (OperationCanceledException) { }
        catch { _status.Text = "Couldn't set that one. Check your connection and try again."; }
        finally { if (_applyCts == cts) t.Progress.Visibility = Visibility.Collapsed; }
    }
}
