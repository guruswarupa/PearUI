using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PearUI.Native;

namespace PearUI.Core;

/// <summary>
/// Makes ordinary app windows slightly see-through (like a translucent Terminal), except full-screen apps.
///
/// How: Windows can give any top-level window a constant opacity (a "layered window" alpha). PearUI sets it on
/// the windows of other apps and removes it again when the setting is turned off, when a window goes full screen
/// and when PearUI quits.
///
/// Windows that can't take it safely are left alone:
///  • windows with no redirection surface (Chromium browsers and Electron apps such as Brave, Chrome, Edge, VS Code,
///    modern WinUI apps) — making them see-through from outside can leave them blank,
///  • windows that already are layered (the app uses it itself), Store-app frames, elevated apps,
///  • video players and screen recorders, and PearUI's own windows.
/// </summary>
public sealed class WindowTranslucency : IDisposable
{
    private const long WS_EX_LAYERED = 0x00080000L, WS_EX_NOREDIRECTIONBITMAP = 0x00200000L, WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L;
    private const uint LWA_ALPHA = 0x2;
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);

    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameWindow", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "#32770" /* system message boxes */,
    };

    private static readonly HashSet<string> SkipPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc.exe", "mpv.exe", "mpc-hc.exe", "mpc-hc64.exe", "mpc-be.exe", "mpc-be64.exe", "potplayermini.exe", "potplayermini64.exe",
        "obs64.exe", "obs32.exe", "wmplayer.exe", "teams.exe", "ms-teams.exe", "zoom.exe",
    };

    private readonly Dictionary<IntPtr, byte> _applied = new();   // windows we made translucent → the alpha we set
    private readonly HashSet<IntPtr> _leaveAlone = new();         // windows we decided not to touch (until they close)
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public WindowTranslucency()
    {
        _timer.Tick += (_, _) => Apply();
        _soon.Tick += (_, _) => { _soon.Stop(); Apply(); };
        WindowTracker.Instance.AppsChanged += Soon;
        WindowTracker.Instance.ForegroundChanged += Soon;
        WindowTracker.Instance.ForegroundMoved += _ => Soon(); // F11 / leaving full screen resizes the window
        _timer.Start();
        Apply();
    }


    private void Soon() { _soon.Stop(); _soon.Start(); }

    /// <summary>Brings every window in line with the current setting.</summary>
    public void Apply()
    {
        var s = SettingsStore.Current;
        if (!s.TranslucentWindows) { RestoreAll(); return; }

        byte alpha = (byte)Math.Round(Math.Clamp(s.WindowOpacity, 60, 100) * 255 / 100.0);
        var seen = new HashSet<IntPtr>();
        foreach (var app in WindowTracker.Instance.Apps)
        {
            foreach (var w in app.Windows)
            {
                IntPtr h = w.Hwnd;
                seen.Add(h);
                if (w.Minimized) continue;
                if (Eligible(h, app) && !FullScreenGuard.IsFullScreenWindow(h)) Make(h, alpha);
                else Restore(h);
            }
        }

        // Forget windows that are gone.
        foreach (var gone in _applied.Keys.Where(h => !Win32.IsWindow(h)).ToList()) _applied.Remove(gone);
        _leaveAlone.RemoveWhere(h => !Win32.IsWindow(h));
    }

    private bool Eligible(IntPtr h, RunningApp app)
    {
        if (_leaveAlone.Contains(h)) return false;
        Win32.GetWindowThreadProcessId(h, out uint pid);
        if (pid == _ownPid) return false;
        if (SkipClasses.Contains(Win32.GetClass(h))) return false;
        if (app.ExePath != null && SkipPrograms.Contains(Path.GetFileName(app.ExePath))) return false;

        long style = Win32.GetWindowLongPtr(h, Win32.GWL_STYLE).ToInt64();
        if ((style & WS_CAPTION) != WS_CAPTION && (style & WS_THICKFRAME) == 0) return false; // popups, splash screens, borderless windows

        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_NOREDIRECTIONBITMAP) != 0 || (ex & WS_EX_TRANSPARENT) != 0)
        {
            _leaveAlone.Add(h);
            return false;
        }
        return true;
    }

    private void Make(IntPtr h, byte alpha)
    {
        if (_applied.TryGetValue(h, out byte current))
        {
            if (current == alpha) return;
            if (SetLayeredWindowAttributes(h, 0, alpha, LWA_ALPHA)) _applied[h] = alpha;
            return;
        }

        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_LAYERED) != 0)
        {
            // Already layered. If it carries a plain alpha we set earlier (PearUI ended abnormally), take it over;
            // otherwise the app is using layered rendering itself: hands off.
            if (GetLayeredWindowAttributes(h, out _, out byte a, out uint flags) && flags == LWA_ALPHA && a >= 140 && a <= 255)
            {
                _applied[h] = a;
                if (a != alpha && SetLayeredWindowAttributes(h, 0, alpha, LWA_ALPHA)) _applied[h] = alpha;
            }
            else _leaveAlone.Add(h);
            return;
        }

        Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
        if (SetLayeredWindowAttributes(h, 0, alpha, LWA_ALPHA)) _applied[h] = alpha;
        else
        {
            // Not allowed (an elevated app, for example): undo and don't try again.
            Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex));
            _leaveAlone.Add(h);
        }
    }

    private void Restore(IntPtr h)
    {
        if (!_applied.Remove(h) || !Win32.IsWindow(h)) return;
        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
        Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_LAYERED));
        RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
    }

    /// <summary>Gives every window back its normal, solid look.</summary>
    public void RestoreAll()
    {
        foreach (var h in _applied.Keys.ToList()) Restore(h);
        _applied.Clear();
    }

    public void Dispose()
    {
        _timer.Stop();
        _soon.Stop();
        RestoreAll();
    }
}
