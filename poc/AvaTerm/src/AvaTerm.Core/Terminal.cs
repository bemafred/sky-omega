using System.Text;

namespace AvaTerm.Core;

public enum MouseTracking { None, Normal, Button, Any }
public enum CursorShape { Block, Underline, Bar }

internal sealed class SavedCursor
{
    public int X, Y, GL;
    public uint Fg, Bg;
    public CellAttr Attr;
    public bool Origin, WrapPending;
    public char G0, G1;
}

internal sealed class Screen
{
    public TermLine[] Lines = [];
    public int X, Y, Top, Bottom;
    public bool WrapPending;
    public SavedCursor? Saved;
}

/// <summary>
/// xterm-compatible terminal state machine: two screens, scrollback, modes and selection.
/// Not thread-safe by itself; every caller takes <see cref="Sync"/>.
/// </summary>
public sealed partial class Terminal
{
    public readonly object Sync = new();

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    readonly Screen _primary = new(), _alt = new();
    Screen _s;
    readonly RingBuffer<TermLine> _scrollback;
    long _baseId;          // absolute id of _scrollback[0]
    int _viewOffset;

    // pen
    uint _fg, _bg;
    CellAttr _attr;

    // modes
    bool _autoWrap = true, _origin, _insert, _lnm;
    char _g0 = 'B', _g1 = 'B';
    int _gl;
    bool[] _tabs;
    int _lastPrinted;

    public bool CursorVisible { get; private set; } = true;
    public bool CursorBlink { get; private set; } = true;
    public CursorShape CursorShape { get; private set; } = CursorShape.Block;
    public bool AppCursorKeys { get; private set; }
    public bool AppKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool FocusEvents { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool AlternateScroll { get; private set; } = true;
    public MouseTracking MouseMode { get; private set; }
    public bool MouseSgr { get; private set; }
    public bool NewLineMode => _lnm;
    public string Title { get; private set; } = "";

    /// <summary>Colours reported to applications asking via OSC 10/11 (0xRRGGBB).</summary>
    public uint DefaultForegroundRgb { get; set; } = 0xD8D8D8;
    public uint DefaultBackgroundRgb { get; set; } = 0x101418;
    /// <summary>Allow applications to set the clipboard through OSC 52.</summary>
    public bool AllowClipboardWrite { get; set; } = true;

    /// <summary>Bytes the terminal wants to send back to the application. Raised under <see cref="Sync"/>.</summary>
    public event Action<byte[]>? Reply;
    public event Action<string>? TitleChanged;
    public event Action? Bell;
    public event Action<string>? ClipboardSet;

    public Terminal(int cols, int rows, int scrollbackLines = 10_000)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(1, rows);
        _scrollback = new RingBuffer<TermLine>(scrollbackLines);
        InitScreen(_primary);
        InitScreen(_alt);
        _s = _primary;
        _tabs = MakeTabs(Cols);
    }

    void InitScreen(Screen s)
    {
        s.Lines = new TermLine[Rows];
        for (int i = 0; i < Rows; i++) s.Lines[i] = new TermLine(Cols);
        s.X = s.Y = 0;
        s.Top = 0;
        s.Bottom = Rows - 1;
        s.WrapPending = false;
        s.Saved = null;
    }

    static bool[] MakeTabs(int cols)
    {
        var t = new bool[cols];
        for (int i = 8; i < cols; i += 8) t[i] = true;
        return t;
    }

    // ------------------------------------------------------------------ view

    public bool AltScreenActive => _s == _alt;
    public int ScrollbackCount => AltScreenActive ? 0 : _scrollback.Count;
    public int CursorX => _s.X;
    public int CursorY => _s.Y;

    /// <summary>How many lines the view is scrolled back from the live screen.</summary>
    public int ViewOffset
    {
        get => _viewOffset;
        set => _viewOffset = Math.Clamp(value, 0, ScrollbackCount);
    }

    /// <summary>Absolute id of the first live screen row. Ids stay stable while lines scroll into history.</summary>
    public long ScreenTopId => _baseId + _scrollback.Count;
    public long ViewTopId => ScreenTopId - _viewOffset;

    public TermLine? GetLine(long id)
    {
        long idx = id - _baseId;
        if (idx < 0) return null;
        if (idx < _scrollback.Count) return AltScreenActive ? null : _scrollback[(int)idx];
        idx -= _scrollback.Count;
        return idx < Rows ? _s.Lines[idx] : null;
    }

    public TermLine GetViewLine(int row) => GetLine(ViewTopId + row) ?? _s.Lines[Math.Clamp(row, 0, Rows - 1)];

    // ------------------------------------------------------------------ helpers

