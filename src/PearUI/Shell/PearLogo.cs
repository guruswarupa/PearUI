using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace PearUI.Shell;

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

    /// <summary>
    /// The full-colour app icon (a green tile with a white pear), rendered at exactly <paramref name="size"/> pixels.
    /// It is drawn at four times the size and scaled down, so edges stay smooth at every size.
    /// </summary>
    public static Drawing.Bitmap CreateColorBitmap(int size) => Render(size, size, (dc, px) => DrawTile(dc, px));

    /// <summary>Just the pear (no tile) in full colour on a transparent background, for places such as the terminal's <c>info</c> command.</summary>
    public static Drawing.Bitmap CreateGlyphBitmap(int size) => Render(size, size, (dc, px) => DrawGlyphOnly(dc, px));

    /// <summary>The pear alone, stretched to a <paramref name="width"/> by <paramref name="height"/> grid whose pixels need not be square (terminal sub-cells are taller than wide).</summary>
    public static Drawing.Bitmap CreateGlyphBitmap(int width, int height, bool forLightBackground = false) => Render(width, height, (dc, px) => DrawGlyphOnly(dc, px, forLightBackground));

    private static Drawing.Bitmap Render(int width, int height, Action<DrawingContext, int> draw)
    {
        const int Supersample = 4;
        var big = RenderLayer(width * Supersample, height * Supersample, draw);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (var dc = visual.RenderOpen()) dc.DrawImage(big, new Rect(0, 0, width, height));
        var small = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        small.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(small));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var decoded = new Drawing.Bitmap(ms);
        return new Drawing.Bitmap(decoded);   // detach from the stream
    }

    private static RenderTargetBitmap RenderLayer(int pxW, int pxH, Action<DrawingContext, int> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pxW / 256.0, pxH / 256.0));
            draw(dc, Math.Min(pxW, pxH));
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static void DrawGlyphOnly(DrawingContext dc, int px, bool forLightBackground = false)
    {
        // The pear fills the square: 15.1 units tall (leaf tip to base), centred.
        const double k = 16.2;
        var glyph = new TransformGroup();
        glyph.Children.Add(new TranslateTransform(-8, -8.05));
        glyph.Children.Add(new ScaleTransform(k, k));
        glyph.Children.Add(new TranslateTransform(128, 128));

        var body = new CombinedGeometry(GeometryCombineMode.Exclude, Geometry.Parse(BodyData), new EllipseGeometry(BiteCenter, BiteRadius, BiteRadius));
        dc.PushTransform(glyph);
        dc.DrawGeometry(new LinearGradientBrush(forLightBackground ? Color.FromRgb(0x8C, 0xC8, 0x3C) : Color.FromRgb(0xC6, 0xF0, 0x6E), forLightBackground ? Color.FromRgb(0x2E, 0x8A, 0x3C) : Color.FromRgb(0x4C, 0xA8, 0x4A), new Point(0.25, 0), new Point(0.75, 1)), null, body);
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x9A, 0x66, 0x38)), null, Geometry.Parse(StemData));
        dc.DrawGeometry(new SolidColorBrush(forLightBackground ? Color.FromRgb(0x1F, 0x8A, 0x3C) : Color.FromRgb(0x46, 0xC4, 0x60)), null, Geometry.Parse(LeafData));
        dc.Pop();
    }

    private static void DrawTile(DrawingContext dc, int px)
    {
        bool small = px <= 32 * 4;
        var tile = new RectangleGeometry(new Rect(8, 8, 240, 240), 58, 58);

        // tile: fresh lime at the top to leaf green at the bottom
        var fill = new LinearGradientBrush(Color.FromRgb(0xBC, 0xEC, 0x6C), Color.FromRgb(0x2F, 0x9C, 0x52), new Point(0.2, 0), new Point(0.8, 1));
        dc.DrawGeometry(fill, null, tile);

        // soft gloss over the top half
        dc.PushClip(tile);
        var gloss = new LinearGradientBrush(Color.FromArgb(0x55, 255, 255, 255), Color.FromArgb(0x00, 255, 255, 255), new Point(0, 0), new Point(0, 1));
        dc.DrawRectangle(gloss, null, new Rect(8, 8, 240, 118));
        dc.Pop();

        // fine inner edge
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255)), 2.5), new RectangleGeometry(new Rect(9.5, 9.5, 237, 237), 56.5, 56.5));

        // the pear, centred; a little larger at small sizes so it stays readable
        double k = small ? 11.6 : 10.2;
        var glyph = new TransformGroup();
        glyph.Children.Add(new TranslateTransform(-8, -8.05));
        glyph.Children.Add(new ScaleTransform(k, k));
        glyph.Children.Add(new TranslateTransform(128, 134));

        var body = new CombinedGeometry(GeometryCombineMode.Exclude, Geometry.Parse(BodyData), new EllipseGeometry(BiteCenter, BiteRadius, BiteRadius));
        var stem = Geometry.Parse(StemData);
        var leaf = Geometry.Parse(LeafData);

        if (!small)
        {
            // a soft drop shadow, built from a few faint copies
            for (int i = 1; i <= 5; i++)
            {
                dc.PushTransform(new TranslateTransform(0, i * 1.6));
                dc.PushTransform(glyph);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x16, 0x0B, 0x3D, 0x1A)), null, body);
                dc.Pop();
                dc.Pop();
            }
        }

        dc.PushTransform(glyph);
        dc.DrawGeometry(new LinearGradientBrush(Colors.White, Color.FromRgb(0xE3, 0xF3, 0xCF), new Point(0.3, 0), new Point(0.7, 1)), null, body);
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2A, 0x6B, 0x35)), null, stem);
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x3C)), null, leaf);
        dc.Pop();
    }

    /// <summary>
    /// Gives a window the pear icon drawn at exactly the title-bar and Alt+Tab sizes for the display's scaling,
    /// so it is crisp instead of a stretched copy of the nearest stored size.
    /// </summary>
    public static void ApplyWindowIcon(Window window)
    {
        void Set()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            int small = Math.Max(16, GetSystemMetricsForDpi(49 /*SM_CXSMICON*/, dpi));
            int big = Math.Max(32, GetSystemMetricsForDpi(11 /*SM_CXICON*/, dpi));
            SendMessage(hwnd, 0x0080 /*WM_SETICON*/, (IntPtr)0, CreateIcon(small).Handle);
            SendMessage(hwnd, 0x0080, (IntPtr)1, CreateIcon(big).Handle);
        }
        if (new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero) Set();
        else window.SourceInitialized += (_, _) => Set();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static Drawing.Icon CreateIcon(int size = 32) => Drawing.Icon.FromHandle(CreateColorBitmap(size).GetHicon());
}
