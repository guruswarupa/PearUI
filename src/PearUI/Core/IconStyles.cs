using System.Windows;
using System.Windows.Controls;
using PearUI.Native;

namespace PearUI.Core;

/// <summary>
/// How app icons look. One style for every app (Settings), a different style for a single app (its right-click menu → Icon),
/// and icons picked by hand from macOSicons.com, which always win.
/// </summary>
internal static class IconStyles
{
    /// <summary>Sources of the Dock's own items (the Recycle Bin, Downloads and All Apps), so they follow the icon style too.</summary>
    public const string BinSource = "::{645FF040-5081-101B-9F08-00AA002F954E}", BinFullKey = "special:trash-full", AllAppsKey = "special:allapps";
    public static readonly string DownloadsSource = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public const string Original = "Original", BigSur = "BigSur", WhiteSur = "WhiteSur", Mixed = "Mixed";

    private static string Clean(string s) => s is Original or BigSur or WhiteSur or Mixed ? s : null;

    public static string Global => Clean(SettingsStore.Current.IconStyle) is Mixed ? WhiteSur : Clean(SettingsStore.Current.IconStyle) ?? Original;

    /// <summary>
    /// One key for an app however it is listed: the Dock may know it by its program file and All Apps by its Start menu entry,
    /// so both are turned into the program file when it is known. A choice made in one place then shows in the other.
    /// </summary>
    public static string Key(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        string s = source;
        const string pre = @"shell:AppsFolder\";
        if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
        {
            var e = AppCatalog.Instance.Apps.FirstOrDefault(a => a.Target.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(e?.ExePath)) s = e.ExePath;
            else if (SystemApps.TryGetValue(s.Substring(pre.Length), out var exe)) s = exe;
        }
        try { if (s.Contains('\\') && !s.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("::")) s = System.IO.Path.GetFullPath(s); } catch { }
        return s.ToLowerInvariant();
    }

    // Windows apps listed by name only in All Apps, but known by their program file in the Dock.
    private static readonly Dictionary<string, string> SystemApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Windows.Explorer"] = @"C:\Windows\explorer.exe",
        ["windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"] = @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe",
    };

    /// <summary>Moves choices saved under an older kind of key to the current one (once the app list is known), so they apply everywhere.</summary>
    public static void Rekey()
    {
        var s = SettingsStore.Current;
        bool changed = false;
        Dictionary<string, T> Fix<T>(Dictionary<string, T> d)
        {
            if (d == null) return d;
            var n = new Dictionary<string, T>();
            foreach (var kv in d)
            {
                string k = kv.Key.StartsWith("special:") ? kv.Key : Key(kv.Key);
                if (k != kv.Key) changed = true;
                n[k] = kv.Value;
            }
            return n;
        }
        s.IconStyleOverrides = Fix(s.IconStyleOverrides);
        s.MacIconOverrides = Fix(s.MacIconOverrides);
        if (changed) SettingsStore.Save(raiseEvent: false);
        WhiteSurIcons.Rekey();
    }

    /// <summary>Looks an app up in a table keyed by <see cref="Key"/>, also finding entries saved under the plain source by earlier versions.</summary>
    public static bool TryGet<T>(Dictionary<string, T> table, string source, out T value)
    {
        value = default;
        if (table == null || string.IsNullOrEmpty(source)) return false;
        return table.TryGetValue(Key(source), out value) || table.TryGetValue(source, out value);
    }

    public static string StyleFor(string source)
    {
        if (TryGet(SettingsStore.Current.IconStyleOverrides, source, out var v) && Clean(v) != null) return v;
        return Global;
    }

    /// <summary>Gives one app a style (null = follow the style in Settings). A hand-picked icon is dropped.</summary>
    public static void SetFor(string source, string style)
    {
        if (string.IsNullOrEmpty(source)) return;
        var s = SettingsStore.Current;
        s.IconStyleOverrides ??= new();
        s.MacIconOverrides ??= new();
        string key = Key(source);
        s.MacIconOverrides.Remove(key); s.MacIconOverrides.Remove(source);
        s.IconStyleOverrides.Remove(source);
        if (Clean(style) == null) s.IconStyleOverrides.Remove(key); else s.IconStyleOverrides[key] = style;
        ShellIcons.InvalidateAll();
        SettingsStore.Save(raiseEvent: false);
        MacIcons.RaiseChanged();
    }

    /// <summary>Adds the icon choices to an app's right-click menu in the Dock and in All Apps (flat, so the menu's own click handling works).</summary>
    public static void AddMenuItems(ItemCollection root, string name, string exe, string source)
    {
        var s = SettingsStore.Current;
        bool picked = MacIcons.OverrideFor(source) != null;
        string own = TryGet(s.IconStyleOverrides, source, out var v) && Clean(v) != null ? v : null;

        root.Add(new Separator());
        root.Add(new MenuItem { Header = "Icon", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        MenuItem Item(string header, bool on, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsChecked = on, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            root.Add(mi);
            return mi;
        }
        Item("Default (as in Settings)", !picked && own == null, () => SetFor(source, null));
        root.Add(new Separator());
        Item("App's Own Icon", !picked && own == Original, () => SetFor(source, Original));
        Item("Big Sur Tile", !picked && own == BigSur, () => SetFor(source, BigSur));
        Item("WhiteSur Icon (Big Sur tile if it has none)", !picked && own is WhiteSur or Mixed, async () =>
        {
            await WhiteSurIcons.EnsureOneAsync(name, exe, source);
            SetFor(source, WhiteSur);
        });
        root.Add(new Separator());
        Item(MacIcons.Ready ? "macOS Icon…" : "macOS Icon… (add a key in Settings → Dock)", picked,
            () => PearUI.Settings.MacIconPickerWindow.ShowFor(name, source, null), MacIcons.Ready);
    }
}
