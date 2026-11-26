using System.Windows.Interop;

namespace PearUI.Native;

/// <summary>System-wide hotkeys, backed by a hidden message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0x4D41; // 'MA'

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("PearUI.Hotkeys") { ParentWindow = new IntPtr(-3) /*HWND_MESSAGE*/, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Parses strings like "Alt+Space", "Ctrl+Alt+K". Returns false if the combo is taken.</summary>
    public bool Register(string combo, Action handler, out int id)
    {
        id = 0;
        if (!TryParse(combo, out uint mods, out uint vk)) return false;
        id = _nextId++;
        if (!Win32.RegisterHotKey(_source.Handle, id, mods | Win32.MOD_NOREPEAT, vk)) return false;
        _handlers[id] = handler;
        return true;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id)) Win32.UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys.ToList()) Unregister(id);
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= Win32.MOD_ALT; break;
                case "ctrl": case "control": mods |= Win32.MOD_CONTROL; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "win": mods |= Win32.MOD_WIN; break;
                case "space": vk = Win32.VK_SPACE; break;
                default:
                    if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) vk = char.ToUpperInvariant(raw[0]);
                    else if (Enum.TryParse<System.Windows.Forms.Keys>(raw, true, out var k)) vk = (uint)k;
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var h))
        {
            handled = true;
            h();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
