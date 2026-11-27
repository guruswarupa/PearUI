using System.Windows.Input;
using PearUI.Core;

namespace PearUI.Shell;

/// <summary>One user-defined shortcut: a key combination that runs an action, like a key binding in a Linux window manager.</summary>
public sealed class ShortcutBinding
{
    /// <summary>For example "Win+Alt+Left" or "Ctrl+Shift+F1".</summary>
    public string Keys { get; set; } = "";
    /// <summary>The id of an action from <see cref="ShortcutActions"/>.</summary>
    public string Action { get; set; } = "";
    /// <summary>The program, command or address for the actions that need one.</summary>
    public string Arg { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

[Flags]
public enum KeyMods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>The table of active shortcuts, looked up by the keyboard hook, plus reading and writing key names.</summary>
public static class Shortcuts
{
    private static Dictionary<(KeyMods, int), ShortcutBinding> _map = new();
    private static HashSet<int> _keys = new();
    private static Dictionary<string, int> _byName;

    public static List<ShortcutBinding> Defaults() => new()
    {
        new() { Keys = "Win+S", Action = "search" },
        new() { Keys = "Win+T", Action = "terminal" },
        new() { Keys = "Win+Shift+V", Action = "clipboard" },
    };

    /// <summary>Rebuilds the lookup table from the settings. Called at start-up and whenever settings change.</summary>
    public static void Reload()
    {
        var map = new Dictionary<(KeyMods, int), ShortcutBinding>();
        foreach (var b in SettingsStore.Current.Shortcuts ?? new())
        {
            if (!b.Enabled || !TryParse(b.Keys, out var mods, out int vk) || ShortcutActions.Find(b.Action) == null) continue;
            map[(mods, vk)] = b;
        }
        // Built-in number keys (the user's own shortcuts on the same keys win):
        //   Win+1 … Win+0        switch to virtual desktop 1 … 10
        //   Win+Shift+1 … 0      move the window in front to that desktop
        //   Alt+1 … Alt+0        switch to window 1 … 10 on the current desktop
        for (int i = 1; i <= WindowSlots.Count; i++)
        {
            int vk = 0x30 + i % 10;                    // '1' … '9', then '0' for number 10
            void Add(KeyMods mods, string prefix, string action)
            {
                if (!map.ContainsKey((mods, vk))) map[(mods, vk)] = new ShortcutBinding { Keys = prefix + (i % 10), Action = action + i };
            }
            if (SettingsStore.Current.VirtualDesktopShortcuts)
            {
                Add(KeyMods.Win, "Win+", "desktop-");
                Add(KeyMods.Win | KeyMods.Shift, "Win+Shift+", "movedesktop-");
            }
            if (SettingsStore.Current.WindowNumberShortcuts) Add(KeyMods.Alt, "Alt+", "slot-");
        }
        _keys = map.Keys.Select(k => k.Item2).ToHashSet();
        _map = map;
    }

    /// <summary>Quick test used on every key press: is this key part of any shortcut?</summary>
    public static bool HasKey(int vk) => _keys.Contains(vk);

    public static bool TryGet(KeyMods mods, int vk, out ShortcutBinding binding) => _map.TryGetValue((mods, vk), out binding);

    // ---------- key names ----------
    public static string KeyName(int vk)
    {
        var k = KeyInterop.KeyFromVirtualKey(vk);
        return k switch
        {
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Oem1 => ";",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemMinus => "-",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.Oem3 => "`",
            Key.OemOpenBrackets => "[",
            Key.Oem5 => "\\",
            Key.OemCloseBrackets => "]",
            Key.OemQuotes => "'",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
            _ => k.ToString(),
        };
    }

    public static string Format(KeyMods mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & KeyMods.Win) != 0) parts.Add("Win");
        if ((mods & KeyMods.Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & KeyMods.Alt) != 0) parts.Add("Alt");
        if ((mods & KeyMods.Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    public static bool TryParse(string spec, out KeyMods mods, out int vk)
    {
        mods = KeyMods.None; vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        _byName ??= BuildNames();
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win": case "super": case "meta": mods |= KeyMods.Win; break;
                case "ctrl": case "control": mods |= KeyMods.Ctrl; break;
                case "alt": mods |= KeyMods.Alt; break;
                case "shift": mods |= KeyMods.Shift; break;
                default:
                    if (vk != 0 || !_byName.TryGetValue(raw, out vk)) return false;
                    break;
            }
        }
        return vk != 0;
    }

    private static Dictionary<string, int> BuildNames()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int vk = 8; vk < 255; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) continue;   // modifiers
            if (KeyInterop.KeyFromVirtualKey(vk) == Key.None) continue;
            d.TryAdd(KeyName(vk), vk);
        }
        return d;
    }

    /// <summary>A shortcut needs a modifier (or must be a function key), so ordinary typing never triggers it.</summary>
    public static bool IsSafe(string spec) =>
        TryParse(spec, out var mods, out int vk) && (mods != KeyMods.None || vk is >= 0x70 and <= 0x87);
}
