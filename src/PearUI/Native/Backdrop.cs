using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace PearUI.Native;

/// <summary>
/// Frosted-glass backgrounds via SetWindowCompositionAttribute (undocumented but stable since Windows 10).
/// Unlike Mica / DWMSBT backdrops it keeps blurring while the window is inactive — essential for the
/// menu bar and Dock, which never take focus.
///
/// Blur only renders on NON-layered windows (WPF AllowsTransparency=false) and only while the Windows
/// "Transparency effects" setting is on; <see cref="BlurAvailable"/> reports that.
/// </summary>
public static class Backdrop
{
    private enum AccentState
    {
        Disabled = 0,
        Gradient = 1,
        TransparentGradient = 2,
        BlurBehind = 3,
        AcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor; // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute; // WCA_ACCENT_POLICY = 19
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>Windows "Transparency effects" (Settings → Personalization → Colors).</summary>
    public static bool SystemTransparencyEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("EnableTransparency") is not int v || v != 0;
            }
            catch { return true; }
        }
    }

    public static void SetSystemTransparency(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            k.SetValue("EnableTransparency", on ? 1 : 0, RegistryValueKind.DWord);
            Win32.SendMessageTimeout(new IntPtr(0xFFFF), Win32.WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 500, out _);
        }
        catch { }
    }

    /// <summary>True when real blur can be shown: user enabled it in PearUI and Windows allows it.</summary>
    public static bool BlurAvailable => Core.SettingsStore.Current.UseBlur && SystemTransparencyEnabled;

    /// <summary>Acrylic blur behind the window. tint alpha controls how strongly the color covers the blur.</summary>
    public static void EnableBlur(IntPtr hwnd, Color tint, bool acrylic = true) => Apply(hwnd, new AccentPolicy
    {
        AccentState = acrylic ? AccentState.AcrylicBlurBehind : AccentState.BlurBehind,
        AccentFlags = 2,
        GradientColor = ToAbgr(tint),
    });

    /// <summary>A fully see-through (non-layered) window: the desktop shows through unblurred.</summary>
    public static void EnableClear(IntPtr hwnd) => Apply(hwnd, new AccentPolicy
    {
        AccentState = AccentState.TransparentGradient,
        AccentFlags = 2,
        GradientColor = 0,
    });

    public static void Disable(IntPtr hwnd) => Apply(hwnd, new AccentPolicy { AccentState = AccentState.Disabled });

    private static uint ToAbgr(Color c) => (uint)(c.A << 24 | c.B << 16 | c.G << 8 | c.R);

    private static void Apply(IntPtr hwnd, AccentPolicy policy)
    {
        int size = Marshal.SizeOf(policy);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch { /* unsupported: the fallback brush still looks fine */ }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Clip a (non-layered) window to a rounded rectangle, in device pixels.</summary>
    public static void SetRoundedRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx)
    {
        IntPtr rgn = Win32.CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, radiusPx * 2, radiusPx * 2);
        if (Win32.SetWindowRgn(hwnd, rgn, true) == 0) Win32.DeleteObject(rgn);
    }

    /// <summary>Native Windows 11 window corners and border.</summary>
    public static void SetCorners(IntPtr hwnd, bool round, Color? border)
    {
        int pref = round ? Win32.DWMWCP_ROUND : Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        int color = border is Color c ? (c.B << 16 | c.G << 8 | c.R) : Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }

    public static void SetDarkFrame(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }
}
