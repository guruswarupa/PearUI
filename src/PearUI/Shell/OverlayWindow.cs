using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using PearUI.Native;
using PearUI.Themes;

namespace PearUI.Shell;

/// <summary>How a shell window draws its background.</summary>
public enum SurfaceKind
{
    /// <summary>Per-pixel transparent (layered). Used by the Dock, which draws its own plate.</summary>
    Transparent,
    /// <summary>Frosted bar with square corners (menu bar).</summary>
    Bar,
    /// <summary>Frosted panel with native rounded corners and hairline border (search, Control Center).</summary>
    Popup,
    /// <summary>Frosted full-screen overlay (All Apps).</summary>
    Fullscreen,
    /// <summary>Non-layered but fully see-through (Stage Manager strip: needs DWM thumbnails).</summary>
    Clear,
}

/// <summary>
/// Base for every PearUI surface: borderless, topmost, hidden from Alt+Tab and the taskbar.
///
/// "Glass" surfaces are real non-layered windows: Windows draws acrylic blur behind them and native,
/// anti-aliased rounded corners + border around them. When blur isn't available (Transparency effects
/// off) they fall back to layered windows with a semi-transparent background and WPF-drawn rounded corners.
/// The choice is made once, at construction; the app recreates windows when it changes.
/// </summary>
public class OverlayWindow : Window
{
    public SurfaceKind Kind { get; }
    public bool IsGlass { get; }
    public bool NoActivate { get; set; }
    /// <summary>Hidden from the taskbar and Alt+Tab (right for popups and bars). Off for ordinary windows such as Settings.</summary>
    public bool ToolWindow { get; set; } = true;
    /// <summary>Wrap the XAML content in a themed surface (background / corners). Off for Dock & backdrops.</summary>
    public bool WrapContent { get; set; } = true;
    public double FallbackCornerRadius { get; set; } = 12;

    private Border _surface;

    public OverlayWindow() : this(SurfaceKind.Transparent, true) { }

    /// <param name="useChrome">
    /// WindowChrome extends the glass frame cleanly but resets any window region on every move.
    /// Windows that clip themselves with SetWindowRgn (the Dock backdrop) pass false.
    /// </param>
    public OverlayWindow(SurfaceKind kind, bool noActivate, bool useChrome = true)
    {
        Kind = kind;
        NoActivate = noActivate;
        IsGlass = kind == SurfaceKind.Clear || (kind != SurfaceKind.Transparent && Backdrop.BlurAvailable);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !noActivate;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = Brushes.Transparent;
        AllowsTransparency = !IsGlass;
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FontSize = 13;

        if (IsGlass && useChrome)
        {
            // Extend the (invisible) glass frame over the whole window so DWM effects show through.
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                GlassFrameThickness = new Thickness(-1),
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        }
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// Wraps the content in the themed surface. Runs once in OnSourceInitialized (just before the first
    /// show) — doing it from OnInitialized can re-enter a Content assignment that is still in progress.
    /// </summary>
    private void WrapIfNeeded()
    {
        if (_surface != null || Content == null) return;
        if (!WrapContent || Kind == SurfaceKind.Transparent || Kind == SurfaceKind.Clear) return;

        var content = Content as UIElement;
        Content = null;
        _surface = new Border { Child = content };
        string key = Kind switch
        {
            SurfaceKind.Bar => IsGlass ? "Brush.BarGlass" : "Brush.BarBackground",
            SurfaceKind.Popup => IsGlass ? "Brush.PopupGlass" : "Brush.PopupBackground",
            _ => IsGlass ? "Brush.FullscreenGlass" : "Brush.FullscreenBackground",
        };
        _surface.SetResourceReference(Border.BackgroundProperty, key);
        if (!IsGlass && Kind == SurfaceKind.Popup)
        {
            // Layered fallback: WPF draws smooth corners itself.
            _surface.CornerRadius = new CornerRadius(FallbackCornerRadius);
            _surface.BorderThickness = new Thickness(1);
            _surface.SetResourceReference(Border.BorderBrushProperty, "Brush.PopupBorder");
        }
        Content = _surface;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WrapIfNeeded();
        long flags = ToolWindow ? Win32.WS_EX_TOOLWINDOW : 0;
        if (NoActivate) flags |= Win32.WS_EX_NOACTIVATE;
        Win32.AddExStyle(Handle, flags);
        HwndSource.FromHwnd(Handle)?.AddHook(WndProc);

        if (IsGlass)
        {
            var src = HwndSource.FromHwnd(Handle);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32.DwmExtendFrameIntoClientArea(Handle, ref m);
            ApplyGlass();
            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(ApplyGlass);

    /// <summary>Tint laid over the blur; override for custom surfaces.</summary>
    protected virtual Color GlassTint => Kind switch
    {
        SurfaceKind.Bar => ThemeManager.BarTint,
        SurfaceKind.Popup => ThemeManager.PopupTint,
        SurfaceKind.Fullscreen => ThemeManager.FullscreenTint,
        _ => ThemeManager.DockTint,
    };

    protected virtual void ApplyGlass()
    {
        if (Handle == IntPtr.Zero) return;
        Backdrop.SetDarkFrame(Handle, ThemeManager.IsDark);
        if (Kind == SurfaceKind.Clear) Backdrop.EnableClear(Handle);
        else Backdrop.EnableBlur(Handle, GlassTint);
        Backdrop.SetCorners(Handle, Kind == SurfaceKind.Popup, Kind == SurfaceKind.Popup ? ThemeManager.PopupBorderColor : null);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (NoActivate && msg == Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Moves/resizes the window in device pixels.</summary>
    public void PlacePx(int x, int y, int w, int h, bool topmost = true)
    {
        Win32.SetWindowPos(Handle, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
    }

    public double DpiScale => Win32.GetScaleForWindow(Handle);
}
