using System.Collections.Concurrent;
using System.Diagnostics;
using SkyOmega.Mercury.Compression;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Per-block decode time of the parallel bzip2 stream at 1 and 14 workers, on the generated 50 MB fixture
/// (<see cref="BZip2LargeFixture"/>). If workers compete for memory bandwidth, a block takes longer to decode when 14
/// run than when one does; if the producer and consumer limit scaling, the time per block stays the same and the
/// wall-clock time still does not drop.
/// </summary>
/// <remarks>
/// <para>Run with <c>dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- bzip2-contention</c>. It is a
/// trace, not a BenchmarkDotNet benchmark: it reads <c>ParallelBZip2DecompressorStream.DiagPerBlockDecode</c>, which
/// times each block inside one process.</para>
/// <para>This was <c>ParallelBZip2ContentionTrace</c> in the test suite, which read a file in <c>/tmp</c> that existed
/// only where it was made, and otherwise returned and reported passed.</para>
/// </remarks>
internal static class BZip2ContentionTrace
{
    public static int Run(string[] args)
    {
        var compressed = BZip2LargeFixture.Compressed();
        Console.WriteLine($"Fixture: {compressed.Length:N0} bytes compressed");

        // Warm-up, so the JIT does not count against the first configuration.
        Decompress(compressed, workers: 4);

        var single = Trace(compressed, workers: 1);
        var fourteen = Trace(compressed, workers: 14);

        Console.WriteLine($"Blocks: {single.Decodes.Count} at 1 worker, {fourteen.Decodes.Count} at 14");
        Report("1 worker", single);
        Report("14 workers", fourteen);

        double singleMedian = MedianMilliseconds(single.Decodes);
        double fourteenMedian = MedianMilliseconds(fourteen.Decodes);
        Console.WriteLine($"Median decode time per block: {singleMedian:F2} ms at 1 worker, {fourteenMedian:F2} ms at 14 " +
                          $"({fourteenMedian / singleMedian:F2} times)");
        return 0;
    }

    private static (int Workers, long WallTicks, List<(int WorkerId, long Ticks)> Decodes) Trace(byte[] compressed, int workers)
    {
        var decodes = new ConcurrentBag<(int WorkerId, int Ordinal, long DecodeTicks)>();
        ParallelBZip2DecompressorStream.DiagPerBlockDecode = decodes;
        Interlocked.Exchange(ref ParallelBZip2DecompressorStream._diagNextWorkerId, 0);
        try
        {
            var wall = Stopwatch.StartNew();
            Decompress(compressed, workers);
            wall.Stop();
            return (workers, wall.ElapsedTicks, decodes.Select(d => (d.WorkerId, d.DecodeTicks)).ToList());
        }
        finally
        {
            ParallelBZip2DecompressorStream.DiagPerBlockDecode = null;
        }
    }

    private static void Report(string label, (int Workers, long WallTicks, List<(int WorkerId, long Ticks)> Decodes) trace)
    {
        var milliseconds = trace.Decodes.Select(d => d.Ticks * 1000.0 / Stopwatch.Frequency).OrderBy(m => m).ToList();
        double wall = trace.WallTicks * 1000.0 / Stopwatch.Frequency;
        double decoding = milliseconds.Sum();
        Console.WriteLine($"{label}: wall {wall:F1} ms, decoding {decoding:F1} ms in all, " +
                          $"parallel efficiency {decoding / trace.Workers / wall:P1}");
        Console.WriteLine($"  per block: median {milliseconds[milliseconds.Count / 2]:F2} ms, " +
                          $"p95 {milliseconds[(int)(milliseconds.Count * 0.95)]:F2} ms, max {milliseconds[^1]:F2} ms");
        foreach (var worker in trace.Decodes.GroupBy(d => d.WorkerId).OrderBy(g => g.Key))
            Console.WriteLine($"  worker {worker.Key}: {worker.Count()} blocks, " +
                              $"{worker.Sum(d => d.Ticks) * 1000.0 / Stopwatch.Frequency:F1} ms");
    }

    private static double MedianMilliseconds(List<(int WorkerId, long Ticks)> decodes)
        => decodes.Select(d => d.Ticks).OrderBy(t => t).ElementAt(decodes.Count / 2) * 1000.0 / Stopwatch.Frequency;

    private static void Decompress(byte[] compressed, int workers)
    {
        using var input = new MemoryStream(compressed);
        using var decompressor = new ParallelBZip2DecompressorStream(input, workerCount: workers);
        var buffer = new byte[64 * 1024];
        while (decompressor.Read(buffer, 0, buffer.Length) > 0) { }
    }
}
