using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PearUI.Core;
using PearUI.Native;
using PearUI.Themes;
using Forms = System.Windows.Forms;

namespace PearUI.Launcher;

/// <summary>Spotlight-style search window.</summary>
public partial class LauncherWindow : Shell.OverlayWindow
{
    private readonly ObservableCollection<SearchResult> _results = new();
    private readonly DispatcherTimer _fileDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource _fileCts;
    private DateTime _shownAt;

    public LauncherWindow() : base(Shell.SurfaceKind.Popup, noActivate: false)
    {
        FallbackCornerRadius = 14;
        InitializeComponent();
        Results.ItemsSource = _results;
        Deactivated += (_, _) => { if ((DateTime.Now - _shownAt).TotalMilliseconds > 150) HideLauncher(); };
        _fileDebounce.Tick += async (_, _) =>
        {
            _fileDebounce.Stop();
            await SearchFilesAsync(Query.Text);
        };
    }

    public void Toggle()
    {
        if (IsVisible) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        // Center on the display holding the mouse, a fifth of the way down.
        Win32.GetCursorPos(out var p);
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
        double s = Win32.GetScaleForScreen(screen);
        var b = screen.Bounds;

        _shownAt = DateTime.Now;
        if (AppCatalog.Instance.Apps.Count == 0) _ = AppCatalog.Instance.LoadAsync();
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        int w = (int)(Width * s);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, b.Left + (b.Width - w) / 2, b.Top + (int)(b.Height * 0.2), 0, 0,
            Win32.SWP_NOSIZE);
        Activate();
        Win32.SetForegroundWindow(hwnd);
        Query.Focus();
        Keyboard.Focus(Query);
        Query.SelectAll();
        if (Query.Text.Length > 0) RunSearch();
    }

    public void HideLauncher()
    {
        _fileCts?.Cancel();
        Hide();
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunSearch();
    }

    private void RunSearch()
    {
        string q = Query.Text;
        _fileCts?.Cancel();
        _results.Clear();
        foreach (var r in SearchProviders.Instant(q)) _results.Add(r);
        LoadIcons(_results.ToList());

        _fileDebounce.Stop();
        if (SettingsStore.Current.LauncherSearchFiles && q.Trim().Length >= 2) _fileDebounce.Start();

        bool any = _results.Count > 0;
        Results.Visibility = Divider.Visibility = Footer.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (any) Results.SelectedIndex = 0;
    }

    private async Task SearchFilesAsync(string q)
    {
        _fileCts = new CancellationTokenSource();
        var ct = _fileCts.Token;
        var files = await Task.Run(() => SearchProviders.Files(q, ct));
        if (ct.IsCancellationRequested || q != Query.Text || files.Count == 0) return;

        // Insert files before the trailing web-search entry.
        int insertAt = Math.Max(0, _results.Count - 1);
        foreach (var f in files.Take(6))
        {
            if (_results.Any(r => r.RevealPath != null && r.RevealPath.Equals(f.RevealPath, StringComparison.OrdinalIgnoreCase))) continue;
            _results.Insert(insertAt++, f);
        }
        LoadIcons(files);
    }

    private static void LoadIcons(IEnumerable<SearchResult> items)
    {
        foreach (var r in items.Where(r => r.IconSource != null && r.Icon == null))
        {
            var item = r;
            ShellIcons.GetAsync(item.IconSource, ShellIcons.PixelsFor(40)).ContinueWith(t =>
            {
                if (t.Result != null) item.Icon = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (Query.Text.Length > 0) Query.Clear(); else HideLauncher();
                e.Handled = true;
                break;
            case Key.Down:
                Move(1); e.Handled = true; break;
            case Key.Up:
                Move(-1); e.Handled = true; break;
            case Key.Enter:
                bool reveal = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                ExecuteSelected(reveal);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_results.Count == 0) return;
        int i = Math.Clamp(Results.SelectedIndex + delta, 0, _results.Count - 1);
        Results.SelectedIndex = i;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void Results_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) ExecuteSelected(false);
    }

    private void ExecuteSelected(bool reveal)
    {
        if (Results.SelectedItem is not SearchResult r) return;
        HideLauncher();
        if (reveal && r.RevealPath != null) Launch.RevealInExplorer(r.RevealPath);
        else r.Execute?.Invoke();
        Query.Clear();
    }
}
