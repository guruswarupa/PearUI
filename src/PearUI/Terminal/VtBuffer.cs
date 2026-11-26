using System.Text;

namespace PearUI.Terminal;

[Flags]
internal enum CellAttr : byte { None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16, Strike = 32 }

/// <summary>One character cell. Colours are 0xAARRGGBB; 0 means "the terminal's default colour".</summary>
internal struct Cell
{
    public int Cp;        // Unicode code point; 0 = empty, -1 = the right half of a wide character
    public byte Width;    // 1, or 2 for a wide character
    public uint Fg, Bg;
    public CellAttr Attr;
}

internal sealed class Line
{
    public Cell[] Cells;
    public bool Wrapped;   // continues the line above (soft wrap)
    public Line(int cols) { Cells = new Cell[cols]; }
}

/// <summary>
/// A screen of character cells plus scrollback, fed by the escape-sequence stream a shell prints.
/// It understands the subset of xterm sequences that Windows' pseudo console and everyday command-line
/// programs use: cursor movement, erasing, scrolling regions, colours (16, 256 and true colour),
/// the alternate screen, titles and the mode switches for the cursor, wrapping and pasting.
/// </summary>
internal sealed class VtBuffer
{
    public const int MaxScrollback = 10000;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    private Line[] _screen, _mainScreen;
    public readonly List<Line> Scrollback = new();

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AppCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AltScreen { get; private set; }
    public string Title { get; private set; } = "";

    /// <summary>Raised on the feeding thread when the program sets a title.</summary>
    public event Action<string> TitleChanged;
    /// <summary>Text the terminal must send back to the program (answers to cursor-position and device queries).</summary>
    public event Action<string> Reply;

    // current drawing state
    private uint _fg, _bg;
    private CellAttr _attr;
    private bool _wrapNext, _autoWrap = true, _originMode;
    private int _top, _bottom;                       // scroll region (inclusive)
    private int _savedX, _savedY; private uint _savedFg, _savedBg; private CellAttr _savedAttr;
    private bool[] _tabs;

    // parser
    private enum St { Ground, Esc, EscIntermediate, Csi, Osc, OscEsc, Ignore, IgnoreEsc }
    private St _st = St.Ground;
    private readonly StringBuilder _csi = new(), _osc = new();
    private char _highSurrogate;

