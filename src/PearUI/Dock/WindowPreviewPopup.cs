using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PearUI.Core;
using PearUI.Native;
using PearUI.Shell;

namespace PearUI.Dock;

/// <summary>
/// The hover preview above a Dock icon (like the Windows taskbar): a live DWM thumbnail and the title
/// of every window of the app. Click a card to bring that window forward; the × closes it.
/// </summary>
internal sealed class WindowPreviewPopup : OverlayWindow
{
    private const double CardW = 230, ThumbH = 140;

    private sealed class Card
    {
        public Border Frame, Thumb;
        public AppWindow Window;
        public IntPtr Handle;
    }

    private readonly List<Card> _cards = new();
    public string AppKey { get; }

    public WindowPreviewPopup(RunningApp app, string fallbackName) : base(SurfaceKind.Popup, noActivate: true)
    {
        AppKey = app.Key;
        Title = "PearUI Window Preview";
        FallbackCornerRadius = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var w in app.Windows.Take(8)) row.Children.Add(MakeCard(w, fallbackName));
        Content = row;
        Closed += (_, _) => ReleaseThumbs();
    }

    private UIElement MakeCard(AppWindow w, string fallbackName)
    {
        string title = string.IsNullOrWhiteSpace(w.Title) ? fallbackName : w.Title;

        var label = new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

        var close = new TextBlock { Text = "✕", FontSize = 11, Opacity = 0, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        close.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);

        var head = new DockPanel { Margin = new Thickness(2, 0, 2, 6), Height = 18 };
        head.Children.Add(close);
        head.Children.Add(label);

        var thumb = new Border { Height = ThumbH, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        var stack = new StackPanel { Width = CardW };
        stack.Children.Add(head);
        stack.Children.Add(thumb);

        var frame = new Border { Padding = new Thickness(8), Margin = new Thickness(2), CornerRadius = new CornerRadius(10), Child = stack, Background = Brushes.Transparent };
        var card = new Card { Frame = frame, Thumb = thumb, Window = w };
        _cards.Add(card);

        var hwnd = w.Hwnd;
        frame.MouseEnter += (_, _) => { frame.SetResourceReference(Border.BackgroundProperty, "Brush.Hover"); close.Opacity = 0.7; };
        frame.MouseLeave += (_, _) => { frame.Background = Brushes.Transparent; close.Opacity = 0; };
        close.MouseEnter += (_, _) => close.Opacity = 1;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowTracker.Close(hwnd);
            Close();
        };
        frame.MouseLeftButtonUp += (_, _) =>
        {
            if (Win32.IsIconic(hwnd)) Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
            Win32.ActivateWindow(hwnd);
            Close();
        };
        return frame;
    }

    /// <summary>Shows the popup centred above <paramref name="centerXPx"/>, its bottom edge at <paramref name="bottomPx"/>.</summary>
    public void ShowAbove(int centerXPx, int bottomPx, System.Windows.Forms.Screen screen)
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        helper.EnsureHandle();
        Win32.SetWindowPos(helper.Handle, Win32.HWND_TOPMOST, centerXPx, bottomPx - 1, 1, 1, Win32.SWP_NOACTIVATE); // land on the right monitor (DPI)
        Show();
        UpdateLayout();

        double s = DpiScale;
        int w = (int)Math.Ceiling(ActualWidth * s), h = (int)Math.Ceiling(ActualHeight * s);
        int x = Math.Clamp(centerXPx - w / 2, screen.Bounds.Left + 8, Math.Max(screen.Bounds.Left + 8, screen.Bounds.Right - w - 8));
        PlacePx(x, bottomPx - h, w, h);
        UpdateLayout();
        AttachThumbs(s);
    }

    private void AttachThumbs(double scale)
    {
        foreach (var c in _cards)
        {
            try
            {
                if (Win32.DwmRegisterThumbnail(Handle, c.Window.Hwnd, out c.Handle) != 0 || c.Handle == IntPtr.Zero) continue;
                Win32.DwmQueryThumbnailSourceSize(c.Handle, out var src);
                if (src.X <= 0 || src.Y <= 0) continue;

                var origin = c.Thumb.TransformToAncestor(this).Transform(new Point(0, 0));
                double bw = c.Thumb.ActualWidth * scale, bh = c.Thumb.ActualHeight * scale;
                double k = Math.Min(bw / src.X, bh / src.Y);
                int tw = Math.Max(1, (int)(src.X * k)), th = Math.Max(1, (int)(src.Y * k));
                int left = (int)(origin.X * scale + (bw - tw) / 2), top = (int)(origin.Y * scale + (bh - th) / 2);

                var props = new Win32.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_VISIBLE | Win32.DWM_TNP_OPACITY,
                    rcDestination = new Win32.RECT { Left = left, Top = top, Right = left + tw, Bottom = top + th },
                    opacity = 255,
                    fVisible = true,
                };
                Win32.DwmUpdateThumbnailProperties(c.Handle, ref props);
            }
            catch { }
        }
    }

    private void ReleaseThumbs()
    {
        foreach (var c in _cards)
            if (c.Handle != IntPtr.Zero) { try { Win32.DwmUnregisterThumbnail(c.Handle); } catch { } c.Handle = IntPtr.Zero; }
    }
}
