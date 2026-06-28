using System.Buffers.Binary;
using System.IO.Compression;

namespace SkyOmega.DrHook.Viz;

/// <summary>A minimal, dependency-free PNG encoder for 8-bit GRAYSCALE images — the substrate piece that lets a
/// view rasterize its (monochrome) text output to a real image the LLM operator can see, with NOTHING beyond the
/// BCL (so DrHook.Viz stays Wire-pure: no System.Drawing — gone cross-platform — no SkiaSharp, no NuGet).
/// Grayscale is faithful here: the console view emits plain text with no ANSI colour, so one luminance sample per
/// pixel loses nothing. PNG = an 8-byte signature + length-prefixed, CRC32-tagged chunks (IHDR, IDAT, IEND); the
/// scanlines are zlib-compressed via the BCL <see cref="ZLibStream"/>, the per-chunk CRC is the standard PNG CRC32.</summary>
public static class PngImageWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encode an 8-bit grayscale image to PNG bytes. <paramref name="pixels"/> is row-major, one byte per
    /// pixel (0 = black … 255 = white), exactly <paramref name="width"/> × <paramref name="height"/> long.</summary>
    public static byte[] EncodeGrayscale(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "width and height must be positive");
        if (pixels.Length != (long)width * height)
            throw new ArgumentException($"pixels length {pixels.Length} != width*height {(long)width * height}", nameof(pixels));

        using var png = new MemoryStream();
        png.Write(Signature);

        // IHDR: dimensions + 8-bit grayscale, deflate, adaptive filtering, no interlace.
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 0;   // colour type: grayscale
        ihdr[10] = 0;  // compression: deflate
        ihdr[11] = 0;  // filter method: adaptive (each scanline carries a filter-type byte)
        ihdr[12] = 0;  // interlace: none
        WriteChunk(png, "IHDR", ihdr);

        // IDAT: zlib over the scanlines, each prefixed with filter byte 0 (None).
        int stride = width + 1;
        var raw = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * stride] = 0; // filter: None
            pixels.Slice(y * width, width).CopyTo(raw.AsSpan(y * stride + 1, width));
        }
        byte[] idat;
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            idat = compressed.ToArray();
        }
        WriteChunk(png, "IDAT", idat);

        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        png.Write(typeBytes);

        png.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(typeBytes, data));
        png.Write(crc);
    }

    // The standard PNG CRC32 (polynomial 0xEDB88320, init/final XOR 0xFFFFFFFF), computed over chunk type + data
    // with no allocation. Table-driven; the BCL's only CRC32 lives in the System.IO.Hashing NuGet, which the
    // Wire-pure DrHook.Viz must not take, so it is hand-rolled here (≈ 15 lines).
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            foreach (byte x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
