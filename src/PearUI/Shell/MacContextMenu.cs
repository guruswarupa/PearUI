using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using PearUI.Core;
using PearUI.Native;

namespace PearUI.Shell;

/// <summary>
/// A Finder-style right-click menu for File Explorer and the desktop. A right-click on a file, folder or empty space
/// shows PearUI's own frosted menu (Open, Quick Look, Move to Trash, Get Info, New Folder...). Anything it does not
/// recognise (the navigation pane, column headers, the address bar) gets the normal Windows menu, and "Show More
/// Options" (or Shift + right-click) brings the full Windows menu with every app's extra items.
/// </summary>
public sealed class MacContextMenu : IDisposable
{
    public static MacContextMenu Instance { get; } = new();

    private const int WH_MOUSE_LL = 14, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Win32.POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public Win32.POINT pt; public uint mouseData, flags, time; public UIntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }

    /// <summary>For automated tests and screenshots only: PEARUI_TEST_INPUT=1 lets simulated clicks count as real ones.</summary>
    private static readonly bool TestInput = Environment.GetEnvironmentVariable("PEARUI_TEST_INPUT") == "1";

    private readonly HookProc _proc;
    private IntPtr _hook;
    private bool _swallowedDown;
    private static Win32.POINT _lastPt;
    private bool _busy;
    private GlassMenu _menu;

    private MacContextMenu() => _proc = Callback;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        Application.Current?.Dispatcher.BeginInvoke(() => _menu?.CloseMenu());
    }

    public void Dispose() => Stop();

    // ---------- the hook: keep it tiny ----------
    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_RBUTTONDOWN or WM_RBUTTONUP)
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool injected = (m.flags & 0x1) != 0 && !TestInput; // sent by us or another program: leave alone
                if (!injected)
                {
                    try
                    {
                        if (msg == WM_RBUTTONDOWN)
                        {
                            if (WantsClick(m.pt)) { _swallowedDown = true; return (IntPtr)1; }
                        }
                        else if (_swallowedDown)
                        {
                            _swallowedDown = false;
                            var pt = m.pt;
                            Handle(pt);
                            return (IntPtr)1;
                        }
                    }
                    catch { _swallowedDown = false; }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool WantsClick(Win32.POINT pt)
    {
        if (!SettingsStore.Current.MacContextMenu) return false;
        if (_busy) return false; // the previous click is still being worked out
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0) return false; // Shift / Ctrl: the Windows menu
        IntPtr hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;
        IntPtr root = Win32.GetAncestor(hwnd, 2 /*GA_ROOT*/);
        return Win32.GetClass(root) is "CabinetWClass" or "Progman" or "WorkerW";
    }

    // ---------- work out what was clicked ----------
    private enum Target { Item, Background, Other }

    private static void Log(string text)
    {
        try
        {
            string file = Path.Combine(SettingsStore.Folder, "contextmenu.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch { }
    }

    private void Handle(Win32.POINT pt)
    {
        _busy = true;
        _lastPt = pt;
        var t = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                IntPtr hwnd = WindowFromPoint(pt);
                IntPtr root = hwnd == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestor(hwnd, 2);
                string cls = Win32.GetClass(root);
                bool desktop = cls is "Progman" or "WorkerW";
                var (kind, element) = Classify(pt);
                Log($"click {pt.X},{pt.Y} root={cls} child={Win32.GetClass(hwnd)} kind={kind} ({clock.ElapsedMilliseconds} ms)");

                // The desktop has nothing but icons and empty space, so anything that is not an icon is the background.
                if (desktop && kind == Target.Other) kind = Target.Background;
                if (kind == Target.Other) { Log("  -> Windows menu (not a file or empty space)"); Replay(false); return; }

                if (kind == Target.Item)
                {
                    var plan = PlanItem(root, cls, element);
                    if (plan == null) { Log($"  -> Windows menu (could not find the file '{SafeName(element)}')"); Replay(false); return; }
                    Log($"  -> item menu for {plan.Paths.Count} item(s), first: {plan.Paths[0]} ({clock.ElapsedMilliseconds} ms)");
                    Application.Current?.Dispatcher.BeginInvoke(() => ShowMenu(pt, ItemMenu(root, plan)));
                }
                else
                {
                    string folder = desktop ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                                            : QuickLook.ExplorerFolder(root, null);
                    if (folder == null) { Log("  -> Windows menu (could not find the folder)"); Replay(false); return; }
                    Log($"  -> background menu for {folder} ({clock.ElapsedMilliseconds} ms)");
                    Application.Current?.Dispatcher.BeginInvoke(() => ShowMenu(pt, BackgroundMenu(root, folder, desktop)));
                }
            }
            catch (Exception ex) { Log("  -> Windows menu after an error: " + ex.Message); Replay(false); }
            finally { _busy = false; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    private static string SafeName(AutomationElement e) { try { return e.Current.Name; } catch { return "?"; } }

    private static (Target, AutomationElement) Classify(Win32.POINT pt)
    {
        AutomationElement el;
        try { el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y)); }
        catch { return (Target.Other, null); }

        var walker = TreeWalker.ControlViewWalker;
        var cur = el;
        for (int i = 0; i < 7 && cur != null; i++)
        {
            ControlType type;
            try { type = cur.Current.ControlType; } catch { return (Target.Other, null); }

            if (type == ControlType.ListItem || type == ControlType.DataItem) return (Target.Item, cur);
            // In File Explorer's Details view every cell of a row (Name, Date modified...) is a read-only "Edit" called UIProperty:
            // that is part of the file's row, so keep looking upwards. Any other edit box (search, address bar, rename) is left alone.
            if (type == ControlType.Edit)
            {
                string cls;
                try { cls = cur.Current.ClassName; } catch { return (Target.Other, null); }
                if (cls != "UIProperty") return (Target.Other, null);
            }
            else if (type == ControlType.HeaderItem || type == ControlType.Header || type == ControlType.ScrollBar || type == ControlType.Button ||
                type == ControlType.TreeItem || type == ControlType.Tree || type == ControlType.TabItem ||
                type == ControlType.MenuItem || type == ControlType.ToolBar || type == ControlType.ComboBox)
                return (Target.Other, null);
            if (type == ControlType.List || type == ControlType.DataGrid) return (Target.Background, cur);
            try { cur = walker.GetParent(cur); } catch { return (Target.Other, null); }
        }
        return (Target.Other, null);
    }

    private sealed record ItemPlan(List<string> Paths, string Name);

    private static ItemPlan PlanItem(IntPtr root, string cls, AutomationElement item)
    {
        string name = item.Current.Name;
        bool wasSelected = false;
        object pattern;
        if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
        {
            var sel = (SelectionItemPattern)pattern;
            wasSelected = sel.Current.IsSelected;
            if (!wasSelected) { try { sel.Select(); } catch { } } // like Finder: right-clicking selects the item
        }

        if (cls is "Progman" or "WorkerW")
        {
            string p = QuickLook.FindOnDesktop(name);
            return p == null ? null : new ItemPlan(new List<string> { p }, name);
        }

        if (wasSelected)
        {
            var many = QuickLook.ExplorerSelection(root);
            if (many.Count > 1 && many.Any(p => QuickLook.NameMatches(p, name))) return new ItemPlan(many, name);
        }
        string folder = QuickLook.ExplorerFolder(root, name);
        if (folder == null) return null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            if (QuickLook.NameMatches(entry, name)) return new ItemPlan(new List<string> { entry }, name);
        return null;
    }

    // ---------- the menus ----------
    private static GlassMenu.Entry Item(string header, Action action, string gesture = null, bool enabled = true) => new(header, gesture, action, enabled);

    private List<GlassMenu.Entry> ItemMenu(IntPtr window, ItemPlan plan)
    {
        var paths = plan.Paths;
        string first = paths[0];
        bool multiple = paths.Count > 1;
        bool isDir = Directory.Exists(first);
        string label = multiple ? $"{paths.Count} Items" : "“" + Path.GetFileName(first.TrimEnd('\\')) + "”";

        var list = new List<GlassMenu.Entry>
        {
            Item(multiple ? "Open" : isDir ? "Open Folder" : "Open", () => { foreach (var p in paths) Launch.Open(p); }),
        };
        if (!multiple)
        {
            if (isDir) list.Add(Item("Open in Terminal", () => Launch.Terminal(true, first)));
            if (!isDir) list.Add(Item("Open With…", () => Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + first) { UseShellExecute = false })));
            list.Add(Item("Quick Look " + label, () => QuickLook.Instance.ShowFor(first, window), "Space"));
        }
        list.Add(null);
        list.Add(Item(multiple ? $"Move {paths.Count} Items to Trash" : "Move to Trash", () => _ = FileOps.RecycleAsync(paths), "Del"));
        list.Add(null);
        if (!multiple)
        {
            list.Add(Item("Get Info", () => ShowProperties(first), "Alt+Enter"));
            list.Add(Item("Rename", () => Keys.SendTo(window, 0x71 /*F2*/), "F2"));
            list.Add(Item("Compress " + label, () => Compress(first)));
            if (!isDir) list.Add(Item("Duplicate", () => Duplicate(first)));
            list.Add(null);
        }
        list.Add(Item("Copy " + label, () => Keys.SendTo(window, Keys.Ctrl, (byte)'C'), "Ctrl+C"));
        list.Add(Item(multiple ? "Copy Paths" : "Copy as Path", () => System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, paths))));
        if (!multiple) list.Add(Item("Show in Folder", () => Launch.RevealInExplorer(first)));
        list.Add(null);
        list.Add(Item("Show More Options…", () => Replay(true), "Shift+Right-click"));
        return list;
    }

    private List<GlassMenu.Entry> BackgroundMenu(IntPtr window, string folder, bool desktop)
    {
        bool canPaste = false;
        try { canPaste = System.Windows.Clipboard.ContainsFileDropList(); } catch { }
        var list = new List<GlassMenu.Entry>
        {
            Item("New Folder", () => NewFolder(folder), "Ctrl+Shift+N"),
            Item("New Text Document", () => NewTextFile(folder)),
            null,
            Item("Paste", () => Keys.SendTo(window, Keys.Ctrl, (byte)'V'), "Ctrl+V", canPaste),
            null,
            Item("Get Info", () => ShowProperties(folder)),
            Item("Open in Terminal", () => Launch.Terminal(true, folder)),
        };
        if (desktop)
        {
            list.Add(null);
            list.Add(Item("Change Wallpaper…", () => Launch.Settings("personalization-background")));
            list.Add(Item("Display Settings…", () => Launch.Settings("display")));
        }
        else
        {
            list.Add(Item("Refresh", () => Keys.SendTo(window, 0x74 /*F5*/), "F5"));
        }
        list.Add(null);
        list.Add(Item("Show More Options…", () => Replay(true), "Shift+Right-click"));
        return list;
    }

    private void ShowMenu(Win32.POINT pt, List<GlassMenu.Entry> entries)
    {
        _menu?.CloseMenu();
        var menu = new GlassMenu(entries);
        var opened = Stopwatch.StartNew();
        menu.MenuClosed += () =>
        {
            Log($"  menu closed after {opened.ElapsedMilliseconds} ms");
            if (_menu == menu) _menu = null;
        };
        _menu = menu;
        menu.ShowAt(pt.X, pt.Y);
        Log("  menu shown");
    }

    // ---------- actions ----------
    private static void ShowProperties(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { Verb = "properties", UseShellExecute = true }); } catch { }
    }

    private static string Unique(string dir, string name, string ext, string suffix)
    {
        string candidate = Path.Combine(dir, name + ext);
        for (int i = 1; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, i == 1 ? name + suffix + ext : $"{name}{suffix} {i}{ext}");
        return candidate;
    }

    private static void NewFolder(string dir)
    {
        try { Directory.CreateDirectory(Unique(dir, "untitled folder", "", "")); } catch { }
    }

    private static void NewTextFile(string dir)
    {
        try { File.WriteAllBytes(Unique(dir, "New Text Document", ".txt", ""), Array.Empty<byte>()); } catch { }
    }

    private static void Duplicate(string path)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            File.Copy(path, Unique(dir, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path), " copy"));
        }
        catch { }
    }

    private static void Compress(string path)
    {
        try
        {
            string dir = Path.GetDirectoryName(path.TrimEnd('\\'));
            string name = Path.GetFileName(path.TrimEnd('\\'));
            string zip = Unique(dir, Path.GetFileNameWithoutExtension(name) is { Length: > 0 } n && File.Exists(path) ? n : name, ".zip", "");
            var psi = new ProcessStartInfo("tar.exe") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in new[] { "-a", "-c", "-f", zip, name }) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { }
    }

    // ---------- hand the click back to Windows ----------
    /// <summary>Sends the right-click again so Windows shows its own menu (Shift makes it the full classic menu).</summary>
    private static void Replay(bool classic)
    {
        SetCursorPos(_lastPt.X, _lastPt.Y); // the click belongs where it was made, not where the menu item was
        var inputs = new List<INPUT>();
        INPUT Key(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { vk = vk, flags = up ? 2u : 0u } } };
        INPUT Mouse(uint flag) => new() { type = 0, u = new InputUnion { mi = new MOUSEINPUT { flags = flag } } };
        if (classic) inputs.Add(Key(0x10, false));
        inputs.Add(Mouse(0x0008)); // right down
        inputs.Add(Mouse(0x0010)); // right up
        if (classic) inputs.Add(Key(0x10, true));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
