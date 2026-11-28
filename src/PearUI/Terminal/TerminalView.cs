using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PearUI.Terminal;

/// <summary>
/// One terminal: runs a shell in a pseudo console, keeps the screen in a <see cref="VtBuffer"/> and draws it.
/// The background is never painted, so the window's blur and the wallpaper show straight through.
/// </summary>
internal sealed class TerminalView : FrameworkElement
{
    // ---- theme ----
    public sealed record Theme(Color Foreground, Color Cursor, Color Selection, Color InverseBackground, uint[] Ansi);

    public static readonly Theme Pro = new(
        Color.FromRgb(0xF2, 0xF2, 0xF2), Color.FromRgb(0xF2, 0xF2, 0xF2), Color.FromArgb(0x70, 0x7A, 0x96, 0xFF), Color.FromRgb(0x14, 0x14, 0x18),
        new uint[]
        {
            0xFF000000, 0xFFE5483F, 0xFF2FC84A, 0xFFD6C91B, 0xFF5C7CFA, 0xFFD65BD2, 0xFF27C4CF, 0xFFD0D0D0,
            0xFF7B7B7B, 0xFFFF7A72, 0xFF5FF56D, 0xFFFFFA6E, 0xFF8AA0FF, 0xFFFF82FB, 0xFF68FBFF, 0xFFFFFFFF,
        });

    public static readonly Theme Basic = new(
        Color.FromRgb(0x1D, 0x1D, 0x1F), Color.FromRgb(0x1D, 0x1D, 0x1F), Color.FromArgb(0x60, 0x28, 0x5A, 0xFF), Color.FromRgb(0xF7, 0xF7, 0xF8),
        new uint[]
        {
            0xFF000000, 0xFFC4271B, 0xFF1A9A2E, 0xFF8F8600, 0xFF2236C9, 0xFFA621A3, 0xFF117F89, 0xFF9B9B9B,
            0xFF5B5B5B, 0xFFE2574C, 0xFF25B53C, 0xFFA89D00, 0xFF3B53E6, 0xFFC13FBD, 0xFF1696A1, 0xFF3A3A3A,
        });

    private static readonly Regex Prompt = new(@"^.*? % (.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private const double PadL = 16, PadT = 10, PadR = 8, PadB = 8;

    private readonly ShellDef _shell;
    private VtBuffer _buf;
    private PseudoConsole _pty;
    private readonly ConcurrentQueue<string> _incoming = new();
    private int _flushQueued;
    private bool _exited;

    private Theme _theme = Pro;
    private double _fontSize = 14;
    private double _cellW = 8, _cellH = 18;
    private Typeface _regular, _bold, _italic, _boldItalic;
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private SolidColorBrush _fgBrush, _cursorBrush, _selBrush;
    private double _dpi = 1;

    private int _scrollOffset;                 // lines scrolled back from the bottom
    private bool _hasSel, _selecting;
    private (int Line, int Col) _selA, _selB;
    private bool _blinkOn = true;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };

    // command history capture and Up/Down recall
    private int _pendingRow = -1;
    private int _navIdx = -1;
    private string _draft = "";
    private string _shown = "";   // what is currently typed on the prompt while walking through history

    public event Action<string> TitleChanged;
    public event Action Exited;
    public event Action<string> CommandEntered;
    /// <summary>Commands from earlier sessions, newest first. Used by the Up and Down keys.</summary>
    public Func<IReadOnlyList<string>> HistoryProvider;

    public string Title => _buf?.Title ?? "";
    public ShellDef Shell => _shell;

    /// <summary>Folder the shell starts in (the user's home folder when empty). Set before the view is shown.</summary>
    public string StartDirectory;

