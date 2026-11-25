using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MacUI.Core;
using MacUI.Native;
using MacUI.Shell;

namespace MacUI.Dock;

/// <summary>
/// macOS-style "stack" for the Dock's Downloads folder: a frosted grid of the most recent files with
/// thumbnails. Click to open; drag a file out to the Recycle Bin in the Dock, to File Explorer or the desktop.
/// </summary>
public sealed class DownloadsStack : OverlayWindow
{
    private const int MaxItems = 12, Columns = 4;
    private const double Cell = 96, Thumb = 56;

    private readonly string _folder;
    private readonly WrapPanel _grid = new() { Width = Cell * Columns };
    private readonly TextBlock _empty = new() { Text = "No recent downloads", Opacity = 0.6, Margin = new Thickness(0, 24, 0, 24), HorizontalAlignment = HorizontalAlignment.Center };
    private Point? _pressAt;
    private string _pressPath;
    private bool _dragging;
    private bool _closing;

    public event Action StackClosed;

    public DownloadsStack(string folder) : base(SurfaceKind.Popup, noActivate: false)
    {
        _folder = folder;
        Title = "PearUI Downloads";
        FallbackCornerRadius = 14;
        SizeToContent = SizeToContent.WidthAndHeight;

        var header = new Grid { Margin = new Thickness(14, 10, 12, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Downloads", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var open = new Button { Content = "Open in File Explorer", Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 3, 10, 4), FontSize = 12 };
        open.Click += (_, _) => { CloseStack(); Launch.Open(_folder); };
        Grid.SetColumn(open, 1);
        header.Children.Add(open);

        var body = new StackPanel { Margin = new Thickness(6, 0, 6, 10) };
        body.Children.Add(header);
        body.Children.Add(_grid);
        body.Children.Add(_empty);
        Content = body;

        Deactivated += (_, _) => { if (!_dragging) CloseStack(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseStack(); };
        Refresh();
    }

    /// <summary>Shows the stack centered above a point (device pixels), e.g. the top of the Downloads icon.</summary>
    public void ShowAbove(int centerX, int bottomY)
    {
        Show();
        double s = DpiScale;
        int w = (int)Math.Ceiling(ActualWidth * s), h = (int)Math.Ceiling(ActualHeight * s);
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(centerX, bottomY)).Bounds;
        int x = Math.Clamp(centerX - w / 2, area.Left + 6, area.Right - w - 6);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, bottomY - h - (int)(10 * s), 0, 0, Win32.SWP_NOSIZE);
        Activate();
        Win32.ActivateWindow(Handle);

        // Grow up from the Dock like a stack.
        var content = (FrameworkElement)Content;
        content.RenderTransformOrigin = new Point(0.5, 1);
        var scale = new ScaleTransform(0.92, 0.92);
        content.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    public void CloseStack()
    {
        if (_closing) return;
        _closing = true;
        StackClosed?.Invoke();
        Close();
    }

    public static List<string> Recent(string folder, int count)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System)
                            && !f.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                            && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime).Take(count).Select(f => f.FullName).ToList();
        }
        catch { return new List<string>(); }
    }

    private void Refresh()
    {
        _grid.Children.Clear();
        var files = Recent(_folder, MaxItems);
        _empty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var path in files) _grid.Children.Add(MakeItem(path));
    }

    private FrameworkElement MakeItem(string path)
    {
        var img = new Image { Width = Thumb, Height = Thumb, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Task.Run(() => ShellIcons.GetThumbnail(path, 128)).ContinueWith(t => img.Source = t.Result,
            TaskScheduler.FromCurrentSynchronizationContext());

        var name = new TextBlock
        {
            Text = Path.GetFileName(path), FontSize = 11.5, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 30, Width = Cell - 12, Margin = new Thickness(0, 6, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(img);
        stack.Children.Add(name);
        var item = new Border
        {
            Child = stack, Width = Cell, Padding = new Thickness(4, 8, 4, 6), CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path,
        };
        item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;

        item.MouseLeftButtonDown += (_, e) => { _pressAt = e.GetPosition(this); _pressPath = path; };
        item.MouseMove += (_, e) =>
        {
            if (_pressAt == null || _pressPath != path || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressAt.Value.X) < 6 && Math.Abs(p.Y - _pressAt.Value.Y) < 6) return;
            _pressAt = null;
            StartDrag(path, item);
        };
        item.MouseLeftButtonUp += (_, _) =>
        {
            if (_pressAt == null || _pressPath != path) return;
            _pressAt = null;
            CloseStack();
            Launch.Open(path);
        };
        item.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowItemMenu(path, item); };
        return item;
    }

    /// <summary>Standard Windows drag of the file: works with the Dock's Recycle Bin, Explorer and the desktop.</summary>
    private void StartDrag(string path, FrameworkElement item)
    {
        _dragging = true;
        item.Opacity = 0.5;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(item, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { }
        finally
        {
            _dragging = false;
            item.Opacity = 1;
        }
        // The file may have been moved or deleted by the drop: refresh after the shell finishes.
        Dispatcher.BeginInvoke(async () => { await Task.Delay(600); if (!_closing) Refresh(); });
    }

    private void ShowItemMenu(string path, FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
        void Add(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Add("Open", () => { CloseStack(); Launch.Open(path); });
        Add("Show in File Explorer", () => { CloseStack(); Launch.RevealInExplorer(path); });
        m.Items.Add(new Separator());
        Add("Move to Recycle Bin", async () => { await FileOps.RecycleAsync(new[] { path }); Refresh(); });
        _dragging = true; // keep the stack open while its menu is up
        m.Closed += (_, _) => _dragging = false;
        m.IsOpen = true;
    }
}
