using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using PearUI.Core;
using PearUI.Native;

namespace PearUI.Shell;

/// <summary>
/// Quick Look: press Space on a selected file in File Explorer (or on the desktop) to preview it without opening it.
/// Press Space or Esc again to close. Text boxes (search, rename, address bar) are never affected: the preview only
/// opens when a file in the list has the keyboard focus.
/// </summary>
public sealed class QuickLook : IDisposable
{
    public static QuickLook Instance { get; } = new();

    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, VK_SPACE = 0x20;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }

    private readonly HookProc _proc;
    private IntPtr _hook;
    private bool _spaceDown;
    private bool _busy;
    private QuickLookWindow _window;

    private QuickLook() => _proc = Callback;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        Application.Current?.Dispatcher.BeginInvoke(() => _window?.Close());
    }

    public void Dispose() => Stop();

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode == VK_SPACE && (k.flags & 0x10) == 0) // a real key press, not one we or another app sent
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYUP) _spaceDown = false;
                else if (msg == WM_KEYDOWN && !_spaceDown)
                {
                    _spaceDown = true; // ignore auto-repeat while the key is held
                    try { OnSpace(); } catch { }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Runs inside the keyboard hook: do only a very cheap test here, the rest happens on another thread.</summary>
    private void OnSpace()
    {
        if (!SettingsStore.Current.QuickLook || _busy) return;
        if ((GetAsyncKeyState(0x11) | GetAsyncKeyState(0x10) | GetAsyncKeyState(0x12) | GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) < 0) return;

        IntPtr fg = Win32.GetForegroundWindow();
        string cls = Win32.GetClass(fg);
        if (cls is not ("CabinetWClass" or "Progman" or "WorkerW")) return;

        _busy = true;
        var t = new Thread(() =>
        {
            try
            {
                string path = ResolveSelection(fg, cls);
                if (path != null)
                    Application.Current?.Dispatcher.BeginInvoke(() => Show(path, fg));
            }
            catch { }
            finally { _busy = false; }
        });
        t.SetApartmentState(ApartmentState.STA); // the Shell COM objects want an STA thread
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>Opens the preview for a file chosen another way (for example from the right-click menu).</summary>
    internal void ShowFor(string path, IntPtr returnTo) => Show(path, returnTo);

    private void Show(string path, IntPtr returnTo)
    {
        try { _window?.Close(); } catch { }
        _window = new QuickLookWindow(path, returnTo);
        _window.Closed += (_, _) => _window = null;
        _window.ShowPreview();
    }

    // ---------- which file is selected? ----------
    /// <summary>The file that has the keyboard focus in an Explorer window or on the desktop, or null when focus is elsewhere.</summary>
    private static string ResolveSelection(IntPtr fg, string cls)
    {
        // Only react when a list item is focused (not the search box, the address bar or a rename box).
        string focusedName;
        try
        {
            var el = AutomationElement.FocusedElement;
            var type = el?.Current.ControlType;
            if (type != ControlType.ListItem && type != ControlType.DataItem) return null;
            focusedName = el.Current.Name;
        }
        catch { return null; }

        if (cls is "Progman" or "WorkerW") return FindOnDesktop(focusedName);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
        string fallback = null;
        foreach (dynamic w in shell.Windows())
        {
            try
            {
                if ((long)w.HWND != fg.ToInt64()) continue;
                dynamic selected = w.Document.SelectedItems();
                int count = selected.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = selected.Item(i);
                    string path = item.Path;
                    fallback ??= path;
                    if (NameMatches(path, focusedName)) return path;
                }
            }
            catch { }
        }
        return fallback;
    }

    /// <summary>Every selected item of an Explorer window (empty when none).</summary>
    internal static List<string> ExplorerSelection(IntPtr window)
    {
        var result = new List<string>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if ((long)w.HWND != window.ToInt64()) continue;
                    dynamic selected = w.Document.SelectedItems();
                    int count = selected.Count;
                    for (int i = 0; i < count; i++) result.Add((string)selected.Item(i).Path);
                    if (result.Count > 0) return result;
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    /// <summary>The folder an Explorer window shows. With several tabs, the one that contains an item called <paramref name="itemName"/>.</summary>
    internal static string ExplorerFolder(IntPtr window, string itemName)
    {
        string first = null;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if ((long)w.HWND != window.ToInt64()) continue;
                    string folder = w.Document.Folder.Self.Path;
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                    first ??= folder;
                    if (itemName == null) return folder;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                        if (NameMatches(entry, itemName)) return folder;
                }
                catch { }
            }
        }
        catch { }
        return first;
    }

    internal static string FindOnDesktop(string name)
    {
        foreach (var dir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        })
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    if (NameMatches(entry, name)) return entry;
            }
            catch { }
        }
        return null;
    }

    internal static bool NameMatches(string path, string shown)
    {
        if (string.IsNullOrEmpty(shown)) return false;
        string file = Path.GetFileName(path.TrimEnd('\\'));
        return string.Equals(file, shown, StringComparison.CurrentCultureIgnoreCase) ||
               string.Equals(Path.GetFileNameWithoutExtension(file), shown, StringComparison.CurrentCultureIgnoreCase);
    }
}
