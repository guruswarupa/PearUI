using System.Windows.Threading;
using MacUI.Core;
using MacUI.Native;
using Forms = System.Windows.Forms;

namespace MacUI.Shell;

/// <summary>
/// macOS Hot Corners: push the pointer into a screen corner to trigger an action.
/// A short dwell avoids accidental triggers; the corner re-arms once the pointer leaves it.
/// </summary>
public sealed class HotCorners : IDisposable
{
    private const int CornerPx = 2;     // how close to the corner counts
    private const int RearmPx = 40;     // must move this far away before triggering again
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(180);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private string _current;           // corner the pointer is in
    private DateTime _enteredAt;
    private bool _fired;

    public HotCorners()
    {
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var s = SettingsStore.Current;
        if (s.HotCornerTopLeft == "None" && s.HotCornerTopRight == "None" &&
            s.HotCornerBottomLeft == "None" && s.HotCornerBottomRight == "None") return;
        if (!Win32.GetCursorPos(out var p)) return;
        if ((Forms.Control.MouseButtons & (Forms.MouseButtons.Left | Forms.MouseButtons.Right)) != 0) return; // dragging

        var b = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).Bounds;
        string corner = null;
        bool left = p.X <= b.Left + CornerPx, right = p.X >= b.Right - 1 - CornerPx;
        bool top = p.Y <= b.Top + CornerPx, bottom = p.Y >= b.Bottom - 1 - CornerPx;
        if (top && left) corner = "TL";
        else if (top && right) corner = "TR";
        else if (bottom && left) corner = "BL";
        else if (bottom && right) corner = "BR";

        if (corner == null)
        {
            if (_current != null)
            {
                // Re-arm only after moving clearly away from the corner.
                bool far = Math.Min(Math.Abs(p.X - b.Left), Math.Abs(p.X - b.Right)) > RearmPx ||
                           Math.Min(Math.Abs(p.Y - b.Top), Math.Abs(p.Y - b.Bottom)) > RearmPx;
                if (far) { _current = null; _fired = false; }
            }
            return;
        }

        if (corner != _current)
        {
            _current = corner;
            _enteredAt = DateTime.Now;
            _fired = false;
            return;
        }

        if (!_fired && DateTime.Now - _enteredAt >= Dwell)
        {
            _fired = true;
            string action = corner switch
            {
                "TL" => s.HotCornerTopLeft,
                "TR" => s.HotCornerTopRight,
                "BL" => s.HotCornerBottomLeft,
                _ => s.HotCornerBottomRight,
            };
            if (!string.IsNullOrEmpty(action) && action != "None" && !FullScreenGuard.IsFullScreenAppActive()) Features.Run(action); // never over a game or a movie
        }
    }

    public void Dispose() => _timer.Stop();
}
