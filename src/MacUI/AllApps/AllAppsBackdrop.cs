using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MacUI.AllApps;

/// <summary>
/// Launchpad background: the desktop wallpaper, heavily blurred, with only a light darkening, so its
/// colors glow through like on macOS (instead of a gray frosted panel). The blurred image is rendered
/// once at low resolution and cached until the wallpaper changes. Without a wallpaper image, the
/// window's acrylic glass shows instead.
/// </summary>
public sealed partial class AllAppsWindow
{
    private readonly Grid _backdropLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Image _wallpaper = new() { Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Rectangle _wallpaperTint = new() { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) };
    private string _wallpaperKey;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);

    private static string WallpaperPath()
    {
        var sb = new StringBuilder(520);
        if (SystemParametersInfo(0x0073 /*SPI_GETDESKWALLPAPER*/, (uint)sb.Capacity, sb, 0) && File.Exists(sb.ToString()))
            return sb.ToString();
        string transcoded = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    /// <param name="screenW">Full screen width (DIP).</param>
    /// <param name="screenH">Full screen height (DIP).</param>
    /// <param name="topOffset">How far below the screen top this window starts (the menu bar).</param>
    private void UpdateBackdrop(double screenW, double screenH, double topOffset)
    {
        if (_backdropLayer.Children.Count == 0)
        {
            _backdropLayer.Children.Add(_wallpaper);
            _backdropLayer.Children.Add(_wallpaperTint);
        }

        // The window starts below the menu bar; shift the image so it lines up with the real wallpaper.
        _wallpaper.Width = screenW;
        _wallpaper.Height = screenH;
        _wallpaper.Margin = new Thickness(0, -topOffset, 0, 0);

        string path = WallpaperPath();
        if (path == null) { _wallpaper.Source = null; _wallpaperTint.Visibility = Visibility.Collapsed; return; }
        _wallpaperTint.Visibility = Visibility.Visible;

        string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + (int)screenW + "x" + (int)screenH;
        if (key == _wallpaperKey && _wallpaper.Source != null) return;
        try
        {
            _wallpaper.Source = RenderBlurred(path, screenW / screenH);
            _wallpaperKey = key;
        }
        catch { _wallpaper.Source = null; }
    }

    /// <summary>Blur a small copy of the wallpaper (cheap, and it is scaled up smoothly anyway).</summary>
    private static BitmapSource RenderBlurred(string path, double screenAspect)
    {
        const int w = 480;
        int h = (int)Math.Round(w / screenAspect);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.UriSource = new Uri(path);
        bmp.DecodePixelWidth = w * 2;
        bmp.EndInit();
        bmp.Freeze();

        // Draw slightly larger than the frame so the blur doesn't fade the edges ("Fill" wallpaper style).
        const double bleed = 40;
        var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Width = w + bleed * 2, Height = h + bleed * 2 };
        img.Effect = new BlurEffect { Radius = 34, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
        var host = new Canvas { Width = w, Height = h, ClipToBounds = true, Background = Brushes.Black };
        Canvas.SetLeft(img, -bleed);
        Canvas.SetTop(img, -bleed);
        host.Children.Add(img);
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        return rtb;
    }
}
