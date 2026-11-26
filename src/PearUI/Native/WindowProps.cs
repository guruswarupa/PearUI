using System.Runtime.InteropServices;

namespace PearUI.Native;

/// <summary>Reads the AppUserModelID of a top-level window (identifies Store apps and grouped desktop apps).</summary>
public static class WindowProps
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(16)] public IntPtr pad; // keeps the struct 24 bytes on x64
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pv);

    private static PROPERTYKEY AppUserModelIdKey = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    public static string GetAppUserModelId(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
            try
            {
                if (store.GetValue(ref AppUserModelIdKey, out var pv) != 0) return null;
                try
                {
                    return pv.vt == 31 /*VT_LPWSTR*/ && pv.ptr != IntPtr.Zero ? Marshal.PtrToStringUni(pv.ptr) : null;
                }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }
}
