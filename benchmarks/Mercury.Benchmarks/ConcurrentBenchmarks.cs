using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks for concurrent read operations.
/// Measures throughput of multiple readers accessing the same store.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class ConcurrentReadBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;
    private const int DataSize = 10_000;

    [Params(1, 2, 4, 8)]
    public int ThreadCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("concread");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Pre-populate with test data
        _store.BeginBatch();
        for (int i = 0; i < DataSize; i++)
        {
            _store.AddCurrentBatched(
                $"<http://ex.org/s{i}>",
                $"<http://ex.org/p{i % 10}>",
                $"<http://ex.org/o{i % 100}>"
            );
        }
        _store.CommitBatch();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "Concurrent predicate queries")]
    public long ConcurrentPredicateQuery()
    {
        long totalCount = 0;
        var threads = new Thread[ThreadCount];
        var counts = new long[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                long count = 0;
                // Each thread queries a different predicate
                var predicate = $"<http://ex.org/p{threadId % 10}>";

                _store.AcquireReadLock();
                try
                {
                    var results = _store.QueryCurrent(
                        ReadOnlySpan<char>.Empty,
                        predicate,
                        ReadOnlySpan<char>.Empty
                    );
                    while (results.MoveNext())
                    {
                        count++;
                        _ = results.Current.Subject.Length;
                    }
                    results.Dispose();
                }
                finally
                {
                    _store.ReleaseReadLock();
                }

                counts[threadId] = count;
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        for (int i = 0; i < ThreadCount; i++)
            totalCount += counts[i];

        return totalCount;
    }

    [Benchmark(Description = "Concurrent full scans")]
    public long ConcurrentFullScan()
    {
        long totalCount = 0;
        var threads = new Thread[ThreadCount];
        var counts = new long[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                long count = 0;

                _store.AcquireReadLock();
                try
                {
                    var results = _store.QueryCurrent(
                        ReadOnlySpan<char>.Empty,
                        ReadOnlySpan<char>.Empty,
                        ReadOnlySpan<char>.Empty
                    );
                    while (results.MoveNext())
                    {
                        count++;
                        _ = results.Current.Subject.Length;
                    }
                    results.Dispose();
                }
                finally
                {
                    _store.ReleaseReadLock();
                }

                counts[threadId] = count;
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        for (int i = 0; i < ThreadCount; i++)
            totalCount += counts[i];

        return totalCount;
    }
}

/// <summary>
/// Benchmarks for concurrent write operations.
/// Measures throughput with multiple writers competing for the write lock.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class ConcurrentWriteBenchmarks
{
    private string _dbPath = null!;
    private const int WritesPerThread = 500;

    [Params(1, 2, 4)]
    public int ThreadCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        var tempPath = TempPath.Benchmark("concwrite");
        tempPath.MarkOwnership();
        _dbPath = tempPath;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "Concurrent single writes")]
    public int ConcurrentSingleWrites()
    {
        using var store = new QuadStore(_dbPath);

        var threads = new Thread[ThreadCount];
        var writeCounts = new int[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                int count = 0;
                for (int i = 0; i < WritesPerThread; i++)
                {
                    store.AddCurrent(
                        $"<http://ex.org/t{threadId}/s{i}>",
                        "<http://ex.org/value>",
                        $"\"{threadId * 10000 + i}\""
                    );
                    count++;
                }
                writeCounts[threadId] = count;
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        return writeCounts.Sum();
    }
}

