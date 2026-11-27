using System.Runtime.InteropServices;

namespace PearUI.Native;

/// <summary>
/// Windows virtual desktops through the shell's internal COM interfaces (the public IVirtualDesktopManager can only move a
/// window that belongs to the calling program). These interfaces are not documented and change between Windows builds, so the
/// layout below is the one of Windows 11 24H2 / 25H2 (build 26100 and newer); <see cref="IsSupported"/> says whether this PC has it.
/// </summary>
public static class VirtualDesktopApi
{
    // ---- the shell's service provider ----
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    private interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, out IntPtr obj);
    }

    // IVirtualDesktopManagerInternal, Windows 11 24H2 / 25H2 (build 26100+). Order of the methods is what matters here;
    // the layout is the one maintained by the VirtualDesktop project (github.com/MScholtes/VirtualDesktop).
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    private interface IVirtualDesktopManagerInternal
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int MoveViewToDesktop(IntPtr view, IntPtr desktop);
        [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
        [PreserveSig] int GetCurrentDesktop(out IntPtr desktop);
        [PreserveSig] int GetDesktops(out IntPtr desktops);
        [PreserveSig] int GetAdjacentDesktop(IntPtr from, int direction, out IntPtr desktop);
        [PreserveSig] int SwitchDesktop(IntPtr desktop);
        [PreserveSig] int SwitchDesktopAndMoveForegroundView(IntPtr desktop);
        [PreserveSig] int CreateDesktop(out IntPtr desktop);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    private interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IntPtr array);
        [PreserveSig] int GetViewsByZOrder(out IntPtr array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
    }

    private static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ManagerService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid ManagerIid = new("53F5CA0B-158F-4124-900C-057158060B27");
    private static readonly Guid ViewCollectionIid = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
    private static readonly Guid DesktopIid = new("3F07F4BE-B107-441A-AF0F-39D82529072C");

    private static IVirtualDesktopManagerInternal _manager;
    private static IApplicationViewCollection _views;
    private static bool _tried;

    /// <summary>True on the Windows builds this layout is known to match (Windows 11 24H2 and newer), and the shell answered.</summary>
    public static bool IsSupported
    {
        get
        {
            Init();
            return _manager != null && _views != null;
        }
    }

    private static void Init()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (Environment.OSVersion.Version.Build < 26100) return;   // other builds have other layouts: do not risk a bad call
            var type = Type.GetTypeFromCLSID(ImmersiveShell);
            if (type == null) return;
            var shell = (IServiceProvider)Activator.CreateInstance(type);

            Guid svc = ManagerService, iid = ManagerIid;
            if (shell.QueryService(ref svc, ref iid, out IntPtr pm) != 0 || pm == IntPtr.Zero) return;
            _manager = (IVirtualDesktopManagerInternal)Marshal.GetObjectForIUnknown(pm);
            Marshal.Release(pm);

            Guid svc2 = ViewCollectionIid, iid2 = ViewCollectionIid;
            if (shell.QueryService(ref svc2, ref iid2, out IntPtr pv) != 0 || pv == IntPtr.Zero) { _manager = null; return; }
            _views = (IApplicationViewCollection)Marshal.GetObjectForIUnknown(pv);
            Marshal.Release(pv);

            // Sanity check: the count must agree with Explorer's own list, otherwise the layout is not what we think it is.
            if (_manager.GetCount(out int n) != 0 || n < 1 || n > 64) { _manager = null; _views = null; }
        }
        catch { _manager = null; _views = null; }
    }

    public static int Count()
    {
        Init();
        return _manager != null && _manager.GetCount(out int n) == 0 ? n : 0;
    }

    /// <summary>The COM object of desktop number <paramref name="index"/> (0-based), or Zero. Release it with Marshal.Release.</summary>
    private static IntPtr DesktopAt(int index)
    {
        Log("calling GetDesktops");
        int hr = _manager.GetDesktops(out IntPtr arrayPtr);
        Log($"GetDesktops hr=0x{hr:X8} array={arrayPtr}");
        if (hr != 0 || arrayPtr == IntPtr.Zero) { LastStep = $"GetDesktops hr=0x{hr:X8} array={arrayPtr}"; return IntPtr.Zero; }
        try
        {
            var array = (IObjectArray)Marshal.GetObjectForIUnknown(arrayPtr);
            Log("calling array.GetCount");
            hr = array.GetCount(out uint count);
            Log($"array count={count} hr=0x{hr:X8}");
            if (hr != 0 || index < 0 || index >= count) { LastStep = $"array.GetCount hr=0x{hr:X8} count={count} index={index}"; return IntPtr.Zero; }
            Guid iid = DesktopIid;
            Log("calling array.GetAt");
            hr = array.GetAt((uint)index, ref iid, out IntPtr desktop);
            Log($"GetAt hr=0x{hr:X8} desktop={desktop}");
            if (hr != 0) LastStep = $"array.GetAt hr=0x{hr:X8}";
            return hr == 0 ? desktop : IntPtr.Zero;
        }
        finally { Marshal.Release(arrayPtr); }
    }

    /// <summary>Makes sure at least <paramref name="number"/> desktops exist.</summary>
    public static bool EnsureCount(int number)
    {
        if (!IsSupported) return false;
        for (int guard = 0; Count() < number && guard < 16; guard++)
        {
            if (_manager.CreateDesktop(out IntPtr created) != 0) return false;
            if (created != IntPtr.Zero) Marshal.Release(created);
        }
        return Count() >= number;
    }

    /// <summary>Shows desktop <paramref name="number"/> (1-based), creating it if needed.</summary>
    public static bool SwitchTo(int number)
    {
        if (!EnsureCount(number)) return false;
        IntPtr desktop = DesktopAt(number - 1);
        if (desktop == IntPtr.Zero) return false;
        try { return _manager.SwitchDesktop(desktop) == 0; }
        finally { Marshal.Release(desktop); }
    }

    /// <summary>What the last call did, for troubleshooting.</summary>
    public static string LastStep = "";

    private static void Log(string m) => LastStep = m;

    /// <summary>Moves a window (of any program) to desktop <paramref name="number"/> (1-based), creating the desktop if needed.</summary>
    public static bool MoveWindowTo(IntPtr hwnd, int number)
    {
        LastStep = "";
        if (!EnsureCount(number)) { LastStep = "EnsureCount failed"; return false; }
        IntPtr desktop = DesktopAt(number - 1);
        if (desktop == IntPtr.Zero) return false;   // LastStep says why
        IntPtr view = IntPtr.Zero;
        try
        {
            Log("calling GetViewForHwnd");
            int hr = _views.GetViewForHwnd(hwnd, out view);
            Log($"GetViewForHwnd hr=0x{hr:X8} view={view}");
            if (hr != 0 || view == IntPtr.Zero) { LastStep = $"GetViewForHwnd hr=0x{hr:X8} view={view}"; return false; }
            Log("calling MoveViewToDesktop");
            hr = _manager.MoveViewToDesktop(view, desktop);
            LastStep = $"MoveViewToDesktop hr=0x{hr:X8}";
            return hr == 0;
        }
        finally
        {
            if (view != IntPtr.Zero) Marshal.Release(view);
            Marshal.Release(desktop);
        }
    }
}
