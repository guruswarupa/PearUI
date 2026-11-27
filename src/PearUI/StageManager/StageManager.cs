using PearUI.Core;
using PearUI.Native;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PearUI.StageManager;

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
    private string _stageKey;                              // the app in front
    private HashSet<string> _stageKeys = new();            // every app on stage (several when windows are snapped side by side)
    private StageStripWindow _strip;
    private DispatcherTimer _settle;

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
            _stageKeys = _stageKey == null ? new() : new() { _stageKey };

            // Everything except the current app moves to the strip.
            foreach (var app in Tracker.Apps.Where(a => a.Key != _stageKey)) TuckAway(app);

            _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            _settle.Tick += (_, _) => { _settle.Stop(); ApplyForeground(); };
            Tracker.ForegroundChanged += OnForeground;
            Tracker.AppsChanged += OnAppsChanged;
            _strip = new StageStripWindow(this);
            _strip.Show();
        }
        else
        {
            Tracker.ForegroundChanged -= OnForeground;
            Tracker.AppsChanged -= OnAppsChanged;
            _settle?.Stop();
            _settle = null;
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
                    bool offStage = !_stageKeys.Contains(key);
                    if (!Tracker.IsStageWindow(w.Hwnd)) continue; // dialogs and popups are not cards
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

    private void TuckAway(RunningApp app, HashSet<IntPtr> except = null)
    {
        foreach (var w in app.Windows)
        {
            if (except != null && except.Contains(w.Hwnd)) continue;
            if (w.Minimized || Win32.IsIconic(w.Hwnd) || !Win32.IsOnMonitor(w.Hwnd, Screen)) continue;
            TakeSnapshot(w.Hwnd);
            // SHOWMINNOACTIVE: minimizing must not hand focus to yet another app.
            Win32.ShowWindowAsync(w.Hwnd, SW_SHOWMINNOACTIVE);
            _minimizedByUs.Add(w.Hwnd);
        }
    }

    /// <summary>
    /// The foreground flickers while windows open, dialogs pop up and menus appear. Wait a moment, then look at
    /// what is in front: this keeps transient windows from kicking the app off the stage.
    /// </summary>
    private void OnForeground()
    {
        if (_settle == null) return;
        _settle.Stop();
        _settle.Start();
    }

    private void ApplyForeground()
    {
        var app = Tracker.ForegroundApp;
        var fg = Tracker.ForegroundWindow;
        if (app == null || fg == IntPtr.Zero || !Win32.IsOnMonitor(fg, Screen)) return;

        // Popups, menus, tooltips, notifications, splash screens and tiny tool windows are not apps: never swap the stage for them.
        if (!Tracker.IsStageWindow(fg)) { StripChanged?.Invoke(); return; }

        // A dialog or popup that belongs to a window of an app on stage is part of that app: leave the stage alone.
        if (!_stageKeys.Contains(app.Key))
        {
            IntPtr owner = Win32.GetAncestor(fg, Win32.GA_ROOTOWNER);
            if (owner != IntPtr.Zero && owner != fg &&
                Tracker.Apps.Any(a => _stageKeys.Contains(a.Key) && a.Windows.Any(w => w.Hwnd == owner)))
                return;
        }

        _recent.Remove(app.Key);
        _recent.Insert(0, app.Key);

        // Windows that are snapped side by side (Snap Assist, Win+arrows, snap layouts) belong on stage together:
        // keep the other snapped windows that do not overlap the one you just picked.
        var keep = new HashSet<IntPtr>();
        var newKeys = new HashSet<string> { app.Key };
        if (IsSnapped(fg))
        {
            foreach (var a in Tracker.Apps.Where(a => _stageKeys.Contains(a.Key)))
                foreach (var w in a.Windows)
                    if (w.Hwnd != fg && !Win32.IsIconic(w.Hwnd) && Win32.IsOnMonitor(w.Hwnd, Screen)
                        && IsSnapped(w.Hwnd) && !Overlaps(w.Hwnd, fg))
                    {
                        keep.Add(w.Hwnd);
                        newKeys.Add(a.Key);
                    }
        }

        foreach (var a in Tracker.Apps.Where(a => _stageKeys.Contains(a.Key) && !newKeys.Contains(a.Key))) TuckAway(a);
        foreach (var a in Tracker.Apps.Where(a => newKeys.Contains(a.Key) && a.Key != app.Key)) TuckAway(a, keep);
        _stageKeys = newKeys;
        _stageKey = app.Key;
        StripChanged?.Invoke();
    }

    /// <summary>A window tiled to a half, third or quarter of the work area (not maximized).</summary>
    private static bool IsSnapped(IntPtr hwnd)
    {
        if (Win32.IsZoomed(hwnd) || Win32.IsIconic(hwnd)) return false;
        var f = Win32.GetFrameBounds(hwnd);
        var wa = Forms.Screen.FromHandle(hwnd).WorkingArea;
        const int tol = 28;
        bool left = Math.Abs(f.Left - wa.Left) <= tol, right = Math.Abs(f.Right - wa.Right) <= tol;
        bool top = Math.Abs(f.Top - wa.Top) <= tol, bottom = Math.Abs(f.Bottom - wa.Bottom) <= tol;
        bool fullW = f.Width >= wa.Width - tol, fullH = f.Height >= wa.Height - tol;
        bool narrow = f.Width <= wa.Width * 0.58 && f.Width >= wa.Width * 0.2;
        bool low = f.Height <= wa.Height * 0.58 && f.Height >= wa.Height * 0.2;
        return ((left || right) && narrow && (fullH || (low && (top || bottom))))
            || ((top || bottom) && low && fullW);
    }

    private static bool Overlaps(IntPtr a, IntPtr b)
    {
        var r1 = Win32.GetFrameBounds(a);
        var r2 = Win32.GetFrameBounds(b);
        long w = Math.Min(r1.Right, r2.Right) - Math.Max(r1.Left, r2.Left);
        long h = Math.Min(r1.Bottom, r2.Bottom) - Math.Max(r1.Top, r2.Top);
        if (w <= 0 || h <= 0) return false;
        long smaller = Math.Min((long)r1.Width * r1.Height, (long)r2.Width * r2.Height);
        return smaller > 0 && w * h > smaller * 0.08; // a few pixels of shared border do not count
    }

    private void OnAppsChanged()
    {
        var keys = Tracker.Apps.Select(a => a.Key).ToHashSet();
        _recent.RemoveAll(k => !keys.Contains(k));
        _stageKeys.RemoveWhere(k => !keys.Contains(k));
        foreach (var k in keys) if (!_recent.Contains(k)) _recent.Add(k);
        _minimizedByUs.RemoveWhere(h => !Win32.IsWindow(h));
        foreach (var dead in _snapshots.Keys.Where(h => !Win32.IsWindow(h)).ToList()) _snapshots.Remove(dead);
        StripChanged?.Invoke();
    }
}
