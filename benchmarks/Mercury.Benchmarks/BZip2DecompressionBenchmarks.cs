using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Compression;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Decompression of the committed multi-block bzip2 fixture (410 KB compressed, 3 MB of text) by the single-threaded
/// stream and by the parallel stream at 1 to 14 workers (ADR-036).
/// </summary>
/// <remarks>
/// <para>This was a test, <c>ParallelBZip2ThroughputMeasurement</c>, that asserted 14 workers decompress faster than
/// one thread. That depends on the machine and its load: it failed on the GitHub macOS x64 runner on 2026-10-30
/// (322 ms against 291 ms) and had passed there the run before. A speed is measured here, not asserted
/// (ADR-059 phase 7).</para>
/// <para>The fixture is embedded from <c>tests/Mercury.Tests/Compression/Fixtures/multiblock.txt.bz2</c>, so the
/// benchmark process reads it without a path.</para>
/// </remarks>
[MemoryDiagnoser]
public class BZip2DecompressionBenchmarks
{
    [Params(1, 2, 4, 8, 14)]
    public int Workers { get; set; }

    private byte[] _compressed = null!;
    private byte[] _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        using var fixture = typeof(BZip2DecompressionBenchmarks).Assembly.GetManifestResourceStream("multiblock.txt.bz2")
            ?? throw new InvalidOperationException("multiblock.txt.bz2 is not embedded in the benchmark assembly.");
        using var copy = new MemoryStream();
        fixture.CopyTo(copy);
        _compressed = copy.ToArray();
        _buffer = new byte[64 * 1024];
    }

    [Benchmark(Baseline = true)]
    public long SingleThreaded()
    {
        using var input = new MemoryStream(_compressed);
        using var decompressor = new BZip2DecompressorStream(input);
        long decompressed = 0;
        int read;
        while ((read = decompressor.Read(_buffer, 0, _buffer.Length)) > 0)
            decompressed += read;
        return decompressed;
    }

    [Benchmark]
    public long Parallel()
    {
        using var input = new MemoryStream(_compressed);
        using var decompressor = new ParallelBZip2DecompressorStream(input, workerCount: Workers);
        long decompressed = 0;
        int read;
        while ((read = decompressor.Read(_buffer, 0, _buffer.Length)) > 0)
            decompressed += read;
        return decompressed;
    }
}
