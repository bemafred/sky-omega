using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SkyOmega.Mercury.Compression;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// Validates the memory-bandwidth-contention hypothesis: if workers compete for memory
/// bandwidth, per-block decode time should INCREASE with worker count (workers slow down
/// each other). If orchestration is the bottleneck, per-block decode time should stay
/// constant but wall-clock doesn't drop. The test instruments per-block decode duration
/// at N=1 and N=14, then compares.
///
/// Math: total decode work = blocks × per-block-time. Wall-clock = total work / N
/// (ideal parallelism). If per-block-time grows with N, scaling stops because each
/// worker is slower in the presence of others.
/// </summary>
public class ParallelBZip2ContentionTrace
{
    private const string FixturePath = "/tmp/multiblock-large.txt.bz2";
    private readonly ITestOutputHelper _output;

    public ParallelBZip2ContentionTrace(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Trace_PerBlockDecodeTime_ByWorkerCount()
    {
        if (!File.Exists(FixturePath))
        {
            _output.WriteLine($"SKIP: fixture not present at {FixturePath}");
            return;
        }

        var bz2 = File.ReadAllBytes(FixturePath);

        // Warmup
        Decompress(bz2, workers: 4);

        // N=1 and N=14, instrumented.
        var resultN1 = MeasureWithDiag(bz2, workers: 1);
        var resultN14 = MeasureWithDiag(bz2, workers: 14);

        _output.WriteLine($"Fixture: {FixturePath}");
        _output.WriteLine($"  blocks observed: N=1: {resultN1.Decodes.Count} | N=14: {resultN14.Decodes.Count}");
        _output.WriteLine("");

        ReportConfiguration("N=1", resultN1);
        _output.WriteLine("");
        ReportConfiguration("N=14", resultN14);
        _output.WriteLine("");

        // The decisive comparison: median per-block decode time.
        long n1MedianTicks = PercentileTicks(resultN1.Decodes, 0.50);
        long n14MedianTicks = PercentileTicks(resultN14.Decodes, 0.50);
        double n1MedianMs = n1MedianTicks / (double)Stopwatch.Frequency * 1000;
        double n14MedianMs = n14MedianTicks / (double)Stopwatch.Frequency * 1000;
        double slowdown = n14MedianMs / n1MedianMs;

        _output.WriteLine($"Per-block decode time (median):");
        _output.WriteLine($"  N=1:  {n1MedianMs:F2} ms");
        _output.WriteLine($"  N=14: {n14MedianMs:F2} ms");
        _output.WriteLine($"  per-block slowdown under N=14: {slowdown:F2}x");
        _output.WriteLine("");

        // Interpretation guide based on the slowdown ratio.
        if (slowdown < 1.3)
            _output.WriteLine("  => Per-block decode time stable across N. Bottleneck is orchestration,");
        else if (slowdown < 3)
            _output.WriteLine("  => Moderate per-block slowdown — partial resource contention.");
        else
            _output.WriteLine("  => Major per-block slowdown — workers compete strongly for shared resource");
        _output.WriteLine($"     (memory bandwidth, GC/ArrayPool, or cache).");
    }

    private void ReportConfiguration(string label, MeasureResult result)
    {
        var decodeMs = result.Decodes.Select(t => t.Ticks / (double)Stopwatch.Frequency * 1000).OrderBy(x => x).ToList();
        double sum = decodeMs.Sum();
        double mean = sum / decodeMs.Count;
        double p50 = decodeMs[decodeMs.Count / 2];
        double p95 = decodeMs[(int)(decodeMs.Count * 0.95)];
        double p99 = decodeMs[Math.Min(decodeMs.Count - 1, (int)(decodeMs.Count * 0.99))];
        double max = decodeMs.Max();
        double wallMs = result.WallTicks / (double)Stopwatch.Frequency * 1000;
        double cpuTotalMs = sum;
        double parallelEfficiency = (cpuTotalMs / result.WorkerCount) / wallMs;
        _output.WriteLine($"{label}: wall={wallMs:F1} ms, sum-of-decode={cpuTotalMs:F1} ms, " +
                          $"workers={result.WorkerCount}, parallel-efficiency={parallelEfficiency:P1}");
        _output.WriteLine($"     per-block decode: mean={mean:F2} ms p50={p50:F2} p95={p95:F2} p99={p99:F2} max={max:F2}");

        // Per-worker breakdown
        var byWorker = result.Decodes.GroupBy(d => d.WorkerId)
            .OrderBy(g => g.Key)
            .Select(g => (Worker: g.Key, Count: g.Count(), TotalMs: g.Sum(d => d.Ticks) / (double)Stopwatch.Frequency * 1000))
            .ToList();
        _output.WriteLine($"     per-worker:");
        foreach (var w in byWorker)
            _output.WriteLine($"       worker {w.Worker}: {w.Count} blocks, {w.TotalMs:F1} ms total");
    }

    private static long PercentileTicks(IReadOnlyCollection<(int W, int O, long Ticks)> decodes, double p)
    {
        var sorted = decodes.Select(d => d.Ticks).OrderBy(x => x).ToList();
        return sorted[(int)(sorted.Count * p)];
    }

    private static MeasureResult MeasureWithDiag(byte[] bz2, int workers)
    {
        var bag = new ConcurrentBag<(int WorkerId, int Ordinal, long DecodeTicks)>();
        ParallelBZip2DecompressorStream.DiagPerBlockDecode = bag;
        Interlocked.Exchange(ref ParallelBZip2DecompressorStream._diagNextWorkerId, 0);
        try
        {
            var sw = Stopwatch.StartNew();
            Decompress(bz2, workers);
            sw.Stop();
            return new MeasureResult(workers, sw.ElapsedTicks, bag.Select(x => (x.WorkerId, x.Ordinal, x.DecodeTicks)).ToList());
        }
        finally
        {
            ParallelBZip2DecompressorStream.DiagPerBlockDecode = null;
        }
    }

    private static void Decompress(byte[] bz2, int workers)
    {
        using var input = new MemoryStream(bz2);
        using var dec = new ParallelBZip2DecompressorStream(input, workerCount: workers);
        var buffer = new byte[64 * 1024];
        while (dec.Read(buffer, 0, buffer.Length) > 0) { }
    }

    private record MeasureResult(int WorkerCount, long WallTicks, IReadOnlyList<(int WorkerId, int Ordinal, long Ticks)> Decodes);
}
