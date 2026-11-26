using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace MacUI.Shell;

/// <summary>
/// The PearUI mark: a pear with a bite taken out of its right side, a stem and a leaf.
/// Drawn in a 16×16 box; one definition is used for WPF (menu bar) and GDI+ (tray / shortcut icon).
/// </summary>
public static class PearLogo
{
    // Pear body: narrow neck, round bottom.
    private const string BodyData =
        "M8,3 C9.3,3 10,4.2 10.3,5.6 C10.6,7 13.2,8.6 13.2,11.4 C13.2,14.2 10.9,15.6 8,15.6 " +
        "C5.1,15.6 2.8,14.2 2.8,11.4 C2.8,8.6 5.4,7 5.7,5.6 C6,4.2 6.7,3 8,3 Z";
    private const string LeafData = "M8.5,2.6 C8.9,1.2 10.5,0.5 11.8,0.8 C11.4,2.2 9.9,2.9 8.5,2.6 Z";
    private const string StemData = "M7.75,3.3 L8.05,1.1 L8.55,1.15 L8.3,3.3 Z";
    private static readonly Point BiteCenter = new(13.7, 9.8);
    private const double BiteRadius = 2.3;

    /// <summary>Single-color WPF geometry (body minus bite, plus stem and leaf).</summary>
    public static Geometry CreateGeometry()
    {
        var body = new CombinedGeometry(GeometryCombineMode.Exclude, Geometry.Parse(BodyData),
            new EllipseGeometry(BiteCenter, BiteRadius, BiteRadius));
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(body);
        group.Children.Add(Geometry.Parse(StemData));
        group.Children.Add(Geometry.Parse(LeafData));
        group.Freeze();
        return group;
    }

    /// <summary>Full-color GDI+ rendering for the tray icon and shortcut icon.</summary>
    public static Drawing.Bitmap CreateColorBitmap(int size)
    {
        var bmp = new Drawing.Bitmap(size, size);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);
        float k = size / 16f;
        g.ScaleTransform(k, k);

        using var body = new Drawing2D.GraphicsPath();
        body.StartFigure();
        body.AddBezier(8f, 3f, 9.3f, 3f, 10f, 4.2f, 10.3f, 5.6f);
        body.AddBezier(10.3f, 5.6f, 10.6f, 7f, 13.2f, 8.6f, 13.2f, 11.4f);
        body.AddBezier(13.2f, 11.4f, 13.2f, 14.2f, 10.9f, 15.6f, 8f, 15.6f);
        body.AddBezier(8f, 15.6f, 5.1f, 15.6f, 2.8f, 14.2f, 2.8f, 11.4f);
        body.AddBezier(2.8f, 11.4f, 2.8f, 8.6f, 5.4f, 7f, 5.7f, 5.6f);
        body.AddBezier(5.7f, 5.6f, 6f, 4.2f, 6.7f, 3f, 8f, 3f);
        body.CloseFigure();

        using var region = new Drawing.Region(body);
        using var bite = new Drawing2D.GraphicsPath();
        bite.AddEllipse((float)(BiteCenter.X - BiteRadius), (float)(BiteCenter.Y - BiteRadius), (float)BiteRadius * 2, (float)BiteRadius * 2);
        region.Exclude(bite);

        using var fill = new Drawing2D.LinearGradientBrush(new Drawing.PointF(3, 3), new Drawing.PointF(13, 15.6f),
            Drawing.Color.FromArgb(255, 0xC8, 0xE0, 0x5A), Drawing.Color.FromArgb(255, 0x7C, 0xB3, 0x2A));
        g.FillRegion(fill, region);

        using var stem = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 0x7A, 0x52, 0x30));
        g.FillPolygon(stem, new[] { new Drawing.PointF(7.75f, 3.3f), new Drawing.PointF(8.05f, 1.1f), new Drawing.PointF(8.55f, 1.15f), new Drawing.PointF(8.3f, 3.3f) });

        using var leaf = new Drawing2D.GraphicsPath();
        leaf.AddBezier(8.5f, 2.6f, 8.9f, 1.2f, 10.5f, 0.5f, 11.8f, 0.8f);
        leaf.AddBezier(11.8f, 0.8f, 11.4f, 2.2f, 9.9f, 2.9f, 8.5f, 2.6f);
        using var leafBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 0x3E, 0x9B, 0x3A));
        g.FillPath(leafBrush, leaf);
        return bmp;
    }

    public static Drawing.Icon CreateIcon(int size = 32) => Drawing.Icon.FromHandle(CreateColorBitmap(size).GetHicon());
}
