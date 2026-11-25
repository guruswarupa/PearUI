using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacUI.Native;

/// <summary>
/// Retrieves high-resolution icons for any shell item: files, executables, folders,
/// "shell:AppsFolder\AUMID" entries (Store apps) and special folders such as the Recycle Bin.
/// </summary>
public static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public int dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, ref DIBSECTION obj);

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool Exists(string parsingName)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var f);
        if (f != null) Marshal.ReleaseComObject(f);
        return hr == 0;
    }

    /// <summary>Only returns an icon that was already loaded (never blocks).</summary>
    public static ImageSource TryGetCached(string parsingName, int size = 96) =>
        parsingName != null && Cache.TryGetValue(parsingName + "|" + size, out var img) ? img : null;

    /// <summary>Returns a frozen ImageSource (safe to use from any thread) or null.</summary>
    public static ImageSource Get(string parsingName, int size = 96, bool useCache = true)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return null;
        string key = parsingName + "|" + size;
        if (useCache && Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource img = null;
        try
        {
            img = parsingName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(parsingName)
                ? LoadIcoFile(parsingName)
                : Load(parsingName, size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK);
        }
        catch { }
        if (img != null && useCache) Cache[key] = img;
        return img;
    }

    /// <summary>Largest image in an .ico file (keeps game icons sharp when magnified).</summary>
    private static ImageSource LoadIcoFile(string path)
    {
        using var fs = System.IO.File.OpenRead(path);
        var decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var best = decoder.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
        if (best == null) return null;
        best.Freeze();
        return best;
    }

    /// <summary>A content preview (photos, videos, PDFs…) when Windows has one, otherwise the file's icon.</summary>
    public static ImageSource GetThumbnail(string path, int size = 128)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string key = path + "|thumb|" + size;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource img = null;
        try { img = Load(path, size, SIIGBF_BIGGERSIZEOK); } catch { }
        img ??= Get(path, size);
        if (img != null) Cache[key] = img;
        return img;
    }

    private static ImageSource Load(string parsingName, int size, int flags)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
            return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out IntPtr hbm) != 0
                || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm); }
            finally { Win32.DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>Copies a 32bpp premultiplied DIB section into a WPF bitmap, preserving alpha.</summary>
    private static unsafe ImageSource FromHBitmap(IntPtr hbm)
    {
        var ds = new DIBSECTION();
        int got = GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds);
        if (got == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
        {
            var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        bool bottomUp = ds.dsBmih.biHeight > 0;
        var pixels = new byte[stride * h];
        byte* src = (byte*)ds.dsBm.bmBits;
        for (int y = 0; y < h; y++)
        {
            int srcRow = bottomUp ? (h - 1 - y) : y;
            Marshal.Copy((IntPtr)(src + srcRow * stride), pixels, y * stride, stride);
        }

        // Some icons come back without any alpha information: treat them as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
