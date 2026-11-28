using Microsoft.Win32;

namespace PearUI.Core;

/// <summary>
/// Optional, reversible File Explorer settings that make it feel more like Finder: a tidier sidebar, hidden file
/// extensions, no recent-file clutter. Everything is a per-user registry value; unticking puts Windows' default back.
/// New Explorer windows pick the change up.
/// </summary>
public static class ExplorerTweaks
{
    public sealed record Reg(string Path, string Name, object Apply, object Undo); // Undo null = delete the value
    public sealed record Tweak(string Title, string Description, Reg[] Entries);

    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    private static Reg Pinned(string clsid) => new(@"Software\Classes\CLSID\" + clsid, "System.IsPinnedToNameSpaceTree", 0, null);

    public static readonly IReadOnlyList<Tweak> All = new List<Tweak>
    {
        new("Clean sidebar: hide Gallery and OneDrive",
            "Takes Gallery and the OneDrive entry out of File Explorer's left sidebar, leaving Home, Desktop, Downloads, Documents, This PC and Network, much like Finder's sidebar.",
            new[] { Pinned("{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}"), Pinned("{018D5C66-4533-4307-9B53-224DE2ED1FE6}") }),
        new("Hide file extensions",
            "Shows \"Report\" instead of \"Report.docx\", as Finder does by default.",
            new[] { new Reg(Advanced, "HideFileExt", 1, 0) }),
        new("Don't show recent files and frequent folders",
            "Keeps Home and Quick access free of recent-file and frequent-folder clutter.",
            new[] { new Reg(ExplorerKey, "ShowRecent", 0, 1), new Reg(ExplorerKey, "ShowFrequent", 0, 1) }),
        new("Open File Explorer to This PC",
            "New File Explorer windows start at This PC (your drives and folders) instead of Home.",
            new[] { new Reg(Advanced, "LaunchTo", 1, 2) }),
    };

    private static bool Matches(object actual, object wanted) => actual != null && Convert.ToInt64(actual) == Convert.ToInt64(wanted);

    public static bool IsApplied(Tweak t)
    {
        try
        {
            foreach (var e in t.Entries)
            {
                using var k = Registry.CurrentUser.OpenSubKey(e.Path);
                if (!Matches(k?.GetValue(e.Name), e.Apply)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public static void Set(Tweak t, bool on)
    {
        foreach (var e in t.Entries)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(e.Path);
                object value = on ? e.Apply : e.Undo;
                if (value == null) k.DeleteValue(e.Name, false);
                else k.SetValue(e.Name, value, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