    public TerminalView(ShellDef shell)
    {
        _shell = shell;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        UseLayoutRounding = true;

        string family = Fonts.SystemFontFamilies.Any(f => f.Source == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";
        var fam = new FontFamily(family);
        _regular = new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _bold = new Typeface(fam, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _italic = new Typeface(fam, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalic = new Typeface(fam, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

        ApplyTheme(Pro);
        _buf = new VtBuffer(80, 24);
        HookBuffer(_buf);

        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; InvalidateVisual(); };
        Loaded += (_, _) => _blink.Start();
        Unloaded += (_, _) => _blink.Stop();
        GotKeyboardFocus += (_, _) => { _blinkOn = true; InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => InvalidateVisual();
    }

    private void HookBuffer(VtBuffer b)
    {
        b.TitleChanged += t => Dispatcher.BeginInvoke(() => TitleChanged?.Invoke(t));
        b.Reply += r => _pty?.Write(r);
    }

    // ================= theme & font =================
    public void ApplyTheme(Theme t)
    {
        _theme = t;
        VtBuffer.SetPalette(t.Ansi);
        _brushes.Clear();
        _fgBrush = Brush(t.Foreground);
        _cursorBrush = Brush(t.Cursor);
        _selBrush = Brush(t.Selection);
        InvalidateVisual();
    }

    public void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, 8, 32);
        MeasureCell();
        FitToSize();
        InvalidateVisual();
    }

    public double FontSizeValue => _fontSize;

    private void MeasureCell()
    {
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(new string('M', 20), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, _fontSize, Brushes.White, _dpi);
        _cellW = ft.WidthIncludingTrailingWhitespace / 20.0;
        _cellH = Math.Ceiling(_fontSize * 1.34);
    }

    private SolidColorBrush Brush(Color c) => Brush(((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);

    private SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var b)) return b;
        b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        _brushes[argb] = b;
        return b;
    }

    // ================= size & process =================
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_cellW <= 0 || _dpi == 0 || _pty == null) MeasureCell();
        FitToSize();
        if (_pty == null && !_exited && info.NewSize.Width > 40 && info.NewSize.Height > 40) StartShell();
    }

    private void FitToSize()
    {
        if (ActualWidth < 40 || ActualHeight < 40) return;
        int cols = Math.Max(10, (int)((ActualWidth - PadL - PadR) / _cellW));
        int rows = Math.Max(3, (int)((ActualHeight - PadT - PadB) / _cellH));
        if (cols == _buf.Cols && rows == _buf.Rows) return;
        _buf.Resize(cols, rows);
        _pty?.Resize(cols, rows);
        _scrollOffset = Math.Min(_scrollOffset, _buf.Scrollback.Count);
        InvalidateVisual();
    }

    private void StartShell()
    {
        string home = !string.IsNullOrWhiteSpace(StartDirectory) && System.IO.Directory.Exists(StartDirectory)
            ? StartDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string cmd;
        Dictionary<string, string> env;
        try { (cmd, env) = ShellCatalog.BuildLaunch(_shell); }
        catch (Exception ex)
        {
            _buf.Feed("\u001b[31mCould not prepare " + _shell.Name + ": " + ex.Message + "\u001b[0m\r\n");
            return;
        }

        try
        {
            _pty = new PseudoConsole(cmd, home, _buf.Cols, _buf.Rows, env);
            _pty.Output += d => { _incoming.Enqueue(d); QueueFlush(); };
            _pty.Exited += () => Dispatcher.BeginInvoke(() => { _exited = true; Exited?.Invoke(); });
        }
        catch (Exception ex)
        {
            _buf.Feed("\u001b[31mCould not start the shell: " + ex.Message + "\u001b[0m\r\n");
        }
    }

    public void Close()
    {
        _blink.Stop();
        var p = _pty; _pty = null;
        p?.Dispose();
    }

    /// <summary>Output from the shell is applied in batches, once per frame, however fast it arrives.</summary>
    private void QueueFlush()
    {
        if (Interlocked.Exchange(ref _flushQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _flushQueued, 0);
            int before = _buf.Scrollback.Count;
            while (_incoming.TryDequeue(out var chunk)) _buf.Feed(chunk);
            int grew = _buf.Scrollback.Count - before;
            if (_scrollOffset > 0 && grew > 0) _scrollOffset = Math.Min(_scrollOffset + grew, _buf.Scrollback.Count);
            CapturePending();
            InvalidateVisual();
        });
    }

    // ================= history capture =================
    private int CursorAbsRow => _buf.Scrollback.Count + _buf.CursorY;

    /// <summary>The text of a (possibly wrapped) line, from its first row to <paramref name="abs"/>.</summary>
    /// <param name="padTo">Keep at least this many characters on the last row, so spaces the user typed (or the one after the prompt) are not cut off.</param>
    private string ChainText(int abs, int padTo = 0)
    {
        int first = abs;
        while (first > 0 && _buf.GetLine(first).Wrapped) first--;
        var sb = new StringBuilder();
        for (int i = first; i <= abs; i++)
        {
            string row = RowText(_buf.GetLine(i), i == abs);
            if (i == abs && row.Length < padTo) row = row.PadRight(padTo);
            sb.Append(row);
        }
        return sb.ToString();
    }

    private static string RowText(Line l, bool trim)
    {
        var sb = new StringBuilder(l.Cells.Length);
        foreach (var c in l.Cells)
        {
            if (c.Cp == -1) continue;
            if (c.Cp == 0) sb.Append(' '); else sb.Append(char.ConvertFromUtf32(c.Cp));
        }
        return trim ? sb.ToString().TrimEnd() : sb.ToString();
    }

    /// <summary>What has been typed after the prompt on the cursor's line, or null when the cursor is not at a prompt.</summary>
    private string CurrentInput()
    {
        if (_buf.AltScreen) return null;
        // The prompt ends with a space ('% '), which trimming would remove from an empty prompt: keep everything up to the cursor.
        var m = Prompt.Match(ChainText(CursorAbsRow, _buf.CursorX));
        return m.Success ? m.Groups[1].Value : null;
    }

    private void CapturePending()
    {
        if (_pendingRow < 0) return;
        if (CursorAbsRow == _pendingRow && !_buf.AltScreen) return;   // the shell has not moved on yet
        int row = _pendingRow;
        _pendingRow = -1;
        if (_buf.AltScreen || row >= _buf.TotalLines) return;
        var m = Prompt.Match(ChainText(row));
        if (m.Success && m.Groups[1].Value.Trim().Length > 0) CommandEntered?.Invoke(m.Groups[1].Value.Trim());
    }

    // ================= input =================
    public void SendText(string text) { _pty?.Write(text); }

    private void Send(string s)
    {
        _scrollOffset = 0;
        _hasSel = false;
        _pty?.Write(s);
        InvalidateVisual();
    }

    public void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return; }
        if (string.IsNullOrEmpty(text)) return;
        _navIdx = -1;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        Send(_buf.BracketedPaste ? "\u001b[200~" + text + "\u001b[201~" : text);
    }

    public bool CopySelection()
    {
        if (!_hasSel) return false;
        string t = SelectedText();
        if (t.Length == 0) return false;
        try { Clipboard.SetText(t); } catch { }
        return true;
    }

    public void ClearScreen()
    {
        _buf.ClearScrollback();
        _scrollOffset = 0;
        Send("\f"); // Ctrl+L: ask the shell to redraw
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        _navIdx = -1;
        Send(e.Text);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, alt = (mods & ModifierKeys.Alt) != 0, shift = (mods & ModifierKeys.Shift) != 0;

        // Clipboard
        if (ctrl && !shift && key == Key.C && _hasSel) { CopySelection(); _hasSel = false; InvalidateVisual(); e.Handled = true; return; }
        if (shift && key == Key.Insert) { Paste(); e.Handled = true; return; }

        // Scrollback with the keyboard
        if (shift && key == Key.PageUp && !_buf.AltScreen) { ScrollBy(_buf.Rows - 1); e.Handled = true; return; }
        if (shift && key == Key.PageDown && !_buf.AltScreen) { ScrollBy(-(_buf.Rows - 1)); e.Handled = true; return; }

        // Up / Down walk through the history of earlier sessions too (the shell itself forgets them).
        if (!ctrl && !alt && !shift && (key == Key.Up || key == Key.Down) && TryHistoryKey(key == Key.Up)) { e.Handled = true; return; }

        string seq = KeySequence(key, ctrl, alt, shift);
        if (seq == null) return;
        if (key == Key.Return) { _pendingRow = _buf.AltScreen ? -1 : CursorAbsRow; _navIdx = -1; }
        else if (key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)) _navIdx = -1;
        Send(seq);
        e.Handled = true;
    }

    private bool TryHistoryKey(bool up)
    {
        var provider = HistoryProvider;
        if (provider == null) return false;
        string current = CurrentInput();
        if (current == null) return false;           // a full-screen program or a prompt we don't know: leave the key alone
        var list = provider();
        if (up)
        {
            if (list.Count == 0) return true;
            if (_navIdx == -1) { _draft = current; _shown = current; }
            if (_navIdx + 1 < list.Count) _navIdx++;
            Replace(list[_navIdx]);
        }
        else if (_navIdx >= 0)
        {
            _navIdx--;
            Replace(_navIdx == -1 ? _draft : list[_navIdx]);
        }
        return true;
    }

    /// <summary>Replaces what is typed at the prompt with <paramref name="text"/>: jump to the end of the line, erase it, type the new text.</summary>
    private void Replace(string text)
    {
        Send("\u001b[F" + new string('\u007f', _shown.Length) + text);
        _shown = text;
    }

    /// <summary>Puts a command (for example one picked from the history panel) on the prompt in place of whatever is typed.</summary>
    public void PutOnPrompt(string text)
    {
        _shown = CurrentInput() ?? "";
        _navIdx = -1;
        Replace(text);
    }

    private string KeySequence(Key key, bool ctrl, bool alt, bool shift)
    {
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Csi(char final, int n = 1) => mod > 1 ? $"\u001b[{n};{mod}{final}" : (_buf.AppCursorKeys && n == 1 ? $"\u001bO{final}" : $"\u001b[{final}");
        string Tilde(int n) => mod > 1 ? $"\u001b[{n};{mod}~" : $"\u001b[{n}~";

        switch (key)
        {
            case Key.Return: return alt ? "\u001b\r" : "\r";
            case Key.Back: return ctrl ? "\u0017" : alt ? "\u001b\u007f" : "\u007f";
            case Key.Tab: return shift ? "\u001b[Z" : "\t";
            case Key.Escape: return "\u001b";
            case Key.Up: return Csi('A');
            case Key.Down: return Csi('B');
            case Key.Right: return Csi('C');
            case Key.Left: return Csi('D');
            case Key.Home: return Csi('H');
            case Key.End: return Csi('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return "\u001bOP";
            case Key.F2: return "\u001bOQ";
            case Key.F3: return "\u001bOR";
            case Key.F4: return "\u001bOS";
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
        }

        if (ctrl && !alt)
        {
            if (key >= Key.A && key <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
            switch (key)
            {
                case Key.OemOpenBrackets: return "\u001b";
                case Key.OemBackslash or Key.OemPipe: return "\u001c";
                case Key.OemCloseBrackets: return "\u001d";
                case Key.D2: return "\0";
                case Key.Space: return "\0";
            }
        }
        if (alt && !ctrl && key >= Key.A && key <= Key.Z)
        {
            char c = (char)('a' + (key - Key.A));
            return "\u001b" + (shift ? char.ToUpperInvariant(c) : c);
        }
        if (alt && !ctrl && key >= Key.D0 && key <= Key.D9 && !shift) return "\u001b" + (char)('0' + (key - Key.D0));
        return null;   // ordinary characters arrive through OnTextInput
    }

    // ================= mouse =================
    private void ScrollBy(int lines)
    {
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, _buf.Scrollback.Count);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int notches = Math.Max(1, Math.Abs(e.Delta) / 40);
        if (_buf.AltScreen)
        {
            // Full-screen programs (less, vim…) get arrow keys, like most terminals do.
            _pty?.Write(string.Concat(Enumerable.Repeat(e.Delta > 0 ? "\u001b[A" : "\u001b[B", notches)));
            return;
        }
        ScrollBy((e.Delta > 0 ? 1 : -1) * notches);
        e.Handled = true;
    }

    private (int Line, int Col) HitTest(Point p)
    {
        int row = Math.Clamp((int)((p.Y - PadT) / _cellH), 0, _buf.Rows - 1);
        int col = Math.Clamp((int)((p.X - PadL) / _cellW), 0, _buf.Cols - 1);
        return (_buf.Scrollback.Count - _scrollOffset + row, col);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var hit = HitTest(e.GetPosition(this));
        if (e.ClickCount == 2) { SelectWord(hit); return; }
        _selA = _selB = hit;
        _hasSel = false;
        _selecting = true;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting) return;
        var p = e.GetPosition(this);
        if (p.Y < 0) ScrollBy(1); else if (p.Y > ActualHeight) ScrollBy(-1);
        _selB = HitTest(p);
        _hasSel = _selA != _selB;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_selecting) { _selecting = false; ReleaseMouseCapture(); }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_hasSel) { CopySelection(); _hasSel = false; InvalidateVisual(); }   // right-click copies a selection, or pastes
        else Paste();
        e.Handled = true;
    }

    private void SelectWord((int Line, int Col) at)
    {
        var l = _buf.GetLine(at.Line);
        bool Word(int i) => l.Cells[i].Cp != 0 && !char.IsWhiteSpace((char)Math.Min(l.Cells[i].Cp, 0xFFFF));
        if (at.Col >= l.Cells.Length || !Word(at.Col)) return;
        int a = at.Col, b = at.Col;
        while (a > 0 && Word(a - 1)) a--;
        while (b < l.Cells.Length - 1 && Word(b + 1)) b++;
        _selA = (at.Line, a); _selB = (at.Line, b + 1);
        _hasSel = true;
        InvalidateVisual();
    }

    private ((int Line, int Col) From, (int Line, int Col) To) OrderedSelection()
    {
        var a = _selA; var b = _selB;
        return (a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col)) ? (a, b) : (b, a);
    }

    private string SelectedText()
    {
        var (from, to) = OrderedSelection();
        var sb = new StringBuilder();
        for (int i = from.Line; i <= to.Line && i < _buf.TotalLines; i++)
        {
            var l = _buf.GetLine(i);
            int c0 = i == from.Line ? from.Col : 0;
            int c1 = i == to.Line ? Math.Min(to.Col, l.Cells.Length) : l.Cells.Length;
            var row = new StringBuilder();
            for (int c = c0; c < c1; c++)
            {
                var cell = l.Cells[c];
                if (cell.Cp == -1) continue;
                if (cell.Cp == 0) row.Append(' '); else row.Append(char.ConvertFromUtf32(cell.Cp));
            }
            string text = i == to.Line ? row.ToString() : row.ToString().TrimEnd();
            sb.Append(text.TrimEnd());
            if (i < to.Line && !(i + 1 < _buf.TotalLines && _buf.GetLine(i + 1).Wrapped)) sb.Append('\n');
        }
        return sb.ToString();
    }

    // ================= drawing =================
    protected override void OnRender(DrawingContext dc)
    {
        // A fully transparent fill keeps the whole area hit-testable without hiding the wallpaper.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_buf == null) return;

        int cols = _buf.Cols, rows = _buf.Rows;
        int startAbs = _buf.Scrollback.Count - _scrollOffset;
        var (selFrom, selTo) = OrderedSelection();

        for (int r = 0; r < rows; r++)
        {
            int abs = startAbs + r;
            if (abs < 0 || abs >= _buf.TotalLines) continue;
            var line = _buf.GetLine(abs);
            double y = PadT + r * _cellH;
            int n = Math.Min(cols, line.Cells.Length);

            // backgrounds (explicit colours, inverse video)
            for (int c = 0; c < n;)
            {
                uint bg = EffectiveBg(line.Cells[c]);
                int e = c + 1;
                while (e < n && EffectiveBg(line.Cells[e]) == bg) e++;
                if (bg != 0) dc.DrawRectangle(Brush(bg), null, new Rect(PadL + c * _cellW, y, (e - c) * _cellW, _cellH));
                c = e;
            }

            // selection
            if (_hasSel && abs >= selFrom.Line && abs <= selTo.Line)
            {
                int c0 = abs == selFrom.Line ? selFrom.Col : 0, c1 = abs == selTo.Line ? selTo.Col : cols;
                if (c1 > c0) dc.DrawRectangle(_selBrush, null, new Rect(PadL + c0 * _cellW, y, (c1 - c0) * _cellW, _cellH));
            }

            // text
            for (int c = 0; c < n;)
            {
                var cell = line.Cells[c];
                if (cell.Cp == -1 || (cell.Cp == 0 && (cell.Attr & CellAttr.Underline) == 0 && (cell.Attr & CellAttr.Strike) == 0)) { c++; continue; }

                uint fg = EffectiveFg(cell);
                bool ascii = cell.Cp >= 0x20 && cell.Cp < 0x7F;
                int e = c + 1;
                if (ascii || cell.Cp == 0)
                {
                    while (e < n)
                    {
                        var d = line.Cells[e];
                        bool dAscii = (d.Cp >= 0x20 && d.Cp < 0x7F) || d.Cp == 0;
                        if (!dAscii || EffectiveFg(d) != fg || d.Attr != cell.Attr) break;
                        e++;
                    }
                }
                else if (cell.Width == 2) e = Math.Min(n, c + 2);

                if (cell.Cp is >= 0x2580 and <= 0x259F && DrawBlock(dc, cell.Cp, c, y, fg)) { c++; continue; }
                DrawRun(dc, line, c, e, y, fg, cell.Attr);
                c = e;
            }
        }

        DrawCursor(dc, startAbs);
        DrawScrollbar(dc);
    }

    private uint EffectiveFg(Cell c)
    {
        uint fg = c.Fg != 0 ? c.Fg : (uint)(0xFF000000 | ((uint)_theme.Foreground.R << 16) | ((uint)_theme.Foreground.G << 8) | _theme.Foreground.B);
        if ((c.Attr & CellAttr.Inverse) != 0)
            fg = c.Bg != 0 ? c.Bg : (uint)(0xFF000000 | ((uint)_theme.InverseBackground.R << 16) | ((uint)_theme.InverseBackground.G << 8) | _theme.InverseBackground.B);
        if ((c.Attr & CellAttr.Dim) != 0) fg = (fg & 0x00FFFFFF) | 0x99000000;
        return fg;
    }

    private uint EffectiveBg(Cell c)
    {
        if ((c.Attr & CellAttr.Inverse) != 0)
            return c.Fg != 0 ? c.Fg : (uint)(0xFF000000 | ((uint)_theme.Foreground.R << 16) | ((uint)_theme.Foreground.G << 8) | _theme.Foreground.B);
        return c.Bg;
    }

    /// <summary>Block and shade characters are drawn as exact rectangles, so neighbouring cells join without seams.</summary>
    private bool DrawBlock(DrawingContext dc, int cp, int col, double y, uint fg)
    {
        double x0 = Math.Round(PadL + col * _cellW), x1 = Math.Round(PadL + (col + 1) * _cellW);
        double y0 = y, y1 = y + _cellH, mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
        Rect r;
        uint colour = fg;
        switch (cp)
        {
            case 0x2588: r = new Rect(x0, y0, x1 - x0, y1 - y0); break;           // █
            case 0x2580: r = new Rect(x0, y0, x1 - x0, my - y0); break;           // ▀
            case 0x2584: r = new Rect(x0, my, x1 - x0, y1 - my); break;           // ▄
            case 0x258C: r = new Rect(x0, y0, mx - x0, y1 - y0); break;           // ▌
            case 0x2590: r = new Rect(mx, y0, x1 - mx, y1 - y0); break;           // ▐
            case 0x2591: r = new Rect(x0, y0, x1 - x0, y1 - y0); colour = (fg & 0x00FFFFFF) | 0x40000000; break;   // ░
            case 0x2592: r = new Rect(x0, y0, x1 - x0, y1 - y0); colour = (fg & 0x00FFFFFF) | 0x80000000; break;   // ▒
            case 0x2593: r = new Rect(x0, y0, x1 - x0, y1 - y0); colour = (fg & 0x00FFFFFF) | 0xC0000000; break;   // ▓
            case >= 0x2596 and <= 0x259F:                                           // quadrants ▖ ▗ ▘ ▙ ▚ ▛ ▜ ▝ ▞ ▟
            {
                // bits: 1 top-left, 2 top-right, 4 bottom-left, 8 bottom-right
                int mask = new[] { 4, 8, 1, 13, 9, 7, 11, 2, 6, 14 }[cp - 0x2596];
                var brush = Brush(colour);
                if ((mask & 1) != 0) dc.DrawRectangle(brush, null, new Rect(x0, y0, mx - x0, my - y0));
                if ((mask & 2) != 0) dc.DrawRectangle(brush, null, new Rect(mx, y0, x1 - mx, my - y0));
                if ((mask & 4) != 0) dc.DrawRectangle(brush, null, new Rect(x0, my, mx - x0, y1 - my));
                if ((mask & 8) != 0) dc.DrawRectangle(brush, null, new Rect(mx, my, x1 - mx, y1 - my));
                return true;
            }
            default: return false;
        }
        dc.DrawRectangle(Brush(colour), null, r);
        return true;
    }

    private void DrawRun(DrawingContext dc, Line line, int c0, int c1, double y, uint fg, CellAttr attr)
    {
        var sb = new StringBuilder();
        for (int i = c0; i < c1; i++)
        {
            var cell = line.Cells[i];
            if (cell.Cp == -1) continue;
            sb.Append(cell.Cp == 0 ? ' ' : char.ConvertFromUtf32(cell.Cp));
        }
        string text = sb.ToString();
        double x = PadL + c0 * _cellW;
        var brush = Brush(fg);

        if (text.Trim().Length > 0)
        {
            var face = (attr & CellAttr.Bold) != 0 ? ((attr & CellAttr.Italic) != 0 ? _boldItalic : _bold) : ((attr & CellAttr.Italic) != 0 ? _italic : _regular);
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _fontSize, brush, _dpi);
            dc.DrawText(ft, new Point(x, y + (_cellH - ft.Height) / 2));
        }
        double w = (c1 - c0) * _cellW;
        if ((attr & CellAttr.Underline) != 0) dc.DrawRectangle(brush, null, new Rect(x, y + _cellH - 3, w, 1));
        if ((attr & CellAttr.Strike) != 0) dc.DrawRectangle(brush, null, new Rect(x, y + _cellH / 2, w, 1));
    }

    private void DrawCursor(DrawingContext dc, int startAbs)
    {
        if (!_buf.CursorVisible || _scrollOffset > 0) return;
        double x = PadL + _buf.CursorX * _cellW, y = PadT + _buf.CursorY * _cellH;
        var rect = new Rect(x, y, _cellW, _cellH);
        if (!IsKeyboardFocused) { dc.DrawRectangle(null, new Pen(_cursorBrush, 1), new Rect(x + 0.5, y + 0.5, _cellW - 1, _cellH - 1)); return; }
        if (!_blinkOn) return;
        dc.DrawRectangle(_cursorBrush, null, rect);
        var cell = _buf.ScreenLine(_buf.CursorY).Cells[Math.Min(_buf.CursorX, _buf.Cols - 1)];
        if (cell.Cp > 0x20)
        {
            var ft = new FormattedText(char.ConvertFromUtf32(cell.Cp), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, _fontSize, Brush(_theme.InverseBackground), _dpi);
            dc.DrawText(ft, new Point(x, y + (_cellH - ft.Height) / 2));
        }
    }

    private void DrawScrollbar(DrawingContext dc)
    {
        if (_buf.AltScreen || _buf.Scrollback.Count == 0 || _scrollOffset == 0) return;
        double total = _buf.TotalLines, h = ActualHeight - PadT - PadB;
        double barH = Math.Max(24, h * _buf.Rows / total);
        double top = PadT + (h - barH) * ((double)(_buf.Scrollback.Count - _scrollOffset) / _buf.Scrollback.Count);
        dc.DrawRoundedRectangle(Brush(0x66808080), null, new Rect(ActualWidth - 7, top, 4, barH), 2, 2);
    }
}
