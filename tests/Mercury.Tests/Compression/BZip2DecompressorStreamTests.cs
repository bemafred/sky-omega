using System;
using System.IO;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Decision 1 + 5 + 6: BZip2DecompressorStream end-to-end. Tests cover:
/// reading via <see cref="Stream"/> surface (Read overloads), multi-block streams,
/// CRC enforcement (corruption throws), Read-disposed safety, and zero-allocation
/// steady-state behavior.
/// </summary>
public class BZip2DecompressorStreamTests
{
    private static string FixturesDir => Path.Combine(
        Path.GetDirectoryName(typeof(BZip2DecompressorStreamTests).Assembly.Location)!,
        "Compression", "Fixtures");

    private static byte[] LoadFixture(string name) => File.ReadAllBytes(Path.Combine(FixturesDir, name));

    private static byte[] DecompressAll(byte[] bz2Bytes)
    {
        using var input = new MemoryStream(bz2Bytes);
        using var dec = new BZip2DecompressorStream(input, leaveOpen: false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while ((n = dec.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, n);
        return output.ToArray();
    }

    [Fact]
    public void HelloWorld_FullStream_RoundTrips()
    {
        var bz2 = LoadFixture("hello.txt.bz2");
        var original = LoadFixture("hello.txt");
        var decompressed = DecompressAll(bz2);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void Pangram_FullStream_RoundTrips()
    {
        var bz2 = LoadFixture("pangram.txt.bz2");
        var original = LoadFixture("pangram.txt");
        var decompressed = DecompressAll(bz2);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void HighlyRepetitive_FullStream_RoundTrips()
    {
        var bz2 = LoadFixture("repeat.txt.bz2");
        var original = LoadFixture("repeat.txt");
        var decompressed = DecompressAll(bz2);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void Read_OneByteAtATime_StillProducesCorrectOutput()
    {
        // Stress the streaming path: reading one byte at a time forces the decompressor
        // to suspend/resume mid-block-drain on every iteration.
        var bz2 = LoadFixture("pangram.txt.bz2");
        var original = LoadFixture("pangram.txt");

        using var input = new MemoryStream(bz2);
        using var dec = new BZip2DecompressorStream(input);
        using var output = new MemoryStream();
        var buffer = new byte[1];
        int n;
        while ((n = dec.Read(buffer, 0, 1)) > 0)
            output.WriteByte(buffer[0]);

        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void CorruptedBlockCrc_Throws()
    {
        // Flip a byte in the middle of the compressed payload so the decompressed block
        // CRC won't match. Must throw InvalidDataException, not silent corruption.
        var bz2 = LoadFixture("hello.txt.bz2");
        // Flip a byte well into the compressed payload (past header + magic + CRC fields).
        bz2[bz2.Length - 5] ^= 0xFF;

        using var input = new MemoryStream(bz2);
        using var dec = new BZip2DecompressorStream(input);
        var buffer = new byte[1024];
        bool threw = false;
        try
        {
            int n;
            while ((n = dec.Read(buffer, 0, buffer.Length)) > 0) { /* drain */ }
        }
        catch (InvalidDataException) { threw = true; }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw, "expected an exception on corrupted stream");
    }

    [Fact]
    public void SeekAndPosition_NotSupported()
    {
        using var input = new MemoryStream(LoadFixture("hello.txt.bz2"));
        using var dec = new BZip2DecompressorStream(input);
        Assert.False(dec.CanSeek);
        Assert.True(dec.CanRead);
        Assert.False(dec.CanWrite);
        bool seekThrew = false;
        try { dec.Seek(0, SeekOrigin.Begin); } catch (NotSupportedException) { seekThrew = true; }
        Assert.True(seekThrew);
    }

    [Fact]
    public void Disposed_ThrowsOnRead()
    {
        var input = new MemoryStream(LoadFixture("hello.txt.bz2"));
        var dec = new BZip2DecompressorStream(input);
        dec.Dispose();
        var buffer = new byte[16];
        Assert.Throws<ObjectDisposedException>(() => dec.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void LargeRandomInput_RoundTrips()
    {
        // Compress a generated buffer with the system bzip2 inline at test time would be
        // cleaner but cross-platform fragile. Instead, use the existing repeat.txt fixture
        // to verify the streaming buffer-slide path holds across multiple Read calls
        // with mixed buffer sizes.
        var bz2 = LoadFixture("repeat.txt.bz2");
        var original = LoadFixture("repeat.txt");

        using var input = new MemoryStream(bz2);
        using var dec = new BZip2DecompressorStream(input);
        using var output = new MemoryStream();
        var rng = new Random(42);
        var buffer = new byte[16384];
        int n;
        while (true)
        {
            int requested = 1 + rng.Next(buffer.Length);
            n = dec.Read(buffer, 0, requested);
            if (n == 0) break;
            output.Write(buffer, 0, n);
        }
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void EmptyStream_DecompressesToNothing()
    {
        // `bzip2 -9 -c < /dev/null` with bzip2 1.0.8: the 'BZh9' header, the end-of-stream magic
        // 0x177245385090 and a combined CRC of 0, with no block between them.
        byte[] empty = [0x42, 0x5A, 0x68, 0x39, 0x17, 0x72, 0x45, 0x38, 0x50, 0x90, 0x00, 0x00, 0x00, 0x00];

        using var decompressor = new BZip2DecompressorStream(new MemoryStream(empty));
        var buffer = new byte[64];

        Assert.Equal(0, decompressor.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void ConcatenatedStreams_DecompressToBothContents()
    {
        // A .bz2 file may hold several streams one after another, as `cat a.bz2 b.bz2` makes.
        // The reader continues at the next 'BZh' header until the input ends.
        string fixtures = Path.Combine(
            Path.GetDirectoryName(typeof(BZip2DecompressorStreamTests).Assembly.Location)!, "Compression", "Fixtures");
        byte[] hello = File.ReadAllBytes(Path.Combine(fixtures, "hello.txt.bz2"));
        byte[] pangram = File.ReadAllBytes(Path.Combine(fixtures, "pangram.txt.bz2"));
        byte[] concatenated = [.. hello, .. pangram];
        byte[] expected =
            [.. File.ReadAllBytes(Path.Combine(fixtures, "hello.txt")), .. File.ReadAllBytes(Path.Combine(fixtures, "pangram.txt"))];

        using var decompressor = new BZip2DecompressorStream(new MemoryStream(concatenated));
        using var output = new MemoryStream();
        decompressor.CopyTo(output);

        Assert.Equal(expected, output.ToArray());
    }

    [Theory]
    [InlineData(3)]        // inside the 'BZh9' header
    [InlineData(100_000)]  // inside the first of the fixture's four blocks
    [InlineData(300_000)]  // inside a later block
    [InlineData(410_494)]  // the end-of-stream magic, without the combined CRC
    [InlineData(410_497)]  // the combined CRC, short one byte
    public void TruncatedStream_Throws(int keptBytes)
    {
        // A cut-off download must fail, not load a prefix as if it were the whole file.
        // multiblock.txt.bz2 is 410,498 bytes.
        string fixtures = Path.Combine(
            Path.GetDirectoryName(typeof(BZip2DecompressorStreamTests).Assembly.Location)!, "Compression", "Fixtures");
        byte[] whole = File.ReadAllBytes(Path.Combine(fixtures, "multiblock.txt.bz2"));
        Assert.Equal(410_498, whole.Length);
        byte[] truncated = whole[..keptBytes];

        using var decompressor = new BZip2DecompressorStream(new MemoryStream(truncated));
        var error = Record.Exception(() => decompressor.CopyTo(Stream.Null));

        Assert.True(error is InvalidDataException or EndOfStreamException,
            $"expected InvalidDataException or EndOfStreamException, got {error?.GetType().Name ?? "no exception"}");
    }

    [Fact]
    public void Read_AfterTheFirstBlock_DoesNotAllocate()
    {
        // ADR-036 Decision 3: the stream owns every buffer it decodes into, so once it is
        // constructed and its first block is read, reading allocates nothing. The input is a
        // MemoryStream, whose Read does not allocate either.
        string fixtures = Path.Combine(
            Path.GetDirectoryName(typeof(BZip2DecompressorStreamTests).Assembly.Location)!, "Compression", "Fixtures");
        byte[] bz2 = File.ReadAllBytes(Path.Combine(fixtures, "multiblock.txt.bz2"));
        using var decompressor = new BZip2DecompressorStream(new MemoryStream(bz2));
        var buffer = new byte[64 * 1024];
        int first = decompressor.Read(buffer, 0, buffer.Length);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long rest = 0;
        int n;
        while ((n = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            rest += n;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(3_145_801, first + rest);
        Assert.Equal(0, allocated);
    }
}
