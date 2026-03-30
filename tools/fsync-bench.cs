#!/usr/bin/env -S dotnet
#:package System.IO.Hashing@9.0.4

// Measure raw fsync latency on the current SSD.
// Tests: single fsync, batched fsync, F_FULLFSYNC vs fdatasync.

using System.Diagnostics;
using System.Runtime.InteropServices;

const int ITERATIONS = 100;
const int BATCH_SIZE = 100;
var testDir = Path.Combine(Path.GetTempPath(), $"fsync-bench-{Environment.ProcessId}");
Directory.CreateDirectory(testDir);

try
{
    // Test 1: Individual fsync per write (simulates Mercury single-write path)
    var singleFile = Path.Combine(testDir, "single.dat");
    var singleTimes = new List<double>();

    using (var fs = new FileStream(singleFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
    {
        var data = new byte[256];
        Random.Shared.NextBytes(data);

        for (int i = 0; i < ITERATIONS; i++)
        {
            fs.Write(data);
            var sw = Stopwatch.StartNew();
            fs.Flush(flushToDisk: true);
            sw.Stop();
            singleTimes.Add(sw.Elapsed.TotalMicroseconds);
        }
    }

    singleTimes.Sort();
    Console.WriteLine("=== Individual fsync (256 bytes per write) ===");
    Console.WriteLine($"  Iterations: {ITERATIONS}");
    Console.WriteLine($"  Median:     {singleTimes[ITERATIONS / 2]:F0} us");
    Console.WriteLine($"  P95:        {singleTimes[(int)(ITERATIONS * 0.95)]:F0} us");
    Console.WriteLine($"  P99:        {singleTimes[(int)(ITERATIONS * 0.99)]:F0} us");
    Console.WriteLine($"  Min:        {singleTimes[0]:F0} us");
    Console.WriteLine($"  Max:        {singleTimes[^1]:F0} us");
    Console.WriteLine($"  Mean:       {singleTimes.Average():F0} us");
    Console.WriteLine();

    // Test 2: Batch writes, single fsync (simulates Mercury batch path)
    var batchFile = Path.Combine(testDir, "batch.dat");
    var batchTimes = new List<double>();

    for (int round = 0; round < ITERATIONS; round++)
    {
        using var fs = new FileStream(batchFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        var data = new byte[256];
        Random.Shared.NextBytes(data);

        for (int i = 0; i < BATCH_SIZE; i++)
            fs.Write(data);

        var sw = Stopwatch.StartNew();
        fs.Flush(flushToDisk: true);
        sw.Stop();
        batchTimes.Add(sw.Elapsed.TotalMicroseconds);
    }

    batchTimes.Sort();
    Console.WriteLine($"=== Batch fsync ({BATCH_SIZE} x 256 bytes, single flush) ===");
    Console.WriteLine($"  Iterations: {ITERATIONS}");
    Console.WriteLine($"  Median:     {batchTimes[ITERATIONS / 2]:F0} us");
    Console.WriteLine($"  P95:        {batchTimes[(int)(ITERATIONS * 0.95)]:F0} us");
    Console.WriteLine($"  P99:        {batchTimes[(int)(ITERATIONS * 0.99)]:F0} us");
    Console.WriteLine($"  Min:        {batchTimes[0]:F0} us");
    Console.WriteLine($"  Max:        {batchTimes[^1]:F0} us");
    Console.WriteLine($"  Mean:       {batchTimes.Average():F0} us");
    Console.WriteLine();

    // Test 3: FileOptions.WriteThrough vs normal (macOS F_FULLFSYNC behavior)
    var wtFile = Path.Combine(testDir, "writethrough.dat");
    var normalFile = Path.Combine(testDir, "normal.dat");

    var wtTimes = MeasureFlush(wtFile, FileOptions.WriteThrough);
    var normalTimes = MeasureFlush(normalFile, FileOptions.None);

    Console.WriteLine("=== WriteThrough vs Normal flush (100 x 256 bytes batched) ===");
    Console.WriteLine($"  WriteThrough median: {wtTimes[ITERATIONS / 2]:F0} us");
    Console.WriteLine($"  Normal flush median: {normalTimes[ITERATIONS / 2]:F0} us");
    Console.WriteLine($"  Ratio:               {wtTimes[ITERATIONS / 2] / normalTimes[ITERATIONS / 2]:F2}x");
    Console.WriteLine();

    // Test 4: Different write sizes
    Console.WriteLine("=== Fsync latency by write size (single write + flush) ===");
    foreach (var size in new[] { 64, 256, 1024, 4096, 16384, 65536 })
    {
        var sizeTimes = MeasureSingleWriteFlush(Path.Combine(testDir, $"size-{size}.dat"), size);
        Console.WriteLine($"  {size,6} bytes: median {sizeTimes[ITERATIONS / 2]:F0} us, p95 {sizeTimes[(int)(ITERATIONS * 0.95)]:F0} us");
    }
    Console.WriteLine();

    // System info
    Console.WriteLine("=== System ===");
    Console.WriteLine($"  Machine: {Environment.MachineName}");
    Console.WriteLine($"  OS:      {RuntimeInformation.OSDescription}");
    Console.WriteLine($"  Arch:    {RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"  Temp:    {Path.GetTempPath()}");
}
finally
{
    Directory.Delete(testDir, recursive: true);
}

List<double> MeasureFlush(string path, FileOptions options)
{
    var times = new List<double>();
    for (int round = 0; round < ITERATIONS; round++)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, options);
        var data = new byte[256];
        Random.Shared.NextBytes(data);
        for (int i = 0; i < 100; i++)
            fs.Write(data);

        var sw = Stopwatch.StartNew();
        fs.Flush(flushToDisk: true);
        sw.Stop();
        times.Add(sw.Elapsed.TotalMicroseconds);
    }
    times.Sort();
    return times;
}

List<double> MeasureSingleWriteFlush(string path, int size)
{
    var times = new List<double>();
    var data = new byte[size];
    Random.Shared.NextBytes(data);

    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
    for (int i = 0; i < ITERATIONS; i++)
    {
        fs.Write(data);
        var sw = Stopwatch.StartNew();
        fs.Flush(flushToDisk: true);
        sw.Stop();
        times.Add(sw.Elapsed.TotalMicroseconds);
    }
    times.Sort();
    return times;
}
