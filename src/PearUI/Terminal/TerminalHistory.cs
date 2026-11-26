using System.IO;
using System.Text.Json;

namespace PearUI.Terminal;

/// <summary>Commands typed in PearUI Terminal, kept between sessions in %APPDATA%\PearUI\terminal-history.json.</summary>
internal static class TerminalHistory
{
    public sealed record Entry(string Text, DateTime Time);

    private const int Max = 2000;
    private static readonly object Gate = new();
    private static List<Entry> _items;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PearUI", "terminal-history.json");

    private static List<Entry> Items
    {
        get
        {
            if (_items != null) return _items;
            try { _items = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { _items = new(); }
            return _items;
        }
    }

    /// <summary>Newest first.</summary>
    public static List<Entry> All()
    {
        lock (Gate) { var l = new List<Entry>(Items); l.Reverse(); return l; }
    }

    public static void Add(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 2000) return;
        lock (Gate)
        {
            var items = Items;
            if (items.Count > 0 && items[^1].Text == text) return; // same command twice in a row
            items.Add(new Entry(text, DateTime.Now));
            if (items.Count > Max) items.RemoveRange(0, items.Count - Max);
            Save();
        }
    }

    public static void Clear()
    {
        lock (Gate) { Items.Clear(); Save(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items));
        }
        catch { }
    }
}
