using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PearUI.Native;
using Forms = System.Windows.Forms;

namespace PearUI.Shell;

/// <summary>A small frosted label shown for a moment when a shortcut fires, so you can see it worked.</summary>
internal sealed class ShortcutToast : OverlayWindow
{
    private static ShortcutToast _current;

    public static void Show(string keys, string message)
    {
        try
        {
            _current?.Close();
            _current = new ShortcutToast(keys, message);
            _current.Show();
        }
        catch { }
    }

    private ShortcutToast(string keys, string message) : base(SurfaceKind.Popup, noActivate: true)
    {
        Title = "PearUI Shortcut";
        FallbackCornerRadius = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 11, 18, 11) };
        var chip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        chip.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");
        var keyText = new TextBlock { Text = keys, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
        keyText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        chip.Child = keyText;
        var text = new TextBlock { Text = message, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        if (!string.IsNullOrEmpty(keys)) row.Children.Add(chip);
        row.Children.Add(text);
        Content = row;

        Loaded += (_, _) =>
        {
            // bottom centre of the display the pointer is on, above the Dock
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
            double s = DpiScale;
            int w = (int)Math.Ceiling(ActualWidth * s), h = (int)Math.Ceiling(ActualHeight * s);
            PlacePx(screen.WorkingArea.Left + (screen.WorkingArea.Width - w) / 2, screen.WorkingArea.Bottom - h - (int)(70 * s), w, h);

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350)) { BeginTime = TimeSpan.FromMilliseconds(1100) };
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
    }
}
