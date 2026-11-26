using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PearUI.Native;

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

    private static double _scale;
    private static DateTime _scaleAt;

    /// <summary>
    /// How many pixels to load for an icon shown <paramref name="dip"/> units wide: enough for the sharpest screen
    /// attached (a 4K screen at 200% shows a 96-unit icon with 192 pixels), rounded to a size the cache reuses.
    /// </summary>
    public static int PixelsFor(double dip)
    {
        if (_scale == 0 || DateTime.UtcNow - _scaleAt > TimeSpan.FromSeconds(30))
        {
            double s = 1;
            try { foreach (var screen in System.Windows.Forms.Screen.AllScreens) s = Math.Max(s, Win32.GetScaleForScreen(screen)); } catch { }
            _scale = s; _scaleAt = DateTime.UtcNow;
        }
        double px = dip * _scale;
        foreach (int step in new[] { 128, 192, 256 }) if (px <= step) return step;   // never below 128: icons are also shown magnified
        return 256;
    }

    /// <summary>Only returns an icon that was already loaded (never blocks).</summary>
    public static ImageSource TryGetCached(string parsingName, int size = 96) =>
        parsingName != null && Cache.TryGetValue(parsingName + "|" + size, out var img) ? img : null;

    // ---- background loading: dedicated STA workers (shell icon handlers want STA), de-duplicated, with retries ----
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = new();
    private static readonly ConcurrentDictionary<string, Task<ImageSource>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static int _started;

    private static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        int n = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(() =>
            {
                foreach (var job in Queue.GetConsumingEnumerable())
                    try { job(); } catch { }
            }) { IsBackground = true, Name = "PearUI.Icons" + i };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    /// <summary>
    /// Loads an icon on the icon workers (never blocks the UI). Instant when cached; retries a few times when the
    /// shell isn't ready yet, so icons don't stay blank. Completes with null only after every attempt failed.
    /// </summary>
    public static Task<ImageSource> GetAsync(string parsingName, int size = 128, bool useCache = true)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return Task.FromResult<ImageSource>(null);
        string key = parsingName + "|" + size;
        if (useCache && Cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);
        EnsureWorkers();
        string flightKey = useCache ? key : key + "|nocache";
        return InFlight.GetOrAdd(flightKey, k =>
        {
            var tcs = new TaskCompletionSource<ImageSource>();
            void Attempt(int n)
            {
                Queue.Add(() =>
                {
                    ImageSource img = null;
                    try { img = Get(parsingName, size, useCache, n > 0); } catch { }
                    if (img == null && n < 3)
                    {
                        Task.Delay(400 * (n + 1)).ContinueWith(t2 => Attempt(n + 1));
                        return;
                    }
                    InFlight.TryRemove(flightKey, out _);
                    tcs.TrySetResult(img);
                });
            }
            Attempt(0);
            return tcs.Task;
        });
    }

    /// <summary>Loads in the background and assigns to the image on the UI thread.</summary>
    public static void LoadInto(System.Windows.Controls.Image target, string parsingName, int size = 128, bool useCache = true)
    {
        var cached = useCache ? TryGetCached(parsingName, size) : null;
        if (cached != null) { target.Source = cached; return; }
        var ui = target.Dispatcher;
        GetAsync(parsingName, size, useCache).ContinueWith(t =>
        {
            if (t.Result != null) ui.BeginInvoke(() => target.Source = t.Result);
        });
    }

    /// <summary>Warms the cache (memory + disk) for many icons without blocking anything.</summary>
    public static void Prefetch(IEnumerable<string> sources, int size = 128)
    {
        foreach (var s in sources) if (!string.IsNullOrWhiteSpace(s)) _ = GetAsync(s, size);
    }

    // ---- persistent cache: icons survive restarts, so later launches are instant ----
    private static readonly string DiskDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearUI", "icons");

    private static string DiskPath(string key)
    {
        // "v3|": icons saved by older versions were lower quality and are not reused
        var h = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes("v3|" + key.ToLowerInvariant()));
        return System.IO.Path.Combine(DiskDir, Convert.ToHexString(h) + ".png");
    }

    private static ImageSource ReadDisk(string key)
    {
        try
        {
            var fi = new System.IO.FileInfo(DiskPath(key));
            if (!fi.Exists || DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromDays(5)) return null;
            using var fs = fi.OpenRead();
            var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var fr = dec.Frames[0];
            fr.Freeze();
            return fr;
        }
        catch { return null; }
    }

    private static void WriteDisk(string key, ImageSource img)
    {
        try
        {
            if (img is not BitmapSource bs) return;
            System.IO.Directory.CreateDirectory(DiskDir);
            string f = DiskPath(key), tmp = f + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var fs = System.IO.File.Create(tmp))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bs));
                enc.Save(fs);
            }
            System.IO.File.Move(tmp, f, true);
        }
        catch { }
    }

    /// <summary>Returns a frozen ImageSource (safe to use from any thread) or null.</summary>
    public static ImageSource Get(string parsingName, int size = 96, bool useCache = true) =>
        Get(parsingName, size, useCache, false);

    private static ImageSource Get(string parsingName, int size, bool useCache, bool lenient)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return null;
        string key = parsingName + "|" + size;
        if (useCache && Cache.TryGetValue(key, out var cached)) return cached;

        bool isIco = parsingName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(parsingName);
        ImageSource img = null;
        if (useCache && !isIco && !lenient && (img = ReadDisk(key)) != null) { Cache[key] = img; return img; }

        try
        {
            // First choice: the biggest picture stored in the program itself (usually 256 px). The shell's version is the backup.
            if (!isIco && size >= 96) img = HighResIcons.TryLoad(parsingName, minPixels: Math.Min(size, 128));
            img ??= isIco ? LoadIcoFile(parsingName)
                : Load(parsingName, size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK);
        }
        catch { }
        if (img == null && !isIco)
        {
            // Some items refuse "icon only": take whatever the shell has, then the classic API. Never leave it blank.
            try { img = Load(parsingName, size, SIIGBF_BIGGERSIZEOK); } catch { }
            if (img == null) try { img = LoadViaFileInfo(parsingName); } catch { }
            lenient = true;
        }
        if (img != null && useCache)
        {
            Cache[key] = img;
            if (!isIco && !lenient) WriteDisk(key, img);
        }
        return img;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO fi, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    /// <summary>Last resort for items the image factory can't handle.</summary>
    private static ImageSource LoadViaFileInfo(string path)
    {
        var fi = new SHFILEINFO();
        const uint SHGFI_ICON = 0x100;
        if (SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON) == IntPtr.Zero || fi.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        finally { DestroyIcon(fi.hIcon); }
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
