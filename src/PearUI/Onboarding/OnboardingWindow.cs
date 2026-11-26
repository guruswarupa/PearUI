using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PearUI.Core;
using PearUI.Native;
using PearUI.Shell;
using PearUI.Themes;
using Forms = System.Windows.Forms;

namespace PearUI.Onboarding;

/// <summary>
/// The welcome tour shown the first time PearUI starts: a frosted full-screen overlay with a centered card,
/// a handful of short pages and a quick "make it yours" step (appearance, accent color, three switches).
/// Everything applies live; Skip, Esc or Get Started all mark the tour as done. Reopen it from the Pear menu.
/// </summary>
public sealed class OnboardingWindow : OverlayWindow
{
    private const double CardW = 700, CardH = 500;

    private readonly List<Func<UIElement>> _pages = new();
    private readonly ContentPresenter _host = new();
    private readonly Border _card = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back, _next, _skip;
    private readonly ScaleTransform _cardScale = new(0.94, 0.94);
    private int _index = -1;
    private bool _finishing;

    private static AppSettings S => SettingsStore.Current;

    public OnboardingWindow() : base(SurfaceKind.Fullscreen, noActivate: false)
    {
        Title = "PearUI Welcome";

        _back = FlatButton("Back", () => Step(-1));
        _next = AccentButton("Continue", () => { if (_index == _pages.Count - 1) Finish(); else Step(1); });
        _skip = FlatButton("Skip", Finish);
        _skip.Opacity = 0.75;

        BuildPages();
        Content = BuildShell();

        PreviewKeyDown += OnKey;
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    // ===================== opening / closing =====================
    public void Open()
    {
        var screen = Forms.Screen.PrimaryScreen;
        double s = Win32.GetScaleForScreen(screen);
        // Everything except the menu bar, like the All Apps screen.
        var full = screen.Bounds;
        int top = SettingsStore.Current.MenuBarEnabled ? (int)Math.Ceiling(MenuBar.MenuBarWindow.BarHeight * s) : 0;
        Width = full.Width / s;
        Height = (full.Height - top) / s;

        Opacity = 0;
        Show();
        PlacePx(full.Left, full.Top + top, full.Width, full.Height - top);
        Activate();
        Win32.ActivateWindow(Handle);
        Focus();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
        Go(0, 0);
    }

    private void Finish()
    {
        if (_finishing) return;
        _finishing = true;
        S.OnboardingDone = true;
        SettingsStore.Save(raiseEvent: false);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    // ===================== shell =====================
    private UIElement BuildShell()
    {
        var root = new Grid { Background = Brushes.Transparent };

        // Soft colored glows behind the card, for depth.
        var glows = new Canvas { IsHitTestVisible = false };
        glows.Children.Add(Glow(560, 0.22, new Point(0.14, 0.20), () => ThemeManager.Accent));
        glows.Children.Add(Glow(480, 0.14, new Point(0.86, 0.82), () => ThemeManager.Accent));
        root.Children.Add(glows);

        // Card
        _card.Width = CardW;
        _card.Height = CardH;
        _card.CornerRadius = new CornerRadius(28);
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.VerticalAlignment = VerticalAlignment.Center;
        _card.BorderThickness = new Thickness(1);
        _card.RenderTransform = _cardScale;
        _card.RenderTransformOrigin = new Point(0.5, 0.5);
        _card.Effect = new DropShadowEffect { BlurRadius = 70, ShadowDepth = 18, Direction = 270, Opacity = 0.34 };
        RefreshCard();

        var layout = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.Children.Add(_host);

        // Skip, top-right
        _skip.HorizontalAlignment = HorizontalAlignment.Right;
        _skip.VerticalAlignment = VerticalAlignment.Top;
        _skip.Margin = new Thickness(0, 16, 18, 0);
        layout.Children.Add(_skip);

        // Footer: Back | dots | Continue
        var footer = new Grid { Margin = new Thickness(28, 6, 28, 26) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _next.HorizontalAlignment = HorizontalAlignment.Right;
        _next.MinWidth = 130;
        Grid.SetColumn(_dots, 1);
        Grid.SetColumn(_next, 2);
        footer.Children.Add(_back);
        footer.Children.Add(_dots);
        footer.Children.Add(_next);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);

        _card.Child = layout;
        root.Children.Add(_card);
        return root;
    }

    private static UIElement Glow(double size, double opacity, Point at, Func<Color> color)
    {
        var e = new Ellipse
        {
            Width = size, Height = size, Opacity = opacity, IsHitTestVisible = false,
            Fill = new SolidColorBrush(color()),
            Effect = new BlurEffect { Radius = 150, KernelType = KernelType.Gaussian },
        };
        // Position as a fraction of the window once it is laid out.
        e.Loaded += (_, _) =>
        {
            if (e.Parent is Canvas c && Window.GetWindow(e) is Window w)
            {
                Canvas.SetLeft(e, w.ActualWidth * at.X - size / 2);
                Canvas.SetTop(e, w.ActualHeight * at.Y - size / 2);
            }
        };
        return e;
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(RefreshCard);

    /// <summary>Frosted card: translucent so the blur behind the window shows through.</summary>
    private void RefreshCard()
    {
        bool dark = ThemeManager.IsDark;
        _card.Background = new SolidColorBrush(dark ? Color.FromArgb(0xD2, 0x26, 0x26, 0x2A) : Color.FromArgb(0xDC, 0xFB, 0xFB, 0xFD));
        _card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    }

    // ===================== navigation =====================
    private void Step(int delta)
    {
        int next = Math.Clamp(_index + delta, 0, _pages.Count - 1);
        if (next != _index) Go(next, delta);
    }

    private void Go(int index, int direction)
    {
        _index = index;
        var page = _pages[index]();
        var move = new TranslateTransform(direction * 40, 0);
        page.RenderTransform = move;
        page.Opacity = 0;
        _host.Content = page;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 40, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });

        bool last = index == _pages.Count - 1;
        _back.Visibility = index == 0 ? Visibility.Hidden : Visibility.Visible;
        _skip.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = last ? "Get Started" : index == 0 ? "Let's go" : "Continue";
        BuildDots();
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        for (int i = 0; i < _pages.Count; i++)
        {
            bool active = i == _index;
            var dot = new Border
            {
                Width = active ? 22 : 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(3.5, 0, 3.5, 0),
                Opacity = active ? 1 : 0.35,
            };
            if (active) dot.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            else dot.SetResourceReference(Border.BackgroundProperty, "Brush.Text");
            _dots.Children.Add(dot);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right: case Key.Enter: case Key.Space:
                if (_index == _pages.Count - 1 && e.Key != Key.Right) Finish(); else Step(1);
                e.Handled = true; break;
            case Key.Left: Step(-1); e.Handled = true; break;
            case Key.Escape: Finish(); e.Handled = true; break;
        }
    }

    // ===================== pages =====================
    private void BuildPages()
    {
        _pages.Add(() => Page(Hero("pear"),
            "Welcome to PearUI",
            "A calm, Mac-inspired desktop for Windows: a menu bar, a Dock, instant search and more. " +
            "Let's take a quick look around. It takes less than a minute."));

        _pages.Add(() => Page(Hero("dock"),
            "Your menu bar and Dock",
            "The menu bar on top shows the active app, battery, Wi‑Fi, volume and the clock. " +
            "The Dock hides until you push your pointer to the bottom edge. Hover over it and the icons grow. " +
            "Right-click any icon for more.",
            Hint("", "Dock", "Push the pointer to the bottom edge of the screen")));

        _pages.Add(() => Page(Hero("search"),
            "Find anything, fast",
            "Search apps, files, settings and even do quick math. Or see every app on one screen and organize them into folders.",
            Shortcut(KeysOf(S.LauncherHotkey), "Search"),
            Shortcut(new[] { "Win" }, "All Apps"),
            Shortcut(KeysOf(S.AllAppsHotkey), "All Apps")));

        _pages.Add(() => Page(Hero("control"),
            "Control Center",
            "Click the toggle icon in the menu bar for volume, brightness, dark mode and media. " +
            "Click Wi‑Fi or Bluetooth to turn them on or off, and right-click them to pick a network or device.",
            Hint("", "Wi‑Fi and Bluetooth", "Left-click turns on or off, right-click chooses")));

        _pages.Add(() => Page(Hero("stage"),
            "Stage Manager and gestures",
            "Turn on Stage Manager in Control Center to keep one app in focus. Push the pointer to the left edge to bring the others back. " +
            "In All Apps, swipe with two fingers to change page and drag one app onto another to make a folder.",
            Hint("", "Hot corners", "Bottom-left: all windows. Bottom-right: show the desktop")));

        _pages.Add(PersonalizePage);

        _pages.Add(() => Page(Hero("check"),
            "You're all set",
            "Everything is ready. You can change any of this later from the pear menu → PearUI Settings, " +
            "and bring this tour back from the same menu."));
    }

    private UIElement Page(UIElement hero, string title, string body, params UIElement[] extras)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(54, 22, 54, 0) };
        hero.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        s.Children.Add(hero);

