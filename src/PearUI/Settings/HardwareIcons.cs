using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PearUI.Settings;

/// <summary>
/// Illustrated, full-colour pictures of computer parts for About This PC: a processor with its metal lid, a green memory stick
/// with black chips and gold contacts, a graphics card with fans, an open hard disk, an SSD, a glossy battery, a monitor.
/// Each is drawn in a 64 x 64 box (so they stay sharp) and scaled to the size asked for.
/// </summary>
internal static class HardwareIcons
{
    // ---------- drawing helpers ----------
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static LinearGradientBrush Lin(double x1, double y1, double x2, double y2, params (double At, string Hex)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2) };
        foreach (var (at, hex) in stops) b.GradientStops.Add(new GradientStop(C(hex), at));
        b.Freeze();
        return b;
    }

    /// <summary>Top to bottom.</summary>
    private static LinearGradientBrush Down(params (double At, string Hex)[] stops) => Lin(0, 0, 0, 1, stops);

    private static RadialGradientBrush Rad(double cx, double cy, double r, params (double At, string Hex)[] stops)
    {
        var b = new RadialGradientBrush { Center = new Point(cx, cy), GradientOrigin = new Point(cx - 0.12, cy - 0.15), RadiusX = r, RadiusY = r };
        foreach (var (at, hex) in stops) b.GradientStops.Add(new GradientStop(C(hex), at));
        b.Freeze();
        return b;
    }

    private static SolidColorBrush Solid(string hex, double opacity = 1)
    {
        var b = new SolidColorBrush(C(hex)) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static Rectangle Rect(double x, double y, double w, double h, double r, Brush fill, Brush stroke = null, double thickness = 0)
    {
        var e = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = fill, Stroke = stroke, StrokeThickness = thickness };
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    private static Ellipse Circle(double cx, double cy, double r, Brush fill, Brush stroke = null, double thickness = 0)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = stroke, StrokeThickness = thickness };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        return e;
    }

    private static Path Shape(string data, Brush fill, Brush stroke = null, double thickness = 0)
    {
        var p = new Path { Data = Geometry.Parse(data), Fill = fill, Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        return p;
    }

    private static Canvas Board() => new() { Width = 64, Height = 64 };

    private static FrameworkElement Wrap(Canvas canvas, double size, double shadow = 2.5)
    {
        canvas.Effect = new DropShadowEffect { BlurRadius = shadow * 2, ShadowDepth = shadow * 0.6, Direction = 270, Opacity = 0.38, Color = Colors.Black };
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    }

    // ---------- processor ----------
    public static FrameworkElement Cpu(double size)
    {
        var c = Board();
        // the green circuit board with gold pads along its edges
        c.Children.Add(Rect(6, 6, 52, 52, 5, Down((0, "#3B8C63"), (1, "#1F5E40")), Solid("#12402B"), 1));
        for (int i = 0; i < 9; i++)
        {
            double p = 11 + i * 5.2;
            c.Children.Add(Rect(p, 7.6, 2.6, 2.6, 0.6, Solid("#E9C46A")));
            c.Children.Add(Rect(p, 53.8, 2.6, 2.6, 0.6, Solid("#E9C46A")));
            c.Children.Add(Rect(7.6, p, 2.6, 2.6, 0.6, Solid("#E9C46A")));
            c.Children.Add(Rect(53.8, p, 2.6, 2.6, 0.6, Solid("#E9C46A")));
        }
        // the metal lid (heat spreader): bright bevel, brushed face
        c.Children.Add(Rect(13, 13, 38, 38, 3.5, Lin(0, 0, 1, 1, (0, "#FBFBFD"), (0.45, "#B5BAC3"), (1, "#E7E9EE")), Solid("#6F7580"), 0.8));
        c.Children.Add(Rect(15.5, 15.5, 33, 33, 2.2, Lin(0, 0, 1, 1, (0, "#E4E7EC"), (0.5, "#C6CAD2"), (1, "#F3F4F7")), Solid("#FFFFFF", 0.7), 0.8));
        // engraved text lines and the corner marker
        c.Children.Add(Rect(21, 24, 22, 1.8, 0.9, Solid("#6C727C", 0.55)));
        c.Children.Add(Rect(21, 29, 16, 1.8, 0.9, Solid("#6C727C", 0.45)));
        c.Children.Add(Rect(21, 34, 20, 1.8, 0.9, Solid("#6C727C", 0.45)));
        c.Children.Add(Shape("M10,10 L17,10 L10,17 Z", Solid("#F4D58D")));
        return Wrap(c, size);
    }

    // ---------- memory ----------
    public static FrameworkElement Ram(double size)
    {
        var c = Board();
        // green board, slightly rounded, with the gold contacts and the key notch
        c.Children.Add(Rect(2.5, 19, 59, 25, 2, Down((0, "#33A06B"), (1, "#1A7048")), Solid("#0F4A2F"), 0.9));
        c.Children.Add(Rect(2.5, 19, 59, 3, 2, Solid("#FFFFFF", 0.22)));                 // light catching the top edge
        for (int i = 0; i < 26; i++)
        {
            double x = 4.2 + i * 2.2;
            if (x > 29.2 && x < 33.4) continue;                                           // the key notch
            c.Children.Add(Rect(x, 38.4, 1.4, 5.2, 0.3, Lin(0, 0, 0, 1, (0, "#F7DD94"), (1, "#C9972E"))));
        }
        // black memory chips with a soft highlight
        for (int i = 0; i < 6; i++)
        {
            double x = 6 + i * 9.1 + (i >= 3 ? 1.6 : 0);
            c.Children.Add(Rect(x, 23.2, 7.6, 11.2, 1, Down((0, "#474A52"), (1, "#16171B")), Solid("#0B0B0D"), 0.5));
            c.Children.Add(Rect(x + 0.9, 24.1, 5.8, 1.4, 0.5, Solid("#FFFFFF", 0.18)));
            c.Children.Add(Circle(x + 1.7, 32.8, 0.5, Solid("#FFFFFF", 0.35)));
        }
        // tiny silkscreen parts
        c.Children.Add(Rect(58, 24, 2, 4, 0.4, Solid("#D9D4C7", 0.9)));
        c.Children.Add(Rect(58, 30, 2, 2, 0.4, Solid("#D9D4C7", 0.7)));
        return Wrap(c, size);
    }

    // ---------- graphics card ----------
    public static FrameworkElement Gpu(double size)
    {
        var c = Board();
        // metal bracket on the left and the gold connector underneath
        c.Children.Add(Rect(1.5, 13, 5, 30, 1, Lin(0, 0, 1, 0, (0, "#F1F2F5"), (1, "#A7ACB4")), Solid("#6A6F78"), 0.7));
        c.Children.Add(Rect(2.8, 17, 2.4, 3, 0.4, Solid("#2A2C31")));
        c.Children.Add(Rect(2.8, 24, 2.4, 3, 0.4, Solid("#2A2C31")));
        for (int i = 0; i < 13; i++) c.Children.Add(Rect(13 + i * 2.5, 46, 1.5, 6, 0.3, Lin(0, 0, 0, 1, (0, "#F7DD94"), (1, "#C9972E"))));
        // the dark cooler shroud
        c.Children.Add(Rect(6, 13, 56, 33, 3.5, Down((0, "#4B4F59"), (0.5, "#2D3037"), (1, "#1B1D22")), Solid("#0E0F12"), 0.9));
        c.Children.Add(Rect(8, 14.2, 52, 2.2, 1.1, Solid("#FFFFFF", 0.2)));
        c.Children.Add(Rect(8, 42, 52, 2.2, 1.1, Solid("#E5483C")));                      // the red accent strip
        // two fans
        foreach (double cx in new[] { 22.5, 46.5 })
        {
            c.Children.Add(Circle(cx, 29, 11.5, Rad(0.5, 0.5, 0.7, (0, "#15171A"), (1, "#050506")), Solid("#5C606A"), 1.2));
            for (int b = 0; b < 9; b++)
            {
                var blade = Shape("M0,0 C4,-3 9,-5 10,-9.5 C6.6,-8.6 2.4,-6 0,0 Z", Lin(0, 0, 1, 1, (0, "#9EA3AE"), (1, "#3A3D45")));
                blade.RenderTransform = new TransformGroup { Children = { new RotateTransform(b * 40), new TranslateTransform(cx, 29) } };
                c.Children.Add(blade);
            }
            c.Children.Add(Circle(cx, 29, 3.6, Rad(0.5, 0.5, 0.7, (0, "#4A4E58"), (1, "#16171A")), Solid("#6C707A"), 0.6));
            c.Children.Add(Circle(cx, 29, 1.2, Solid("#C9CDD6", 0.9)));
        }
        return Wrap(c, size);
    }

    // ---------- storage ----------
    public static FrameworkElement HardDisk(double size)
    {
        var c = Board();
        // the metal case, wider than tall like a real 3.5 inch drive, seen with the lid off
        c.Children.Add(Rect(3, 12, 58, 40, 4, Lin(0, 0, 1, 1, (0, "#F2F3F6"), (0.5, "#B7BCC5"), (1, "#E2E4E9")), Solid("#6C727C"), 1));
        c.Children.Add(Rect(6.5, 15.5, 51, 33, 2.6, Down((0, "#34373E"), (1, "#1B1D22")), Solid("#0C0D0F"), 0.8));
        foreach (var (x, y) in new[] { (4.6, 13.6), (57.4, 13.6), (4.6, 48.4), (57.4, 48.4) })
            c.Children.Add(Circle(x, y, 1.15, Rad(0.5, 0.5, 0.7, (0, "#E9EBF0"), (1, "#7B808A"))));
        // the shiny platter
        c.Children.Add(Circle(27, 32, 14.5, Rad(0.5, 0.5, 0.75, (0, "#F7F8FA"), (0.35, "#C5C9D1"), (0.7, "#8C919B"), (1, "#E1E3E8")), Solid("#5E636D"), 0.9));
        c.Children.Add(Circle(27, 32, 9.6, Rad(0.5, 0.5, 0.7, (0, "#E8EAEE"), (1, "#A2A7B0")), Solid("#FFFFFF", 0.35), 0.6));
        c.Children.Add(Circle(27, 32, 3.5, Rad(0.5, 0.5, 0.7, (0, "#7C818B"), (1, "#2B2D33")), Solid("#C9CDD6"), 0.7));
        c.Children.Add(Circle(27, 32, 1.1, Solid("#E7E9EE")));
        // the read arm with its pivot in the corner
        c.Children.Add(Shape("M51,21.5 L36,31 L37.8,33.4 L53,24 Z", Lin(0, 0, 1, 1, (0, "#E9EBEF"), (1, "#8D929C")), Solid("#4C5059"), 0.5));
        c.Children.Add(Circle(52.2, 22.6, 2.8, Rad(0.5, 0.5, 0.7, (0, "#E9EBEF"), (1, "#6E737D")), Solid("#33363D"), 0.5));
        c.Children.Add(Rect(9.5, 44, 7, 2, 1, Solid("#4EC27A")));                           // activity light
        return Wrap(c, size);
    }

    public static FrameworkElement Ssd(double size, bool nvme = false)
    {
        var c = Board();
        if (nvme)
        {
            // an M.2 stick: dark board, two memory chips and a controller, gold contacts at one end
            c.Children.Add(Rect(3, 22, 58, 20, 2, Down((0, "#3B4049"), (1, "#1F2227")), Solid("#0C0D0F"), 0.9));
            // gold fingers sit on the short left end, laid out across the width of the stick, with the key gap
            for (int i = 0; i < 8; i++)
            {
                double y = 24.2 + i * 2.1 + (i >= 5 ? 1.8 : 0);
                c.Children.Add(Rect(3.6, y, 6, 1.3, 0.3, Lin(0, 0, 1, 0, (0, "#C9972E"), (1, "#F7DD94"))));
            }
            c.Children.Add(Rect(15, 25, 12, 14, 1.2, Down((0, "#D9DCE2"), (1, "#8D929C")), Solid("#4C5059"), 0.6));
            c.Children.Add(Rect(29, 25, 12, 14, 1.2, Down((0, "#D9DCE2"), (1, "#8D929C")), Solid("#4C5059"), 0.6));
            c.Children.Add(Rect(43, 27, 9, 10, 1, Down((0, "#4A4E58"), (1, "#16171A")), Solid("#0B0B0D"), 0.5));
            c.Children.Add(Circle(58.2, 32, 1.8, Solid("#12141A"), Solid("#C9CDD6"), 0.8));    // the screw notch
            return Wrap(c, size);
        }

        // a 2.5 inch drive: dark metal case with a label and the SATA connector
        c.Children.Add(Rect(5, 12, 54, 38, 4, Lin(0, 0, 1, 1, (0, "#5A5F69"), (0.5, "#2F3238"), (1, "#464B54")), Solid("#0F1013"), 1));
        c.Children.Add(Rect(7, 13.4, 50, 3, 1.5, Solid("#FFFFFF", 0.2)));
        c.Children.Add(Rect(12, 20, 30, 18, 2, Down((0, "#F4F5F8"), (1, "#C9CCD3")), Solid("#8C919B"), 0.6));
        c.Children.Add(Rect(15, 24, 18, 2.6, 1.3, Solid("#E5483C")));
        c.Children.Add(Rect(15, 29.5, 24, 1.8, 0.9, Solid("#5C616B", 0.7)));
        c.Children.Add(Rect(15, 33, 14, 1.8, 0.9, Solid("#5C616B", 0.5)));
        c.Children.Add(Rect(46, 40, 11, 7, 1, Solid("#16171B"), Solid("#0B0B0D"), 0.5));
        for (int i = 0; i < 5; i++) c.Children.Add(Rect(47.3 + i * 1.9, 43, 1.1, 3, 0.3, Solid("#E9C46A")));
        return Wrap(c, size);
    }

    // ---------- battery ----------
    /// <summary>A glossy battery drawn with its real charge: a fuller cell for a fuller battery, orange when low, red when very low, a bolt while charging.</summary>
    public static FrameworkElement Battery(double size, int percent, bool charging)
    {
        percent = Math.Clamp(percent, 0, 100);
        var c = Board();
        // the casing with its terminal
        c.Children.Add(Rect(57, 24, 5, 16, 2, Lin(0, 0, 0, 1, (0, "#F2F3F6"), (1, "#8D929C")), Solid("#5C616B"), 0.7));
        c.Children.Add(Rect(2, 14, 56, 36, 7, Down((0, "#F7F8FA"), (0.5, "#C9CDD5"), (1, "#9EA3AD")), Solid("#5C616B"), 1));
        c.Children.Add(Rect(5.5, 17.5, 49, 29, 4.5, Down((0, "#2A2D33"), (1, "#16171B")), Solid("#0B0B0D"), 0.6));

        // the charge itself
        double width = 49 * percent / 100.0 - 3;
        if (percent > 0 && width > 2)
        {
            string top = percent <= 20 ? "#FF7A6E" : percent <= 40 ? "#FFC165" : "#6BE08C";
            string mid = percent <= 20 ? "#E5483C" : percent <= 40 ? "#F2A12B" : "#34C759";
            string bottom = percent <= 20 ? "#A8281F" : percent <= 40 ? "#B8700F" : "#1E8E3E";
            c.Children.Add(Rect(7, 19, width, 26, 3, Down((0, top), (0.45, mid), (1, bottom))));
            c.Children.Add(Rect(8.2, 19.8, Math.Max(0, width - 2.4), 7, 2.4, Solid("#FFFFFF", 0.32)));            // the gloss
        }
        if (charging) c.Children.Add(Shape("M33.5,17 L22,34 H29.5 L26.5,48 L40,28.5 H32 Z", Lin(0, 0, 0, 1, (0, "#FFFFFF"), (1, "#E7E9EE")), Solid("#16171B", 0.75), 1.1));
        return Wrap(c, size);
    }

    // ---------- wireless ----------
    /// <summary>A Wi-Fi router: two antennas, a glossy white body with status lights and signal waves above it. Greyed out when the radio is off.</summary>
    public static FrameworkElement Wifi(double size, bool on = true)
    {
        var c = Board();
        string wave = on ? "#2F7BFF" : "#8B919C";
        // signal waves between the antennas
        foreach (var r in new[] { 5.5, 10.5, 15.5 })
        {
            double d = r * 0.7071;
            c.Children.Add(new Path
            {
                Data = Geometry.Parse($"M{32 - d:0.##},{27 - d:0.##} A{r},{r} 0 0 1 {32 + d:0.##},{27 - d:0.##}"),
                Stroke = Solid(wave), StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }
        c.Children.Add(Circle(32, 27, 1.9, Solid(wave)));

        // antennas
        foreach (double x in new[] { 11.5, 50.5 })
        {
            c.Children.Add(Rect(x - 2.2, 5, 4.4, 30, 2.2, Lin(0, 0, 1, 0, (0, "#2A2C31"), (0.4, "#5B606B"), (1, "#1B1D22")), Solid("#0B0B0D"), 0.5));
            c.Children.Add(Rect(x - 1.2, 6.5, 1.2, 14, 0.6, Solid("#FFFFFF", 0.28)));
            c.Children.Add(Circle(x, 36, 3.2, Rad(0.5, 0.5, 0.7, (0, "#7C818B"), (1, "#2B2D33")), Solid("#0B0B0D"), 0.5));
        }

        // feet and body
        c.Children.Add(Rect(11, 52, 8, 3, 1, Solid("#16171B")));
        c.Children.Add(Rect(45, 52, 8, 3, 1, Solid("#16171B")));
        c.Children.Add(Rect(5, 34, 54, 20, 6, Down((0, "#FFFFFF"), (0.55, "#DDE0E6"), (1, "#A9AEB8")), Solid("#6C727C"), 0.9));
        c.Children.Add(Rect(8, 35.2, 48, 4, 3, Solid("#FFFFFF", 0.7)));                                     // gloss
        c.Children.Add(Rect(10, 42, 44, 8, 3, Down((0, "#2E3138"), (1, "#16171B")), Solid("#0B0B0D"), 0.5)); // the dark light panel
        string[] leds = on ? new[] { "#43D17A", "#43D17A", "#4DA2FF", "#43D17A" } : new[] { "#4B4F58", "#4B4F58", "#4B4F58", "#4B4F58" };
        for (int i = 0; i < leds.Length; i++)
        {
            c.Children.Add(Circle(17 + i * 7, 46, 1.9, Solid(leds[i])));
            if (on) c.Children.Add(Circle(17 + i * 7 - 0.5, 45.4, 0.6, Solid("#FFFFFF", 0.7)));
        }
        return Wrap(c, size, 2);
    }

    /// <summary>A USB Bluetooth adapter: a silver USB plug, a dark glossy body and a blue plate with the Bluetooth rune. Greyed out when the radio is off.</summary>
    public static FrameworkElement Bluetooth(double size, bool on = true)
    {
        var c = Board();
        // USB plug
        c.Children.Add(Rect(2, 24, 17, 16, 1.5, Lin(0, 0, 0, 1, (0, "#F6F7F9"), (0.5, "#B9BEC7"), (1, "#E4E6EB")), Solid("#6C727C"), 0.9));
        c.Children.Add(Rect(5.5, 28, 4.5, 3.2, 0.5, Solid("#1B1D22")));
        c.Children.Add(Rect(11.5, 28, 4.5, 3.2, 0.5, Solid("#1B1D22")));
        c.Children.Add(Rect(5.5, 33.4, 4.5, 3.2, 0.5, Solid("#1B1D22")));
        c.Children.Add(Rect(11.5, 33.4, 4.5, 3.2, 0.5, Solid("#1B1D22")));
        // the body
        c.Children.Add(Rect(17, 19, 44, 26, 8, Down((0, "#4A4F59"), (0.5, "#2A2D33"), (1, "#16171B")), Solid("#0B0B0D"), 1));
        c.Children.Add(Rect(20, 20.4, 38, 4.4, 3, Solid("#FFFFFF", 0.22)));                                    // gloss
        c.Children.Add(Rect(17, 30, 4, 4, 0, Solid("#0B0B0D", 0.0)));
        // the blue plate with the rune
        string top = on ? "#6CB6FF" : "#B3B8C2", bottom = on ? "#1F5BE0" : "#6A707B";
        c.Children.Add(Circle(43, 32, 9.6, Rad(0.5, 0.45, 0.8, (0, top), (1, bottom)), Solid("#0B1E4A", 0.6), 0.9));
        c.Children.Add(new Path
        {
            Data = Geometry.Parse("M39.6,28.6 L46.4,35.2 L43,38.4 L43,25.6 L46.4,28.8 L39.6,35.4"),
            Stroke = Solid("#FFFFFF"), StrokeThickness = 1.9, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        c.Children.Add(Circle(56, 25.5, 1.4, Solid(on ? "#4DA2FF" : "#5B606B")));                              // status light
        return Wrap(c, size, 2);
    }

    // ---------- display ----------
    public static FrameworkElement Monitor(double size)
    {
        var c = Board();
        c.Children.Add(Rect(24, 46, 16, 6, 1, Lin(0, 0, 1, 0, (0, "#C9CDD5"), (0.5, "#F4F5F8"), (1, "#A7ACB5"))));
        c.Children.Add(Rect(15, 52, 34, 4, 2, Lin(0, 0, 0, 1, (0, "#F4F5F8"), (1, "#8D929C")), Solid("#5C616B"), 0.6));
        c.Children.Add(Rect(4, 8, 56, 40, 4, Down((0, "#3A3D44"), (1, "#16171B")), Solid("#0B0B0D"), 1));
        c.Children.Add(Rect(7, 11, 50, 31, 1.5, Lin(0, 0, 1, 1, (0, "#4FA3FF"), (0.5, "#7B5CFF"), (1, "#FF7AB6"))));
        c.Children.Add(Shape("M7,11 L33,11 L7,34 Z", Solid("#FFFFFF", 0.14)));                                     // glass reflection
        c.Children.Add(Circle(32, 45, 0.8, Solid("#9EA3AE", 0.9)));
        return Wrap(c, size);
    }
}
