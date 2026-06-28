namespace SkyOmega.DrHook.Viz;

/// <summary>Options for <see cref="TextImageRenderer"/>.</summary>
public sealed class TextImageOptions
{
    /// <summary>Integer pixel-scale per glyph (≥1). 2 ≈ a crisp, readable terminal size.</summary>
    public int Scale { get; init; } = 2;

    /// <summary>Blank border around the text block, in final (scaled) pixels.</summary>
    public int Margin { get; init; } = 8;

    /// <summary>Foreground (text) luminance, 0–255.</summary>
    public byte Foreground { get; init; } = 0xDC;

    /// <summary>Background luminance, 0–255 (a dark terminal by default).</summary>
    public byte Background { get; init; } = 0x10;

    /// <summary>Hard cap on rendered columns, so a pathological line can't blow up the image. Longer lines are
    /// truncated (resource-accounting: the image area is bounded regardless of input).</summary>
    public int MaxColumns { get; init; } = 400;

    /// <summary>Hard cap on rendered rows; extra lines are dropped.</summary>
    public int MaxRows { get; init; } = 400;
}

/// <summary>Rasterizes monospace TEXT to a grayscale PNG — the view-agnostic primitive behind "snapshot the
/// surface as an image". A text view (e.g. <c>ConsoleDebugStateView</c> writing to a <c>StringWriter</c>) produces
/// its exact output; this renders THAT verbatim, so the image is a photograph of the real surface, not a second
/// renderer that could drift from it. BCL-only — embedded <see cref="MonospaceBitmapFont"/> +
/// <see cref="PngImageWriter"/>: no font engine, no System.Drawing, no NuGet; headless and deterministic, so the
/// same primitive serves a live snapshot and CI visual-regression.</summary>
public static class TextImageRenderer
{
    /// <summary>Render <paramref name="text"/> (newline-separated; tabs expanded to four spaces) to PNG bytes.</summary>
    public static byte[] RenderToPng(string text, TextImageOptions? options = null)
    {
        TextImageOptions o = options ?? new TextImageOptions();
        int scale = Math.Max(1, o.Scale);

        // Normalize into a bounded grid. The view emits '\n'; tolerate '\r\n'/'\r' and tabs defensively.
        string[] rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int rows = Math.Min(rawLines.Length, o.MaxRows);
        int cols = 0;
        var lines = new string[rows];
        for (int i = 0; i < rows; i++)
        {
            string line = rawLines[i].Replace("\t", "    ");
            if (line.Length > o.MaxColumns) line = line[..o.MaxColumns];
            lines[i] = line;
            if (line.Length > cols) cols = line.Length;
        }
        if (cols == 0) cols = 1; // a blank frame still yields a valid 1-cell image

        int cellW = MonospaceBitmapFont.Width * scale;
        int cellH = MonospaceBitmapFont.Height * scale;
        int width = cols * cellW + o.Margin * 2;
        int height = rows * cellH + o.Margin * 2;

        var buffer = new byte[width * height];
        buffer.AsSpan().Fill(o.Background);

        for (int r = 0; r < rows; r++)
        {
            string line = lines[r];
            int y0 = o.Margin + r * cellH;
            for (int c = 0; c < line.Length; c++)
                BlitGlyph(buffer, width, MonospaceBitmapFont.Glyph(line[c]), o.Margin + c * cellW, y0, scale, o.Foreground);
        }

        return PngImageWriter.EncodeGrayscale(buffer, width, height);
    }

    // Blit one 8×8 glyph into the buffer at (x0,y0), pixel-doubled by `scale`. bit gx (LSB = leftmost) of row gy
    // set → a scale×scale block of foreground; cells fit within the image by construction (see size maths above).
    private static void BlitGlyph(byte[] buffer, int width, ReadOnlySpan<byte> glyph, int x0, int y0, int scale, byte fg)
    {
        for (int gy = 0; gy < MonospaceBitmapFont.Height; gy++)
        {
            int rowBits = glyph[gy];
            for (int gx = 0; gx < MonospaceBitmapFont.Width; gx++)
            {
                if ((rowBits >> gx & 1) == 0) continue;
                for (int sy = 0; sy < scale; sy++)
                {
                    int rowOffset = (y0 + gy * scale + sy) * width + x0 + gx * scale;
                    for (int sx = 0; sx < scale; sx++) buffer[rowOffset + sx] = fg;
                }
            }
        }
    }
}
