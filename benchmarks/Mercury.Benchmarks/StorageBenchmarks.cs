using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks for TB-scale file-based triple storage
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StorageBenchmarks
{
    private string _dbPath = null!;
    private TripleStore _store = null!;

    [Params(1_000, 10_000, 50_000)]
    public int TripleCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench_storage_{Guid.NewGuid():N}");
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);

        _store = new TripleStore(_dbPath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark]
    public void WriteTriples()
    {
        for (int i = 0; i < TripleCount; i++)
        {
            _store.AddCurrent(
                $"<http://ex.org/s{i}>",
                $"<http://ex.org/p{i % 10}>",
                $"<http://ex.org/o{i % 100}>"
            );
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