    public VtBuffer(int cols, int rows)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(1, rows);
        _screen = NewScreen(Rows, Cols);
        _top = 0; _bottom = Rows - 1;
        ResetTabs();
    }

    private static Line[] NewScreen(int rows, int cols)
    {
        var s = new Line[rows];
        for (int i = 0; i < rows; i++) s[i] = new Line(cols);
        return s;
    }

    private void ResetTabs() { _tabs = new bool[Cols]; for (int i = 0; i < Cols; i += 8) _tabs[i] = true; }

    // ================= access for rendering =================
    public int TotalLines => Scrollback.Count + Rows;

    /// <summary>Line by absolute index: 0 is the oldest scrollback line, Scrollback.Count is the top of the screen.</summary>
    public Line GetLine(int abs) => abs < Scrollback.Count ? Scrollback[abs] : _screen[abs - Scrollback.Count];

    public Line ScreenLine(int row) => _screen[row];

    public void ClearScrollback() => Scrollback.Clear();

    // ================= resize =================
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols); rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;

        // Fewer rows: the lines at the top scroll into history (unless the cursor is near the top).
        if (rows < Rows && !AltScreen)
        {
            int drop = Math.Min(Rows - rows, Math.Max(0, CursorY - (rows - 1)));
            for (int i = 0; i < drop; i++) PushScrollback(_screen[i]);
            var ns = new Line[rows];
            Array.Copy(_screen, drop, ns, 0, rows);
            _screen = ns;
            CursorY -= drop;
        }
        else
        {
            var ns = new Line[rows];
            for (int i = 0; i < rows; i++) ns[i] = i < _screen.Length ? _screen[i] : new Line(Cols);
            _screen = ns;
        }

        foreach (var l in _screen) FitLine(l, cols);
        if (_mainScreen != null)
        {
            var ms = new Line[rows];
            for (int i = 0; i < rows; i++) ms[i] = i < _mainScreen.Length ? _mainScreen[i] : new Line(Cols);
            foreach (var l in ms) FitLine(l, cols);
            _mainScreen = ms;
        }
        foreach (var l in Scrollback) FitLine(l, cols);

        Cols = cols; Rows = rows;
        _top = 0; _bottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1);
        CursorY = Math.Clamp(CursorY, 0, rows - 1);
        _wrapNext = false;
        ResetTabs();
    }

    private static void FitLine(Line l, int cols)
    {
        if (l.Cells.Length != cols) Array.Resize(ref l.Cells, cols);
    }

    // ================= feeding =================
    public void Feed(string text)
    {
        foreach (char ch in text) Process(ch);
    }

    private void Process(char c)
    {
        switch (_st)
        {
            case St.Ground: Ground(c); break;
            case St.Esc: EscChar(c); break;
            case St.EscIntermediate: _st = St.Ground; break; // ESC ( B and friends: one more character, ignored
            case St.Csi:
                if (c >= 0x40 && c <= 0x7E) { RunCsi(c); _st = St.Ground; }
                else if (c == 0x1B) { _st = St.Esc; }
                else if (c >= 0x20) _csi.Append(c);
                else Control(c);
                break;
            case St.Osc:
                if (c == 0x07) { RunOsc(); _st = St.Ground; }
                else if (c == 0x1B) _st = St.OscEsc;
                else _osc.Append(c);
                break;
            case St.OscEsc:
                if (c == '\\') { RunOsc(); _st = St.Ground; }
                else { _st = St.Esc; EscChar(c); }
                break;
            case St.Ignore: // DCS / APC / PM / SOS strings
                if (c == 0x07) _st = St.Ground; else if (c == 0x1B) _st = St.IgnoreEsc;
                break;
            case St.IgnoreEsc:
                _st = c == '\\' ? St.Ground : St.Ignore;
                break;
        }
    }

    private void Ground(char c)
    {
        if (c < 0x20 || c == 0x7F) { Control(c); return; }
        if (char.IsHighSurrogate(c)) { _highSurrogate = c; return; }
        int cp = c;
        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate == 0) return;
            cp = char.ConvertToUtf32(_highSurrogate, c);
            _highSurrogate = '\0';
        }
        Put(cp);
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\x1B': _st = St.Esc; break;
            case '\b': if (CursorX > 0) CursorX--; _wrapNext = false; break;
            case '\t':
                {
                    int x = CursorX + 1;
                    while (x < Cols - 1 && !_tabs[x]) x++;
                    CursorX = Math.Min(x, Cols - 1);
                    _wrapNext = false;
                    break;
                }
            case '\n': case '\v': case '\f': LineFeed(); break;
            case '\r': CursorX = 0; _wrapNext = false; break;
        }
    }

    private void EscChar(char c)
    {
        _st = St.Ground;
        switch (c)
        {
            case '[': _csi.Clear(); _st = St.Csi; break;
            case ']': _osc.Clear(); _st = St.Osc; break;
            case 'P': case '_': case '^': case 'X': _st = St.Ignore; break;
            case '(': case ')': case '*': case '+': case '#': case '%': _st = St.EscIntermediate; break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (CursorX < Cols) _tabs[CursorX] = true; break;
            case 'c': FullReset(); break;
            case '\x1B': _st = St.Esc; break;
        }
    }

    // ================= text output =================
    private static bool IsCombining(int cp) =>
        (cp >= 0x0300 && cp <= 0x036F) || (cp >= 0x200B && cp <= 0x200F) || (cp >= 0xFE00 && cp <= 0xFE0F) || cp == 0x200D;

    private static int CharWidth(int cp)
    {
        if (cp < 0x1100) return 1;
        if ((cp >= 0x1100 && cp <= 0x115F) || (cp >= 0x2E80 && cp <= 0xA4CF) || (cp >= 0xAC00 && cp <= 0xD7A3) ||
            (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE6F) || (cp >= 0xFF00 && cp <= 0xFF60) ||
            (cp >= 0xFFE0 && cp <= 0xFFE6) || (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x20000 && cp <= 0x3FFFD)) return 2;
        return 1;
    }

    private void Put(int cp)
    {
        if (IsCombining(cp)) return;
        int w = CharWidth(cp);

        if (_wrapNext && _autoWrap)
        {
            CursorX = 0;
            LineFeed();
            _screen[CursorY].Wrapped = true;
        }
        _wrapNext = false;
        if (w == 2 && CursorX == Cols - 1)
        {
            if (!_autoWrap) return;
            _screen[CursorY].Cells[CursorX] = default;
            CursorX = 0; LineFeed(); _screen[CursorY].Wrapped = true;
        }

        var row = _screen[CursorY].Cells;
        // Overwriting half of a wide character blanks the other half.
        if (row[CursorX].Cp == -1 && CursorX > 0) row[CursorX - 1] = default;
        if (row[CursorX].Width == 2 && CursorX + 1 < Cols) row[CursorX + 1] = default;

        row[CursorX] = new Cell { Cp = cp, Width = (byte)w, Fg = _fg, Bg = _bg, Attr = _attr };
        if (w == 2 && CursorX + 1 < Cols) row[CursorX + 1] = new Cell { Cp = -1, Width = 0, Fg = _fg, Bg = _bg, Attr = _attr };

        CursorX += w;
        if (CursorX >= Cols) { CursorX = Cols - 1; _wrapNext = true; }
    }

    // ================= cursor & scrolling =================
    private void LineFeed()
    {
        _wrapNext = false;
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        _wrapNext = false;
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    private void PushScrollback(Line l)
    {
        Scrollback.Add(l);
        if (Scrollback.Count > MaxScrollback) Scrollback.RemoveRange(0, Scrollback.Count - MaxScrollback);
    }

    private void ScrollUp(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (int i = 0; i < n; i++)
        {
            var gone = _screen[_top];
            if (_top == 0 && _bottom == Rows - 1 && !AltScreen) PushScrollback(gone);
            Array.Copy(_screen, _top + 1, _screen, _top, _bottom - _top);
            _screen[_bottom] = BlankLine();
        }
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (int i = 0; i < n; i++)
        {
            Array.Copy(_screen, _top, _screen, _top + 1, _bottom - _top);
            _screen[_top] = BlankLine();
        }
    }

    private Line BlankLine()
    {
        var l = new Line(Cols);
        if (_bg != 0) for (int i = 0; i < Cols; i++) l.Cells[i].Bg = _bg;
        return l;
    }

    private void SaveCursor() { _savedX = CursorX; _savedY = CursorY; _savedFg = _fg; _savedBg = _bg; _savedAttr = _attr; }
    private void RestoreCursor()
    {
        CursorX = Math.Clamp(_savedX, 0, Cols - 1); CursorY = Math.Clamp(_savedY, 0, Rows - 1);
        _fg = _savedFg; _bg = _savedBg; _attr = _savedAttr; _wrapNext = false;
    }

    private void FullReset()
    {
        _fg = _bg = 0; _attr = 0; _autoWrap = true; _originMode = false; CursorVisible = true; AppCursorKeys = false; BracketedPaste = false;
        _top = 0; _bottom = Rows - 1; CursorX = CursorY = 0; _wrapNext = false;
        _screen = NewScreen(Rows, Cols); ResetTabs();
    }

    private void MoveTo(int row, int col)
    {
        int top = _originMode ? _top : 0, bottom = _originMode ? _bottom : Rows - 1;
        CursorY = Math.Clamp(row + top, top, bottom);
        CursorX = Math.Clamp(col, 0, Cols - 1);
        _wrapNext = false;
    }

    // ================= CSI =================
    private int[] ParseParams(out bool priv, out char inter)
    {
        string s = _csi.ToString();
        priv = false; inter = '\0';
        if (s.Length > 0 && (s[0] == '?' || s[0] == '>' || s[0] == '<' || s[0] == '=')) { priv = s[0] == '?'; s = s.Substring(1); }
        int end = s.Length;
        while (end > 0 && (s[end - 1] < '0' || s[end - 1] > '9') && s[end - 1] != ';' && s[end - 1] != ':') { inter = s[end - 1]; end--; }
        s = s.Substring(0, end);
        if (s.Length == 0) return Array.Empty<int>();
        var parts = s.Split(';');
        var r = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            int colon = p.IndexOf(':'); if (colon >= 0) p = p.Substring(0, colon);
            r[i] = int.TryParse(p, out int v) ? v : 0;
        }
        return r;
    }

    private static int P(int[] p, int i, int def) => i < p.Length && p[i] > 0 ? p[i] : def;

    private void RunCsi(char final)
    {
        var p = ParseParams(out bool priv, out char inter);
        if (inter != '\0' && final != 'p') { if (inter != '"') return; } // DECSTR, cursor style and the like: not needed

        switch (final)
        {
            case 'A': CursorY = Math.Max(CursorY - P(p, 0, 1), _top <= CursorY ? _top : 0); _wrapNext = false; break;
            case 'B': CursorY = Math.Min(CursorY + P(p, 0, 1), CursorY <= _bottom ? _bottom : Rows - 1); _wrapNext = false; break;
            case 'C': case 'a': CursorX = Math.Min(CursorX + P(p, 0, 1), Cols - 1); _wrapNext = false; break;
            case 'D': CursorX = Math.Max(CursorX - P(p, 0, 1), 0); _wrapNext = false; break;
            case 'E': CursorX = 0; CursorY = Math.Min(CursorY + P(p, 0, 1), Rows - 1); _wrapNext = false; break;
            case 'F': CursorX = 0; CursorY = Math.Max(CursorY - P(p, 0, 1), 0); _wrapNext = false; break;
            case 'G': case '`': CursorX = Math.Clamp(P(p, 0, 1) - 1, 0, Cols - 1); _wrapNext = false; break;
            case 'H': case 'f': MoveTo(P(p, 0, 1) - 1, P(p, 1, 1) - 1); break;
            case 'd': MoveTo(P(p, 0, 1) - 1, CursorX); break;
            case 'J': EraseDisplay(p.Length > 0 ? p[0] : 0); break;
            case 'K': EraseLine(p.Length > 0 ? p[0] : 0); break;
            case 'L': InsertLines(P(p, 0, 1)); break;
            case 'M': DeleteLines(P(p, 0, 1)); break;
            case '@': InsertChars(P(p, 0, 1)); break;
            case 'P': DeleteChars(P(p, 0, 1)); break;
            case 'X': EraseChars(P(p, 0, 1)); break;
            case 'S': ScrollUp(P(p, 0, 1)); break;
            case 'T': ScrollDown(P(p, 0, 1)); break;
            case 'm': Sgr(p); break;
            case 'r':
                {
                    int t = P(p, 0, 1) - 1, b = P(p, 1, Rows) - 1;
                    if (t < b && b < Rows) { _top = t; _bottom = b; }
                    else { _top = 0; _bottom = Rows - 1; }
                    MoveTo(0, 0);
                    break;
                }
            case 's': if (!priv) SaveCursor(); break;
            case 'u': if (!priv) RestoreCursor(); break;
            case 'g': if (p.Length == 0 || p[0] == 0) { if (CursorX < Cols) _tabs[CursorX] = false; } else if (p[0] == 3) Array.Clear(_tabs); break;
            case 'h': SetModes(p, priv, true); break;
            case 'l': SetModes(p, priv, false); break;
            case 'n':
                if (!priv && p.Length > 0 && p[0] == 6) Reply?.Invoke($"\x1b[{CursorY - (_originMode ? _top : 0) + 1};{CursorX + 1}R");
                else if (!priv && p.Length > 0 && p[0] == 5) Reply?.Invoke("\x1b[0n");
                break;
            case 'c': if (p.Length == 0 || p[0] == 0) Reply?.Invoke("\x1b[?1;0c"); break;
        }
    }

    private void SetModes(int[] p, bool priv, bool on)
    {
        foreach (int m in p)
        {
            if (!priv) continue;
            switch (m)
            {
                case 1: AppCursorKeys = on; break;
                case 6: _originMode = on; MoveTo(0, 0); break;
                case 7: _autoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 2004: BracketedPaste = on; break;
                case 47: case 1047: case 1049: SwitchScreen(on, m == 1049); break;
            }
        }
    }

    private void SwitchScreen(bool alt, bool saveCursor)
    {
        if (alt == AltScreen) return;
        if (alt)
        {
            if (saveCursor) SaveCursor();
            _mainScreen = _screen;
            _screen = NewScreen(Rows, Cols);
            AltScreen = true;
        }
        else
        {
            _screen = _mainScreen ?? NewScreen(Rows, Cols);
            _mainScreen = null;
            AltScreen = false;
            if (saveCursor) RestoreCursor();
        }
        _top = 0; _bottom = Rows - 1; _wrapNext = false;
    }

    // ----- erase / insert / delete -----
    private void ClearCells(Line l, int from, int to)
    {
        from = Math.Max(from, 0); to = Math.Min(to, Cols);
        for (int i = from; i < to; i++) l.Cells[i] = new Cell { Bg = _bg };
        if (from == 0 && to == Cols) l.Wrapped = false;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                ClearCells(_screen[CursorY], CursorX, Cols);
                for (int y = CursorY + 1; y < Rows; y++) ClearCells(_screen[y], 0, Cols);
                break;
            case 1:
                for (int y = 0; y < CursorY; y++) ClearCells(_screen[y], 0, Cols);
                ClearCells(_screen[CursorY], 0, CursorX + 1);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) ClearCells(_screen[y], 0, Cols);
                break;
            case 3: Scrollback.Clear(); break;
        }
    }

    private void EraseLine(int mode)
    {
        var l = _screen[CursorY];
        if (mode == 0) ClearCells(l, CursorX, Cols);
        else if (mode == 1) ClearCells(l, 0, CursorX + 1);
        else ClearCells(l, 0, Cols);
    }

    private void EraseChars(int n) => ClearCells(_screen[CursorY], CursorX, CursorX + n);

    private void InsertChars(int n)
    {
        var c = _screen[CursorY].Cells;
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(c, CursorX, c, CursorX + n, Cols - CursorX - n);
        for (int i = 0; i < n; i++) c[CursorX + i] = new Cell { Bg = _bg };
    }

    private void DeleteChars(int n)
    {
        var c = _screen[CursorY].Cells;
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(c, CursorX + n, c, CursorX, Cols - CursorX - n);
        for (int i = Cols - n; i < Cols; i++) c[i] = new Cell { Bg = _bg };
    }

    private void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        n = Math.Min(n, _bottom - CursorY + 1);
        Array.Copy(_screen, CursorY, _screen, CursorY + n, _bottom - CursorY + 1 - n);
        for (int i = 0; i < n; i++) _screen[CursorY + i] = BlankLine();
        CursorX = 0; _wrapNext = false;
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        n = Math.Min(n, _bottom - CursorY + 1);
        Array.Copy(_screen, CursorY + n, _screen, CursorY, _bottom - CursorY + 1 - n);
        for (int i = 0; i < n; i++) _screen[_bottom - i] = BlankLine();
        CursorX = 0; _wrapNext = false;
    }

    // ================= colours =================
    private static readonly uint[] Palette16 = new uint[16];
    private static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;

    /// <summary>The 16 base colours; the view sets them once from the active theme.</summary>
    public static void SetPalette(uint[] colours) { for (int i = 0; i < 16 && i < colours.Length; i++) Palette16[i] = colours[i]; }

    private static uint Xterm256(int n)
    {
        if (n < 16) return Palette16[n];
        if (n < 232)
        {
            n -= 16;
            int r = n / 36, g = n / 6 % 6, b = n % 6;
            int V(int x) => x == 0 ? 0 : 55 + x * 40;
            return Rgb(V(r), V(g), V(b));
        }
        int gray = 8 + (n - 232) * 10;
        return Rgb(gray, gray, gray);
    }

    private void Sgr(int[] p)
    {
        if (p.Length == 0) { _fg = _bg = 0; _attr = 0; return; }
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: _fg = _bg = 0; _attr = 0; break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Dim; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4: _attr |= CellAttr.Underline; break;
                case 7: _attr |= CellAttr.Inverse; break;
                case 9: _attr |= CellAttr.Strike; break;
                case 22: _attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 27: _attr &= ~CellAttr.Inverse; break;
                case 29: _attr &= ~CellAttr.Strike; break;
                case 39: _fg = 0; break;
                case 49: _bg = 0; break;
                case 38: case 48:
                    {
                        uint colour = 0;
                        if (i + 1 < p.Length && p[i + 1] == 5 && i + 2 < p.Length) { colour = Xterm256(Math.Clamp(p[i + 2], 0, 255)); i += 2; }
                        else if (i + 1 < p.Length && p[i + 1] == 2 && i + 4 < p.Length) { colour = Rgb(p[i + 2] & 255, p[i + 3] & 255, p[i + 4] & 255); i += 4; }
                        else break;
                        if (v == 38) _fg = colour; else _bg = colour;
                        break;
                    }
                default:
                    if (v >= 30 && v <= 37) _fg = Palette16[v - 30];
                    else if (v >= 40 && v <= 47) _bg = Palette16[v - 40];
                    else if (v >= 90 && v <= 97) _fg = Palette16[v - 90 + 8];
                    else if (v >= 100 && v <= 107) _bg = Palette16[v - 100 + 8];
                    break;
            }
        }
    }

    // ================= OSC =================
    private void RunOsc()
    {
        string s = _osc.ToString();
        int semi = s.IndexOf(';');
        if (semi < 0) return;
        string code = s.Substring(0, semi);
        if (code is "0" or "2")
        {
            Title = s.Substring(semi + 1);
            TitleChanged?.Invoke(Title);
        }
    }
}
