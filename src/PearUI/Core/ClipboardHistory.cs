using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PearUI.Core;

public sealed class ClipEntry
{
    public string Text { get; set; } = "";
    public bool Pinned { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>
/// Remembers text copied to the clipboard (like Maccy) until the user clears it. Entries can be pinned (kept
/// through "Clear History") or deleted one by one. History is saved to disk encrypted for the current Windows
/// user (DPAPI), and anything a password manager marks as "exclude from clipboard history" is never recorded.
/// </summary>
public sealed class ClipboardHistory
{
    public static ClipboardHistory Instance { get; } = new();

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxUnpinned = 5000; // safety net only; history otherwise stays until the user clears it
    private const int MaxLength = 200_000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly List<ClipEntry> _items = new(); // newest first
    private HwndSource _source;
    private string _ignore; // text we put on the clipboard ourselves
    private bool _loaded;
    private DispatcherTimer _saveTimer;

    private static string FilePath => Path.Combine(SettingsStore.Folder, "clipboard.dat");

    /// <summary>Pinned entries first, then the rest, newest first.</summary>
    public IReadOnlyList<ClipEntry> Items
    {
        get
        {
            EnsureLoaded();
            return _items.Where(i => i.Pinned).Concat(_items.Where(i => !i.Pinned)).ToList();
        }
    }

    public event Action Changed;

    public void Start()
    {
        EnsureLoaded();
        if (_source != null) return;
        _source = new HwndSource(new HwndSourceParameters("PearUIClipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    public void Stop()
    {
        SaveNow();
        if (_source == null) return;
        RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            if (SettingsStore.Current.ClipboardHistory)
                Application.Current?.Dispatcher.BeginInvoke(() => _ = CaptureAsync());
        }
        return IntPtr.Zero;
    }

    private async Task CaptureAsync()
    {
        if (!SettingsStore.Current.ClipboardHistory) return;
        for (int attempt = 0; attempt < 5; attempt++) // the clipboard is often locked by the app that just copied
        {
            try
            {
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (!Clipboard.ContainsText()) return;
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return;
                if (text == _ignore) return;
                Add(text);
                return;
            }
            catch { await Task.Delay(60); }
        }
    }

    private void Add(string text)
    {
        EnsureLoaded();
        bool pinned = false;
        var existing = _items.FirstOrDefault(i => i.Text == text);
        if (existing != null) { pinned = existing.Pinned; _items.Remove(existing); }
        _items.Insert(0, new ClipEntry { Text = text, Pinned = pinned, Time = DateTime.Now });
        TrimAndSave();
    }

    /// <summary>Puts an entry back on the clipboard and moves it to the top of the list.</summary>
    public bool Restore(ClipEntry entry)
    {
        _ignore = entry.Text;
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(entry.Text, true); break; }
            catch { System.Threading.Thread.Sleep(30); if (i == 4) return false; }
        }
        if (_items.Remove(entry)) _items.Insert(0, entry);
        entry.Time = DateTime.Now;
        TrimAndSave();
        return true;
    }

    public void TogglePin(ClipEntry entry)
    {
        entry.Pinned = !entry.Pinned;
        TrimAndSave();
    }

    public void Remove(ClipEntry entry)
    {
        if (_items.Remove(entry)) TrimAndSave();
    }

    /// <summary>Clears everything except pinned entries.</summary>
    public void Clear()
    {
        EnsureLoaded();
        _items.RemoveAll(i => !i.Pinned);
        TrimAndSave();
    }

    private void TrimAndSave()
    {
        int unpinned = 0;
        for (int i = 0; i < _items.Count; i++)
            if (!_items[i].Pinned && ++unpinned > MaxUnpinned) { _items.RemoveAt(i); i--; }
        Changed?.Invoke();
        ScheduleSave();
    }

    // ---------- persistence (encrypted for this Windows user) ----------
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            var list = JsonSerializer.Deserialize<List<ClipEntry>>(Encoding.UTF8.GetString(plain));
            if (list != null) _items.AddRange(list.Where(e => !string.IsNullOrEmpty(e.Text)));
        }
        catch { }
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTick(object sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveNow();
    }

    public void SaveNow()
    {
        if (!_loaded) return;
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            var json = JsonSerializer.Serialize(_items);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
        }
        catch { }
    }
}
