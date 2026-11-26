using MacUI.Core;
using MacUI.Native;
using Forms = System.Windows.Forms;

namespace MacUI.StageManager;

/// <summary>
/// Stage Manager: the app you're using stays "on stage"; every other app is tucked away (minimized)
/// and shown as a live thumbnail in a strip on the left. Clicking a thumbnail swaps it onto the stage.
/// Works on the primary display; windows on other displays are left alone.
/// </summary>
public sealed class StageManager
{
    public static StageManager Instance { get; } = new();

    private const int SW_SHOWMINNOACTIVE = 7;
    private readonly List<string> _recent = new();        // app keys, most recent first
    private readonly HashSet<IntPtr> _minimizedByUs = new();
    private string _stageKey;
    private StageStripWindow _strip;

    public bool Enabled { get; private set; }
    public event Action StripChanged;

    private static WindowTracker Tracker => WindowTracker.Instance;
    private static Forms.Screen Screen => Forms.Screen.PrimaryScreen;

    public void Toggle() => SetEnabled(!Enabled);

    public void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        Enabled = on;
        if (SettingsStore.Current.StageManager != on)
        {
            SettingsStore.Current.StageManager = on;
            SettingsStore.Save(raiseEvent: false);
        }

        if (on)
        {
            Tracker.Refresh();
            _recent.Clear();
            _recent.AddRange(Tracker.Apps.OrderBy(a => a.ZOrder).Select(a => a.Key));
            _stageKey = Tracker.ForegroundApp?.Key ?? _recent.FirstOrDefault();

            // Everything except the current app moves to the strip.
            foreach (var app in Tracker.Apps.Where(a => a.Key != _stageKey)) TuckAway(app);

            Tracker.ForegroundChanged += OnForeground;
            Tracker.AppsChanged += OnAppsChanged;
            _strip = new StageStripWindow(this);
            _strip.Show();
        }
        else
        {
            Tracker.ForegroundChanged -= OnForeground;
            Tracker.AppsChanged -= OnAppsChanged;
            _strip?.Close();
            _strip = null;
            // Give back the windows we tucked away, without stealing focus.
            foreach (var h in _minimizedByUs)
                if (Win32.IsWindow(h) && Win32.IsIconic(h)) Win32.ShowWindowAsync(h, Win32.SW_SHOWNOACTIVATE);
            _minimizedByUs.Clear();
        }
        StripChanged?.Invoke();
    }

    /// <summary>Recent apps shown in the strip (not the one on stage).</summary>
    public IReadOnlyList<RunningApp> StripApps
    {
        get
        {
            var apps = Tracker.Apps;
            return _recent.Where(k => k != _stageKey)
                .Select(k => apps.FirstOrDefault(a => a.Key == k))
                .Where(a => a != null && a.Windows.Any(w => w.Minimized || Win32.IsOnMonitor(w.Hwnd, Screen)))
                .Take(6).ToList();
        }
    }

    /// <summary>
    /// One card per window: every window of the apps off stage, plus the tucked-away windows of the app on stage,
    /// so several windows of the same app can each be picked directly.
    /// </summary>
    public IReadOnlyList<(RunningApp App, AppWindow Window)> StripWindows
    {
        get
        {
            var apps = Tracker.Apps;
            var list = new List<(RunningApp, AppWindow)>();
            foreach (var key in _recent)
            {
                var app = apps.FirstOrDefault(a => a.Key == key);
                if (app == null) continue;
                foreach (var w in app.Windows)
                {
                    bool offStage = key != _stageKey;
                    if ((offStage && (w.Minimized || Win32.IsOnMonitor(w.Hwnd, Screen))) || (!offStage && w.Minimized && Win32.IsIconic(w.Hwnd)))
                        list.Add((app, w));
                }
            }
            return list;
        }
    }

    /// <summary>Puts one window from the strip onto the stage (its app's other windows stay in the strip).</summary>
    public void Bring(RunningApp app, AppWindow window)
    {
        if (Win32.IsIconic(window.Hwnd)) Win32.ShowWindow(window.Hwnd, Win32.SW_RESTORE);
        _minimizedByUs.Remove(window.Hwnd);
        Win32.ActivateWindow(window.Hwnd); // the foreground change moves the old app to the strip
    }

    // ---------- snapshots: DWM has no live image of a minimized window, so keep the last frame ----------
    private readonly Dictionary<IntPtr, System.Windows.Media.ImageSource> _snapshots = new();

    public System.Windows.Media.ImageSource Snapshot(IntPtr hwnd) => _snapshots.TryGetValue(hwnd, out var s) ? s : null;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    public void TakeSnapshot(IntPtr hwnd)
    {
        try
        {
            var r = Win32.GetFrameBounds(hwnd);
            Win32.GetWindowRect(hwnd, out var full);
            if (full.Width <= 0 || full.Height <= 0) return;
            using var bmp = new System.Drawing.Bitmap(full.Width, full.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 2 /*PW_RENDERFULLCONTENT: works for GPU-rendered apps*/); }
                finally { g.ReleaseHdc(hdc); }
            }
            // Crop the invisible resize borders, then downscale to thumbnail size.
            var crop = new System.Drawing.Rectangle(r.Left - full.Left, r.Top - full.Top, r.Width, r.Height);
            crop.Intersect(new System.Drawing.Rectangle(0, 0, full.Width, full.Height));
            if (crop.Width <= 0 || crop.Height <= 0) return;
            double k = Math.Min(1.0, 360.0 / crop.Width);
            int tw = Math.Max(1, (int)(crop.Width * k)), th = Math.Max(1, (int)(crop.Height * k));
            using var small = new System.Drawing.Bitmap(tw, th);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, new System.Drawing.Rectangle(0, 0, tw, th), crop, System.Drawing.GraphicsUnit.Pixel);
            }
            IntPtr hb = small.GetHbitmap();
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _snapshots[hwnd] = src;
            }
            finally { Win32.DeleteObject(hb); }
        }
        catch { /* a missing snapshot only means the app icon is shown instead */ }
    }

    private void TuckAway(RunningApp app)
    {
        foreach (var w in app.Windows)
        {
            if (w.Minimized || Win32.IsIconic(w.Hwnd) || !Win32.IsOnMonitor(w.Hwnd, Screen)) continue;
            TakeSnapshot(w.Hwnd);
            // SHOWMINNOACTIVE: minimizing must not hand focus to yet another app.
            Win32.ShowWindowAsync(w.Hwnd, SW_SHOWMINNOACTIVE);
            _minimizedByUs.Add(w.Hwnd);
        }
    }

    private void OnForeground()
    {
        var app = Tracker.ForegroundApp;
        var fg = Tracker.ForegroundWindow;
        if (app == null || fg == IntPtr.Zero || !Win32.IsOnMonitor(fg, Screen)) return;

        // A dialog or popup that belongs to a window of the app on stage is part of that app: leave the stage alone.
        if (app.Key != _stageKey)
        {
            IntPtr owner = Win32.GetAncestor(fg, Win32.GA_ROOTOWNER);
            if (owner != IntPtr.Zero && owner != fg &&
                Tracker.Apps.Any(a => a.Key == _stageKey && a.Windows.Any(w => w.Hwnd == owner)))
                return;
        }

        _recent.Remove(app.Key);
        _recent.Insert(0, app.Key);
        if (app.Key != _stageKey)
        {
            var previous = Tracker.Apps.FirstOrDefault(a => a.Key == _stageKey);
            _stageKey = app.Key;
            if (previous != null) TuckAway(previous);
        }
        StripChanged?.Invoke();
    }

    private void OnAppsChanged()
    {
        var keys = Tracker.Apps.Select(a => a.Key).ToHashSet();
        _recent.RemoveAll(k => !keys.Contains(k));
        foreach (var k in keys) if (!_recent.Contains(k)) _recent.Add(k);
        _minimizedByUs.RemoveWhere(h => !Win32.IsWindow(h));
        foreach (var dead in _snapshots.Keys.Where(h => !Win32.IsWindow(h)).ToList()) _snapshots.Remove(dead);
        StripChanged?.Invoke();
    }
}
