using System;
using System.IO;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Phase 2: ParallelBZip2DecompressorStream correctness. The critical invariant is
/// "produces identical decompressed bytes to the single-threaded BZip2DecompressorStream
/// for every input." The single-threaded decoder is the oracle.
/// </summary>
public class ParallelBZip2DecompressorStreamTests
{
    private static string FixturesDir => Path.Combine(
        Path.GetDirectoryName(typeof(ParallelBZip2DecompressorStreamTests).Assembly.Location)!,
        "Compression", "Fixtures");

    private static byte[] LoadFixture(string name) => File.ReadAllBytes(Path.Combine(FixturesDir, name));

    private static byte[] DecompressParallel(byte[] bz2Bytes, int workerCount = -1)
    {
        using var input = new MemoryStream(bz2Bytes);
        using var dec = new ParallelBZip2DecompressorStream(input, workerCount: workerCount);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = dec.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, n);
        return output.ToArray();
    }

    private static byte[] DecompressSingleThreaded(byte[] bz2Bytes)
    {
        using var input = new MemoryStream(bz2Bytes);
        using var dec = new BZip2DecompressorStream(input);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = dec.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, n);
        return output.ToArray();
    }

    // --- Single-block correctness: equivalence with single-threaded oracle. ---

    [Fact]
    public void Hello_EquivalentToSingleThreaded()
    {
        var bz2 = LoadFixture("hello.txt.bz2");
        Assert.Equal(DecompressSingleThreaded(bz2), DecompressParallel(bz2));
    }

    [Fact]
    public void Pangram_EquivalentToSingleThreaded()
    {
        var bz2 = LoadFixture("pangram.txt.bz2");
        Assert.Equal(DecompressSingleThreaded(bz2), DecompressParallel(bz2));
    }

    [Fact]
    public void Repeat_EquivalentToSingleThreaded()
    {
        var bz2 = LoadFixture("repeat.txt.bz2");
        Assert.Equal(DecompressSingleThreaded(bz2), DecompressParallel(bz2));
    }

    [Fact]
    public void Hello_EquivalentToOriginal()
    {
        var bz2 = LoadFixture("hello.txt.bz2");
        var original = LoadFixture("hello.txt");
        Assert.Equal(original, DecompressParallel(bz2));
    }

    // --- Worker count knob. ---

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(14)]
    public void DifferentWorkerCounts_ProduceSameOutput(int workers)
    {
        var bz2 = LoadFixture("pangram.txt.bz2");
        var oracle = DecompressSingleThreaded(bz2);
        Assert.Equal(oracle, DecompressParallel(bz2, workerCount: workers));
    }

    // --- Read in odd chunk sizes. ---

    [Fact]
    public void OneByteAtATime_StillProducesCorrectOutput()
    {
        var bz2 = LoadFixture("pangram.txt.bz2");
        var oracle = DecompressSingleThreaded(bz2);

        using var input = new MemoryStream(bz2);
        using var dec = new ParallelBZip2DecompressorStream(input, workerCount: 4);
        using var output = new MemoryStream();
        var buffer = new byte[1];
        int n;
        while ((n = dec.Read(buffer, 0, 1)) > 0)
            output.WriteByte(buffer[0]);

        Assert.Equal(oracle, output.ToArray());
    }

    // --- Multi-block correctness: uses static fixture, no Process.Start dependency. ---

    [Fact]
    public void MultiBlock_EquivalentToSingleThreaded()
    {
        var bz2 = LoadFixture("multiblock.txt.bz2");
        var oracle = DecompressSingleThreaded(bz2);
        var parallelOutput = DecompressParallel(bz2, workerCount: 4);
        Assert.Equal(oracle, parallelOutput);
    }

    [Fact]
    public void MultiBlock_ManyWorkers_EquivalentToSingleThreaded()
    {
        var bz2 = LoadFixture("multiblock.txt.bz2");
        var oracle = DecompressSingleThreaded(bz2);
        var parallelOutput = DecompressParallel(bz2, workerCount: 14);
        Assert.Equal(oracle, parallelOutput);
    }

    [Fact]
    public void MultiBlock_RandomChunkReads_PreserveOrdering()
    {
        var bz2 = LoadFixture("multiblock.txt.bz2");
        var oracle = DecompressSingleThreaded(bz2);

        using var input = new MemoryStream(bz2);
        using var dec = new ParallelBZip2DecompressorStream(input, workerCount: 6);
        using var output = new MemoryStream();
        var rng = new Random(1234);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int chunkSize = 1 + rng.Next(buffer.Length);
            int n = dec.Read(buffer, 0, chunkSize);
            if (n == 0) break;
            output.Write(buffer, 0, n);
        }

        Assert.Equal(oracle, output.ToArray());
    }

    [Fact]
    public void CorruptedBlock_ThrowsInvalidDataException()
    {
        var bz2 = LoadFixture("multiblock.txt.bz2");

        // Flip a byte deep in the bitstream — past any block's header.
        Assert.True(bz2.Length > 200);
        bz2[bz2.Length / 2] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() =>
        {
            DecompressParallel(bz2, workerCount: 4);
        });
    }
}
