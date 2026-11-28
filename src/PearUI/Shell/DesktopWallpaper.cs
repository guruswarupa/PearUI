using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PearUI.Shell;

/// <summary>The picture Windows currently uses as the desktop wallpaper.</summary>
internal static class DesktopWallpaper
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint flags);

    /// <summary>A frozen image scaled down to <paramref name="decodeWidth"/> pixels wide, or null when it can't be read.</summary>
    public static ImageSource Load(int decodeWidth)
    {
        try
        {
            var sb = new StringBuilder(520);
            string path = SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0) ? sb.ToString() : "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
