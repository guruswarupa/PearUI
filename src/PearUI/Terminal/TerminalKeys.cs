using PearUI.Core;
using PearUI.Shell;

namespace PearUI.Terminal;

/// <summary>The keys used inside PearUI Terminal (new tab, copy, zoom…). Each one has a default and can be changed in Settings → Shortcuts.</summary>
internal static class TerminalKeys
{
    public sealed record Def(string Id, string Title, string Default);

    public static readonly IReadOnlyList<Def> All = new List<Def>
    {
        new("new-tab", "New tab", "Ctrl+Shift+T"),
        new("new-powershell-tab", "New PowerShell tab", "Ctrl+Shift+P"),
        new("shell-menu", "Choose a shell for a new tab", "Ctrl+Shift+N"),
        new("close-tab", "Close tab", "Ctrl+Shift+W"),
        new("next-tab", "Next tab", "Ctrl+Tab"),
        new("previous-tab", "Previous tab", "Ctrl+Shift+Tab"),
        new("history", "Command history", "Ctrl+Shift+H"),
        new("clear", "Clear the screen", "Ctrl+K"),
        new("copy", "Copy", "Ctrl+Shift+C"),
        new("paste", "Paste", "Ctrl+V"),
        new("zoom-in", "Zoom in", "Ctrl+="),
        new("zoom-out", "Zoom out", "Ctrl+-"),
        new("zoom-reset", "Reset zoom", "Ctrl+0"),
    };

    public static Def Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>The keys currently assigned to an action: the user's choice, otherwise the default.</summary>
    public static string Get(string id)
    {
        var saved = SettingsStore.Current.TerminalKeys;
        if (saved != null && saved.TryGetValue(id, out var keys) && !string.IsNullOrWhiteSpace(keys)) return keys;
        return Find(id)?.Default ?? "";
    }

    public static void Set(string id, string keys)
    {
        var s = SettingsStore.Current;
        s.TerminalKeys ??= new();
        if (keys == Find(id)?.Default) s.TerminalKeys.Remove(id); else s.TerminalKeys[id] = keys;
    }

    public static void ResetAll() => SettingsStore.Current.TerminalKeys = new();

    /// <summary>The action that a key combination such as "Ctrl+Shift+T" triggers, or null.</summary>
    public static string ActionFor(string spec)
    {
        foreach (var d in All)
            if (string.Equals(Normalize(Get(d.Id)), spec, StringComparison.OrdinalIgnoreCase)) return d.Id;
        return null;
    }

    /// <summary>Another terminal action that already uses these keys, or null.</summary>
    public static Def Conflict(string id, string keys)
    {
        string n = Normalize(keys);
        return All.FirstOrDefault(d => d.Id != id && string.Equals(Normalize(Get(d.Id)), n, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string spec) =>
        Shortcuts.TryParse(spec, out var mods, out int vk) ? Shortcuts.Format(mods, vk) : spec ?? "";
}
