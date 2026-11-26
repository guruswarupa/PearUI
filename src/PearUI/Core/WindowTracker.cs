using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using PearUI.Native;

namespace PearUI.Core;

public sealed class AppWindow
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; }
    public uint Pid { get; init; }
    public bool Minimized { get; set; }
}

public sealed class RunningApp
{
    /// <summary>Grouping key: the AUMID if the window has one, otherwise the lower-cased exe path.</summary>
    public string Key { get; init; }
    public string Name { get; set; }
    public string ExePath { get; init; }
    public string Aumid { get; init; }
    public List<AppWindow> Windows { get; } = new();
    /// <summary>Z-order rank of the app's top window (0 = frontmost). Used to seed recency.</summary>
    public int ZOrder { get; set; }
    /// <summary>
    /// Where the icon comes from: the Apps-list entry when Windows knows this app id (Store apps, most apps),
    /// otherwise the program file. Some apps (e.g. Steam: "Valve.Steam.Client") use ids Windows can't resolve.
    /// </summary>
    public string IconSource =>
        Aumid != null && (ExePath == null || Aumid.Contains('!') || AppCatalog.Instance.FindByAumid(Aumid) != null)
            ? @"shell:AppsFolder\" + Aumid
            : ExePath;
}

/// <summary>
/// Keeps track of top-level application windows (the same set Alt+Tab shows) and of the
/// foreground app. All events are raised on the UI thread.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    public static WindowTracker Instance { get; } = new();

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
    };

    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly ConcurrentDictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Win32.WinEventDelegate _winEventProc; // keep alive for the native hook
    private readonly List<IntPtr> _hooks = new();
    private DispatcherTimer _timer;
    private string _lastSignature = "";

    public IReadOnlyList<RunningApp> Apps { get; private set; } = Array.Empty<RunningApp>();

    /// <summary>The last foreground window that does not belong to PearUI.</summary>
    public IntPtr ForegroundWindow { get; private set; }
    public RunningApp ForegroundApp { get; private set; }

    public event Action AppsChanged;
    public event Action ForegroundChanged;
    /// <summary>Raised when the foreground window moves or resizes (used by window controls).</summary>
    public event Action<IntPtr> ForegroundMoved;

    private WindowTracker()
    {
        _winEventProc = OnWinEvent;
    }

    public void Start()
    {
        if (_timer != null) return;
        Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND);
        Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_HIDE);
        Hook(Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE);
        Hook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
        UpdateForeground(Win32.GetForegroundWindow());
    }

    private void Hook(uint min, uint max)
    {
        var h = Win32.SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private DispatcherOperation _pendingRefresh;

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        switch (ev)
        {
            case Win32.EVENT_SYSTEM_FOREGROUND:
                UpdateForeground(hwnd);
                QueueRefresh();
                break;
            case Win32.EVENT_OBJECT_LOCATIONCHANGE:
                if (hwnd == ForegroundWindow) ForegroundMoved?.Invoke(hwnd);
                break;
            case Win32.EVENT_OBJECT_NAMECHANGE:
                if (hwnd == ForegroundWindow) ForegroundChanged?.Invoke();
                break;
            default:
                // Only top-level windows matter; ignore the flood of child-window events.
                if (Win32.GetAncestor(hwnd, 2 /*GA_ROOT*/) == hwnd) QueueRefresh();
                break;
        }
    }

    private void QueueRefresh()
    {
        if (_pendingRefresh != null && _pendingRefresh.Status == DispatcherOperationStatus.Pending) return;
        _pendingRefresh = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
    }

    private void UpdateForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownPid) return; // menus / popups of PearUI must not change the "active app"

        string cls = Win32.GetClass(hwnd);
        if (cls is "Progman" or "WorkerW" || hwnd == Win32.GetShellWindow())
        {
            ForegroundWindow = IntPtr.Zero;
            ForegroundApp = null;
        }
        else if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow"
                 or "XamlExplorerHostIslandWindow" or "ForegroundStaging" or "MultitaskingViewFrame")
        {
            return; // Start menu, search, Alt+Tab, etc.: keep the previous app
        }
        else
        {
            ForegroundWindow = hwnd;
            ForegroundApp = Describe(hwnd, pid);
        }
        ForegroundChanged?.Invoke();
    }

    /// <summary>Re-enumerates windows; raises AppsChanged if anything relevant changed.</summary>
    public void Refresh()
    {
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd, out uint pid)) return true;
            var info = Describe(hwnd, pid);
            if (info == null) return true;
            if (!apps.TryGetValue(info.Key, out var app))
            {
                app = info;
                app.ZOrder = order.Count;
                apps[info.Key] = app;
                order.Add(info.Key);
            }
            app.Windows.Add(new AppWindow { Hwnd = hwnd, Pid = pid, Title = Win32.GetTitle(hwnd), Minimized = Win32.IsIconic(hwnd) });
            return true;
        }, IntPtr.Zero);

        // Stable ordering: order of first appearance is Z-order; sort by name to avoid jumping icons.
        var list = order.Select(k => apps[k]).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        string sig = string.Join("|", list.Select(a => a.Key + ":" + a.Windows.Count + ":" + a.Windows.Count(w => w.Minimized)));
        Apps = list;
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            AppsChanged?.Invoke();
        }
    }

    private bool IsAppWindow(IntPtr hwnd, out uint pid)
    {
        pid = 0;
        if (!Win32.IsWindowVisible(hwnd)) return false;
        if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return false;
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return false;
        if ((ex & Win32.WS_EX_NOACTIVATE) != 0) return false;
        if (Win32.GetWindowTextLength(hwnd) == 0) return false;
        if (Win32.IsCloaked(hwnd)) return false;
        if (IgnoredClasses.Contains(Win32.GetClass(hwnd))) return false;
        Win32.GetWindowThreadProcessId(hwnd, out pid);
        return pid != _ownPid;
    }

    /// <summary>Builds a RunningApp (without windows) describing which app owns a window.</summary>
    private RunningApp Describe(IntPtr hwnd, uint pid)
    {
        string exe = Win32.GetProcessPath(pid);
        string aumid = WindowProps.GetAppUserModelId(hwnd);

        // Store apps are hosted by ApplicationFrameHost: find the real app process inside the frame.
        if (exe != null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint childPid = 0;
            Win32.EnumChildWindows(hwnd, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out uint p);
                if (p != pid) { childPid = p; return false; }
                return true;
            }, IntPtr.Zero);
            if (childPid != 0) exe = Win32.GetProcessPath(childPid) ?? exe;
        }

        // Apps whose windows live in a helper process (Steam's steamwebhelper.exe, other CEF/Electron
        // helpers): use the program that started the helper for the icon, the name and Dock matching.
        exe = MainProgramFor(exe, pid);

        if (exe == null && aumid == null) return null;

        // One program = one app. Browsers (and other Chromium/Electron apps) give popups and installed web apps
        // their own app id although they run in the same program; keying by app id would make Stage Manager hide
        // the browser when you click a popup, and would show extra Dock icons. Only when the real program couldn't
        // be found (an unresolved Store-app frame) is the app id the identity.
        bool unresolvedFrame = exe == null || exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        string key = !unresolvedFrame ? exe.ToLowerInvariant() : (aumid ?? exe.ToLowerInvariant());
        return new RunningApp { Key = key, ExePath = exe, Aumid = aumid, Name = ResolveName(aumid, exe) };
    }

    private readonly ConcurrentDictionary<uint, string> _mainProgramCache = new();

    private static bool IsHelper(string exe) =>
        exe != null && System.IO.Path.GetFileNameWithoutExtension(exe).Contains("helper", StringComparison.OrdinalIgnoreCase);

    /// <summary>For a helper process, walks up to the program that launched it (at most a few levels).</summary>
    private string MainProgramFor(string exe, uint pid)
    {
        if (!IsHelper(exe)) return exe;
        return _mainProgramCache.GetOrAdd(pid, _ =>
        {
            string current = exe;
            uint p = pid;
            for (int i = 0; i < 4 && IsHelper(current); i++)
            {
                p = Win32.GetParentPid(p);
                if (p == 0) break;
                string parent = Win32.GetProcessPath(p);
                if (parent == null || parent.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return IsHelper(current) ? exe : current;
        });
    }

    private string ResolveName(string aumid, string exe)
    {
        string cacheKey = aumid ?? exe;
        return _nameCache.GetOrAdd(cacheKey, _ =>
        {
            if (aumid != null)
            {
                var entry = AppCatalog.Instance.FindByAumid(aumid);
                if (entry != null) return entry.Name;
            }
            if (exe != null)
            {
                string file = Path.GetFileName(exe);
                if (file.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
                if (file.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase) ||
                    file.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase)) return "Terminal";
                var byExe = AppCatalog.Instance.FindByExe(exe);
                if (byExe != null) return byExe.Name;
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
                    if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
                }
                catch { }
                return Path.GetFileNameWithoutExtension(exe);
            }
            return aumid;
        });
    }

    /// <summary>Does a running app belong to a pinned dock item?</summary>
    public static bool Matches(PinnedApp pin, RunningApp app)
    {
        if (pin == null || app == null) return false;
        if (app.Aumid != null && pin.Target != null &&
            pin.Target.Equals(@"shell:AppsFolder\" + app.Aumid, StringComparison.OrdinalIgnoreCase)) return true;

        string pinExe = pin.ExePath ?? (pin.Target != null && pin.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pin.Target : null);
        if (pinExe != null && app.ExePath != null && string.Equals(Path.GetFullPath(pinExe), app.ExePath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Store app pinned by AUMID, running window without AUMID: compare package family names.
        if (pin.Target != null && pin.Target.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) && app.ExePath != null)
        {
            string aumid = pin.Target.Substring(@"shell:AppsFolder\".Length);
            int bang = aumid.IndexOf('!');
            string family = bang > 0 ? aumid[..bang] : null;
            string appFamily = PackageFamilyFromPath(app.ExePath);
            if (family != null && appFamily != null && family.Equals(appFamily, StringComparison.OrdinalIgnoreCase)) return true;

            // Desktop apps listed in AppsFolder by exe path or known-folder-relative path.
            string resolved = AppCatalog.ResolveAppsFolderPath(aumid);
            if (resolved != null && string.Equals(resolved, app.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>C:\Program Files\WindowsApps\Name_1.2.3.0_x64__hash\app.exe → Name_hash</summary>
    public static string PackageFamilyFromPath(string exe)
    {
        const string marker = @"\WindowsApps\";
        int i = exe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = exe[(i + marker.Length)..];
        int slash = rest.IndexOf('\\');
        if (slash < 0) return null;
        string folder = rest[..slash];
        int dbl = folder.IndexOf("__", StringComparison.Ordinal);
        int first = folder.IndexOf('_');
        if (dbl < 0 || first < 0) return null;
        return folder[..first] + "_" + folder[(dbl + 2)..];
    }

    /// <summary>True when the foreground window covers the whole monitor (games, videos, presentations).</summary>
    public bool IsFullScreenOn(Win32.RECT monitor)
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Win32.GetShellWindow()) return false;
        Win32.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _ownPid) return false;
        string cls = Win32.GetClass(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        // A window with a normal title bar is just a big window (WindowFitter shrinks it), not full screen.
        long style = Win32.GetWindowLongPtr(fg, Win32.GWL_STYLE).ToInt64();
        if ((style & 0x00C00000L) == 0x00C00000L) return false;
        Win32.GetWindowRect(fg, out var r);
        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    // ---------- window actions ----------
    public static void Close(IntPtr hwnd) => Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    // ShowWindow works even when the window has no system menu / caption buttons (see CaptionButtonHider).
    public static void Minimize(IntPtr hwnd) => Win32.ShowWindowAsync(hwnd, Win32.SW_MINIMIZE);
    public static void ToggleZoom(IntPtr hwnd) =>
        Win32.ShowWindowAsync(hwnd, Win32.IsZoomed(hwnd) ? Win32.SW_RESTORE : Win32.SW_MAXIMIZE);

    public static void QuitApp(RunningApp app)
    {
        foreach (var w in app.Windows) Close(w.Hwnd);
    }

    public static void ActivateApp(RunningApp app)
    {
        if (app.Windows.Count == 0) return;
        // EnumWindows returns Z-order, so the first window is the most recently used one.
        Win32.ActivateWindow(app.Windows[0].Hwnd);
    }

    public void Dispose()
    {
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
        _timer?.Stop();
    }
}
