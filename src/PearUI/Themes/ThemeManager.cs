using System.Windows;
using System.Windows.Media;
using MacUI.Core;
using Microsoft.Win32;

namespace MacUI.Themes;

/// <summary>
/// Owns the color palette. Every control binds with DynamicResource so switching
/// light/dark/accent at runtime updates the whole UI instantly.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static Color Accent { get; private set; } = Color.FromRgb(0x0A, 0x84, 0xFF);

    // Tints laid over the acrylic blur (alpha = how much of the color covers the blurred background).
    // Kept light so the desktop really shows through, like macOS vibrancy.
    public static Color BarTint => IsDark ? Color.FromArgb(0x70, 0x1A, 0x1A, 0x1C) : Color.FromArgb(0x60, 0xF2, 0xF2, 0xF5);
    public static Color PopupTint => IsDark ? Color.FromArgb(0x58, 0x22, 0x22, 0x26) : Color.FromArgb(0x58, 0xF6, 0xF6, 0xF8);
    public static Color DockTint => IsDark ? Color.FromArgb(0xA8, 0x10, 0x10, 0x16) : Color.FromArgb(0x92, 0xF3, 0xF4, 0xF8);
    public static Color FullscreenTint => IsDark ? Color.FromArgb(0x58, 0x10, 0x10, 0x16) : Color.FromArgb(0x48, 0x20, 0x22, 0x2A);
    public static Color PopupBorderColor => IsDark ? Color.FromRgb(0x55, 0x55, 0x5A) : Color.FromRgb(0xC4, 0xC4, 0xC8);

    public static event Action ThemeChanged;

    private static readonly Dictionary<string, Color> AccentPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Blue"] = Color.FromRgb(0x0A, 0x84, 0xFF),
        ["Purple"] = Color.FromRgb(0xA0, 0x5A, 0xE0),
        ["Pink"] = Color.FromRgb(0xF0, 0x4F, 0x9A),
        ["Red"] = Color.FromRgb(0xF0, 0x44, 0x3A),
        ["Orange"] = Color.FromRgb(0xF7, 0x82, 0x1B),
        ["Yellow"] = Color.FromRgb(0xF5, 0xB4, 0x00),
        ["Green"] = Color.FromRgb(0x34, 0xB8, 0x4A),
        ["Graphite"] = Color.FromRgb(0x8C, 0x8C, 0x8C),
    };

    public static IEnumerable<string> AccentNames => new[] { "System" }.Concat(AccentPresets.Keys);

    /// <summary>The named accent colors (for color pickers).</summary>
    public static IReadOnlyList<(string Name, Color Color)> AccentChoices =>
        AccentPresets.Select(p => (p.Key, p.Value)).ToList();

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
        Apply();
    }

    public static void Apply()
    {
        var s = SettingsStore.Current;
        IsDark = s.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemStatus.IsSystemDark(),
        };
        Accent = AccentPresets.TryGetValue(s.Accent ?? "", out var c) ? c : SystemStatus.GetSystemAccent() ?? AccentPresets["Blue"];

        bool blur = Native.Backdrop.BlurAvailable;
        var r = Application.Current.Resources;

        Color text = IsDark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F);
        Color secondary = IsDark ? Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0x8C, 0x3C, 0x3C, 0x43);

        Set(r, "Brush.Text", text);
        Set(r, "Brush.SecondaryText", secondary);
        Set(r, "Brush.Accent", Accent);
        Set(r, "Brush.AccentText", Colors.White);

        // Glass surfaces: the blur + tint come from DWM, WPF only adds a whisper of color.
        Set(r, "Brush.BarGlass", Color.FromArgb(0x00, 0, 0, 0));
        Set(r, "Brush.PopupGlass", Color.FromArgb(0x00, 0, 0, 0));
        Set(r, "Brush.FullscreenGlass", Color.FromArgb(0x00, 0, 0, 0));
        // Fallback surfaces (no blur available): see-through, not opaque.
        Set(r, "Brush.BarBackground", IsDark ? Color.FromArgb(0xC8, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0xC0, 0xF2, 0xF2, 0xF4));
        Set(r, "Brush.PopupBackground", IsDark ? Color.FromArgb(0xE6, 0x26, 0x26, 0x29) : Color.FromArgb(0xE6, 0xF5, 0xF5, 0xF7));
        Set(r, "Brush.FullscreenBackground", Color.FromArgb(0xC8, 0x14, 0x14, 0x1A));
        // Context menus have no blur, keep them nearly opaque.
        Set(r, "Brush.Menu", IsDark ? Color.FromArgb(0xF5, 0x2B, 0x2B, 0x2D) : Color.FromArgb(0xF7, 0xF7, 0xF7, 0xF7));
        Set(r, "Brush.PopupBorder", IsDark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        Set(r, "Brush.Separator", IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        Set(r, "Brush.Hover", IsDark ? Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
        Set(r, "Brush.Pressed", IsDark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2C, 0x00, 0x00, 0x00));
        Set(r, "Brush.Tile", IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Set(r, "Brush.TileIcon", IsDark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00));
        Set(r, "Brush.Field", IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        Set(r, "Brush.Track", IsDark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        Set(r, "Brush.SliderTrack", IsDark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Set(r, "Brush.SliderEdge", IsDark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        Set(r, "Brush.Window", IsDark ? Color.FromRgb(0x1E, 0x1E, 0x20) : Color.FromRgb(0xF2, 0xF2, 0xF4));
        Set(r, "Brush.Card", IsDark ? Color.FromRgb(0x2A, 0x2A, 0x2D) : Colors.White);
        Set(r, "Brush.Sidebar", IsDark ? Color.FromRgb(0x26, 0x26, 0x28) : Color.FromRgb(0xE8, 0xE8, 0xEB));

        // Dock plate: drawn by WPF on top of a separate blurred window.
        Set(r, "Brush.DockPlate", blur
            ? (IsDark ? Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF))
            : (IsDark ? Color.FromArgb(0xD2, 0x1B, 0x1B, 0x21) : Color.FromArgb(0xDA, 0xF1, 0xF2, 0xF6)));
        Set(r, "Brush.DockBorder", IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF));
        Set(r, "Brush.DockIndicator", IsDark ? Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xA0, 0x00, 0x00, 0x00));
        Set(r, "Brush.Tooltip", IsDark ? Color.FromArgb(0xF0, 0x3A, 0x3A, 0x3C) : Color.FromArgb(0xF0, 0xEC, 0xEC, 0xEC));

        ThemeChanged?.Invoke();
    }

    private static void Set(ResourceDictionary r, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        r[key] = b;
    }
}