/// <summary>
/// Benchmarks for mixed read/write workloads.
/// Measures performance with readers and writers competing.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class MixedWorkloadBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;
    private const int InitialDataSize = 5_000;
    private const int OperationsPerThread = 200;

    [Params(2, 4, 8)]
    public int TotalThreads { get; set; }

    // 25% writers, 75% readers
    private int WriterCount => Math.Max(1, TotalThreads / 4);
    private int ReaderCount => TotalThreads - WriterCount;

    [IterationSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("mixed");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Pre-populate with some data
        _store.BeginBatch();
        for (int i = 0; i < InitialDataSize; i++)
        {
            _store.AddCurrentBatched(
                $"<http://ex.org/init/s{i}>",
                "<http://ex.org/init>",
                $"\"{i}\""
            );
        }
        _store.CommitBatch();
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "Mixed readers/writers (75%/25%)")]
    public long MixedReadWrite()
    {
        long totalOps = 0;
        var threads = new Thread[TotalThreads];
        var opCounts = new long[TotalThreads];

        // Create writer threads
        for (int w = 0; w < WriterCount; w++)
        {
            int threadId = w;
            threads[w] = new Thread(() =>
            {
                long ops = 0;
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    _store.AddCurrent(
                        $"<http://ex.org/w{threadId}/s{i}>",
                        "<http://ex.org/written>",
                        $"\"{threadId * 10000 + i}\""
                    );
                    ops++;
                }
                opCounts[threadId] = ops;
            });
        }

        // Create reader threads
        for (int r = 0; r < ReaderCount; r++)
        {
            int threadId = WriterCount + r;
            threads[threadId] = new Thread(() =>
            {
                long ops = 0;
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    _store.AcquireReadLock();
                    try
                    {
                        var results = _store.QueryCurrent(
                            ReadOnlySpan<char>.Empty,
                            "<http://ex.org/init>",
                            ReadOnlySpan<char>.Empty
                        );
                        while (results.MoveNext())
                        {
                            _ = results.Current.Subject.Length;
                        }
                        results.Dispose();
                        ops++;
                    }
                    finally
                    {
                        _store.ReleaseReadLock();
                    }
                }
                opCounts[threadId] = ops;
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        for (int i = 0; i < TotalThreads; i++)
            totalOps += opCounts[i];

        return totalOps;
    }
}

/// <summary>
/// Benchmarks for lock contention and throughput scaling.
/// Measures how throughput scales with increasing thread counts.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class LockContentionBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;
    private const int DataSize = 10_000;
    private const int IterationsPerThread = 100;

    [Params(1, 2, 4, 8, 16)]
    public int ThreadCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("contention");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Pre-populate
        _store.BeginBatch();
        for (int i = 0; i < DataSize; i++)
        {
            _store.AddCurrentBatched(
                $"<http://ex.org/s{i}>",
                $"<http://ex.org/p{i % 10}>",
                $"<http://ex.org/o{i % 100}>"
            );
        }
        _store.CommitBatch();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "Read lock acquisition (rapid)")]
    public int RapidReadLockAcquisition()
    {
        int totalAcquisitions = 0;
        var threads = new Thread[ThreadCount];
        var acquisitions = new int[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                int count = 0;
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    _store.AcquireReadLock();
                    _store.ReleaseReadLock();
                    count++;
                }
                acquisitions[threadId] = count;
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        for (int i = 0; i < ThreadCount; i++)
            totalAcquisitions += acquisitions[i];

        return totalAcquisitions;
    }

    [Benchmark(Description = "Read with minimal work")]
    public long ReadMinimalWork()
    {
        long totalCount = 0;
        var threads = new Thread[ThreadCount];
        var counts = new long[ThreadCount];

        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                long count = 0;
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    _store.AcquireReadLock();
                    try
                    {
                        // Single point query - minimal work under lock
                        var results = _store.QueryCurrent(
                            $"<http://ex.org/s{(threadId * 100 + i) % DataSize}>",
                            ReadOnlySpan<char>.Empty,
                            ReadOnlySpan<char>.Empty
                        );
                        while (results.MoveNext())
                        {
                            count++;
                        }
                        results.Dispose();
                    }
                    finally
                    {
                        _store.ReleaseReadLock();
                    }
                }
                counts[threadId] = count;
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        for (int i = 0; i < ThreadCount; i++)
            totalCount += counts[i];

        return totalCount;
    }
}

/// <summary>
/// Benchmarks comparing batch writes under contention.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class ConcurrentBatchBenchmarks
{
    private string _dbPath = null!;
    private const int BatchSize = 1_000;

    [Params(1, 2, 4)]
    public int WriterCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        var tempPath = TempPath.Benchmark("concbatch");
        tempPath.MarkOwnership();
        _dbPath = tempPath;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "Sequential batch commits")]
    public int SequentialBatches()
    {
        using var store = new QuadStore(_dbPath);

        var threads = new Thread[WriterCount];
        var completedBatches = new int[WriterCount];

        for (int w = 0; w < WriterCount; w++)
        {
            int writerId = w;
            threads[w] = new Thread(() =>
            {
                store.BeginBatch();
                try
                {
                    for (int i = 0; i < BatchSize; i++)
                    {
                        store.AddCurrentBatched(
                            $"<http://ex.org/batch{writerId}/s{i}>",
                            "<http://ex.org/batch>",
                            $"\"{writerId * 10000 + i}\""
                        );
                    }
                    store.CommitBatch();
                    completedBatches[writerId] = 1;
                }
                catch
                {
                    store.RollbackBatch();
                    throw;
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        return completedBatches.Sum();
    }
}