        var t = new TextBlock { Text = title, FontSize = 29, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        s.Children.Add(t);

        var b = new TextBlock { Text = body, FontSize = 15, LineHeight = 23, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520, Margin = new Thickness(0, 12, 0, 0) };
        b.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        s.Children.Add(b);

        foreach (var x in extras)
        {
            x.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 14, 0, 0));
            x.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            s.Children.Add(x);
        }
        return s;
    }

    // ----- hero icons: simple line drawings in the accent color on a soft tint (no gradients, no glow) -----
    private static Geometry RR(double x, double y, double w, double h, double r) => new RectangleGeometry(new Rect(x, y, w, h), r, r);
    private static Geometry Dot(double cx, double cy, double r) => new EllipseGeometry(new Point(cx, cy), r, r);
    private static Geometry Seg(double x1, double y1, double x2, double y2) => new LineGeometry(new Point(x1, y1), new Point(x2, y2));
    private static GeometryGroup Group(params Geometry[] parts)
    {
        var g = new GeometryGroup();
        foreach (var p in parts) g.Children.Add(p);
        return g;
    }

    /// <summary>A 48×48 line icon by name, centered in a soft accent-tinted disc.</summary>
    private static UIElement Hero(string name)
    {
        Geometry outline, filled = null;
        switch (name)
        {
            case "dock":      // screen with a menu bar and a Dock
                outline = Group(RR(6, 8, 36, 27, 4.5), Seg(6, 14.5, 42, 14.5), RR(14, 39, 20, 5, 2.5));
                break;
            case "search":
                outline = Group(Dot(21, 21, 12), Seg(29.8, 29.8, 39, 39));
                break;
            case "control":   // two toggles, like the Control Center icon in the menu bar
                outline = Group(RR(8, 9.5, 32, 12, 6), RR(8, 26.5, 32, 12, 6));
                filled = Group(Dot(33.5, 15.5, 3.4), Dot(14.5, 32.5, 3.4));
                break;
            case "stage":     // one app on stage, three waiting at the side
                outline = Group(RR(18, 9, 24, 30, 3.5), RR(6, 11, 7, 8, 2), RR(6, 20, 7, 8, 2), RR(6, 29, 7, 8, 2));
                break;
            case "check":
                outline = Group(Dot(24, 24, 17), Seg(15.5, 24.5, 21.5, 30.5), Seg(21.5, 30.5, 33, 18));
                break;
            default:          // "pear": the PearUI mark in a flat, muted green
                return Disc(Color.FromRgb(0x6F, 0x9A, 0x3D), new Path
                {
                    Data = PearLogo.CreateGeometry(), Fill = new SolidColorBrush(Color.FromRgb(0x6F, 0x9A, 0x3D)),
                    Stretch = Stretch.Uniform, Width = 46, Height = 46,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
        }

        var stroke = new Path
        {
            Data = outline, StrokeThickness = 2.3, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        stroke.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        var canvas = new Grid { Width = 48, Height = 48 };
        canvas.Children.Add(stroke);
        if (filled != null)
        {
            var fill = new Path { Data = filled };
            fill.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            canvas.Children.Add(fill);
        }
        return Disc(ThemeManager.Accent, new Viewbox { Width = 52, Height = 52, Child = canvas });
    }

    private static Border Disc(Color tint, UIElement content) => new()
    {
        Width = 104, Height = 104, CornerRadius = new CornerRadius(52),
        Background = new SolidColorBrush(Color.FromArgb(0x26, tint.R, tint.G, tint.B)),
        Child = content,
    };

    // ----- small building blocks -----
    private static string[] KeysOf(string combo) =>
        (combo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Border KeyCap(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 5), MinWidth = 34, BorderThickness = new Thickness(1), Child = t, Margin = new Thickness(0, 0, 0, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Field");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Separator");
        t.TextAlignment = TextAlignment.Center;
        return b;
    }

    private static UIElement Shortcut(string[] keys, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                var plus = new TextBlock { Text = "+", Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 };
                plus.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                row.Children.Add(plus);
            }
            row.Children.Add(KeyCap(keys[i]));
        }
        var l = new TextBlock { Text = "   " + label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        row.Children.Add(l);
        return row;
    }

    private static UIElement Hint(string glyph, string title, string text)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        var t1 = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold };
        t1.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var t2 = new TextBlock { Text = text, FontSize = 12.5 };
        t2.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(t1);
        col.Children.Add(t2);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(col);
        var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 18, 10), Child = row };
        chip.SetResourceReference(Border.BackgroundProperty, "Brush.Field");
        return chip;
    }

    // ===================== "Make it yours" =====================
    private UIElement PersonalizePage()
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(54, 26, 54, 0), Width = 520 };

        var title = new TextBlock { Text = "Make it yours", FontSize = 29, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        s.Children.Add(title);

        var sub = new TextBlock { Text = "Changes apply right away. You can adjust them any time in Settings.", FontSize = 14, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 20) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        s.Children.Add(sub);

        // Appearance
        s.Children.Add(SettingRow("Appearance", Segmented(new[] { "System", "Light", "Dark" }, () => S.Theme ?? "System", v =>
        {
            S.Theme = v;
            SettingsStore.Save();
        })));

        // Accent color
        s.Children.Add(SettingRow("Accent color", Swatches()));

        s.Children.Add(Divider());
        s.Children.Add(SwitchRow("Start PearUI as soon as I sign in", S.StartWithWindows, v => { S.StartWithWindows = v; SettingsStore.Save(); }));
        s.Children.Add(SwitchRow("Windows key opens All Apps instead of Start", S.ReplaceStartMenu, v => { S.ReplaceStartMenu = v; SettingsStore.Save(); }));
        s.Children.Add(SwitchRow("Hide the Windows taskbar", S.HideTaskbar, v => { S.HideTaskbar = v; SettingsStore.Save(); }));
        return s;
    }

    private static UIElement Divider()
    {
        var d = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 6) };
        d.SetResourceReference(Border.BackgroundProperty, "Brush.Separator");
        return d;
    }

    private static UIElement SettingRow(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6), MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        control.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        Grid.SetColumn(control, 1);
        g.Children.Add(l);
        g.Children.Add(control);
        return g;
    }

    private static UIElement SwitchRow(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 7, 0, 7), FontSize = 14.5, Style = (Style)Application.Current.FindResource("Switch") };
        cb.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        cb.Click += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    private static UIElement Segmented(string[] options, Func<string> get, Action<string> set)
    {
        var host = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(3) };
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Field");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        host.Child = panel;
        var buttons = new List<(Border Pill, TextBlock Text, string Value)>();

        void Refresh()
        {
            string cur = get();
            foreach (var (pill, text, value) in buttons)
            {
                bool on = value == cur;
                if (on) pill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent"); else pill.Background = Brushes.Transparent;
                if (on) text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText"); else text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            }
        }

        foreach (var opt in options)
        {
            var t = new TextBlock { Text = opt, FontSize = 13.5, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center };
            var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 6, 16, 7), MinWidth = 72, Child = t, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            string value = opt;
            pill.MouseLeftButtonUp += (_, _) => { set(value); Refresh(); };
            buttons.Add((pill, t, value));
            panel.Children.Add(pill);
        }
        Refresh();
        return host;
    }

    private static UIElement Swatches()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var rings = new List<(Ellipse Ring, string Name)>();

        void Refresh()
        {
            string cur = S.Accent ?? "System";
            foreach (var (ring, name) in rings)
            {
                bool on = name == cur || (cur == "System" && name == "Blue");
                ring.StrokeThickness = on ? 2.4 : 0;
            }
        }

        foreach (var (name, color) in ThemeManager.AccentChoices)
        {
            var dot = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(color) };
            var ring = new Ellipse { Width = 30, Height = 30, Fill = Brushes.Transparent, StrokeThickness = 0 };
            ring.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
            var g = new Grid { Width = 30, Height = 30, Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = name };
            g.Children.Add(ring);
            g.Children.Add(dot);
            string n = name;
            g.MouseLeftButtonUp += (_, _) => { S.Accent = n; SettingsStore.Save(); Refresh(); };
            rings.Add((ring, name));
            panel.Children.Add(g);
        }
        Refresh();
        return panel;
    }

    // ===================== buttons =====================
    private static Button FlatButton(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("FlatButton"), Background = Brushes.Transparent, Padding = new Thickness(14, 7, 14, 8), FontSize = 14 };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button AccentButton(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("AccentButton"), Padding = new Thickness(22, 8, 22, 9), FontSize = 14, FontWeight = FontWeights.SemiBold };
        b.Click += (_, _) => click();
        return b;
    }
}