    Cell Blank => new() { Bg = _bg };

    TermLine NewLine()
    {
        var l = new TermLine(Cols);
        if (_bg != 0) l.Fill(0, Cols, Blank);
        return l;
    }

    void ClearLine(TermLine l)
    {
        l.Fill(0, l.Cells.Length, Blank);
        l.Wrapped = false;
    }

    void SendReply(string s) => Reply?.Invoke(Encoding.ASCII.GetBytes(s));

    void PushScrollback(TermLine line)
    {
        if (_scrollback.Capacity == 0) return;
        if (_scrollback.Push(line)) _baseId++;
        // Keep a scrolled-back view anchored to the same text while output continues.
        if (_viewOffset > 0) _viewOffset = Math.Min(_viewOffset + 1, _scrollback.Count);
    }

    // ------------------------------------------------------------------ scrolling

    void ScrollRegionUp(int top, int bottom, int n, bool save)
    {
        var s = _s;
        n = Math.Clamp(n, 0, bottom - top + 1);
        if (n == 0) return;
        bool toHistory = save && s == _primary && top == 0;
        if (!toHistory) ClearSelection();
        for (int k = 0; k < n; k++)
        {
            var removed = s.Lines[top];
            Array.Copy(s.Lines, top + 1, s.Lines, top, bottom - top);
            s.Lines[bottom] = NewLine();
            if (toHistory) PushScrollback(removed);
        }
    }

    void ScrollRegionDown(int top, int bottom, int n)
    {
        var s = _s;
        n = Math.Clamp(n, 0, bottom - top + 1);
        if (n == 0) return;
        ClearSelection();
        for (int k = 0; k < n; k++)
        {
            Array.Copy(s.Lines, top, s.Lines, top + 1, bottom - top);
            s.Lines[top] = NewLine();
        }
    }

    void LineFeed()
    {
        var s = _s;
        s.WrapPending = false;
        if (s.Y == s.Bottom) ScrollRegionUp(s.Top, s.Bottom, 1, true);
        else if (s.Y < Rows - 1) s.Y++;
    }

    void ReverseIndex()
    {
        var s = _s;
        s.WrapPending = false;
        if (s.Y == s.Top) ScrollRegionDown(s.Top, s.Bottom, 1);
        else if (s.Y > 0) s.Y--;
    }

    // ------------------------------------------------------------------ printing

    static readonly int[] DecGraphics =
        "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·".EnumerateRunes().Select(r => r.Value).ToArray();

    void Print(int cp)
    {
        if ((_gl == 0 ? _g0 : _g1) == '0' && cp >= 0x60 && cp <= 0x7E) cp = DecGraphics[cp - 0x60];
        PrintRaw(cp);
    }

    void PrintRaw(int cp)
    {
        if (cp is >= 0x80 and <= 0x9F) return;   // C1 controls are not printable
        var s = _s;
        int w = UnicodeWidth.Get(cp);

        if (w == 0)
        {
            int px = s.WrapPending ? s.X : s.X - 1;
            if (px < 0) return;
            var cells = s.Lines[s.Y].Cells;
            if ((cells[px].Attr & CellAttr.WideTail) != 0 && px > 0) px--;
            ref var target = ref cells[px];
            if (target.Rune != 0 && (target.Combining?.Length ?? 0) < 16)
                target.Combining += char.ConvertFromUtf32(cp);
            return;
        }

        if (s.WrapPending)
        {
            s.WrapPending = false;
            if (_autoWrap)
            {
                s.Lines[s.Y].Wrapped = true;
                s.X = 0;
                LineFeed();
            }
        }

        if (w == 2 && s.X >= Cols - 1)
        {
            if (_autoWrap)
            {
                var cur = s.Lines[s.Y];
                cur.Cells[s.X] = Blank;
                cur.Wrapped = true;
                s.X = 0;
                LineFeed();
            }
            else s.X = Math.Max(0, Cols - 2);
        }

        var line = s.Lines[s.Y];
        if (_insert) InsertChars(w);

        BreakWide(line, s.X);
        line.Cells[s.X] = new Cell { Rune = cp, Fg = _fg, Bg = _bg, Attr = _attr | (w == 2 ? CellAttr.Wide : 0) };
        if (w == 2 && s.X + 1 < Cols)
        {
            BreakWide(line, s.X + 1);
            line.Cells[s.X + 1] = new Cell { Fg = _fg, Bg = _bg, Attr = _attr | CellAttr.WideTail };
        }

        s.X += w;
        if (s.X >= Cols)
        {
            s.X = Cols - 1;
            s.WrapPending = _autoWrap;
        }
        _lastPrinted = cp;
    }

