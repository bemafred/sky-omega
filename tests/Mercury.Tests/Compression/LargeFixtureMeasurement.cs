using System;
using System.Diagnostics;
using System.IO;
using SkyOmega.Mercury.Compression;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// One-shot architectural validation: measures parallel-bz2 throughput on a fixture
/// large enough to fully feed all workers with steady-state work. The 410 KB committed
/// fixture is too small (~4 blocks) to amortize thread startup on a 14-worker pool.
/// This loads a 6.6 MB compressed / 50 MB raw fixture (~55 blocks) from /tmp; the
/// fixture is generated separately and is NOT committed unless results warrant it.
/// Skipped when the fixture is absent.
/// </summary>
public class LargeFixtureMeasurement
{
    private const string FixturePath = "/tmp/multiblock-large.txt.bz2";
    private readonly ITestOutputHelper _output;

    public LargeFixtureMeasurement(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Measure_LargeFixture_ScalingByWorkerCount()
    {
        if (!File.Exists(FixturePath))
        {
            _output.WriteLine($"SKIP: fixture not present at {FixturePath} — generate via 'bzip2 -9' on a 50 MB text file");
            return;
        }

        var bz2 = File.ReadAllBytes(FixturePath);
        const int iterations = 3;  // bigger fixture, fewer iterations to stay quick

        // Warmup
        DecompressSingleThreaded(bz2);
        for (int w = 1; w <= 14; w *= 2) DecompressParallel(bz2, w);

        long singleThreadedTicks = MeasureMean(iterations, () => DecompressSingleThreaded(bz2));
        var workerCounts = new[] { 1, 2, 4, 8, 14 };
        var parallelTicks = new long[workerCounts.Length];
        for (int i = 0; i < workerCounts.Length; i++)
            parallelTicks[i] = MeasureMean(iterations, () => DecompressParallel(bz2, workerCounts[i]));

        long compressedBytes = bz2.Length;
        long decompressedBytes = DecompressSingleThreaded(bz2).LongLength;

        _output.WriteLine($"Fixture: {FixturePath}");
        _output.WriteLine($"  compressed:   {compressedBytes:N0} bytes ({compressedBytes / (1024.0 * 1024):F2} MB)");
        _output.WriteLine($"  decompressed: {decompressedBytes:N0} bytes ({decompressedBytes / (1024.0 * 1024):F1} MB)");
        _output.WriteLine($"  estimated blocks: ~{decompressedBytes / 900_000} (at 900 KB/block default)");
        _output.WriteLine($"  iterations:   {iterations} (mean reported)");
        _output.WriteLine("");
        _output.WriteLine($"  Configuration         | wall (ms) | input MB/s | output MB/s | speedup");
        _output.WriteLine($"  ----------------------|-----------|------------|-------------|--------");
        ReportRow("single-threaded baseline", singleThreadedTicks, compressedBytes, decompressedBytes, baseline: singleThreadedTicks);
        for (int i = 0; i < workerCounts.Length; i++)
            ReportRow($"parallel ({workerCounts[i]} workers)", parallelTicks[i], compressedBytes, decompressedBytes, baseline: singleThreadedTicks);
    }

    private void ReportRow(string label, long ticks, long compressed, long decompressed, long baseline)
    {
        double seconds = ticks / (double)Stopwatch.Frequency;
        double inputMBps = (compressed / (1024.0 * 1024)) / seconds;
        double outputMBps = (decompressed / (1024.0 * 1024)) / seconds;
        double speedup = baseline / (double)ticks;
        _output.WriteLine($"  {label,-22} | {seconds * 1000,9:F1} | {inputMBps,10:F1} | {outputMBps,11:F1} | {speedup,7:F2}x");
    }

    private static long MeasureMean(int iterations, Action action)
    {
        var sw = new Stopwatch();
        long total = 0;
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            total += sw.ElapsedTicks;
        }
        return total / iterations;
    }

    private static byte[] DecompressSingleThreaded(byte[] bz2)
    {
        using var input = new MemoryStream(bz2);
        using var dec = new BZip2DecompressorStream(input);
        using var output = new MemoryStream(capacity: bz2.Length * 10);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = dec.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
        return output.ToArray();
    }

    private static byte[] DecompressParallel(byte[] bz2, int workers)
    {
        using var input = new MemoryStream(bz2);
        using var dec = new ParallelBZip2DecompressorStream(input, workerCount: workers);
        using var output = new MemoryStream(capacity: bz2.Length * 10);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = dec.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
        return output.ToArray();
    }
}
