using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace PearUI.Native;

/// <summary>
/// Reads the biggest picture stored inside a program's own icon (usually 256 x 256) straight from the file, so large
/// screens get a sharp icon. Windows' shortcut icons and its icon cache often hold only 32 or 48 pixel versions.
/// Returns null when the program has nothing better, and the caller falls back to the shell's icon.
/// </summary>
internal static class HighResIcons
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private static readonly IntPtr RT_ICON = (IntPtr)3, RT_GROUP_ICON = (IntPtr)14;

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr h, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc proc, IntPtr param);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LoadResource(IntPtr module, IntPtr res);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr data);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr res);

    /// <summary>
    /// The biggest icon of a program, library, icon file or shortcut. <paramref name="minPixels"/> is the smallest picture worth
    /// using: anything smaller is left to the shell (which may still do better, for example for Store apps).
    /// </summary>
    public static BitmapSource TryLoad(string path, int minPixels)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            BitmapSource img = null;

            if (ext == ".lnk") img = FromShortcut(path);
            else if (ext is ".exe" or ".dll") img = FromResources(path, 0);
            return img != null && img.PixelWidth >= minPixels ? img : null;
        }
        catch { return null; }
    }

    // ---------- shortcuts ----------
    private static BitmapSource FromShortcut(string lnk)
    {
        if (!File.Exists(lnk)) return null;
        var (target, iconFile, iconIndex) = ReadShortcut(lnk);
        // The shortcut's own icon first, then the program it starts.
        foreach (var (file, index) in new[] { (iconFile, iconIndex), (target, 0) })
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            string f = Environment.ExpandEnvironmentVariables(file);
            if (!File.Exists(f)) continue;
            BitmapSource img = f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? FromIcoFile(f)
                : f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? FromResources(f, index)
                : null;
            if (img != null && img.PixelWidth >= 128) return img;
        }
        return null;
    }

    private static BitmapSource FromIcoFile(string file)
    {
        using var fs = File.OpenRead(file);
        var dec = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var best = dec.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
        best?.Freeze();
        return best;
    }

    // ---------- icons stored in a program ----------
    /// <param name="index">0, 1, 2… = the nth icon of the file; negative = the icon with that resource id (as in shortcuts).</param>
    private static BitmapSource FromResources(string file, int index)
    {
        IntPtr module = LoadLibraryEx(file, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (module == IntPtr.Zero) return null;
        try
        {
            // find the icon group
            IntPtr groupName = IntPtr.Zero;
            if (index < 0) groupName = (IntPtr)(-index);
            else
            {
                int n = 0; IntPtr found = IntPtr.Zero; bool have = false;
                EnumResourceNames(module, RT_GROUP_ICON, (m, t, name, p) =>
                {
                    if (n++ == index) { found = name; have = true; return false; }
                    return true;
                }, IntPtr.Zero);
                if (!have) return null;
                groupName = found;
            }

            IntPtr groupRes = FindResource(module, groupName, RT_GROUP_ICON);
            if (groupRes == IntPtr.Zero) return null;
            IntPtr groupData = LockResource(LoadResource(module, groupRes));
            uint groupLen = SizeofResource(module, groupRes);
            if (groupData == IntPtr.Zero || groupLen < 6) return null;

            // GRPICONDIR: reserved, type, count, then 14-byte entries (width, height, colours, reserved, planes, bits, size, id)
            int count = Marshal.ReadInt16(groupData, 4);
            int bestId = 0, bestSize = -1, bestBits = -1;
            for (int i = 0; i < count && 6 + (i + 1) * 14 <= groupLen; i++)
            {
                IntPtr e = groupData + 6 + i * 14;
                int w = Marshal.ReadByte(e, 0); if (w == 0) w = 256;
                int bits = Marshal.ReadInt16(e, 6);
                int id = Marshal.ReadInt16(e, 12) & 0xFFFF;
                if (w > bestSize || (w == bestSize && bits > bestBits)) { bestSize = w; bestBits = bits; bestId = id; }
            }
            if (bestId == 0) return null;

            IntPtr imgRes = FindResource(module, (IntPtr)bestId, RT_ICON);
            if (imgRes == IntPtr.Zero) return null;
            uint len = SizeofResource(module, imgRes);
            IntPtr p0 = LockResource(LoadResource(module, imgRes));
            if (p0 == IntPtr.Zero || len == 0) return null;
            var bytes = new byte[len];
            Marshal.Copy(p0, bytes, 0, (int)len);
            return Decode(bytes, bestSize);
        }
        finally { FreeLibrary(module); }
    }

    /// <summary>An icon picture is either a PNG or a bitmap without a file header: wrap it in a one-image .ico so WPF can read it.</summary>
    private static BitmapSource Decode(byte[] data, int size)
    {
        BitmapSource result;
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            using var ms = new MemoryStream(data);
            result = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        else
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)1);                 // ICONDIR
                w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(data.Length); w.Write(22);                                         // size and offset of the image
                w.Write(data);
            }
            ms.Position = 0;
            result = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        result.Freeze();
        return result;
    }

    // ---------- reading a .lnk ----------
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    private static (string Target, string IconFile, int IconIndex) ReadShortcut(string lnk)
    {
        object link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnk, 0);
            var l = (IShellLinkW)link;
            var target = new StringBuilder(520);
            var icon = new StringBuilder(520);
            l.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            l.GetIconLocation(icon, icon.Capacity, out int index);
            return (target.ToString(), icon.ToString(), index);
        }
        catch { return (null, null, 0); }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }
}