    /// <summary>Before overwriting a cell, detach whatever half of a wide character lives there.</summary>
    void BreakWide(TermLine line, int x)
    {
        var cells = line.Cells;
        if ((cells[x].Attr & CellAttr.WideTail) != 0 && x > 0)
        {
            cells[x - 1].Rune = ' ';
            cells[x - 1].Attr &= ~CellAttr.Wide;
        }
        else if ((cells[x].Attr & CellAttr.Wide) != 0 && x + 1 < cells.Length)
        {
            cells[x + 1].Attr &= ~CellAttr.WideTail;
        }
    }

    // ------------------------------------------------------------------ cursor

    void SetCursor(int row, int col)
    {
        var s = _s;
        if (_origin) row = Math.Clamp(row + s.Top, s.Top, s.Bottom);
        else row = Math.Clamp(row, 0, Rows - 1);
        s.Y = row;
        s.X = Math.Clamp(col, 0, Cols - 1);
        s.WrapPending = false;
    }

    void CursorUp(int n)
    {
        var s = _s;
        int limit = s.Y >= s.Top ? s.Top : 0;
        s.Y = Math.Max(limit, s.Y - n);
        s.WrapPending = false;
    }

    void CursorDown(int n)
    {
        var s = _s;
        int limit = s.Y <= s.Bottom ? s.Bottom : Rows - 1;
        s.Y = Math.Min(limit, s.Y + n);
        s.WrapPending = false;
    }

    void CursorCol(int x)
    {
        _s.X = Math.Clamp(x, 0, Cols - 1);
        _s.WrapPending = false;
    }

    void TabForward(int n)
    {
        var s = _s;
        while (n-- > 0 && s.X < Cols - 1)
        {
            s.X++;
            while (s.X < Cols - 1 && !_tabs[s.X]) s.X++;
        }
    }

    void TabBackward(int n)
    {
        var s = _s;
        s.WrapPending = false;
        while (n-- > 0 && s.X > 0)
        {
            s.X--;
            while (s.X > 0 && !_tabs[s.X]) s.X--;
        }
    }

    void SaveCursor()
    {
        var s = _s;
        s.Saved = new SavedCursor
        {
            X = s.X, Y = s.Y, Fg = _fg, Bg = _bg, Attr = _attr,
            Origin = _origin, WrapPending = s.WrapPending, G0 = _g0, G1 = _g1, GL = _gl,
        };
    }

    void RestoreCursor()
    {
        var s = _s;
        var c = s.Saved;
        if (c == null)
        {
            s.X = s.Y = 0;
            s.WrapPending = false;
            _fg = _bg = 0;
            _attr = 0;
            _origin = false;
            return;
        }
        s.X = Math.Clamp(c.X, 0, Cols - 1);
        s.Y = Math.Clamp(c.Y, 0, Rows - 1);
        s.WrapPending = c.WrapPending && s.X == Cols - 1;
        _fg = c.Fg; _bg = c.Bg; _attr = c.Attr;
        _origin = c.Origin;
        _g0 = c.G0; _g1 = c.G1; _gl = c.GL;
    }

    // ------------------------------------------------------------------ editing

    void InsertChars(int n)
    {
        var s = _s;
        var line = s.Lines[s.Y];
        n = Math.Min(n, Cols - s.X);
        if (n <= 0) return;
        BreakWide(line, s.X);
        Array.Copy(line.Cells, s.X, line.Cells, s.X + n, Cols - s.X - n);
        line.Fill(s.X, s.X + n, Blank);
    }

    void DeleteChars(int n)
    {
        var s = _s;
        var line = s.Lines[s.Y];
        n = Math.Min(n, Cols - s.X);
        if (n <= 0) return;
        BreakWide(line, s.X);
        Array.Copy(line.Cells, s.X + n, line.Cells, s.X, Cols - s.X - n);
        line.Fill(Cols - n, Cols, Blank);
        s.WrapPending = false;
    }

    void EraseChars(int n)
    {
        var s = _s;
        var line = s.Lines[s.Y];
        BreakWide(line, s.X);
        line.Fill(s.X, Math.Min(Cols, s.X + n), Blank);
        s.WrapPending = false;
    }

    void EraseLine(int mode)
    {
        var s = _s;
        var line = s.Lines[s.Y];
        switch (mode)
        {
            case 0:
                BreakWide(line, s.X);
                line.Fill(s.X, line.Cells.Length, Blank);
                line.Wrapped = false;
                break;
            case 1:
                line.Fill(0, s.X + 1, Blank);
                break;
            case 2:
                ClearLine(line);
                break;
        }
        s.WrapPending = false;
    }

