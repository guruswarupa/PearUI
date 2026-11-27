using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PearUI.Core;
using PearUI.Native;

namespace PearUI.MenuBar;

/// <summary>
/// Like the hidden-icons flyout of the Windows taskbar: the programs running in the background as a grid of icons.
/// Click one to open it (the way the Windows Security icon opens Windows Security); the small x, or a right-click, quits it.
/// The grid updates by itself while the popup is open.
/// </summary>
public sealed class BackgroundAppsPopup : BarPopup
{
    private const double TileW = 84, TileH = 88;

    private readonly StackPanel _root = new();
    private readonly WrapPanel _grid = new() { Margin = new Thickness(10, 2, 10, 6) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380 };
    private string _signature;

    public BackgroundAppsPopup() : base(4 * TileW + 20 + 8)
    {
        FallbackCornerRadius = 12;

        var title = new TextBlock { Text = "Background apps", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 14, 16, 2) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var hint = new TextBlock { Text = "Click to open. Right-click or ✕ to quit.", FontSize = 11.5, Margin = new Thickness(16, 0, 16, 6) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");

        _scroll.Content = _grid;
        _root.Children.Add(title);
        _root.Children.Add(hint);
        _root.Children.Add(_scroll);
        Content = _root;

        Render();
        Closed += (_, _) => BackgroundAppService.Instance.Changed -= OnChanged;
    }

    protected override void OnShown()
    {
        BackgroundAppService.Instance.Changed += OnChanged;
        BackgroundAppService.Instance.RefreshNow();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var apps = BackgroundAppService.Instance.Apps;
        string sig = string.Join("|", apps.Select(a => a.Target ?? a.ExePath));
        if (sig == _signature) return;          // nothing changed: leave the tiles alone (no flicker)
        _signature = sig;

        _grid.Children.Clear();
        if (apps.Count == 0)
        {
            var none = new TextBlock { Text = "No apps are running in the background.", FontSize = 13, Margin = new Thickness(8, 12, 8, 16), Opacity = 0.6 };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            _grid.Children.Add(none);
        }
        foreach (var app in apps) _grid.Children.Add(MakeTile(app));
        Resize();
    }

    /// <summary>The popup does not size itself to its content after it is shown, so do it when the grid changes.</summary>
    private void Resize()
    {
        _root.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Ceiling(_root.DesiredSize.Height) + 4;
    }

    private async Task QuitAsync(BackgroundApp app)
    {
        await Task.Run(() => BackgroundAppManager.Quit(app));
        await Task.Delay(400);
        BackgroundAppService.Instance.RefreshNow();   // the tile disappears when the next scan no longer finds it
    }

    private UIElement MakeTile(BackgroundApp app)
    {
        var icon = new Image { Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        ShellIcons.LoadInto(icon, app.Target != null ? AppCatalog.Instance.IconFor(app.Target) ?? app.IconSource : app.IconSource, 96);

        var name = new TextBlock
        {
            Text = app.Name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, Margin = new Thickness(4, 6, 4, 0),
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(name);

        // The small x in the corner, only while the pointer is over the tile.
        var x = new TextBlock
        {
            Text = "", FontSize = 9, Width = 18, Height = 18, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.Hand, ToolTip = "Quit " + app.Name, Visibility = Visibility.Collapsed, Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0),
        };
        x.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        x.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        x.MouseLeftButtonDown += (_, e) => e.Handled = true;
        x.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await QuitAsync(app); };

        var content = new Grid();
        content.Children.Add(stack);
        content.Children.Add(x);

        var tile = new Border
        {
            Width = TileW, Height = TileH, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Child = content, ToolTip = app.ExePath != null ? app.Name + "\n" + app.ExePath : app.Name,
        };
        tile.MouseEnter += (_, _) => { tile.SetResourceReference(Border.BackgroundProperty, "Brush.Hover"); x.Visibility = Visibility.Visible; };
        tile.MouseLeave += (_, _) => { tile.Background = Brushes.Transparent; x.Visibility = Visibility.Collapsed; };
        tile.MouseLeftButtonUp += (_, _) => { BackgroundAppManager.Open(app); CloseMenu(); };

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => { BackgroundAppManager.Open(app); CloseMenu(); };
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += async (_, _) => await QuitAsync(app);
        menu.Items.Add(open);
        menu.Items.Add(quit);
        if (app.ExePath != null)
        {
            var reveal = new MenuItem { Header = "Show in File Explorer" };
            reveal.Click += (_, _) => { Launch.RevealInExplorer(app.ExePath); CloseMenu(); };
            menu.Items.Add(new Separator());
            menu.Items.Add(reveal);
        }
        tile.ContextMenu = menu;
        return tile;
    }
}
