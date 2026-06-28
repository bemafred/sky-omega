namespace SkyOmega.DrHook.Viz;

/// <summary>An embedded 8×8 monospace bitmap font — the glyph data for rasterizing the console view's text to an
/// image with no font engine (the BCL has none). The basic-Latin table (U+0000–U+007F) is the public-domain IBM
/// VGA font (Marcel Sondaar / Daniel Hepper's <c>font8x8</c>, Public Domain), embedded as DATA, not a dependency.
/// A small hand-authored supplement covers the handful of non-ASCII glyphs the console view actually emits
/// (─ ► ▸ ● Δ …) so the rendered image stays faithful to what the terminal shows. Each glyph is 8 bytes, one per
/// row top→bottom; within a row bit 0 (LSB) is the LEFTMOST pixel (the font8x8 convention, verified by render).</summary>
internal static class MonospaceBitmapFont
{
    public const int Width = 8;
    public const int Height = 8;

    // 128 glyphs × 8 bytes (basic Latin), decoded once from the embedded public-domain font8x8 table.
    private static readonly byte[] Basic = Convert.FromBase64String(
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGDw8GBgAGAA2NgAAAAAAADY2fzZ/NjYADD4DHjAfDAAAYzMYDGZjABw2HG47M24ABgYDAAAAAAAYDAYGBgwYAAYMGBgYDAYAAGY8/zxmAAAADAw/DAwAAAAAAAAADAwGAAAAPwAAAAAAAAAAAAwMAGAwGAwGAwEAPmNze29nPgAMDgwMDAw/AB4zMBwGMz8AHjMwHDAzHgA4PDYzfzB4AD8DHzAwMx4AHAYDHzMzHgA/MzAYDAwMAB4zMx4zMx4AHjMzPjAYDgAADAwAAAwMAAAMDAAADAwGGAwGAwYMGAAAAD8AAD8AAAYMGDAYDAYAHjMwGAwADAA+Y3t7ewMeAAweMzM/MzMAP2ZmPmZmPwA8ZgMDA2Y8AB82ZmZmNh8Af0YWHhZGfwB/RhYeFgYPADxmAwNzZnwAMzMzPzMzMwAeDAwMDAweAHgwMDAzMx4AZ2Y2HjZmZwAPBgYGRmZ/AGN3f39rY2MAY2dve3NjYwAcNmNjYzYcAD9mZj4GBg8AHjMzMzseOAA/ZmY+NmZnAB4zBw44Mx4APy0MDAwMHgAzMzMzMzM/ADMzMzMzHgwAY2Nja393YwBjYzYcHDZjADMzMx4MDB4Af2MxGExmfwAeBgYGBgYeAAMGDBgwYEAAHhgYGBgYHgAIHDZjAAAAAAAAAAAAAAD/DAwYAAAAAAAAAB4wPjNuAAcGBj5mZjsAAAAeMwMzHgA4MDA+MzNuAAAAHjM/Ax4AHDYGDwYGDwAAAG4zMz4wHwcGNm5mZmcADAAODAwMHgAwADAwMDMzHgcGZjYeNmcADgwMDAwMHgAAADN/f2tjAAAAHzMzMzMAAAAeMzMzHgAAADtmZj4GDwAAbjMzPjB4AAA7bmYGDwAAAD4DHjAfAAgMPgwMLBgAAAAzMzMzbgAAADMzMx4MAAAAY2t/fzYAAABjNhw2YwAAADMzMz4wHwAAPxkMJj8AOAwMBwwMOAAYGBgAGBgYAAcMDDgMDAcAbjsAAAAAAAAAAAAAAAAAAA==");

    // The non-ASCII glyphs the console view renders (see ConsoleDebugStateView). Hand-authored 8×8 in the same
    // bit convention — keeps the image faithful without pulling in font8x8's box/greek/geometric tables.
    private static readonly Dictionary<char, byte[]> Supplemental = new()
    {
        ['─'] = [0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00], // U+2500 box drawings light horizontal (spans the cell so runs connect)
        ['—'] = [0x00, 0x00, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x00], // U+2014 em dash (inset, so it reads as punctuation, not a connector)
        ['►'] = [0x00, 0x02, 0x0E, 0x3E, 0x3E, 0x0E, 0x02, 0x00], // U+25BA black right-pointing pointer (source caret)
        ['▸'] = [0x00, 0x00, 0x02, 0x0E, 0x0E, 0x02, 0x00, 0x00], // U+25B8 black right-pointing small triangle (hypothesis)
        ['●'] = [0x00, 0x1C, 0x3E, 0x3E, 0x3E, 0x1C, 0x00, 0x00], // U+25CF black circle (status)
        ['Δ'] = [0x00, 0x08, 0x14, 0x14, 0x22, 0x22, 0x7F, 0x00], // U+0394 greek capital letter delta (delta marker)
        ['…'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x2A, 0x00], // U+2026 horizontal ellipsis
    };

    /// <summary>The 8 row-bytes for <paramref name="c"/>: ASCII from the embedded table, the few mapped non-ASCII
    /// glyphs from the supplement, anything else falls back to '?' so an unexpected glyph is visible — never blank
    /// or an exception.</summary>
    public static ReadOnlySpan<byte> Glyph(char c)
    {
        if (c < 128) return Basic.AsSpan(c * Height, Height);
        if (Supplemental.TryGetValue(c, out byte[]? g)) return g;
        return Basic.AsSpan('?' * Height, Height);
    }
}
