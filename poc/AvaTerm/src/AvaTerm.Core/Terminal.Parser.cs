using System.Text;

namespace AvaTerm.Core;

public sealed partial class Terminal
{
    enum State { Ground, Esc, EscInter, Csi, Osc, OscEsc, Str, StrEsc }

    State _state;
    readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    readonly char[] _chars = new char[8192];
    char _pendingHigh;

    const int MaxParams = 32;
    readonly int[] _params = new int[MaxParams];
    readonly bool[] _sub = new bool[MaxParams];
    int _pcount;
    char _prefix, _inter;
    readonly StringBuilder _osc = new();
    const int MaxOsc = 1 << 20;

    /// <summary>Feeds raw application output (UTF-8 plus control sequences) into the terminal.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var chunk = data[..Math.Min(data.Length, 4096)];
            data = data[chunk.Length..];
            int n = _decoder.GetChars(chunk, _chars, false);
            for (int i = 0; i < n; i++)
            {
                char ch = _chars[i];
                int cp = ch;
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 < n && char.IsLowSurrogate(_chars[i + 1])) cp = char.ConvertToUtf32(ch, _chars[++i]);
                    else { _pendingHigh = ch; continue; }
                }
                else if (char.IsLowSurrogate(ch))
                {
                    cp = _pendingHigh != 0 ? char.ConvertToUtf32(_pendingHigh, ch) : 0xFFFD;
                    _pendingHigh = '\0';
                }

                if (_state == State.Ground && cp >= 0x20 && cp != 0x7F) Print(cp);
                else Process(cp);
            }
        }
    }

    void Process(int cp)
    {
        switch (_state)
        {
            case State.Osc:
                if (cp == 0x07) { _state = State.Ground; OscDispatch(); }
                else if (cp == 0x1B) _state = State.OscEsc;
                else if (cp is 0x18 or 0x1A) _state = State.Ground;
                else if (_osc.Length < MaxOsc && cp >= 0x20) _osc.Append(char.ConvertFromUtf32(cp));
                return;
            case State.OscEsc:
                _state = State.Ground;
                OscDispatch();
                if (cp != '\\') { _state = State.Esc; _inter = '\0'; Process(cp); }
                return;
            case State.Str:
                if (cp == 0x1B) _state = State.StrEsc;
                else if (cp is 0x18 or 0x1A or 0x07) _state = State.Ground;
                return;
            case State.StrEsc:
                _state = State.Ground;
                if (cp != '\\') { _state = State.Esc; _inter = '\0'; Process(cp); }
                return;
        }

        if (cp < 0x20)
        {
            switch (cp)
            {
                case 0x1B: _state = State.Esc; _inter = '\0'; break;
                case 0x18: case 0x1A: _state = State.Ground; break;
                default: Execute(cp); break;
            }
            return;
        }
        if (cp == 0x7F) return;

        switch (_state)
        {
            case State.Ground:
                Print(cp);
                break;

            case State.Esc:
                switch (cp)
                {
                    case '[':
                        _state = State.Csi;
                        _pcount = 1;
                        _params[0] = -1;
                        _sub[0] = false;
                        _prefix = _inter = '\0';
                        break;
                    case ']':
                        _state = State.Osc;
                        _osc.Clear();
                        break;
                    case 'P': case 'X': case '^': case '_':
                        _state = State.Str;
                        break;
                    default:
                        if (cp is >= 0x20 and <= 0x2F) { _inter = (char)cp; _state = State.EscInter; }
                        else { _state = State.Ground; EscDispatch((char)Math.Min(cp, 0xFFFF)); }
                        break;
                }
                break;

            case State.EscInter:
                if (cp is >= 0x20 and <= 0x2F) break;
                _state = State.Ground;
                EscDispatch((char)Math.Min(cp, 0xFFFF));
                break;

            case State.Csi:
                if (cp is >= '0' and <= '9')
                {
                    ref int p = ref _params[_pcount - 1];
                    if (p < 0) p = 0;
                    if (p < 100_000) p = p * 10 + (cp - '0');
                }
                else if (cp is ';' or ':')
                {
                    if (_pcount < MaxParams)
                    {
                        _params[_pcount] = -1;
                        _sub[_pcount] = cp == ':';
                        _pcount++;
                    }
                }
                else if (cp is >= '<' and <= '?') _prefix = (char)cp;
                else if (cp is >= 0x20 and <= 0x2F) _inter = (char)cp;
                else if (cp is >= 0x40 and <= 0x7E)
                {
                    _state = State.Ground;
                    CsiDispatch((char)cp);
                }
                else _state = State.Ground;
                break;
        }
    }

    void Execute(int c)
    {
        var s = _s;
        switch (c)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08:
                if (s.X > 0) s.X--;
                s.WrapPending = false;
                break;
            case 0x09: TabForward(1); break;
            case 0x0A: case 0x0B: case 0x0C:
                LineFeed();
                if (_lnm) s.X = 0;
                break;
            case 0x0D:
                s.X = 0;
                s.WrapPending = false;
                break;
            case 0x0E: _gl = 1; break;
            case 0x0F: _gl = 0; break;
        }
    }

    void EscDispatch(char f)
    {
        var s = _s;
        switch (_inter)
        {
            case '\0':
                switch (f)
                {
                    case '7': SaveCursor(); break;
                    case '8': RestoreCursor(); break;
                    case 'D': LineFeed(); break;
                    case 'E': LineFeed(); s.X = 0; break;
                    case 'H': if (s.X < _tabs.Length) _tabs[s.X] = true; break;
                    case 'M': ReverseIndex(); break;
                    case 'Z': SendReply("\x1b[?62;22c"); break;
                    case 'c': FullReset(); break;
                    case '=': AppKeypad = true; break;
                    case '>': AppKeypad = false; break;
                }
                break;
            case '(': _g0 = f; break;
            case ')': _g1 = f; break;
            case '#':
                if (f == '8')
                {
                    foreach (var l in s.Lines)
                        for (int x = 0; x < Cols; x++) l.Cells[x] = new Cell { Rune = 'E' };
                }
                break;
        }
    }

    int P(int i, int def) => i < _pcount && _params[i] >= 0 ? _params[i] : def;
    int P1(int i) => Math.Max(1, P(i, 1));

    void CsiDispatch(char f)
    {
        var s = _s;

        if (_prefix == '?')
        {
            switch (f)
            {
                case 'h': for (int i = 0; i < _pcount; i++) SetPrivateMode(P(i, 0), true); break;
                case 'l': for (int i = 0; i < _pcount; i++) SetPrivateMode(P(i, 0), false); break;
                case 'n': if (P(0, 0) == 6) SendReply($"\x1b[?{s.Y + 1};{s.X + 1}R"); break;
                case 'p' when _inter == '$':
                {
                    int mode = P(0, 0);
                    bool? v = GetPrivateMode(mode);
                    SendReply($"\x1b[?{mode};{(v == null ? 0 : v.Value ? 1 : 2)}$y");
                    break;
                }
            }
            return;
        }
        if (_prefix == '>')
        {
            if (f == 'c') SendReply("\x1b[>0;10;1c");
            return;
        }
        if (_prefix != '\0') return;

        if (_inter != '\0')
        {
            if (_inter == ' ' && f == 'q')
            {
                int v = P(0, 0);
                CursorShape = v switch { 3 or 4 => CursorShape.Underline, 5 or 6 => CursorShape.Bar, _ => CursorShape.Block };
                CursorBlink = v == 0 || (v & 1) == 1;
            }
            else if (_inter == '!' && f == 'p') SoftReset();
            else if (_inter == '$' && f == 'p') SendReply($"\x1b[{P(0, 0)};0$y");
            return;
        }

        switch (f)
        {
            case '@': InsertChars(P1(0)); break;
            case 'A': CursorUp(P1(0)); break;
            case 'B': case 'e': CursorDown(P1(0)); break;
            case 'C': case 'a': CursorCol(s.X + P1(0)); break;
            case 'D': CursorCol(s.X - P1(0)); break;
            case 'E': CursorDown(P1(0)); s.X = 0; break;
            case 'F': CursorUp(P1(0)); s.X = 0; break;
            case 'G': case '`': CursorCol(P1(0) - 1); break;
            case 'H': case 'f': SetCursor(P1(0) - 1, P1(1) - 1); break;
            case 'I': TabForward(P1(0)); break;
            case 'J': EraseDisplay(P(0, 0)); break;
            case 'K': EraseLine(P(0, 0)); break;
            case 'L': InsertLines(P1(0)); break;
            case 'M': DeleteLines(P1(0)); break;
            case 'P': DeleteChars(P1(0)); break;
            case 'S': ScrollRegionUp(s.Top, s.Bottom, P1(0), true); break;
            case 'T': if (_pcount <= 1) ScrollRegionDown(s.Top, s.Bottom, P1(0)); break;
            case 'X': EraseChars(P1(0)); break;
            case 'Z': TabBackward(P1(0)); break;
            case 'b':
                if (_lastPrinted > 0)
                    for (int n = Math.Min(P1(0), Cols * Rows); n > 0; n--) PrintRaw(_lastPrinted);
                break;
            case 'c': if (P(0, 0) == 0) SendReply("\x1b[?62;22c"); break;
            case 'd':
            {
                int x = s.X;
                SetCursor(P1(0) - 1, x);
                break;
            }
            case 'g':
                if (P(0, 0) == 0) { if (s.X < _tabs.Length) _tabs[s.X] = false; }
                else if (P(0, 0) == 3) Array.Clear(_tabs);
                break;
            case 'h': case 'l':
                for (int i = 0; i < _pcount; i++)
                {
                    if (P(i, 0) == 4) _insert = f == 'h';
                    else if (P(i, 0) == 20) _lnm = f == 'h';
                }
                break;
            case 'm': Sgr(); break;
            case 'n':
                if (P(0, 0) == 5) SendReply("\x1b[0n");
                else if (P(0, 0) == 6) SendReply($"\x1b[{(_origin ? s.Y - s.Top : s.Y) + 1};{s.X + 1}R");
                break;
            case 'r':
            {
                int top = P1(0) - 1, bottom = Math.Min(P(1, Rows), Rows) - 1;
                if (bottom <= 0) bottom = Rows - 1;
                if (top < bottom)
                {
                    s.Top = top;
                    s.Bottom = bottom;
                    SetCursor(0, 0);
                }
                break;
            }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't':
                if (P(0, 0) == 18) SendReply($"\x1b[8;{Rows};{Cols}t");
                break;
        }
    }

    void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 5: ReverseVideo = on; break;
            case 6: _origin = on; SetCursor(0, 0); break;
            case 7: _autoWrap = on; if (!on) _s.WrapPending = false; break;
            case 12: CursorBlink = on; break;
            case 25: CursorVisible = on; break;
            case 47: if (on) EnterAlt(false); else LeaveAlt(); break;
            case 1047: if (on) EnterAlt(true); else LeaveAlt(); break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 1049:
                if (on) { SaveCursor(); EnterAlt(true); }
                else { LeaveAlt(); RestoreCursor(); }
                break;
            case 9: case 1000: MouseMode = on ? MouseTracking.Normal : MouseTracking.None; break;
            case 1002: MouseMode = on ? MouseTracking.Button : MouseTracking.None; break;
            case 1003: MouseMode = on ? MouseTracking.Any : MouseTracking.None; break;
            case 1004: FocusEvents = on; break;
            case 1006: MouseSgr = on; break;
            case 1007: AlternateScroll = on; break;
            case 2004: BracketedPaste = on; break;
        }
    }

    bool? GetPrivateMode(int mode) => mode switch
    {
        1 => AppCursorKeys,
        5 => ReverseVideo,
        6 => _origin,
        7 => _autoWrap,
        12 => CursorBlink,
        25 => CursorVisible,
        47 or 1047 or 1049 => AltScreenActive,
        1000 => MouseMode == MouseTracking.Normal,
        1002 => MouseMode == MouseTracking.Button,
        1003 => MouseMode == MouseTracking.Any,
        1004 => FocusEvents,
        1006 => MouseSgr,
        1007 => AlternateScroll,
        2004 => BracketedPaste,
        _ => null,
    };

    void Sgr()
    {
        for (int i = 0; i < _pcount; i++)
        {
            int p = P(i, 0);
            switch (p)
            {
                case 0: _fg = _bg = 0; _attr = 0; break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Faint; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4:
                    if (i + 1 < _pcount && _sub[i + 1])
                    {
                        if (P(i + 1, 1) == 0) _attr &= ~CellAttr.Underline; else _attr |= CellAttr.Underline;
                        while (i + 1 < _pcount && _sub[i + 1]) i++;
                    }
                    else _attr |= CellAttr.Underline;
                    break;
                case 5: case 6: _attr |= CellAttr.Blink; break;
                case 7: _attr |= CellAttr.Inverse; break;
                case 8: _attr |= CellAttr.Invisible; break;
                case 9: _attr |= CellAttr.Strike; break;
                case 21: _attr |= CellAttr.Underline; break;
                case 22: _attr &= ~(CellAttr.Bold | CellAttr.Faint); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 25: _attr &= ~CellAttr.Blink; break;
                case 27: _attr &= ~CellAttr.Inverse; break;
                case 28: _attr &= ~CellAttr.Invisible; break;
                case 29: _attr &= ~CellAttr.Strike; break;
                case 38: if (ExtendedColor(ref i, out uint fg)) _fg = fg; break;
                case 39: _fg = 0; break;
                case 48: if (ExtendedColor(ref i, out uint bg)) _bg = bg; break;
                case 49: _bg = 0; break;
                case 58: ExtendedColor(ref i, out _); break;
                default:
                    if (p is >= 30 and <= 37) _fg = TermColor.Indexed(p - 30);
                    else if (p is >= 40 and <= 47) _bg = TermColor.Indexed(p - 40);
                    else if (p is >= 90 and <= 97) _fg = TermColor.Indexed(p - 90 + 8);
                    else if (p is >= 100 and <= 107) _bg = TermColor.Indexed(p - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>Parses 38/48/58 in both the ';' and the ':' flavour. Leaves i on the last consumed parameter.</summary>
    bool ExtendedColor(ref int i, out uint color)
    {
        color = 0;
        if (i + 1 < _pcount && _sub[i + 1])
        {
            int start = i + 1, end = start;
            while (end < _pcount && _sub[end]) end++;
            int count = end - start;
            i = end - 1;
            int mode = P(start, 0);
            if (mode == 5 && count >= 2) { color = TermColor.Indexed(P(start + 1, 0)); return true; }
            if (mode == 2 && count >= 4)
            {
                int o = count >= 5 ? end - 3 : start + 1;   // skip the optional colour-space id
                color = TermColor.Rgb(P(o, 0), P(o + 1, 0), P(o + 2, 0));
                return true;
            }
            return false;
        }
        if (i + 1 >= _pcount) return false;
        int m = P(i + 1, 0);
        if (m == 5 && i + 2 < _pcount)
        {
            color = TermColor.Indexed(P(i + 2, 0));
            i += 2;
            return true;
        }
        if (m == 2 && i + 4 < _pcount)
        {
            color = TermColor.Rgb(P(i + 2, 0), P(i + 3, 0), P(i + 4, 0));
            i += 4;
            return true;
        }
        i = _pcount;
        return false;
    }

    void OscDispatch()
    {
        string str = _osc.ToString();
        _osc.Clear();
        int semi = str.IndexOf(';');
        string code = semi < 0 ? str : str[..semi];
        string arg = semi < 0 ? "" : str[(semi + 1)..];
        switch (code)
        {
            case "0":
            case "2":
                Title = arg;
                TitleChanged?.Invoke(arg);
                break;
            case "10": if (arg == "?") ReplyColor(10, DefaultForegroundRgb); break;
            case "11": if (arg == "?") ReplyColor(11, DefaultBackgroundRgb); break;
            case "12": if (arg == "?") ReplyColor(12, DefaultForegroundRgb); break;
            case "52":
            {
                int s2 = arg.IndexOf(';');
                if (s2 < 0 || !AllowClipboardWrite) break;
                string b64 = arg[(s2 + 1)..];
                if (b64 == "?") break;   // never hand the clipboard to an application
                try { ClipboardSet?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(b64))); }
                catch (FormatException) { }
                break;
            }
        }
    }

    void ReplyColor(int code, uint rgb)
    {
        uint r = rgb >> 16 & 0xFF, g = rgb >> 8 & 0xFF, b = rgb & 0xFF;
        SendReply($"\x1b]{code};rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}\x1b\\");
    }
}
