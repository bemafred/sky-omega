// The image-rendering subsystem (ADR-012 visual snapshot, A): the BCL-only PNG encoder, the monospace bitmap
// font, and the text→image renderer. These lock the substrate that lets a view rasterize its text output to an
// image the LLM operator can see. No external files, no display — pure in-memory, deterministic (so the same
// primitive is sound for CI visual-regression).

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkyOmega.DrHook.Viz;
using Xunit;

namespace SkyOmega.DrHook.Viz.Tests;

public sealed class PngImageWriterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void EncodeGrayscale_ProducesAValidPng_ThatRoundTripsToTheSourcePixels()
    {
        // A 3×2 grayscale image with distinct sample values — the round-trip proves the whole encoder: chunk
        // framing, the CRC, the zlib stream, and the per-scanline filter byte.
        byte[] pixels = [0, 128, 255, 10, 20, 30];
        byte[] png = PngImageWriter.EncodeGrayscale(pixels, width: 3, height: 2);

        Assert.True(png.AsSpan(0, 8).SequenceEqual(PngSignature), "PNG signature");

        // IHDR is the first chunk, at a fixed offset: len(8..11), "IHDR"(12..15), width(16..19), height(20..23),
        // bitdepth(24), colourtype(25).
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4)));
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(8, png[24]); // bit depth
        Assert.Equal(0, png[25]); // colour type: grayscale

        // Walk the chunks, gather IDAT, inflate it, strip each scanline's leading filter byte, compare pixels.
        var idat = new MemoryStream();
        int offset = 8;
        string type;
        do
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT") idat.Write(png, offset + 8, length);
            offset += 12 + length; // len(4) + type(4) + data + crc(4)
        } while (type != "IEND");

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        byte[] scanlines = raw.ToArray();

        Assert.Equal((3 + 1) * 2, scanlines.Length); // (width + filter byte) × height
        Assert.Equal(0, scanlines[0]);               // row 0 filter: None
        Assert.Equal(new byte[] { 0, 128, 255 }, scanlines[1..4]);
        Assert.Equal(0, scanlines[4]);               // row 1 filter: None
        Assert.Equal(new byte[] { 10, 20, 30 }, scanlines[5..8]);
    }

    [Fact]
    public void EncodeGrayscale_RejectsMismatchedLengthAndNonPositiveDimensions()
    {
        Assert.Throws<ArgumentException>(() => PngImageWriter.EncodeGrayscale([0, 0, 0], width: 2, height: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngImageWriter.EncodeGrayscale([], width: 0, height: 1));
    }
}

public sealed class TextImageRendererTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void RenderToPng_SizesTheImageToTheTextGrid_PlusMargins()
    {
        // "Hi" = 2 columns, 1 row; default scale 2 → 16px cells; default margin 8.
        byte[] png = TextImageRenderer.RenderToPng("Hi");
        Assert.True(png.AsSpan(0, 8).SequenceEqual(PngSignature));

        // IHDR width is at offset 16, height at 20 (big-endian), the first chunk after the 8-byte signature.
        Assert.Equal(2 * 16 + 8 * 2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));  // cols·cellW + 2·margin
        Assert.Equal(1 * 16 + 8 * 2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));  // rows·cellH + 2·margin
    }

    [Fact]
    public void RenderToPng_GrowsWithRowsColumnsAndScale()
    {
        byte[] scale1 = TextImageRenderer.RenderToPng("ab\ncd", new TextImageOptions { Scale = 1, Margin = 0 });
        Assert.Equal(2 * 8, BinaryPrimitives.ReadInt32BigEndian(scale1.AsSpan(16, 4)));  // 2 cols × 8px
        Assert.Equal(2 * 8, BinaryPrimitives.ReadInt32BigEndian(scale1.AsSpan(20, 4)));  // 2 rows × 8px

        byte[] scale3 = TextImageRenderer.RenderToPng("ab\ncd", new TextImageOptions { Scale = 3, Margin = 0 });
        Assert.Equal(2 * 24, BinaryPrimitives.ReadInt32BigEndian(scale3.AsSpan(16, 4)));  // scale tripled
        Assert.Equal(2 * 24, BinaryPrimitives.ReadInt32BigEndian(scale3.AsSpan(20, 4)));
    }

    [Fact]
    public void RenderToPng_IsDeterministic_SoItCanAnchorVisualRegression()
    {
        byte[] a = TextImageRenderer.RenderToPng("doubled=4, contribution=12");
        byte[] b = TextImageRenderer.RenderToPng("doubled=4, contribution=12");
        Assert.Equal(a, b);
    }

    [Fact]
    public void RenderToPng_ActuallyRasterizesGlyphs_SoDifferentTextDiffers()
    {
        // Different glyphs and a blank glyph must change the pixels — proof the renderer draws the font, not a
        // fixed image. (Same dimensions, so only the rasterized content can differ.)
        byte[] a = TextImageRenderer.RenderToPng("A");
        byte[] b = TextImageRenderer.RenderToPng("B");
        byte[] blank = TextImageRenderer.RenderToPng(" ");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, blank);
    }

    [Fact]
    public void RenderToPng_BoundsTheImage_WhenALineExceedsMaxColumns()
    {
        // A pathological 10 000-char line must not allocate a 10 000-cell-wide image: it is capped (resource
        // accounting — the rendered area is bounded regardless of input).
        byte[] png = TextImageRenderer.RenderToPng(new string('x', 10_000),
            new TextImageOptions { Scale = 1, Margin = 0, MaxColumns = 10 });
        Assert.Equal(10 * 8, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4))); // capped at 10 cells × 8px

    }

    [Fact]
    public void RenderToPng_HandlesEmptyText_WithoutCrashing()
    {
        byte[] png = TextImageRenderer.RenderToPng("");
        Assert.True(png.AsSpan(0, 8).SequenceEqual(PngSignature)); // a valid (small) image, not an exception
    }
}

public sealed class MonospaceBitmapFontTests
{
    [Fact]
    public void Glyph_ReturnsTheEmbeddedAsciiBitmap()
    {
        // 'A' from the public-domain font8x8 table (verified bit pattern, LSB = leftmost pixel).
        Assert.Equal(new byte[] { 0x0C, 0x1E, 0x33, 0x33, 0x3F, 0x33, 0x33, 0x00 },
            MonospaceBitmapFont.Glyph('A').ToArray());
    }

    [Fact]
    public void Glyph_CoversTheNonAsciiGlyphsTheConsoleViewEmits()
    {
        // Each mapped supplemental glyph must be non-blank (the console view emits all of these).
        foreach (char c in new[] { '─', '—', '►', '▸', '●', 'Δ', '…' })
            Assert.Contains(MonospaceBitmapFont.Glyph(c).ToArray(), b => b != 0);
    }

    [Fact]
    public void Glyph_FallsBackToQuestionMark_ForAnUnmappedGlyph()
    {
        // An unexpected glyph is shown as '?', never blank or an exception.
        Assert.Equal(MonospaceBitmapFont.Glyph('?').ToArray(), MonospaceBitmapFont.Glyph('￯').ToArray());
    }
}
