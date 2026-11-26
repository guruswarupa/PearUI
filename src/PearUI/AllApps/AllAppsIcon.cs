using System.Windows;
using System.Windows.Media;

namespace MacUI.AllApps;

/// <summary>Dock icon for All Apps: a graphite tile with a soft 3×3 grid. Restrained on purpose, no rainbow.</summary>
public static class AllAppsIcon
{
    private static ImageSource _cached;

    public static ImageSource Create()
    {
        if (_cached != null) return _cached;
        var g = new DrawingGroup();

        // Tile: deep graphite with a faint top light.
        var tile = new LinearGradientBrush(Color.FromRgb(0x4B, 0x4E, 0x57), Color.FromRgb(0x2A, 0x2C, 0x33), 90);
        g.Children.Add(new GeometryDrawing(tile, new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), 1.5),
            new RectangleGeometry(new Rect(5, 5, 118, 118), 28, 28)));

        // 3×3 grid of soft off-white squares.
        const double cell = 22, gap = 9, start = (128 - (cell * 3 + gap * 2)) / 2;
        for (int i = 0; i < 9; i++)
        {
            double x = start + (i % 3) * (cell + gap), y = start + (i / 3) * (cell + gap);
            var fill = new SolidColorBrush(Color.FromArgb(i == 4 ? (byte)0xF2 : (byte)0xC8, 0xF2, 0xF3, 0xF5));
            g.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(new Rect(x, y, cell, cell), 6, 6)));
        }

        // Keep the full 128×128 box so it scales like the other icons.
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 128, 128))));
        var img = new DrawingImage(g);
        img.Freeze();
        return _cached = img;
    }
}
