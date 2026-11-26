using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PearUI.Core;
using PearUI.Shell;

namespace PearUI.MenuBar;

/// <summary>Maccy-style clipboard history: type to search, click or press Enter / Ctrl+1…9 to paste.</summary>
public sealed class ClipboardPopup : BarPopup
{
    private readonly IntPtr _target; // window that had focus before the popup opened
    private readonly TextBox _search = new();
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330 };
    private readonly List<(Border Row, ClipEntry Entry)> _rows = new();
    private int _hot = -1;

    public ClipboardPopup(IntPtr target) : base(380)
    {
        _target = target;
        FallbackCornerRadius = 10;

        _search.FontSize = 13;
        _search.Padding = new Thickness(8, 4, 8, 4);
        _search.BorderThickness = new Thickness(0);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.SetResourceReference(Control.BackgroundProperty, "Brush.Field");
        _search.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        _search.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text");
        var searchBox = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(8, 8, 8, 4) };
        searchBox.SetResourceReference(Border.BackgroundProperty, "Brush.Field");
        var hint = new TextBlock
        {
            Text = "Type to search…", Opacity = 0.45, FontSize = 13, IsHitTestVisible = false,
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var field = new Grid();
        field.Children.Add(_search);
        field.Children.Add(hint);
        searchBox.Child = field;
        _search.TextChanged += (_, _) => { hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Rebuild(); };

        _scroll.Content = _list;
        var sep = new Border { Height = 1, Margin = new Thickness(12, 4, 12, 4) };
        sep.SetResourceReference(Border.BackgroundProperty, "Brush.Separator");

        var footer = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        footer.Children.Add(MakeRow("Clear History (keeps pinned)", null, () => { ClipboardHistory.Instance.Clear(); Rebuild(); }, isAction: true));

        var root = new StackPanel();
        root.Children.Add(searchBox);
        root.Children.Add(new Border { Height = 4 });
        root.Children.Add(_scroll);
        root.Children.Add(sep);
        root.Children.Add(footer);
        Content = root;

        PreviewKeyDown += OnKey;
        Rebuild();
    }

    protected override void OnShown() => Dispatcher.BeginInvoke(() => _search.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    private void Rebuild()
    {
        double offset = _scroll.VerticalOffset;
        _list.Children.Clear();
        _rows.Clear();
        _hot = -1;
        string q = _search.Text.Trim();
        var items = ClipboardHistory.Instance.Items
            .Where(t => q.Length == 0 || t.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();

        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = q.Length == 0 ? "Nothing copied yet" : "No matches", Opacity = 0.5, FontSize = 13,
                Margin = new Thickness(14, 8, 14, 8),
            });
            return;
        }

        bool lastPinned = false;
        for (int i = 0; i < items.Count; i++)
        {
            var entry = items[i];
            if (lastPinned && !entry.Pinned) // thin line between pinned and the rest
            {
                var line = new Border { Height = 1, Margin = new Thickness(12, 3, 12, 3) };
                line.SetResourceReference(Border.BackgroundProperty, "Brush.Separator");
                _list.Children.Add(line);
            }
            lastPinned = entry.Pinned;
            var row = MakeClipRow(entry, i < 9 ? $"Ctrl+{i + 1}" : null);
            _rows.Add((row, entry));
            _list.Children.Add(row);
        }
        SetHot(0);
        _scroll.UpdateLayout();
        if (offset > 0) _scroll.ScrollToVerticalOffset(offset);
    }

    private TextBlock IconButton(string glyph, string tip, Action click)
    {
        var tb = new TextBlock
        {
            Text = glyph, FontSize = 12, Width = 22, Height = 20, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 2, 0, 0),
            Cursor = Cursors.Hand, ToolTip = tip, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
        };
        tb.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        tb.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        tb.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return tb;
    }

    private Border MakeClipRow(ClipEntry entry, string hint)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // pin marker
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // text
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // shortcut hint
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // pin / delete buttons

        var marker = new TextBlock { Text = entry.Pinned ? "\uE840" : "", FontSize = 11, Width = entry.Pinned ? 18 : 0, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 };
        marker.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        var text = new TextBlock { Text = Preview(entry.Text), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = Tip(entry.Text) };
        var g = new TextBlock { Text = hint ?? "", FontSize = 12, Opacity = 0.5, Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        buttons.Children.Add(IconButton(entry.Pinned ? "\uE77A" : "\uE718", entry.Pinned ? "Unpin" : "Pin to the top", () =>
        {
            ClipboardHistory.Instance.TogglePin(entry); Rebuild();
        }));
        buttons.Children.Add(IconButton("\uE711", "Delete", () =>
        {
            ClipboardHistory.Instance.Remove(entry); Rebuild();
        }));

        Grid.SetColumn(text, 1);
        Grid.SetColumn(g, 2);
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(marker);
        grid.Children.Add(text);
        grid.Children.Add(g);
        grid.Children.Add(buttons);

        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(5), Margin = new Thickness(5, 0, 5, 0),
            Padding = new Thickness(9, 3, 6, 3), Background = Brushes.Transparent, MinHeight = 28,
        };
        row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        row.MouseEnter += (_, _) => { int i = _rows.FindIndex(x => x.Row == row); if (i >= 0) SetHot(i); };
        row.MouseLeftButtonUp += (_, _) => Paste(entry);
        return row;
    }

    private static string Tip(string text) => text.Length > 600 ? text[..600] + "…" : text;

    private static string Preview(string text)
    {
        var line = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        line = line.Replace('\t', ' ');
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private Border MakeRow(string header, string hint, Action action, bool isAction)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var h = new TextBlock { Text = header, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var g = new TextBlock { Text = hint ?? "", FontSize = 12, Opacity = 0.5, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(g, 1);
        grid.Children.Add(h);
        grid.Children.Add(g);
        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(5), Margin = new Thickness(5, 0, 5, 0),
            Padding = new Thickness(9, 4, 10, 5), Background = Brushes.Transparent,
        };
        row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        row.MouseEnter += (_, _) => { if (isAction) { SetHot(-1); Highlight(row, true); } };
        row.MouseLeave += (_, _) => { if (isAction) Highlight(row, false); };
        row.MouseLeftButtonUp += (_, _) => action();
        return row;
    }

    private static void Highlight(Border row, bool on)
    {
        if (on)
        {
            row.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.AccentText");
        }
        else
        {
            row.Background = Brushes.Transparent;
            row.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
        }
    }

    private void SetHot(int index)
    {
        if (_hot >= 0 && _hot < _rows.Count) { Highlight(_rows[_hot].Row, false); ShowButtons(_rows[_hot].Row, false); }
        _hot = index;
        if (_hot >= 0 && _hot < _rows.Count)
        {
            Highlight(_rows[_hot].Row, true);
            ShowButtons(_rows[_hot].Row, true);
            _rows[_hot].Row.BringIntoView();
        }
    }

    private static void ShowButtons(Border row, bool on)
    {
        if (row.Child is Grid g && g.Children.Count > 3 && g.Children[3] is StackPanel sp)
            sp.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int i = e.Key - Key.D1;
            if (i < _rows.Count) Paste(_rows[i].Entry);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Down when _rows.Count > 0: SetHot(Math.Min(_hot + 1, _rows.Count - 1)); break;
            case Key.Up when _rows.Count > 0: SetHot(Math.Max(_hot - 1, 0)); break;
            case Key.Enter when _hot >= 0 && _hot < _rows.Count: Paste(_rows[_hot].Entry); break;
            case Key.Delete when _hot >= 0 && _hot < _rows.Count && Keyboard.Modifiers == ModifierKeys.Shift:
                ClipboardHistory.Instance.Remove(_rows[_hot].Entry); Rebuild(); break;
            case Key.P when ctrl && _hot >= 0 && _hot < _rows.Count:
                ClipboardHistory.Instance.TogglePin(_rows[_hot].Entry); Rebuild(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Paste(ClipEntry entry)
    {
        CloseMenu();
        if (!ClipboardHistory.Instance.Restore(entry)) return;
        Keys.SendTo(_target, Keys.Ctrl, (byte)'V'); // back to the app you were in, then paste
    }
}
