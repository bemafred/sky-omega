using System.Text;

namespace AvaTerm.Core;

public enum TermKey
{
    Up, Down, Left, Right, Home, End, Insert, Delete, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Enter, Backspace, Tab, Escape,
}

[Flags]
public enum TermMods { None = 0, Shift = 1, Alt = 2, Ctrl = 4 }

public enum MouseAction { Press, Release, Move, WheelUp, WheelDown }

/// <summary>Turns keyboard, mouse and paste input into the byte sequences xterm would send.</summary>
public static class InputEncoder
{
    const string Esc = "\u001b";
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    public static byte[] Key(Terminal t, TermKey key, TermMods mods)
    {
        int m = 1 + (int)mods;   // xterm modifier parameter
        bool alt = (mods & TermMods.Alt) != 0;
        switch (key)
        {
            case TermKey.Enter:
                return B((alt ? Esc : "") + (t.NewLineMode ? "\r\n" : "\r"));
            case TermKey.Escape:
                return B((alt ? Esc : "") + Esc);
            case TermKey.Tab:
                return B((mods & TermMods.Shift) != 0 ? Esc + "[Z" : (alt ? Esc : "") + "\t");
            case TermKey.Backspace:
                return B((alt ? Esc : "") + ((mods & TermMods.Ctrl) != 0 ? "\b" : "\u007f"));

            case TermKey.Up: return Letter('A');
            case TermKey.Down: return Letter('B');
            case TermKey.Right: return Letter('C');
            case TermKey.Left: return Letter('D');
            case TermKey.Home: return Letter('H');
            case TermKey.End: return Letter('F');
            case TermKey.F1: return Fn('P');
            case TermKey.F2: return Fn('Q');
            case TermKey.F3: return Fn('R');
            case TermKey.F4: return Fn('S');

            case TermKey.Insert: return Tilde(2);
            case TermKey.Delete: return Tilde(3);
            case TermKey.PageUp: return Tilde(5);
            case TermKey.PageDown: return Tilde(6);
            case TermKey.F5: return Tilde(15);
            case TermKey.F6: return Tilde(17);
            case TermKey.F7: return Tilde(18);
            case TermKey.F8: return Tilde(19);
            case TermKey.F9: return Tilde(20);
            case TermKey.F10: return Tilde(21);
            case TermKey.F11: return Tilde(23);
            case TermKey.F12: return Tilde(24);
        }
        return [];

        byte[] Letter(char c) => m > 1 ? B($"{Esc}[1;{m}{c}") : B(Esc + (t.AppCursorKeys ? "O" : "[") + c);
        byte[] Fn(char c) => m > 1 ? B($"{Esc}[1;{m}{c}") : B(Esc + "O" + c);
        byte[] Tilde(int n) => m > 1 ? B($"{Esc}[{n};{m}~") : B($"{Esc}[{n}~");
    }

    /// <summary>Control code for Ctrl+<paramref name="c"/>, or -1 when the combination has none.</summary>
    public static int ControlCode(char c)
    {
        c = char.ToLowerInvariant(c);
        if (c is >= 'a' and <= 'z') return c - 'a' + 1;
        return c switch
        {
            ' ' or '@' or '2' => 0,
            '[' or '3' => 27,
            '\\' or '4' => 28,
            ']' or '5' => 29,
            '^' or '6' => 30,
            '_' or '/' or '7' or '-' => 31,
            '8' or '?' => 127,
            _ => -1,
        };
    }

    public static byte[] Text(string text, bool altPrefix = false)
    {
        var sb = new StringBuilder(text.Length + 1);
        if (altPrefix) sb.Append(Esc);
        foreach (char ch in text)
            if (ch >= 0x20 && ch != 0x7F) sb.Append(ch);
        return B(sb.ToString());
    }

    public static byte[] Paste(Terminal t, string text)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (!t.BracketedPaste) return B(text);
        // An embedded end marker would let pasted text break out of the bracket.
        text = text.Replace(Esc + "[201~", "");
        return B(Esc + "[200~" + text + Esc + "[201~");
    }

    public static byte[] Focus(bool focused) => B(Esc + (focused ? "[I" : "[O"));

    /// <summary>Cursor-key presses for click-to-place; see <see cref="Terminal.ComputeCursorMove"/>.</summary>
    public static byte[] CursorMove(Terminal t, int delta)
    {
        string one = Esc + (t.AppCursorKeys ? "O" : "[") + (delta > 0 ? 'C' : 'D');
        var sb = new StringBuilder();
        for (int i = Math.Min(Math.Abs(delta), 4096); i > 0; i--) sb.Append(one);
        return B(sb.ToString());
    }

    /// <summary>
    /// Encodes a mouse event for the application, or returns null when the current
    /// tracking mode does not want it. <paramref name="button"/>: 0 left, 1 middle, 2 right, -1 none.
    /// </summary>
    public static byte[]? Mouse(Terminal t, MouseAction action, int button, int col, int row, TermMods mods)
    {
        var mode = t.MouseMode;
        if (mode == MouseTracking.None) return null;

        int code;
        switch (action)
        {
            case MouseAction.Press:
            case MouseAction.Release:
                if (button < 0) return null;
                code = button;
                break;
            case MouseAction.Move:
                if (mode == MouseTracking.Normal) return null;
                if (mode == MouseTracking.Button && button < 0) return null;
                code = 32 + (button < 0 ? 3 : button);
                break;
            case MouseAction.WheelUp: code = 64; break;
            case MouseAction.WheelDown: code = 65; break;
            default: return null;
        }
        if ((mods & TermMods.Shift) != 0) code += 4;
        if ((mods & TermMods.Alt) != 0) code += 8;
        if ((mods & TermMods.Ctrl) != 0) code += 16;

        col = Math.Clamp(col, 0, t.Cols - 1) + 1;
        row = Math.Clamp(row, 0, t.Rows - 1) + 1;

        if (t.MouseSgr)
            return B($"{Esc}[<{code};{col};{row}{(action == MouseAction.Release ? 'm' : 'M')}");

        // Legacy encoding: one byte per value, so it cannot address cells beyond 223.
        if (action == MouseAction.Release) code = code & ~3 | 3;
        if (col > 223 || row > 223) return null;
        return [0x1B, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(32 + col), (byte)(32 + row)];
    }
}
