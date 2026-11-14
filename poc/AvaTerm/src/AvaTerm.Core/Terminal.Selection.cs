using System.Text;

namespace AvaTerm.Core;

public enum SelectionUnit { Char, Word, Line }

/// <summary>A cell position addressed by absolute line id, so it survives scrolling.</summary>
public readonly record struct BufferPos(long Line, int Col) : IComparable<BufferPos>
{
    public int CompareTo(BufferPos o) => Line != o.Line ? Line.CompareTo(o.Line) : Col.CompareTo(o.Col);
    public static BufferPos Min(BufferPos a, BufferPos b) => a.CompareTo(b) <= 0 ? a : b;
    public static BufferPos Max(BufferPos a, BufferPos b) => a.CompareTo(b) >= 0 ? a : b;
}

public sealed partial class Terminal
{
    const int EndOfLine = int.MaxValue / 2;

    BufferPos _anchor0, _anchor1, _active0, _active1;
    SelectionUnit _selMode;

    public bool HasSelection { get; private set; }

    /// <summary>Inclusive start and end of the selection.</summary>
    public (BufferPos Start, BufferPos End) SelectionRange =>
        (BufferPos.Min(_anchor0, _active0), BufferPos.Max(_anchor1, _active1));

    public void StartSelection(long line, int col, SelectionUnit mode)
    {
        _selMode = mode;
        (_anchor0, _anchor1) = Unit(line, col, mode);
        _active0 = _anchor0;
        _active1 = _anchor1;
        HasSelection = true;
    }

    public void ExtendSelection(long line, int col)
    {
        if (!HasSelection) return;
        (_active0, _active1) = Unit(line, col, _selMode);
    }

    public void ClearSelection() => HasSelection = false;

    (BufferPos, BufferPos) Unit(long id, int col, SelectionUnit mode)
    {
        col = Math.Clamp(col, 0, Cols - 1);
        switch (mode)
        {
            case SelectionUnit.Line:
                return (new(id, 0), new(id, EndOfLine));
            case SelectionUnit.Word:
            {
                var line = GetLine(id);
                if (line == null) break;
                int limit = Math.Min(Cols, line.Cells.Length);
                if (col >= limit) break;
                int cls = CharClass(line, col);
                int a = col, b = col;
                if (cls != 2)
                {
                    while (a > 0 && CharClass(line, a - 1) == cls) a--;
                    while (b + 1 < limit && CharClass(line, b + 1) == cls) b++;
                }
                return (new(id, a), new(id, b));
            }
        }
        return (new(id, col), new(id, col));
    }

    static int CharClass(TermLine line, int col)
    {
        var cell = line.Cells[col];
        if ((cell.Attr & CellAttr.WideTail) != 0 && col > 0) cell = line.Cells[col - 1];
        if (cell.IsBlank) return 0;
        if (cell.Rune > 0x7F) return 1;
        char c = (char)cell.Rune;
        return char.IsLetterOrDigit(c) || "_-./~:@%+#=?&".Contains(c) ? 1 : 2;
    }

    public string GetSelectionText()
    {
        if (!HasSelection) return "";
        var (start, end) = SelectionRange;
        var sb = new StringBuilder();
        for (long id = start.Line; id <= end.Line; id++)
        {
            var line = GetLine(id);
            if (line == null) continue;
            int limit = Math.Min(Cols, line.Cells.Length);
            int c0 = id == start.Line ? start.Col : 0;
            int c1 = id == end.Line ? Math.Min(end.Col, limit - 1) : limit - 1;
            bool toEol = c1 >= limit - 1;
            int mark = sb.Length;
            for (int c = c0; c <= c1; c++)
            {
                ref readonly var cell = ref line.Cells[c];
                if ((cell.Attr & CellAttr.WideTail) != 0) continue;
                sb.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
                if (cell.Combining != null) sb.Append(cell.Combining);
            }
            if (toEol && !line.Wrapped)
            {
                while (sb.Length > mark && sb[^1] == ' ') sb.Length--;
                if (id != end.Line) sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Number of cursor-key presses (positive = right, negative = left) that move the shell's
    /// cursor to the clicked cell, or 0 when the click is not on the line being edited.
    /// Only meaningful on the primary screen with the live view showing.
    /// </summary>
    public int ComputeCursorMove(int row, int col)
    {
        if (AltScreenActive || _viewOffset != 0 || MouseMode != MouseTracking.None) return 0;
        var s = _s;
        row = Math.Clamp(row, 0, Rows - 1);
        col = Math.Clamp(col, 0, Cols - 1);

        // The click must land on the same logical (soft-wrapped) line as the cursor.
        for (int y = Math.Min(row, s.Y); y < Math.Max(row, s.Y); y++)
            if (!s.Lines[y].Wrapped) return 0;

        // Do not run past the end of what is on the line.
        int content = s.Lines[row].ContentLength(Cols);
        if (row == s.Y) content = Math.Max(content, s.X);
        col = Math.Min(col, content);

        int from = s.Y * Cols + s.X, to = row * Cols + col;
        if (from == to) return 0;
        int lo = Math.Min(from, to), hi = Math.Max(from, to), steps = 0;
        for (int p = lo; p < hi; p++)
        {
            var cells = s.Lines[p / Cols].Cells;
            if ((cells[p % Cols].Attr & CellAttr.WideTail) == 0) steps++;
        }
        return to > from ? steps : -steps;
    }
}
