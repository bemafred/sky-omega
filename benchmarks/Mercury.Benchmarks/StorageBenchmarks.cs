using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks comparing single writes vs batch writes.
/// Each iteration creates a fresh store to measure true write performance.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class BatchWriteBenchmarks
{
    private string _dbPath = null!;

    [Params(1_000, 10_000)]
    public int TripleCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench_batch_{Guid.NewGuid():N}");
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Baseline = true, Description = "Single writes (fsync each)")]
    public void SingleWrites()
    {
        using var store = new TripleStore(_dbPath);
        for (int i = 0; i < TripleCount; i++)
        {
            store.AddCurrent(
                $"<http://ex.org/s{i}>",
                $"<http://ex.org/p{i % 10}>",
                $"<http://ex.org/o{i % 100}>"
            );
        }
    }

    [Benchmark(Description = "Batch writes (single fsync)")]
    public void BatchWrites()
    {
        using var store = new TripleStore(_dbPath + "_batch");
        store.BeginBatch();
        try
        {
            for (int i = 0; i < TripleCount; i++)
            {
                store.AddCurrentBatched(
                    $"<http://ex.org/s{i}>",
                    $"<http://ex.org/p{i % 10}>",
                    $"<http://ex.org/o{i % 100}>"
                );
            }
            store.CommitBatch();
        }
        catch
        {
            store.RollbackBatch();
            throw;
        }
    }
}

/// <summary>
/// Benchmarks for query operations on pre-populated store
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class QueryBenchmarks
{
    private string _dbPath = null!;
    private TripleStore _store = null!;
    private const int DataSize = 50_000;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench_query_{Guid.NewGuid():N}");
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);

        _store = new TripleStore(_dbPath);

        // Pre-populate with test data
        for (int i = 0; i < DataSize; i++)
        {
            _store.AddCurrent(
                $"<http://ex.org/s{i}>",
                $"<http://ex.org/p{i % 10}>",
                $"<http://ex.org/o{i % 100}>"
            );
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark]
    public long QueryByPredicate()
    {
        long count = 0;
        var results = _store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            "<http://ex.org/p5>",
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext())
        {
            count++;
            var triple = results.Current;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark]
    public long QueryBySubject()
    {
        long count = 0;
        var results = _store.QueryCurrent(
            "<http://ex.org/s1000>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext())
        {
            count++;
            var triple = results.Current;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark]
    public long QueryByObject()
    {
        long count = 0;
        var results = _store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            "<http://ex.org/o50>"
        );

        while (results.MoveNext())
        {
            count++;
            var triple = results.Current;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark]
    public long FullScan()
    {
        long count = 0;
        var results = _store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext())
        {
            count++;
            var triple = results.Current;
            _ = triple.Subject.Length + triple.Predicate.Length + triple.Object.Length;
        }

        return count;
    }
}

/// <summary>
/// Benchmarks for index selection impact
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class IndexSelectionBenchmarks
{
    private string _dbPath = null!;
    private TripleStore _store = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench_index_{Guid.NewGuid():N}");
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);

        _store = new TripleStore(_dbPath);

        // Create skewed data (many triples with same subject)
        for (int s = 0; s < 100; s++)
        {
            for (int p = 0; p < 100; p++)
            {
                _store.AddCurrent(
                    $"<http://ex.org/s{s}>",
                    $"<http://ex.org/p{p}>",
                    $"<http://ex.org/o{s * 100 + p}>"
                );
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "SPO Index (Subject bound)")]
    public int QuerySubjectBound()
    {
        int count = 0;
        var results = _store.QueryCurrent(
            "<http://ex.org/s50>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext()) count++;
        return count;
    }

    [Benchmark(Description = "POS Index (Predicate bound)")]
    public int QueryPredicateBound()
    {
        int count = 0;
        var results = _store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            "<http://ex.org/p50>",
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext()) count++;
        return count;
    }

    [Benchmark(Description = "OSP Index (Object bound)")]
    public int QueryObjectBound()
    {
        int count = 0;
        var results = _store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            "<http://ex.org/o5050>"
        );

        while (results.MoveNext()) count++;
        return count;
    }
}
