using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PearUI.Themes;

namespace PearUI.Shell;

/// <summary>The Windows-style minimize and close buttons and drag-to-move that frosted-glass windows (Settings) share.</summary>
internal static class GlassChrome
{
    public static void AddCaptionButtons(Panel host, Window window, Action toggleMaximize = null)
    {
        host.Children.Add(Button("\uE921", "Minimize", () => window.WindowState = WindowState.Minimized, false, minimize: true));
        if (toggleMaximize != null) host.Children.Add(Button("\uE922", "Maximize", toggleMaximize, false, minimize: false));
        host.Children.Add(Button("\uE8BB", "Close", window.Close, true, minimize: false));
    }

    /// <summary>Only the close button (for windows that cannot be minimized), running <paramref name="close"/> when clicked.</summary>
    public static void AddCloseButton(Panel host, Action close) => host.Children.Add(Button("\uE8BB", "Close", close, true, minimize: false));

    /// <summary>Lets the window be dragged by the empty parts of <paramref name="area"/>.</summary>
    public static void EnableDrag(Window window, FrameworkElement area, Action doubleClick = null)
    {
        area.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is not DependencyObject d || IsInteractive(d)) return;
            if (e.ClickCount == 2 && doubleClick != null) { doubleClick(); return; }
            try { window.DragMove(); } catch { }
        };
    }

    private static bool IsInteractive(DependencyObject d)
    {
        while (d != null)
        {
            if (d is ListBoxItem || d is Button || d is TextBox || (d is FrameworkElement fe && fe.Tag as string == "nodrag")) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static Border Button(string glyph, string tip, Action click, bool close, bool minimize)
    {
        // All three are icon-font glyphs, so they take exactly the same color as each other and as the toolbar icons.
        var icon = new TextBlock { Text = glyph, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");   // white on the dark theme, black on the light one
        UIElement content = icon;

        var b = new Border { Width = 46, Height = 32, Background = Brushes.Transparent, Child = content, ToolTip = tip, Tag = "nodrag" };
        var hover = close ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : new SolidColorBrush(ThemeManager.IsDark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x1A, 0, 0, 0));
        b.MouseEnter += (_, _) => { b.Background = hover; if (close && content is TextBlock t) t.Foreground = Brushes.White; };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; if (close && content is TextBlock t) t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text"); };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }
}
