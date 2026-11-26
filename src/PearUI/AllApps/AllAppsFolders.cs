using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Native;

namespace PearUI.AllApps;

/// <summary>
/// Folders for All Apps, Launchpad-style:
///  • a folder tile shows a 3×3 preview of its apps; click to open it as a panel; click its name to rename;
///  • press and hold an app, then drag it onto another app (new folder) or onto a folder (add to it);
///  • drag an app out of an open folder's panel to take it out (or use the right-click menu);
///  • a folder left with one app disappears and the app goes back to the grid.
/// </summary>
public sealed partial class AllAppsWindow
{
    private readonly Grid _folderLayer = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) };
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    private Border _folderPanel;
    private UniformGrid _folderGrid;
    private AppFolder _openFolder;

    // hold-to-drag state
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private AppEntry _holdApp;
    private FrameworkElement _holdTile;
    private bool _tileDragging;
    private Image _ghost;
    private DateTime _edgeSince = DateTime.MaxValue;

    private static AppSettings S => SettingsStore.Current;

    private void InitFolders(Grid root)
    {
        _folderLayer.MouseLeftButtonUp += (_, e) => { if (!e.Handled) CloseFolder(); e.Handled = true; };
        root.Children.Add(_folderLayer);
        root.Children.Add(_dragLayer);
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); StartTileDrag(); };
    }

    // ===================== model =====================
    private List<AppFolder> Folders => S.AppFolders ??= new List<AppFolder>();

    private AppFolder FolderOf(string aumid) =>
        Folders.FirstOrDefault(f => f.Apps.Contains(aumid, StringComparer.OrdinalIgnoreCase));

    private List<AppEntry> AppsIn(AppFolder f)
    {
        var byId = AppCatalog.Instance.Apps.ToDictionary(a => a.Aumid, StringComparer.OrdinalIgnoreCase);
        return f.Apps.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    /// <summary>Folders (with at least 2 installed apps) and the apps not in any folder, sorted by name.</summary>
    private List<object> GridItems()
    {
        EnsureStarterFolders();
        var apps = AppCatalog.Instance.Apps;
        var folders = Folders.Where(f => AppsIn(f).Count >= 2).ToList();
        var inFolders = new HashSet<string>(folders.SelectMany(f => f.Apps), StringComparer.OrdinalIgnoreCase);
        return folders.Cast<object>()
            .Concat(apps.Where(a => !inFolders.Contains(a.Aumid)))
            .OrderBy(o => o is AppFolder f ? f.Name : ((AppEntry)o).Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void SaveFolders()
    {
        // A folder needs at least two apps.
        Folders.RemoveAll(f => f.Apps.Count < 2);
        SettingsStore.Save(raiseEvent: false);
    }

    private void RemoveFromFolders(string aumid)
    {
        foreach (var f in Folders) f.Apps.RemoveAll(a => a.Equals(aumid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>First run: group system tools, accessibility and developer apps, like Launchpad's "Other"/"Utilities".</summary>
    private void EnsureStarterFolders()
    {
        if (S.AppFolders != null || AppCatalog.Instance.Apps.Count == 0) return;
        var apps = AppCatalog.Instance.Apps;
        S.AppFolders = new List<AppFolder>();

        void Make(string name, Func<string, bool> match)
        {
            var ids = apps.Where(a => match(a.Name) && FolderOf(a.Aumid) == null).Select(a => a.Aumid).ToList();
            if (ids.Count >= 2) S.AppFolders.Add(new AppFolder { Name = name, Apps = ids });
        }
        bool Has(string n, params string[] parts) => parts.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        Make("Windows Tools", n => Has(n, "Command Prompt", "Computer Management", "Component Services", "Control Panel",
            "Defragment", "Disk Cleanup", "Event Viewer", "iSCSI", "Local Security Policy", "ODBC", "Performance Monitor",
            "Print Management", "Recovery Drive", "Registry Editor", "Resource Monitor", "Services", "System Configuration",
            "System Information", "Task Scheduler", "Windows Memory", "Windows PowerShell", "Windows Fax", "Steps Recorder",
            "Character Map", "Remote Desktop", "Task Manager", "Windows Tools", "Install Additional Tools"));
        Make("Accessibility", n => Has(n, "Magnifier", "Narrator", "On-Screen Keyboard", "Live captions", "Voice access", "Windows Speech"));
        Make("Developer", n => Has(n, "Git ", "Git Bash", "Git CMD", "Git GUI", "Node.js", "IDLE", "Python", "Android Studio",
            "Visual Studio", "Developer "));
        SettingsStore.Save(raiseEvent: false);
    }

    // ===================== folder tile =====================
    private FrameworkElement MakeFolderTile(AppFolder folder)
    {
        var apps = AppsIn(folder);
        double box = _iconSize;
        var grid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(box * 0.1) };
        foreach (var a in apps.Take(9))
        {
            var mini = new Image { Margin = new Thickness(box * 0.03) };
            RenderOptions.SetBitmapScalingMode(mini, BitmapScalingMode.HighQuality);
            SetIcon(mini, a.IconSource, 48);
            grid.Children.Add(mini);
        }
        var square = new Border
        {
            Width = box, Height = box, CornerRadius = new CornerRadius(box * 0.22),
            Background = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
            Child = grid,
        };
        var name = new TextBlock
        {
            Text = folder.Name, Foreground = Brushes.White, FontSize = 12.5, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0), MaxWidth = _cellW - 16,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6, Color = Colors.Black },
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(square);
        stack.Children.Add(name);
        var tile = new Border
        {
            Child = stack, Background = Brushes.Transparent, CornerRadius = new CornerRadius(14),
            Padding = new Thickness(4, 8, 4, 4), Margin = new Thickness(4), Cursor = Cursors.Hand,
            Tag = "folder:" + folder.Id,
        };
        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonDown += (_, e) => e.Handled = true;
        tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenFolder(folder); };
        _folderTiles[folder.Id] = tile;
        tile.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowFolderMenu(folder, tile); };
        return tile;
    }

    private static void SetIcon(Image target, string source, int size)
    {
        int px = ShellIcons.PixelsFor(96);
        var cached = ShellIcons.TryGetCached(source, px) ?? ShellIcons.TryGetCached(source, 64) ?? ShellIcons.TryGetCached(source, size);
        if (cached != null) { target.Source = cached; return; }
        ShellIcons.LoadInto(target, source, px);
    }

    private void ShowFolderMenu(AppFolder folder, FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
        void Add(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Add("Open", () => OpenFolder(folder));
        Add("Rename", () => OpenFolder(folder, rename: true));
        m.Items.Add(new Separator());
        Add("Remove Folder (keep apps)", () => { Folders.Remove(folder); SaveFolders(); Rebuild(); });
        m.IsOpen = true;
    }

    // ===================== open folder panel =====================
    private void OpenFolder(AppFolder folder, bool rename = false)
    {
        _openFolder = folder;
        var apps = AppsIn(folder);
        int cols = Math.Min(Math.Max(3, (int)Math.Ceiling(Math.Sqrt(apps.Count))), _cols);

        var title = new TextBox
        {
            Text = folder.Name, FontSize = 24, Foreground = Brushes.White, CaretBrush = Brushes.White,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 200, Margin = new Thickness(0, 0, 0, 14),
            FontWeight = FontWeights.SemiBold,
        };
        void CommitName()
        {
            string n = title.Text.Trim();
            if (n.Length > 0 && n != folder.Name) { folder.Name = n; SaveFolders(); }
        }
        title.LostKeyboardFocus += (_, _) => CommitName();
        title.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitName(); Keyboard.Focus(_search); e.Handled = true; }
        };

        var grid = new UniformGrid { Columns = cols, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var a in apps) grid.Children.Add(GetTile(a));
        _folderGrid = grid;
        var scroll = new ScrollViewer
        {
            Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(_cellH * 2, Height - 260),
        };

        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(scroll);
        _folderPanel = new Border
        {
            Child = body, CornerRadius = new CornerRadius(26), Padding = new Thickness(28, 20, 28, 24),
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x34, 0x34, 0x3A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.45 },
            RenderTransform = new ScaleTransform(0.85, 0.85), RenderTransformOrigin = new Point(0.5, 0.5), Opacity = 0,
        };
        _folderPanel.MouseLeftButtonUp += (_, e) => e.Handled = true; // clicks inside don't close it

        _folderLayer.Children.Clear();
        _folderLayer.Children.Add(_folderPanel);
        _folderLayer.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(220);
        _folderPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        ((ScaleTransform)_folderPanel.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        ((ScaleTransform)_folderPanel.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });

        if (rename) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { title.Focus(); title.SelectAll(); });
    }

    private void CloseFolder()
    {
        if (_openFolder == null) return;
        _openFolder = null;
        // Detach the shared tiles before the panel goes away.
        _folderGrid?.Children.Clear();
        _folderGrid = null;
        _folderLayer.Children.Clear();
        _folderLayer.Visibility = Visibility.Collapsed;
        _folderPanel = null;
        Rebuild();
    }

    // ===================== press, hold and drag apps =====================
    private void BeginHold(AppEntry app, FrameworkElement tile)
    {
        if (_search.Text.Length > 0) return; // folders are edited in the normal grid only
        _holdApp = app;
        _holdTile = tile;
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void CancelHold()
    {
        _holdTimer.Stop();
        if (!_tileDragging) { _holdApp = null; _holdTile = null; }
    }

    private void StartTileDrag()
    {
        if (_holdApp == null || Mouse.LeftButton != MouseButtonState.Pressed) return;
        _tileDragging = true;
        _holdTile.Opacity = 0.3;
        _ghost = new Image
        {
            Source = (_tileImages.TryGetValue(_holdApp.Aumid, out var tileImg) ? tileImg.Source : null)
                     ?? ShellIcons.TryGetCached(_holdApp.IconSource, ShellIcons.PixelsFor(96)) ?? ShellIcons.TryGetCached(_holdApp.IconSource, 64),
            Width = _iconSize * 1.12, Height = _iconSize * 1.12,
            Opacity = 0.92, Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.5 },
        };
        _dragLayer.Children.Add(_ghost);
        MoveGhost(Mouse.GetPosition(_dragLayer));
        _rootGrid.CaptureMouse(); // the grid that has the drag handlers (Content may be the glass wrapper)
    }

    private void MoveGhost(Point p)
    {
        if (_ghost == null) return;
        Canvas.SetLeft(_ghost, p.X - _ghost.Width / 2);
        Canvas.SetTop(_ghost, p.Y - _ghost.Height / 2);
        UpdateDropHighlight(p);

        // Hold near the left/right edge to move to another page (not while a folder is open).
        if (_openFolder != null) return;
        bool edge = p.X < 40 || p.X > ActualWidth - 40;
        if (!edge) { _edgeSince = DateTime.MaxValue; return; }
        if (_edgeSince == DateTime.MaxValue) _edgeSince = DateTime.Now;
        if ((DateTime.Now - _edgeSince).TotalMilliseconds > 550)
        {
            _edgeSince = DateTime.Now;
            SnapTo(_page + (p.X < 40 ? -1 : 1));
        }
    }

    // ---------- drop target highlight ----------
    private readonly Dictionary<string, Border> _folderTiles = new();
    private Border _dropHighlight;

    /// <summary>Lights up the app or folder that the dragged app would be dropped on.</summary>
    private void UpdateDropHighlight(Point p)
    {
        Border el = null;
        string tag = _holdApp == null ? null : TagUnder(p);
        if (tag != null && tag != "app:" + _holdApp.Aumid)
        {
            if (tag.StartsWith("app:") && _tiles.TryGetValue(tag[4..], out var t)) el = t as Border;
            else if (tag.StartsWith("folder:") && _folderTiles.TryGetValue(tag[7..], out var f)) el = f;
        }
        if (el == _dropHighlight) return;
        if (_dropHighlight != null)
        {
            _dropHighlight.Background = Brushes.Transparent;
            _dropHighlight.RenderTransform = null;
        }
        _dropHighlight = el;
        if (el != null)
        {
            el.Background = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new ScaleTransform(1.08, 1.08);
        }
    }

    private void EndTileDrag(Point p)
    {
        UpdateDropHighlightClear();
        var app = _holdApp;
        _tileDragging = false;
        _holdApp = null;
        if (_holdTile != null) _holdTile.Opacity = 1;
        _holdTile = null;
        _dragLayer.Children.Clear();
        _ghost = null;
        _rootGrid.ReleaseMouseCapture();
        if (app == null) return;

        string target = TagUnder(p);
        var source = FolderOf(app.Aumid);

        if (_openFolder != null)
        {
            // Dropped outside the open folder's panel: take the app out of the folder.
            bool insidePanel = _folderPanel != null && new Rect(_folderPanel.TranslatePoint(new Point(), this),
                _folderPanel.RenderSize).Contains(p);
            if (!insidePanel)
            {
                RemoveFromFolders(app.Aumid);
                SaveFolders();
                var still = Folders.FirstOrDefault(f => f.Id == _openFolder.Id);
                CloseFolder();
                if (still != null) OpenFolder(still);
            }
            return;
        }

        if (target == null || target == "app:" + app.Aumid) return;
        if (target.StartsWith("folder:"))
        {
            var folder = Folders.FirstOrDefault(f => "folder:" + f.Id == target);
            if (folder == null) return;
            RemoveFromFolders(app.Aumid);
            folder.Apps.Add(app.Aumid);
        }
        else if (target.StartsWith("app:"))
        {
            string other = target[4..];
            var otherFolder = FolderOf(other);
            RemoveFromFolders(app.Aumid);
            if (otherFolder != null) otherFolder.Apps.Add(app.Aumid);
            else
            {
                var otherApp = AppCatalog.Instance.FindByAumid(other);
                Folders.Add(new AppFolder { Name = SuggestName(app, otherApp), Apps = new List<string> { other, app.Aumid } });
            }
        }
        SaveFolders();
        Rebuild();
    }

    private void UpdateDropHighlightClear()
    {
        if (_dropHighlight == null) return;
        _dropHighlight.Background = Brushes.Transparent;
        _dropHighlight.RenderTransform = null;
        _dropHighlight = null;
    }

    /// <summary>The Tag ("app:…" / "folder:…") of the tile under a point.</summary>
    private string TagUnder(Point p)
    {
        string found = null;
        VisualTreeHelper.HitTest(this, null, r =>
        {
            for (DependencyObject d = r.VisualHit; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement fe && fe.Tag is string s && (s.StartsWith("app:") || s.StartsWith("folder:")))
                {
                    found = s;
                    return HitTestResultBehavior.Stop;
                }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        return found;
    }

    private static string SuggestName(AppEntry a, AppEntry b)
    {
        if (b == null) return "Folder";
        string wa = a.Name.Split(' ')[0], wb = b.Name.Split(' ')[0];
        return wa.Equals(wb, StringComparison.OrdinalIgnoreCase) && wa.Length > 1 ? wa : "Folder";
    }

    /// <summary>Folder entries for an app tile's right-click menu.</summary>
    private void AddFolderMenuItems(ContextMenu m, AppEntry app)
    {
        void Add(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        m.Items.Add(new Separator());
        var current = FolderOf(app.Aumid);
        if (current != null)
        {
            Add($"Remove from “{current.Name}”", () =>
            {
                RemoveFromFolders(app.Aumid);
                SaveFolders();
                bool wasOpen = _openFolder != null;
                CloseFolder();
                var still = Folders.FirstOrDefault(f => f.Id == current.Id);
                if (wasOpen && still != null) OpenFolder(still);
            });
            return;
        }
        foreach (var f in Folders.Where(f => AppsIn(f).Count >= 2).OrderBy(f => f.Name))
        {
            var folder = f;
            Add($"Add to “{folder.Name}”", () => { RemoveFromFolders(app.Aumid); folder.Apps.Add(app.Aumid); SaveFolders(); Rebuild(); });
        }
    }
}
