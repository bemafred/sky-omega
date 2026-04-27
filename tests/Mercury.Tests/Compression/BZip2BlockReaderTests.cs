using System;
using System.IO;
using System.Reflection;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Decision 6: round-trip verification against fixtures generated out-of-band
/// from the canonical bzip2-1.0.8 tool. The decompressor must produce byte-identical
/// output and the per-block CRC must match the fixture's stored CRC.
/// </summary>
public class BZip2BlockReaderTests
{
    private static string FixturesDir
    {
        get
        {
            // Test binaries land in bin/Debug/net10.0/. The MSBuild pipeline copies
            // Compression/Fixtures/** alongside (CopyToOutputDirectory=PreserveNewest).
            return Path.Combine(
                Path.GetDirectoryName(typeof(BZip2BlockReaderTests).Assembly.Location)!,
                "Compression", "Fixtures");
        }
    }

    private static byte[] LoadFixture(string name) => File.ReadAllBytes(Path.Combine(FixturesDir, name));

    /// <summary>
    /// Decompress the bzip2 frame in <paramref name="bz2Bytes"/>: skip the 4-byte 'BZh<level>'
    /// stream header, read one block, drain. Returns the decompressed bytes (capped at
    /// <paramref name="maxOutput"/> to bound test memory).
    /// </summary>
    private static (byte[] Decompressed, uint ExpectedCrc, uint ComputedCrc) DecompressOneBlock(
        byte[] bz2Bytes, int maxOutput = 1 << 20)
    {
        // Skip 4-byte stream header: 'B','Z','h',<level>.
        Assert.Equal((byte)'B', bz2Bytes[0]);
        Assert.Equal((byte)'Z', bz2Bytes[1]);
        Assert.Equal((byte)'h', bz2Bytes[2]);
        Assert.InRange(bz2Bytes[3], (byte)'1', (byte)'9');
        var bitReader = new BZip2BitReader(bz2Bytes.AsSpan(4));

        var blockReader = new BZip2BlockReader();
        bool hasBlock = blockReader.TryReadBlock(ref bitReader);
        Assert.True(hasBlock, "expected a block, got end-of-stream sentinel");

        uint computedCrc = BZip2Crc32.InitialValue;
        var output = new byte[maxOutput];
        int totalWritten = 0;
        while (totalWritten < maxOutput)
        {
            int n = blockReader.Drain(output.AsSpan(totalWritten), ref computedCrc);
            if (n == 0) break;
            totalWritten += n;
        }
        Array.Resize(ref output, totalWritten);
        return (output, blockReader.ExpectedBlockCrc, BZip2Crc32.Finalize(computedCrc));
    }

    [Fact]
    public void HelloWorld_RoundTrips()
    {
        var bz2 = LoadFixture("hello.txt.bz2");
        var original = LoadFixture("hello.txt");
        var (decompressed, expectedCrc, computedCrc) = DecompressOneBlock(bz2);
        Assert.Equal(original, decompressed);
        Assert.Equal(expectedCrc, computedCrc);
    }

    [Fact]
    public void Pangram_RoundTrips()
    {
        var bz2 = LoadFixture("pangram.txt.bz2");
        var original = LoadFixture("pangram.txt");
        var (decompressed, expectedCrc, computedCrc) = DecompressOneBlock(bz2);
        Assert.Equal(original, decompressed);
        Assert.Equal(expectedCrc, computedCrc);
    }

    [Fact]
    public void HighlyRepetitive_RoundTrips()
    {
        // 100 × "AAAABBBB" = 800 bytes — exercises BWT inverse on highly-redundant input
        // where periodic structure tests the algorithm's tie-breaking robustness.
        var bz2 = LoadFixture("repeat.txt.bz2");
        var original = LoadFixture("repeat.txt");
        var (decompressed, expectedCrc, computedCrc) = DecompressOneBlock(bz2);
        Assert.Equal(original, decompressed);
        Assert.Equal(expectedCrc, computedCrc);
    }
}
