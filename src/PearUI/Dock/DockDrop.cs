using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using PearUI.Native;

namespace PearUI.Dock;

/// <summary>
/// Drag and drop onto the Dock, macOS-style:
///  • Recycle Bin – move the files to the Recycle Bin (restorable);
///  • Downloads – move the files into the Downloads folder;
///  • an app – open the files with that app.
/// Also hosts the Downloads stack popup.
/// </summary>
public partial class DockWindow
{
    private Item _dropHot;
    private DownloadsStack _stack;

    private Item ItemAtX(double x) =>
        _items.FirstOrDefault(i => i.Kind != Kind.Separator && x >= i.X - Gap / 2 && x < i.X + i.Size + Gap / 2);

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var files = DroppedFiles(e);
        var pos = e.GetPosition(Surface);

        // Magnify under the pointer while dragging, like when hovering.
        _mouseX = pos.X;
        _lastInside = DateTime.Now;
        if (!_mouseInside)
        {
            _mouseInside = true;
            HoverZone.Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }
        StartRendering();

        var it = files == null ? null : ItemAtX(pos.X);
        if (it != null)
        {
            switch (it.Kind)
            {
                case Kind.Trash:
                case Kind.Downloads:
                    e.Effects = DragDropEffects.Move;
                    break;
                case Kind.App when AppExe(it) != null:
                    e.Effects = DragDropEffects.Copy;
                    break;
                default:
                    it = null;
                    break;
            }
        }
        SetDropHot(it);
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        SetDropHot(null);
        _mouseInside = false;
        HoverZone.Fill = null;
        StartRendering(0.5);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = _dropHot;
        SetDropHot(null);
        _mouseInside = false;
        HoverZone.Fill = null;
        StartRendering(0.5);

        var files = DroppedFiles(e);
        if (target == null || files == null || files.Length == 0) return;

        switch (target.Kind)
        {
            case Kind.Trash:
                await FileOps.RecycleAsync(files);
                _trashCount = -1;
                SlowTick();
                break;
            case Kind.Downloads:
                var outside = files.Where(f => !string.Equals(Path.GetDirectoryName(f)?.TrimEnd('\\'), DownloadsPath.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                if (outside.Length > 0) await FileOps.MoveAsync(outside, DownloadsPath);
                Bounce(target);
                break;
            case Kind.App:
                string exe = AppExe(target);
                if (exe == null) return;
                try
                {
                    Process.Start(new ProcessStartInfo(exe, string.Join(" ", files.Select(f => "\"" + f + "\"")))
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(exe),
                    });
                    Bounce(target);
                }
                catch { }
                break;
        }
    }

    /// <summary>An exe that can be started with file arguments (Store apps can't).</summary>
    private static string AppExe(Item it)
    {
        string exe = it.Running?.ExePath ?? it.Pin?.ExePath;
        if (exe == null && it.Pin?.Target?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true) exe = it.Pin.Target;
        if (exe == null || !File.Exists(exe) || exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return null;
        return exe;
    }

    /// <summary>Highlights the item a drop would go to.</summary>
    private void SetDropHot(Item it)
    {
        if (_dropHot == it) return;
        if (_dropHot?.View?.Image != null) _dropHot.View.Image.Opacity = 1;
        _dropHot = it;
        if (it?.View?.Image != null) it.View.Image.Opacity = 0.6;
        UpdateTooltipFor(it);
    }

    private void UpdateTooltipFor(Item it)
    {
        if (it == null) return;
        NameTipText.Text = it.Kind == Kind.Trash ? "Move to Recycle Bin" : it.Kind == Kind.Downloads ? "Move to Downloads" : "Open with " + it.Name;
    }

    // ===================== Downloads stack =====================
    private void ToggleDownloadsStack(Item it)
    {
        if (_stack != null) { _stack.CloseStack(); return; }
        _stack = new DownloadsStack(DownloadsPath);
        _menuOpen = true; // keep an auto-hiding Dock visible while the stack is open
        _stack.StackClosed += () =>
        {
            _stack = null;
            _menuOpen = false;
            _lastInside = DateTime.Now;
        };
        var top = it.View.Root.PointToScreen(new Point(it.Size / 2, 0));
        _stack.ShowAbove((int)top.X, (int)top.Y);
    }
}
