using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PearUI.Core;
using PearUI.Native;
using PearUI.Shell;
using PearUI.Themes;

namespace PearUI.Terminal;

/// <summary>
/// PearUI's own terminal: a frosted-glass window that lets the wallpaper show through, with tabs, a
/// Terminal.app-style look and a command history that is kept between sessions.
/// </summary>
public sealed class TerminalWindow : Window
{
    private static TerminalWindow _instance;
    private static double _fontSize = 14;

    /// <summary>Opens the terminal, or brings the open one to the front. <paramref name="newTab"/> adds a tab to it.</summary>
    public static void ShowOrActivate(bool newTab = false)
    {
        if (_instance == null) { _instance = new TerminalWindow(); _instance.Show(); }
        else
        {
            if (!_instance.IsVisible) _instance.Show();
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            if (newTab) _instance.NewTab(ShellCatalog.Default());
        }
        _instance.Activate();
        _instance.FocusActive();
    }

    /// <summary>
    /// The Win+T behaviour: opens the terminal, or hides it again when it is the window in front.
    /// A hidden terminal keeps its tabs and running programs.
    /// </summary>
    public static void Toggle()
    {
        if (_instance != null && _instance.IsVisible && _instance.IsActive && _instance.WindowState != WindowState.Minimized) _instance.Hide();
        else ShowOrActivate();
    }

    private sealed class Tab
    {
        public TerminalView View;
        public Border Pill;
        public TextBlock Label;
        public string Title = "";
    }

