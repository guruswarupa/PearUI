using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PearUI.Core;
using PearUI.Native;
using Forms = System.Windows.Forms;

namespace PearUI.Shell;

/// <summary>The Quick Look preview card: a frosted window showing an image, text, video, folder contents or a thumbnail.</summary>
internal sealed class QuickLookWindow : OverlayWindow
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp", ".heic", ".avif", ".dib", ".wdp", ".jxr" };
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".mp3", ".wav", ".m4a", ".wma", ".aac" };
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonc", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".toml",
        ".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".cc", ".rs", ".go", ".swift",
        ".php", ".rb", ".lua", ".sh", ".bash", ".zsh", ".bat", ".cmd", ".ps1", ".psm1", ".sql", ".css", ".scss", ".html", ".htm", ".xaml",
        ".svg", ".gitignore", ".env", ".properties", ".gradle", ".csproj", ".sln", ".reg", ".inf", ".tex", ".rtf", ".vue", ".dart",
    };

    private string _path;
    private readonly IntPtr _returnTo;
    private bool _isDir;
    private readonly Stack<string> _history = new(); // folders and files visited before this one (for Back)
    private readonly List<(Border Row, string Path)> _rows = new();
    private int _sel = -1;
    private Grid _layout;
    private MediaElement _media;
    private bool _closing;
    private bool _closeRequested;

    /// <summary>Closes once. Handing focus back to Explorer deactivates this window again, which must not close it a second time.</summary>
    private void CloseOnce()
    {
        if (_closeRequested) return;
        _closeRequested = true;
        Close();
    }

    public QuickLookWindow(string path, IntPtr returnTo) : base(SurfaceKind.Popup, noActivate: false)
    {
        _path = path;
        _returnTo = returnTo;
        _isDir = Directory.Exists(path);
        Title = "Quick Look";
        FallbackCornerRadius = 14;
        SizeToContent = SizeToContent.Manual;

        var body = BuildBody(out double w, out double h);
        var screen = Forms.Screen.FromHandle(returnTo != IntPtr.Zero ? returnTo : IntPtr.Zero);
        double s = Win32.GetScaleForScreen(screen);
        double maxW = screen.WorkingArea.Width / s * 0.78, maxH = screen.WorkingArea.Height / s * 0.82;
        Width = Math.Clamp(w, 420, maxW);
        Height = Math.Clamp(h + 58, 300, maxH);

        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = _layout;
        Attach(body);

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => CloseOnce();
        Closing += (_, _) =>
        {
            if (_closing) return;
            _closing = true;
            try { _media?.Stop(); _media?.Close(); } catch { }
        };
        Closed += (_, _) => { if (_returnTo != IntPtr.Zero && Win32.IsWindow(_returnTo)) Win32.ActivateWindow(_returnTo); };
    }

    public void ShowPreview()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var screen = Forms.Screen.FromHandle(_returnTo != IntPtr.Zero ? _returnTo : handle);
        double s = Win32.GetScaleForScreen(screen);
        int w = (int)Math.Ceiling(Width * s), h = (int)Math.Ceiling(Height * s);
        var wa = screen.WorkingArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (wa.Left + (wa.Width - w) / 2) / s;
        Top = (wa.Top + (wa.Height - h) / 2) / s;
        Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Show();
        Win32.ActivateWindow(handle);
        Focus();
    }

    // ---------- navigating (folders open inside the preview, like Finder's column browsing) ----------
    private void Attach(UIElement body)
    {
        _layout.Children.Clear();
        var header = BuildHeader();
        Grid.SetRow(body, 1);
        _layout.Children.Add(header);
        _layout.Children.Add(body);
    }

    private void NavigateTo(string path, bool remember)
    {
        if (remember) _history.Push(_path);
        try { _media?.Stop(); _media?.Close(); } catch { }
        _media = null;
        _path = path;
        _isDir = Directory.Exists(path);
        _rows.Clear();
        _sel = -1;
        Attach(BuildBody(out _, out _));
    }

    private void GoBack()
    {
        if (_history.Count > 0) NavigateTo(_history.Pop(), false);
    }

    private void EnterSelected()
    {
        if (_isDir && _sel >= 0 && _sel < _rows.Count) NavigateTo(_rows[_sel].Path, true);
        else OpenFile();
    }

    private void Select(int index)
    {
        if (_rows.Count == 0) return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        if (_sel >= 0 && _sel < _rows.Count) Paint(_rows[_sel].Row, false);
        _sel = index;
        Paint(_rows[_sel].Row, true);
        _rows[_sel].Row.BringIntoView();
    }

    private static void Paint(Border row, bool selected)
    {
        if (selected)
        {
            row.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.AccentText");
        }
        else
        {
            row.Background = Brushes.Transparent;
            row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space or Key.Escape: CloseOnce(); break;
            case Key.Down when _isDir: Select(_sel + 1); break;
            case Key.Up when _isDir: Select(_sel < 0 ? 0 : _sel - 1); break;
            case Key.Right or Key.Enter when _isDir && _sel >= 0: EnterSelected(); break;
            case Key.Enter: OpenFile(); break;
            case Key.Left or Key.Back: GoBack(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OpenFile()
    {
        string p = _path;
        CloseOnce();
        Launch.Open(p);
    }

    // ---------- header ----------
    private UIElement BuildHeader()
    {
        var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(18, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        try { icon.Source = ShellIcons.Get(_path, 64); } catch { }

        var name = new TextBlock { Text = Path.GetFileName(_path.TrimEnd('\\')) is { Length: > 0 } n ? n : _path, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var info = new TextBlock { Text = Describe(), FontSize = 11.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        texts.Children.Add(info);

        var open = Pill("Open", () => OpenFile());
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // back
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // open
        if (_history.Count > 0)
        {
            var back = new TextBlock
            {
                Text = "\uE72B", FontSize = 15, Width = 34, Height = 34, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 9, 0, 0),
                Cursor = Cursors.Hand, ToolTip = "Back (Left arrow)", Background = Brushes.Transparent, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            back.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
            back.MouseLeftButtonUp += (_, _) => GoBack();
            grid.Children.Add(back);
            icon.Margin = new Thickness(6, 0, 12, 0);
        }
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(texts, 2);
        Grid.SetColumn(open, 3);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        grid.Children.Add(open);
        return grid;
    }

    private Border Pill(string text, Action click)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(14, 5, 14, 6) };
        var b = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(10, 0, 18, 0), Child = tb, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Track");
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private string Describe()
    {
        try
        {
            if (_isDir)
            {
                int items = Directory.EnumerateFileSystemEntries(_path).Take(10_000).Count();
                return $"Folder · {items} item{(items == 1 ? "" : "s")}";
            }
            var fi = new FileInfo(_path);
            string kind = fi.Extension.Length > 1 ? fi.Extension[1..].ToUpperInvariant() + " file" : "File";
            return $"{kind} · {Size(fi.Length)} · modified {fi.LastWriteTime:d MMM yyyy, HH:mm}";
        }
        catch { return ""; }
    }

    private static string Size(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} bytes" : $"{v:0.#} {units[u]}";
    }

    // ---------- body ----------
    private UIElement BuildBody(out double w, out double h)
    {
        w = 560; h = 460;
        try
        {
            if (_isDir) return FolderView(out w, out h);
            string ext = Path.GetExtension(_path);
            if (ImageExts.Contains(ext) && ImageView(out var img, ref w, ref h)) return img;
            if (MediaExts.Contains(ext)) return MediaView(out w, out h);
            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)) return PdfView(out w, out h);
            if (TextExts.Contains(ext) || ext.Length == 0)
            {
                var t = TextView(out w, out h);
                if (t != null) return t;
            }
        }
        catch { }
        return ThumbnailView(out w, out h);
    }

    private bool ImageView(out UIElement view, ref double w, ref double h)
    {
        view = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(_path);
            bmp.DecodePixelWidth = 2600; // plenty for a window, keeps huge photos light
            bmp.EndInit();
            bmp.Freeze();
            var screen = Forms.Screen.FromHandle(_returnTo);
            double s = Win32.GetScaleForScreen(screen);
            double maxW = screen.WorkingArea.Width / s * 0.78, maxH = screen.WorkingArea.Height / s * 0.82 - 58;
            double scale = Math.Min(1, Math.Min(maxW / bmp.Width, maxH / bmp.Height));
            if (scale < 1 || bmp.Width > 300) { /* fit */ }
            w = Math.Max(420, bmp.Width * scale);
            h = Math.Max(240, bmp.Height * scale);
            var image = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(14, 0, 14, 14) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            view = image;
            return true;
        }
        catch { return false; }
    }

    private UIElement TextView(out double w, out double h)
    {
        w = 820; h = 560;
        byte[] buffer = new byte[200 * 1024];
        int read;
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            read = fs.Read(buffer, 0, buffer.Length);
        if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 4096)) >= 0) return null; // binary
        using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, true);
        string text = reader.ReadToEnd();
        if (read == buffer.Length) text += "\n\n… (preview shows the first 200 KB)";

        var box = new TextBox
        {
            Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Padding = new Thickness(18, 6, 18, 14),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, IsTabStop = false, Focusable = false,
        };
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        return box;
    }

    /// <summary>Shows the pages of a PDF one under the other, rendered by Windows' own PDF engine.</summary>
    private UIElement PdfView(out double w, out double h)
    {
        w = 800; h = 1000;
        var pages = new StackPanel { Margin = new Thickness(22, 0, 22, 18) };
        var note = new TextBlock { Text = "Loading…", Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        pages.Children.Add(note);
        _ = LoadPdfAsync(pages, note);
        return new ScrollViewer { Content = pages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task LoadPdfAsync(StackPanel pages, TextBlock note)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_path);
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            if (doc.IsPasswordProtected) { note.Text = "This PDF is password protected. Press Enter to open it."; return; }

            int count = (int)Math.Min(doc.PageCount, 80);
            for (int i = 0; i < count && !_closing; i++)
            {
                using var page = doc.GetPage((uint)i);
                var options = new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1500 };
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options);
                stream.Seek(0);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
                bmp.EndInit();
                bmp.Freeze();

                var image = new Image { Source = bmp, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var sheet = new Border
                {
                    Child = image, Background = Brushes.White, Margin = new Thickness(0, 0, 0, 14),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.25 },
                };
                if (i == 0) pages.Children.Remove(note);
                pages.Children.Add(sheet);
            }
            if (doc.PageCount > count)
                pages.Children.Add(new TextBlock { Text = $"Showing the first {count} of {doc.PageCount} pages", Opacity = 0.5, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        }
        catch
        {
            if (pages.Children.Contains(note)) note.Text = "Can't preview this PDF. Press Enter to open it.";
        }
    }

    private UIElement MediaView(out double w, out double h)
    {
        w = 860; h = 520;
        var host = new Grid { Margin = new Thickness(14, 0, 14, 14), Background = Brushes.Transparent };
        _media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, Volume = 0.8 };
        var note = new TextBlock { Text = "Click to pause or play", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.45, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) };
        var fallbackHolder = new Grid { Visibility = Visibility.Collapsed };
        _media.MediaOpened += (_, _) => { _media.Play(); };
        _media.MediaFailed += (_, _) =>
        {
            // Formats WPF can't play (mkv, flac...): show the thumbnail instead.
            host.Children.Clear();
            host.Children.Add(ThumbnailView(out _, out _));
        };
        bool playing = true;
        host.MouseLeftButtonUp += (_, _) => { if (playing) _media.Pause(); else _media.Play(); playing = !playing; };
        _media.Source = new Uri(_path);
        host.Children.Add(_media);
        host.Children.Add(note);
        _media.Play();
        return host;
    }

    private UIElement FolderView(out double w, out double h)
    {
        w = 560; h = 480;
        var list = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };
        try
        {
            var entries = new DirectoryInfo(_path).EnumerateFileSystemInfos()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System))
                .OrderByDescending(f => f is DirectoryInfo).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(500).ToList();
            if (entries.Count == 0) list.Children.Add(new TextBlock { Text = "This folder is empty", Opacity = 0.5, FontSize = 13, Margin = new Thickness(10, 8, 0, 0) });
            foreach (var e in entries) list.Children.Add(FolderRow(e));
        }
        catch { list.Children.Add(new TextBlock { Text = "Can't read this folder", Opacity = 0.5, Margin = new Thickness(10, 8, 0, 0) }); }
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Border FolderRow(FileSystemInfo e)
    {
        bool dir = e is DirectoryInfo;
        var icon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        ShellIcons.LoadInto(icon, e.FullName, 48);
        var name = new TextBlock { Text = e.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        string detail = dir ? "" : Size(((FileInfo)e).Length);
        var meta = new TextBlock { Text = detail, FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock { Text = dir ? "\uE76C" : "", FontSize = 11, Width = 22, TextAlignment = TextAlignment.Right, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 1);
        Grid.SetColumn(meta, 2);
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(meta);
        grid.Children.Add(chevron);

        var row = new Border { Child = grid, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 8, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        string path = e.FullName;
        _rows.Add((row, path));
        row.MouseLeftButtonUp += (_, ev) =>
        {
            int index = _rows.FindIndex(x => x.Row == row);
            if (ev.ClickCount >= 2 || (_sel == index && index >= 0)) NavigateTo(path, true); // second click goes in
            else Select(index);
        };
        return row;
    }

    private UIElement ThumbnailView(out double w, out double h)
    {
        w = 560; h = 480;
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(30, 6, 30, 30), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        try { image.Source = ShellIcons.GetThumbnail(_path, 512); } catch { }
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
}
