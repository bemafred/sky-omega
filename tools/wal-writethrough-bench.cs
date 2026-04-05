#!/usr/bin/env -S dotnet run
// Micro-benchmark: WAL write throughput with FileOptions.WriteThrough vs FileOptions.None
// Answers whether bulk-mode FileStream is needed for ADR-027.

using System.Diagnostics;

const int RecordSize = 80; // WAL record size
const int WarmupRecords = 1_000;
const int BenchRecords = 100_000;

var tempDir = Path.Combine(Path.GetTempPath(), $"wal-bench-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDir);

try
{
    var record = new byte[RecordSize];
    Random.Shared.NextBytes(record); // Realistic-ish payload

    // --- Benchmark 1: WriteThrough (current WAL behavior) ---
    var wtPath = Path.Combine(tempDir, "writethrough.wal");
    using (var wt = new FileStream(wtPath, FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: 4096, FileOptions.WriteThrough))
    {
        // Warmup
        for (int i = 0; i < WarmupRecords; i++)
            wt.Write(record);
        wt.Flush(flushToDisk: true);
        wt.SetLength(0);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchRecords; i++)
            wt.Write(record);
        sw.Stop();

        var wtElapsed = sw.Elapsed;
        var wtOps = BenchRecords / wtElapsed.TotalSeconds;
        var wtMBs = (BenchRecords * RecordSize) / (1024.0 * 1024.0) / wtElapsed.TotalSeconds;
        Console.WriteLine($"WriteThrough (no explicit fsync):");
        Console.WriteLine($"  {BenchRecords:N0} records in {wtElapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"  {wtOps:N0} records/sec");
        Console.WriteLine($"  {wtMBs:F1} MB/sec");
        Console.WriteLine();
    }

    // --- Benchmark 2: WriteThrough + Flush(true) per write (current Append behavior) ---
    var wtfPath = Path.Combine(tempDir, "writethrough-fsync.wal");
    var fsyncRecords = Math.Min(BenchRecords, 10_000); // fsync is slow, use fewer
    using (var wtf = new FileStream(wtfPath, FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: 4096, FileOptions.WriteThrough))
    {
        // Warmup
        for (int i = 0; i < WarmupRecords; i++)
        {
            wtf.Write(record);
            wtf.Flush(flushToDisk: true);
        }
        wtf.SetLength(0);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < fsyncRecords; i++)
        {
            wtf.Write(record);
            wtf.Flush(flushToDisk: true);
        }
        sw.Stop();

        var wtfElapsed = sw.Elapsed;
        var wtfOps = fsyncRecords / wtfElapsed.TotalSeconds;
        Console.WriteLine($"WriteThrough + Flush(true) per write:");
        Console.WriteLine($"  {fsyncRecords:N0} records in {wtfElapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"  {wtfOps:N0} records/sec");
        Console.WriteLine();
    }

    // --- Benchmark 3: FileOptions.None (proposed bulk mode) ---
    var nonePath = Path.Combine(tempDir, "none.wal");
    using (var none = new FileStream(nonePath, FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: 4096, FileOptions.None))
    {
        // Warmup
        for (int i = 0; i < WarmupRecords; i++)
            none.Write(record);
        none.Flush(flushToDisk: true);
        none.SetLength(0);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchRecords; i++)
            none.Write(record);
        sw.Stop();

        var noneElapsed = sw.Elapsed;
        var noneOps = BenchRecords / noneElapsed.TotalSeconds;
        var noneMBs = (BenchRecords * RecordSize) / (1024.0 * 1024.0) / noneElapsed.TotalSeconds;
        Console.WriteLine($"FileOptions.None (no explicit fsync):");
        Console.WriteLine($"  {BenchRecords:N0} records in {noneElapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"  {noneOps:N0} records/sec");
        Console.WriteLine($"  {noneMBs:F1} MB/sec");
        Console.WriteLine();
    }

    // --- Benchmark 4: FileOptions.None + batch Flush at end ---
    var batchPath = Path.Combine(tempDir, "none-batch.wal");
    using (var batch = new FileStream(batchPath, FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: 4096, FileOptions.None))
    {
        // Warmup
        for (int i = 0; i < WarmupRecords; i++)
            batch.Write(record);
        batch.Flush(flushToDisk: true);
        batch.SetLength(0);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchRecords; i++)
            batch.Write(record);
        batch.Flush(flushToDisk: true); // Single fsync at end
        sw.Stop();

        var batchElapsed = sw.Elapsed;
        var batchOps = BenchRecords / batchElapsed.TotalSeconds;
        var batchMBs = (BenchRecords * RecordSize) / (1024.0 * 1024.0) / batchElapsed.TotalSeconds;
        Console.WriteLine($"FileOptions.None + single Flush(true) at end:");
        Console.WriteLine($"  {BenchRecords:N0} records in {batchElapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"  {batchOps:N0} records/sec");
        Console.WriteLine($"  {batchMBs:F1} MB/sec");
        Console.WriteLine();
    }

    // --- Benchmark 5: Larger batch (1M records, FileOptions.None) ---
    var largePath = Path.Combine(tempDir, "none-large.wal");
    const int largeRecords = 1_000_000;
    using (var large = new FileStream(largePath, FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: 65536, FileOptions.None))
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < largeRecords; i++)
            large.Write(record);
        large.Flush(flushToDisk: true);
        sw.Stop();

        var largeElapsed = sw.Elapsed;
        var largeOps = largeRecords / largeElapsed.TotalSeconds;
        var largeMBs = (largeRecords * RecordSize) / (1024.0 * 1024.0) / largeElapsed.TotalSeconds;
        Console.WriteLine($"FileOptions.None, 1M records, 64KB buffer:");
        Console.WriteLine($"  {largeRecords:N0} records in {largeElapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"  {largeOps:N0} records/sec");
        Console.WriteLine($"  {largeMBs:F1} MB/sec");
        Console.WriteLine();
    }

    Console.WriteLine("--- Summary ---");
    Console.WriteLine("If WriteThrough (no fsync) is significantly slower than None,");
    Console.WriteLine("then bulk-mode FileStream with FileOptions.None is needed.");
}
finally
{
    Directory.Delete(tempDir, recursive: true);
}