    void EraseDisplay(int mode)
    {
        var s = _s;
        if (AltScreenActive) ClearSelection();
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int y = s.Y + 1; y < Rows; y++) ClearLine(s.Lines[y]);
                break;
            case 1:
                for (int y = 0; y < s.Y; y++) ClearLine(s.Lines[y]);
                EraseLine(1);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) ClearLine(s.Lines[y]);
                break;
            case 3:
                ClearSelection();
                _scrollback.Clear();
                _viewOffset = 0;
                break;
        }
        s.WrapPending = false;
    }

    void InsertLines(int n)
    {
        var s = _s;
        if (s.Y < s.Top || s.Y > s.Bottom) return;
        ScrollRegionDown(s.Y, s.Bottom, n);
        s.X = 0;
        s.WrapPending = false;
    }

    void DeleteLines(int n)
    {
        var s = _s;
        if (s.Y < s.Top || s.Y > s.Bottom) return;
        ScrollRegionUp(s.Y, s.Bottom, n, false);
        s.X = 0;
        s.WrapPending = false;
    }

    // ------------------------------------------------------------------ screens & reset

    void EnterAlt(bool clear)
    {
        if (_s == _alt) return;
        _alt.X = _primary.X;
        _alt.Y = _primary.Y;
        _alt.Top = 0;
        _alt.Bottom = Rows - 1;
        _alt.WrapPending = false;
        ClearSelection();
        _s = _alt;
        _viewOffset = 0;
        if (clear)
            foreach (var l in _alt.Lines) ClearLine(l);
    }

    void LeaveAlt()
    {
        if (_s == _primary) return;
        ClearSelection();
        _primary.X = Math.Clamp(_alt.X, 0, Cols - 1);
        _primary.Y = Math.Clamp(_alt.Y, 0, Rows - 1);
        _primary.WrapPending = false;
        _s = _primary;
    }

    void SoftReset()
    {
        CursorVisible = true;
        _origin = false;
        _autoWrap = true;
        _insert = false;
        AppCursorKeys = false;
        AppKeypad = false;
        _s.Top = 0;
        _s.Bottom = Rows - 1;
        _s.Saved = null;
        _fg = _bg = 0;
        _attr = 0;
        _g0 = _g1 = 'B';
        _gl = 0;
    }

    void FullReset()
    {
        LeaveAlt();
        SoftReset();
        MouseMode = MouseTracking.None;
        MouseSgr = false;
        BracketedPaste = false;
        FocusEvents = false;
        ReverseVideo = false;
        AlternateScroll = true;
        _lnm = false;
        CursorShape = CursorShape.Block;
        CursorBlink = true;
        ClearSelection();
        _scrollback.Clear();
        _viewOffset = 0;
        InitScreen(_primary);
        InitScreen(_alt);
        _tabs = MakeTabs(Cols);
    }

    // ------------------------------------------------------------------ resize

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;
        ClearSelection();
        int oldRows = Rows;
        Cols = cols;
        Rows = rows;
        ResizeScreen(_primary, oldRows, true);
        ResizeScreen(_alt, oldRows, false);
        _tabs = MakeTabs(cols);
        _viewOffset = Math.Clamp(_viewOffset, 0, ScrollbackCount);
    }

    void ResizeScreen(Screen s, int oldRows, bool primary)
    {
        var list = new List<TermLine>(s.Lines);
        foreach (var l in list) l.EnsureWidth(Cols);

        if (Rows < oldRows)
        {
            int remove = oldRows - Rows;
            // Blank rows below the cursor go first; after that the top scrolls into history.
            while (remove > 0 && list.Count - 1 > s.Y && list[^1].IsEmpty())
            {
                list.RemoveAt(list.Count - 1);
                remove--;
            }
            while (remove > 0)
            {
                var top = list[0];
                list.RemoveAt(0);
                if (primary) PushScrollback(top);
                s.Y = Math.Max(0, s.Y - 1);
                remove--;
            }
        }
        else if (Rows > oldRows)
        {
            int add = Rows - oldRows;
            while (add > 0 && primary && _scrollback.Count > 0)
            {
                var l = _scrollback.PopLast();
                l.EnsureWidth(Cols);
                list.Insert(0, l);
                s.Y++;
                add--;
            }
            while (add-- > 0) list.Add(new TermLine(Cols));
        }

        s.Lines = list.ToArray();
        s.Top = 0;
        s.Bottom = Rows - 1;
        s.X = Math.Clamp(s.X, 0, Cols - 1);
        s.Y = Math.Clamp(s.Y, 0, Rows - 1);
        s.WrapPending = false;
    }
}
