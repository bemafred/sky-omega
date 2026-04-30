using System;
using System.Diagnostics;
using System.IO;
using SkyOmega.Mercury.Compression;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// EEE Epistemics gate for ADR-036 Phase 2: measures the actual throughput improvement
/// from parallel bzip2 decompression vs the single-threaded baseline. The architectural
/// argument for parallel bz2 (per docs/limits/bz2-decompression-single-threaded.md) is
/// 33 MB/s single-threaded → ~300 MB/s parallel ceiling. This test produces actual
/// measurements on the 3 MB-source / 410 KB-compressed multi-block fixture for a
/// representative workload shape, and validates that parallel ≥ single-threaded at
/// the worker counts that matter for production.
/// </summary>
public class ParallelBZip2ThroughputMeasurement
{
    private readonly ITestOutputHelper _output;

    public ParallelBZip2ThroughputMeasurement(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string FixturesDir => Path.Combine(
        Path.GetDirectoryName(typeof(ParallelBZip2ThroughputMeasurement).Assembly.Location)!,
        "Compression", "Fixtures");

    [Fact]
    public void Measure_SingleThreaded_vs_Parallel()
    {
        var bz2 = File.ReadAllBytes(Path.Combine(FixturesDir, "multiblock.txt.bz2"));
        const int iterations = 5;  // average over 5 runs to dampen noise

        // Warmup: each path runs once before timing, so JIT settles.
        DecompressSingleThreaded(bz2);
        for (int w = 1; w <= 14; w *= 2) DecompressParallel(bz2, w);

        // Measurement.
        long singleThreadedTicks = MeasureMean(iterations, () => DecompressSingleThreaded(bz2));
        var workerCounts = new[] { 1, 2, 4, 8, 14 };
        var parallelTicks = new long[workerCounts.Length];
        for (int i = 0; i < workerCounts.Length; i++)
            parallelTicks[i] = MeasureMean(iterations, () => DecompressParallel(bz2, workerCounts[i]));

        // Report.
        long compressedBytes = bz2.Length;
        long decompressedBytes = DecompressSingleThreaded(bz2).LongLength;

        _output.WriteLine($"Fixture: multiblock.txt.bz2");
        _output.WriteLine($"  compressed:   {compressedBytes:N0} bytes ({compressedBytes / 1024.0:F1} KB)");
        _output.WriteLine($"  decompressed: {decompressedBytes:N0} bytes ({decompressedBytes / (1024.0 * 1024):F1} MB)");
        _output.WriteLine($"  iterations:   {iterations} (mean reported)");
        _output.WriteLine("");
        _output.WriteLine($"  Configuration         | wall (ms) | input MB/s | output MB/s | speedup");
        _output.WriteLine($"  ----------------------|-----------|------------|-------------|--------");
        ReportRow("single-threaded baseline", singleThreadedTicks, compressedBytes, decompressedBytes, baseline: singleThreadedTicks);
        for (int i = 0; i < workerCounts.Length; i++)
            ReportRow($"parallel ({workerCounts[i]} workers)", parallelTicks[i], compressedBytes, decompressedBytes, baseline: singleThreadedTicks);

        // Hard assertion: at the production worker count (~14), parallel must be at
        // least as fast as single-threaded. If it's slower, the architectural argument
        // for parallel bz2 fails and we need to revisit.
        long parallel14Ticks = parallelTicks[Array.IndexOf(workerCounts, 14)];
        Assert.True(parallel14Ticks <= singleThreadedTicks,
            $"parallel-14 ({parallel14Ticks / (double)Stopwatch.Frequency * 1000:F1} ms) " +
            $"should be at least as fast as single-threaded ({singleThreadedTicks / (double)Stopwatch.Frequency * 1000:F1} ms). " +
            "Architectural assumption refuted.");
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
        while ((n = dec.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, n);
        return output.ToArray();
    }

    private static byte[] DecompressParallel(byte[] bz2, int workers)
    {
        using var input = new MemoryStream(bz2);
        using var dec = new ParallelBZip2DecompressorStream(input, workerCount: workers);
        using var output = new MemoryStream(capacity: bz2.Length * 10);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = dec.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, n);
        return output.ToArray();
    }
}
