using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PearUI.Native;

namespace PearUI.Shell;

/// <summary>
/// "Shake to find the pointer", like macOS: wiggle the mouse back and forth quickly and the pointer grows so it is easy
/// to spot, then shrinks back when you stop. The grown pointer is a click-through overlay drawn over the real one.
/// </summary>
public sealed class CursorFinder
{
    public static CursorFinder Instance { get; } = new();

    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public Win32.POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInst, IntPtr name);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr hCursor);
    private const uint IMAGE_CURSOR = 2, LR_LOADFROMFILE = 0x10;
    private const int HighResPixels = 160; // ask Windows for a large version so the grown pointer stays sharp
    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int w, int h, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr hCursor, uint id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint flags);

    private const uint SPI_SETCURSORS = 0x0057;
    // The standard pointers Windows uses (arrow, text, wait, resize, hand ...).
    private static readonly uint[] SystemCursorIds =
        { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650 };

    private const double MaxScale = 3.6;
    private const int Pad = 150; // device pixels the overlay extends around the pointer's hot spot

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(12) };
    private readonly Axis _x = new(), _y = new();
    private Window _overlay;
    private Image _image;
    private ScaleTransform _scale;
    private IntPtr _shownCursor;
    private int _hotX, _hotY;
    private int _normalW = 32, _normalH = 32; // size of the pointer at normal scale, in device pixels
    private bool _grown;
    private bool _realHidden;
    private bool _shrinking;
    private long _lastShakeMs;

    private CursorFinder() => _timer.Tick += (_, _) => Tick();

    public void Start()
    {
        RestoreRealCursor(); // in case an earlier run was cut short while the pointer was hidden
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        Shrink(immediate: true);
        RestoreRealCursor();
    }

    private void OnExit(object sender, EventArgs e) => RestoreRealCursor();

    /// <summary>Makes the real pointer invisible so only the grown copy shows (one big pointer, not two).</summary>
    private void HideRealCursor()
    {
        if (_realHidden) return;
        _realHidden = true;
        var andPlane = Enumerable.Repeat((byte)0xFF, 128).ToArray(); // 32x32, 1 bit per pixel: keep the screen
        var xorPlane = new byte[128];                                // ...and add nothing
        foreach (var id in SystemCursorIds)
        {
            var blank = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andPlane, xorPlane);
            if (blank != IntPtr.Zero) SetSystemCursor(blank, id); // Windows takes ownership of the handle
        }
    }

    /// <summary>Reloads the user's own pointer scheme from the registry.</summary>
    private void RestoreRealCursor()
    {
        if (!_realHidden) return;
        _realHidden = false;
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
    }

    // ---------- detecting the shake ----------
    /// <summary>Counts sharp back-and-forth turns along one axis.</summary>
    private sealed class Axis
    {
        private int _last = int.MinValue, _dir, _turnAt;
        private readonly Queue<long> _turns = new();

        public void Reset() { _last = int.MinValue; _dir = 0; _turns.Clear(); }

        public int Feed(int pos, long nowMs)
        {
            if (_last == int.MinValue) { _last = _turnAt = pos; return 0; }
            int d = pos - _last;
            if (Math.Abs(d) >= 3)
            {
                int dir = Math.Sign(d);
                if (_dir != 0 && dir != _dir)
                {
                    if (Math.Abs(_last - _turnAt) >= 40) _turns.Enqueue(nowMs); // a real stroke, not a tremor
                    _turnAt = _last;
                }
                else if (_dir == 0) _turnAt = _last;
                _dir = dir;
                _last = pos;
            }
            while (_turns.Count > 0 && nowMs - _turns.Peek() > 650) _turns.Dequeue();
            return _turns.Count;
        }
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        if (!Win32.GetCursorPos(out var p)) return;

        // Dragging (windows, selections) must never count as shaking.
        bool dragging = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (dragging) { _x.Reset(); _y.Reset(); Shrink(false); return; }

        int turns = Math.Max(_x.Feed(p.X, now), _y.Feed(p.Y, now));
        if (turns >= 4)
        {
            _lastShakeMs = now;
            if (!_grown) Grow(p);
        }
        else if (_grown && now - _lastShakeMs > 450) Shrink(false);

        if (_grown || _shrinking) Follow(p); // keep following the mouse while it shrinks back
    }

    // ---------- the grown pointer ----------
    private void EnsureOverlay()
    {
        if (_overlay != null) return;
        _image = new Image { Stretch = Stretch.None, SnapsToDevicePixels = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _scale = new ScaleTransform(1, 1);
        _image.RenderTransform = _scale;
        var canvas = new Canvas { Background = Brushes.Transparent, UseLayoutRounding = false };
        canvas.Children.Add(_image);
        _overlay = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, Focusable = false, Content = canvas,
            Title = "PearUI Cursor", Width = 1, Height = 1,
        };
        _overlay.SourceInitialized += (_, _) =>
            Win32.AddExStyle(new WindowInteropHelper(_overlay).Handle, Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED);
    }

    private void Grow(Win32.POINT p)
    {
        EnsureOverlay();
        // Shaking again mid-shrink: the real pointer is still hidden and the image is already loaded; just grow from here.
        if (!_realHidden && !LoadCursorImage()) return;
        _grown = true;
        _shrinking = false;
        _overlay.Show();
        Follow(p);
        HideRealCursor(); // after the shape has been copied
        Animate(MaxScale, 180, EasingMode.EaseOut);
    }

    private void Shrink(bool immediate)
    {
        if (!_grown) return;
        _grown = false;
        _x.Reset(); _y.Reset();
        if (immediate || _overlay == null)
        {
            _shrinking = false;
            _overlay?.Hide();
            RestoreRealCursor();
            return;
        }

        // Shrink slowly and evenly. The real pointer stays hidden until the big one has reached its size,
        // then takes over at the same spot, so there is never a second pointer or a visible jump.
        _shrinking = true;
        var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseInOut } };
        anim.Completed += (_, _) =>
        {
            if (_grown || !_shrinking) return; // shaken again in the meantime
            _shrinking = false;
            RestoreRealCursor();
            _overlay?.Hide();
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim.Clone());
    }

    private void Animate(double to, int ms, EasingMode mode)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new BackEase { EasingMode = mode, Amplitude = 0.35 } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim.Clone());
    }

    /// <summary>Copies the pointer Windows is currently showing (arrow, hand, text cursor...) into the overlay image.</summary>
    private bool LoadCursorImage()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.hCursor == IntPtr.Zero || (ci.flags & 1) == 0) return false;
        if (ci.hCursor == _shownCursor && _image.Source != null) return true;
        try
        {
            var small = Imaging.CreateBitmapSourceFromHIcon(ci.hCursor, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            small.Freeze();
            _normalW = small.PixelWidth;
            _normalH = small.PixelHeight;
            BitmapSource src = HighResVersion(ci.hCursor, small) ?? small;
            if (GetIconInfo(ci.hCursor, out var info))
            {
                _hotX = info.xHotspot; _hotY = info.yHotspot;
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
            _image.Source = src;
            _shownCursor = ci.hCursor;
            return true;
        }
        catch { return false; }
    }

    private static readonly Dictionary<uint, string> CursorValueNames = new()
    {
        [32512] = "Arrow", [32513] = "IBeam", [32514] = "Wait", [32515] = "Crosshair", [32516] = "UpArrow",
        [32642] = "SizeNWSE", [32643] = "SizeNESW", [32644] = "SizeWE", [32645] = "SizeNS", [32646] = "SizeAll",
        [32648] = "No", [32649] = "Hand", [32650] = "AppStarting",
    };

    /// <summary>
    /// The 32 pixel pointer looks blocky when enlarged. For standard pointers, load the user's pointer file again at a
    /// large size (preferring Windows' own "_xl" extra-large variant when it exists). Custom pointers use the small image.
    /// </summary>
    private static BitmapSource HighResVersion(IntPtr current, BitmapSource small)
    {
        try
        {
            foreach (var id in SystemCursorIds)
            {
                if (LoadCursor(IntPtr.Zero, (IntPtr)id) != current) continue;
                if (!CursorValueNames.TryGetValue(id, out var valueName)) return null;
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
                var path = Environment.ExpandEnvironmentVariables(key?.GetValue(valueName) as string ?? "");
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;

                string xl = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),
                    System.IO.Path.GetFileNameWithoutExtension(path) + "_xl" + System.IO.Path.GetExtension(path));
                foreach (var file in new[] { xl, path })
                {
                    if (!System.IO.File.Exists(file)) continue;
                    var big = LoadImage(IntPtr.Zero, file, IMAGE_CURSOR, HighResPixels, HighResPixels, LR_LOADFROMFILE);
                    if (big == IntPtr.Zero) continue;
                    try
                    {
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(big, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        if (bmp.PixelWidth > small.PixelWidth) return bmp;
                    }
                    finally { DestroyCursor(big); }
                }
                return null;
            }
        }
        catch { }
        return null;
    }

    private void Follow(Win32.POINT p)
    {
        if (_overlay == null) return;
        var handle = new WindowInteropHelper(_overlay).Handle;
        if (handle == IntPtr.Zero) return;
        if (!_realHidden) LoadCursorImage(); // once the real pointer is hidden Windows reports a blank one

        double s = Win32.GetScaleForWindow(handle);
        // Drawn at the normal pointer size whatever the source resolution, then scaled up by the animation.
        _image.Stretch = Stretch.Fill;
        _image.Width = _normalW / s;
        _image.Height = _normalH / s;
        int size = Pad * 2;
        Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, p.X - Pad, p.Y - Pad, size, size, Win32.SWP_NOACTIVATE);
        _overlay.Width = size / s;
        _overlay.Height = size / s;

        // The image sits so its hot spot is at the middle of the overlay, and grows around that point.
        double hx = _hotX / s, hy = _hotY / s, mid = Pad / s;
        Canvas.SetLeft(_image, mid - hx);
        Canvas.SetTop(_image, mid - hy);
        _scale.CenterX = hx;
        _scale.CenterY = hy;
    }
}
