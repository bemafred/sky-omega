using System.Globalization;
using System.Text;

namespace AvaTerm.Core;

[Flags]
public enum CellAttr : ushort
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Invisible = 64,
    Strike = 128,
    /// <summary>Lead cell of a double-width character.</summary>
    Wide = 256,
    /// <summary>Second cell of a double-width character.</summary>
    WideTail = 512,
}

/// <summary>
/// Colours are packed into a uint: 0 = default, 0x01xxxxNN = palette index, 0x02RRGGBB = direct colour.
/// </summary>
public static class TermColor
{
    public const uint Default = 0;
    public static uint Indexed(int i) => 0x0100_0000u | (uint)(i & 0xFF);
    public static uint Rgb(int r, int g, int b) => 0x0200_0000u | (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
    public static bool IsDefault(uint c) => c >> 24 == 0;
    public static bool IsIndexed(uint c) => c >> 24 == 1;
    public static bool IsRgb(uint c) => c >> 24 == 2;
    public static int Index(uint c) => (int)(c & 0xFF);
    public static uint RgbValue(uint c) => c & 0xFFFFFF;
}

public struct Cell
{
    /// <summary>Unicode scalar value; 0 means "never written" and renders as a blank.</summary>
    public int Rune;
    public uint Fg;
    public uint Bg;
    public CellAttr Attr;
    /// <summary>Combining marks attached to <see cref="Rune"/>, if any.</summary>
    public string? Combining;

    public readonly bool IsBlank => Rune == 0 || Rune == ' ';
}

public sealed class TermLine
{
    public Cell[] Cells;
    /// <summary>True when this line continues on the next one (soft wrap).</summary>
    public bool Wrapped;

    public TermLine(int cols) => Cells = new Cell[cols];

    public void EnsureWidth(int cols)
    {
        if (Cells.Length < cols) Array.Resize(ref Cells, cols);
    }

    public void Fill(int from, int to, in Cell blank)
    {
        to = Math.Min(to, Cells.Length);
        for (int i = Math.Max(0, from); i < to; i++) Cells[i] = blank;
    }

    /// <summary>Index one past the last non-blank cell, looking at the first <paramref name="limit"/> cells.</summary>
    public int ContentLength(int limit)
    {
        int n = Math.Min(limit, Cells.Length);
        while (n > 0 && Cells[n - 1].IsBlank && (Cells[n - 1].Attr & CellAttr.WideTail) == 0) n--;
        return n;
    }

    public bool IsEmpty()
    {
        foreach (ref readonly var c in Cells.AsSpan())
            if (!c.IsBlank || c.Bg != 0) return false;
        return true;
    }
}

/// <summary>Bounded FIFO used for scrollback. Grows lazily up to its capacity.</summary>
public sealed class RingBuffer<T>
{
    T[] _items;
    int _head, _count;
    public int Capacity { get; }
    public int Count => _count;

    public RingBuffer(int capacity)
    {
        Capacity = Math.Max(0, capacity);
        _items = new T[Math.Min(Capacity, 256)];
    }

    public T this[int i] => _items[(_head + i) % _items.Length];

    /// <summary>Appends an item. Returns true when the oldest item had to be dropped to make room.</summary>
    public bool Push(T item)
    {
        if (Capacity == 0) return false;
        if (_count == _items.Length && _items.Length < Capacity)
        {
            var bigger = new T[Math.Min(Capacity, _items.Length * 2)];
            for (int i = 0; i < _count; i++) bigger[i] = this[i];
            _items = bigger;
            _head = 0;
        }
        if (_count < _items.Length)
        {
            _items[(_head + _count) % _items.Length] = item;
            _count++;
            return false;
        }
        _items[_head] = item;
        _head = (_head + 1) % _items.Length;
        return true;
    }

    public T PopLast()
    {
        int idx = (_head + _count - 1) % _items.Length;
        var item = _items[idx];
        _items[idx] = default!;
        _count--;
        return item;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _head = _count = 0;
    }
}

public static class UnicodeWidth
{
    /// <summary>Number of terminal cells a code point occupies: 0, 1 or 2.</summary>
    public static int Get(int cp)
    {
        if (cp < 0x300) return 1;
        if (cp >= 0x1100 && IsWide(cp)) return 2;
        if (!Rune.IsValid(cp)) return 1;
        switch (Rune.GetUnicodeCategory(new Rune(cp)))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
                return 0;
        }
        return 1;
    }

    static bool IsWide(int cp) =>
        cp <= 0x115F ||
        cp is >= 0x2E80 and <= 0x303E ||
        cp is >= 0x3041 and <= 0x33FF ||
        cp is >= 0x3400 and <= 0x4DBF ||
        cp is >= 0x4E00 and <= 0x9FFF ||
        cp is >= 0xA000 and <= 0xA4CF ||
        cp is >= 0xAC00 and <= 0xD7A3 ||
        cp is >= 0xF900 and <= 0xFAFF ||
        cp is >= 0xFE30 and <= 0xFE4F ||
        cp is >= 0xFF00 and <= 0xFF60 ||
        cp is >= 0xFFE0 and <= 0xFFE6 ||
        cp is >= 0x1F300 and <= 0x1F64F ||
        cp is >= 0x1F680 and <= 0x1F6FF ||
        cp is >= 0x1F900 and <= 0x1F9FF ||
        cp is >= 0x1FA70 and <= 0x1FAFF ||
        cp is >= 0x20000 and <= 0x3FFFD;
}