    private readonly List<Tab> _tabs = new();
    private Tab _active;
    private readonly Grid _views = new();
    private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 2) };
    private readonly Border _history = new();
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 16, 0), Opacity = 0 };
    private FrameworkElement _toolbarShell;
    private readonly TextBox _search = new();
    private readonly StackPanel _historyList = new();

    private TerminalWindow()
    {
        Title = "Terminal";
        Width = 940;
        Height = 600;
        MinWidth = 440;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Shell.PearLogo.ApplyWindowIcon(this);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_tabBar, 0);
        Grid.SetRow(_views, 1);
        root.Children.Add(_tabBar);
        root.Children.Add(_views);
        BuildHistoryPanel();
        BuildToolbar(root);
        Grid.SetRow(_history, 1);
        root.Children.Add(_history);
        Content = root;

        SourceInitialized += (_, _) => ApplyLook();
        Loaded += (_, _) => NewTab(ShellCatalog.Default());
        PreviewKeyDown += OnWindowKey;
        Activated += (_, _) => FocusActive();
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnLookChanged;
            SettingsStore.Changed -= OnLookChanged;
            foreach (var t in _tabs.ToList()) t.View.Close();
            _instance = null;
        };
        ThemeManager.ThemeChanged += OnLookChanged;
        SettingsStore.Changed += OnLookChanged;
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    // ================= look =================
    private static bool Dark => ThemeManager.IsDark;

    private void ApplyLook()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return;
        Backdrop.SetDarkFrame(h, Dark);
        int opacity = Math.Clamp(SettingsStore.Current.TerminalOpacity, 30, 100);

        if (Dark && Backdrop.BlurAvailable)
        {
            // Frosted glass: the wallpaper is blurred behind the whole window and tinted to the theme.
            Win32.RemoveExStyle(h, Win32.WS_EX_LAYERED);
            // WPF paints an opaque surface under the window unless told otherwise, which would hide the blur.
            var src = HwndSource.FromHwnd(h);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32.DwmExtendFrameIntoClientArea(h, ref m);
            var tint = Color.FromArgb((byte)(opacity * 255 / 100), 0x0C, 0x0C, 0x10);
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                // Windows 11: the system's own acrylic backdrop (works with a normal title bar); our tint goes on top of it.
                int acrylic = 3; // DWMSBT_TRANSIENTWINDOW
                DwmSetWindowAttribute(h, 38 /*DWMWA_SYSTEMBACKDROP_TYPE*/, ref acrylic, 4);
                Background = new SolidColorBrush(tint);
            }
            else
            {
                Background = Brushes.Transparent;
                Backdrop.EnableBlur(h, tint);
            }
        }
        else
        {
            // A plain see-through window (the whole window is made a little transparent) instead of frosted glass.
            // Used when Windows' "Transparency effects" is off, and always for the light theme: Windows' glass treats dark
            // pixels as see-through, so dark text on a light window comes out pale and hard to read.
            if (!Dark)
            {
                int none = 1; // DWMSBT_NONE
                DwmSetWindowAttribute(h, 38, ref none, 4);
                var m = new Win32.MARGINS();
                Win32.DwmExtendFrameIntoClientArea(h, ref m);
            }
            Background = new SolidColorBrush(Dark ? Color.FromRgb(0x14, 0x14, 0x18) : Color.FromRgb(0xF7, 0xF7, 0xF8));
            // Light: the slider maps to 88-100 % so the text stays crisp; dark fallback: 60-100 %.
            int alphaPercent = Dark ? Math.Max(opacity, 60) : Math.Clamp(opacity + 28, 88, 100);
            Win32.AddExStyle(h, Win32.WS_EX_LAYERED);
            SetLayeredWindowAttributes(h, 0, (byte)(alphaPercent * 255 / 100), 0x2 /*LWA_ALPHA*/);
        }

        foreach (var t in _tabs) t.View.ApplyTheme(Dark ? TerminalView.Pro : TerminalView.Basic);
        RestyleChrome();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    private void OnLookChanged() => Dispatcher.BeginInvoke(ApplyLook);

    private static Color Ink(double alpha) => Dark ? Color.FromArgb((byte)(alpha * 255), 255, 255, 255) : Color.FromArgb((byte)(alpha * 255), 0, 0, 0);
    private static Brush Text => new SolidColorBrush(Dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1D, 0x1D, 0x1F));

    private void RestyleChrome()
    {
        foreach (var t in _tabs) StyleTab(t);
        _history.Background = new SolidColorBrush(Dark ? Color.FromArgb(0xE6, 0x1E, 0x1E, 0x24) : Color.FromArgb(0xEC, 0xF6, 0xF6, 0xF8));
        _history.BorderBrush = new SolidColorBrush(Ink(0.12));
        _search.Foreground = Text;
        _search.CaretBrush = Text;
        _search.Background = new SolidColorBrush(Ink(0.07));
        _search.BorderBrush = new SolidColorBrush(Ink(0.14));
        if (_history.Visibility == Visibility.Visible) FillHistory();
    }

    // ================= toolbar & shells =================
    /// <summary>Two small buttons in the top-right corner (shell picker and history) that appear when the pointer is over the window.</summary>
    private void BuildToolbar(Grid root)
    {
        Grid.SetRow(_toolbar, 1);
        _toolbarShell = ToolButton("", "New tab with another shell (Ctrl+Shift+N)", () => ShowShellMenu(_toolbarShell));
        _toolbar.Children.Add(_toolbarShell);
        _toolbar.Children.Add(ToolButton("", "Command history (Ctrl+Shift+H)", () => ToggleHistory()));
        root.Children.Add(_toolbar);

        MouseEnter += (_, _) => _toolbar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(140)));
        MouseLeave += (_, _) => _toolbar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
    }

    private FrameworkElement ToolButton(string glyph, string tip, Action click)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Width = 32, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, Child = icon, ToolTip = tip, Cursor = Cursors.Hand };
        icon.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding { Source = this, Path = new PropertyPath("Foreground") });
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Ink(0.14));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    /// <summary>Lists the shells found on this PC; choosing one opens a tab with it and makes it the one used next time.</summary>
    private void ShowShellMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor ?? this, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string current = ShellCatalog.Default().Id;
        foreach (var shell in ShellCatalog.All())
        {
            var def = shell;
            var item = new MenuItem { Header = def.Name, IsCheckable = false, IsChecked = def.Id == current, ToolTip = def.Exe };
            item.Click += (_, _) => { ShellCatalog.SaveDefault(def.Id); NewTab(def); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var refresh = new MenuItem { Header = "Look for shells again" };
        refresh.Click += (_, _) => ShellCatalog.Refresh();
        menu.Items.Add(refresh);
        menu.IsOpen = true;
    }

    /// <summary>
    /// When you type the name of a shell at the prompt (bash, zsh, fish, sh, pwsh…), PearUI Terminal
    /// starts with that shell the next time it opens.
    /// </summary>
    private void RememberTypedShell(string command)
    {
        string id = ShellCatalog.IdFromCommand(command);
        if (id == null || id == SettingsStore.Current.TerminalShell) return;
        var shell = ShellCatalog.Find(id);
        if (shell == null) return;
        ShellCatalog.SaveDefault(id);
        Shell.ShortcutToast.Show("", $"Terminal will start with {shell.Name} next time");
    }

    // ================= tabs =================
    private void NewTab(ShellDef shell)
    {
        var view = new TerminalView(shell) { HistoryProvider = SessionHistory };
        view.ApplyTheme(Dark ? TerminalView.Pro : TerminalView.Basic);
        view.SetFontSize(_fontSize);
        var tab = new Tab { View = view };

        tab.Label = new TextBlock
        {
            Text = shell.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170,
        };
        var close = new TextBlock { Text = "✕", FontSize = 10, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55, Cursor = Cursors.Hand };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseTab(tab); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(tab.Label);
        row.Children.Add(close);
        tab.Pill = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 5, 9, 5), Margin = new Thickness(0, 0, 6, 0) };
        tab.Pill.MouseLeftButtonDown += (_, _) => Select(tab);

        view.TitleChanged += t =>
        {
            tab.Title = t;
            tab.Label.Text = ShortTitle(t, shell.Name);
            if (_active == tab) Title = string.IsNullOrWhiteSpace(t) ? "Terminal" : t;
        };
        view.Exited += () => CloseTab(tab);
        view.CommandEntered += cmd =>
        {
            TerminalHistory.Add(cmd);
            if (_history.Visibility == Visibility.Visible) FillHistory();
            RememberTypedShell(cmd);
        };

        _tabs.Add(tab);
        _views.Children.Add(view);
        RebuildTabBar();
        Select(tab);
    }

    private static string ShortTitle(string t, string fallback)
    {
        if (string.IsNullOrWhiteSpace(t)) return fallback;
        var name = System.IO.Path.GetFileName(t.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? t : name;
    }

    private void Select(Tab tab)
    {
        _active = tab;
        foreach (var t in _tabs) { t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Hidden; StyleTab(t); }
        Title = string.IsNullOrWhiteSpace(tab.Title) ? "Terminal" : tab.Title;
        FocusActive();
    }

    private void StyleTab(Tab t)
    {
        bool on = t == _active;
        t.Pill.Background = new SolidColorBrush(Ink(on ? 0.16 : 0.06));
        var ink = Dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1D, 0x1D, 0x1F);
        t.Label.Foreground = new SolidColorBrush(Color.FromArgb((byte)(on ? 255 : 150), ink.R, ink.G, ink.B));
        ((TextBlock)((StackPanel)t.Pill.Child).Children[1]).Foreground = Text;
    }

    private void CloseTab(Tab tab)
    {
        if (!_tabs.Remove(tab)) return;
        _views.Children.Remove(tab.View);
        tab.View.Close();
        if (_tabs.Count == 0) { Close(); return; }
        RebuildTabBar();
        if (_active == tab) Select(_tabs[^1]);
    }

    private void RebuildTabBar()
    {
        _tabBar.Children.Clear();
        _tabBar.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in _tabs) _tabBar.Children.Add(t.Pill);
        var add = new TextBlock { Text = "+", FontSize = 18, Margin = new Thickness(6, -3, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Foreground = Text, Opacity = 0.6 };
        add.MouseLeftButtonUp += (_, _) => NewTab(ShellCatalog.Default());
        _tabBar.Children.Add(add);
    }

    private void Cycle(int dir)
    {
        if (_tabs.Count < 2) return;
        int i = _tabs.IndexOf(_active);
        Select(_tabs[(i + dir + _tabs.Count) % _tabs.Count]);
    }

    private void FocusActive()
    {
        if (_active != null && _history.Visibility != Visibility.Visible) Keyboard.Focus(_active.View);
    }

    // ================= history =================
    private static IReadOnlyList<string> SessionHistory() =>
        TerminalHistory.All().Select(e => e.Text).Distinct().ToList();

    private void BuildHistoryPanel()
    {
        _history.Width = 340;
        _history.HorizontalAlignment = HorizontalAlignment.Right;
        _history.BorderThickness = new Thickness(1, 0, 0, 0);
        _history.Visibility = Visibility.Collapsed;
        _history.RenderTransform = new TranslateTransform(24, 0);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _search.Padding = new Thickness(9, 6, 9, 6);
        _search.FontSize = 13;
        _search.Margin = new Thickness(12, 12, 8, 10);
        _search.TextChanged += (_, _) => { if (_history.Visibility == Visibility.Visible) FillHistory(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { ToggleHistory(false); e.Handled = true; } };

        var clear = new Button { Content = "Clear", Margin = new Thickness(0, 12, 12, 10), Padding = new Thickness(12, 5, 12, 5), FontSize = 12, Cursor = Cursors.Hand };
        clear.Click += (_, _) => { TerminalHistory.Clear(); FillHistory(); };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(clear, 1);
        head.Children.Add(_search);
        head.Children.Add(clear);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _historyList, Margin = new Thickness(6, 0, 6, 6) };
        Grid.SetRow(scroll, 1);

        var foot = new TextBlock { Text = "Click a command to put it on the prompt", FontSize = 11.5, Margin = new Thickness(14, 6, 14, 10), Opacity = 0.55 };
        Grid.SetRow(foot, 2);

        layout.Children.Add(head);
        layout.Children.Add(scroll);
        layout.Children.Add(foot);
        _history.Child = layout;
    }

    private void ToggleHistory(bool? show = null)
    {
        bool open = show ?? _history.Visibility != Visibility.Visible;
        if (open)
        {
            _search.Text = "";
            _history.Visibility = Visibility.Visible;
            FillHistory();
            foot_Color();
            ((TranslateTransform)_history.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _history.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            _search.Focus();
        }
        else
        {
            _history.Visibility = Visibility.Collapsed;
            FocusActive();
        }
    }

    /// <summary>The small footer line follows the theme's text colour.</summary>
    private void foot_Color()
    {
        if (_history.Child is Grid g && g.Children.Count > 2 && g.Children[2] is TextBlock foot) foot.Foreground = Text;
    }

    private void FillHistory()
    {
        _historyList.Children.Clear();
        string q = _search.Text.Trim();
        var items = TerminalHistory.All().Where(e => q.Length == 0 || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(300).ToList();
        if (items.Count == 0)
        {
            _historyList.Children.Add(new TextBlock
            {
                Text = q.Length > 0 ? "No matching commands" : "Commands you run will show up here",
                Foreground = Text, Opacity = 0.55, FontSize = 13, Margin = new Thickness(10, 24, 10, 0), TextAlignment = TextAlignment.Center,
            });
            return;
        }

        string last = "";
        foreach (var e in items)
        {
            string day = DayLabel(e.Time);
            if (day != last)
            {
                _historyList.Children.Add(new TextBlock { Text = day.ToUpperInvariant(), FontSize = 10.5, Margin = new Thickness(10, 12, 10, 4), Foreground = Text, Opacity = 0.5 });
                last = day;
            }
            string cmd = e.Text;
            var item = new Border
            {
                Child = new TextBlock
                {
                    Text = cmd, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Foreground = Text,
                    TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = cmd,
                },
                Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            };
            item.MouseEnter += (_, _) => item.Background = new SolidColorBrush(Ink(0.10));
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) =>
            {
                ToggleHistory(false);
                _active?.View.PutOnPrompt(cmd);
            };
            _historyList.Children.Add(item);
        }
    }

    private static string DayLabel(DateTime t)
    {
        var d = t.Date;
        if (d == DateTime.Today) return "Today";
        if (d == DateTime.Today.AddDays(-1)) return "Yesterday";
        return t.ToString("dddd, MMM d");
    }

    // ================= shortcuts =================
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && _history.Visibility == Visibility.Visible) { ToggleHistory(false); e.Handled = true; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return;

        var m = Keyboard.Modifiers;
        var mods = KeyMods.None;
        if ((m & ModifierKeys.Control) != 0) mods |= KeyMods.Ctrl;
        if ((m & ModifierKeys.Alt) != 0) mods |= KeyMods.Alt;
        if ((m & ModifierKeys.Shift) != 0) mods |= KeyMods.Shift;
        if ((m & ModifierKeys.Windows) != 0) mods |= KeyMods.Win;

        string id = TerminalKeys.ActionFor(Shortcuts.Format(mods, KeyInterop.VirtualKeyFromKey(key)));
        if (id == null && mods == KeyMods.Ctrl)
        {
            // the number pad also zooms
            id = key switch { Key.Add => "zoom-in", Key.Subtract => "zoom-out", Key.NumPad0 => "zoom-reset", _ => null };
        }
        if (id != null && RunTerminalAction(id)) e.Handled = true;
    }

    private bool RunTerminalAction(string id)
    {
        switch (id)
        {
            case "new-tab": NewTab(ShellCatalog.Default()); return true;
            case "new-powershell-tab":
                var ps = ShellCatalog.Find("pwsh") ?? ShellCatalog.Find("powershell");
                if (ps != null) NewTab(ps);
                return true;
            case "shell-menu": ShowShellMenu(_toolbarShell); return true;
            case "close-tab": if (_active != null) CloseTab(_active); return true;
            case "next-tab": Cycle(1); return true;
            case "previous-tab": Cycle(-1); return true;
            case "history": ToggleHistory(); return true;
            case "clear": _active?.View.ClearScreen(); return true;
            case "copy": _active?.View.CopySelection(); return true;
            case "paste": _active?.View.Paste(); return true;
            case "zoom-in": Zoom(1); return true;
            case "zoom-out": Zoom(-1); return true;
            case "zoom-reset": Zoom(0); return true;
        }
        return false;
    }

    private void Zoom(int dir)
    {
        _fontSize = dir == 0 ? 14 : Math.Clamp(_fontSize + dir, 8, 32);
        foreach (var t in _tabs) t.View.SetFontSize(_fontSize);
    }
}
