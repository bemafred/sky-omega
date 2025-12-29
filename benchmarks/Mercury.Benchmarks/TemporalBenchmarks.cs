using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks for temporal quad storage operations
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class TemporalWriteBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    [Params(1_000, 10_000)]
    public int TripleCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench_temporal_write_{Guid.NewGuid():N}");
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);

        _store = new QuadStore(_dbPath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark]
    public void WriteTemporalTriples()
    {
        var baseTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < TripleCount; i++)
        {
            var validFrom = baseTime.AddDays(i);
            var validTo = baseTime.AddDays(i + 365);

            _store.Add(
                $"<http://ex.org/entity{i % 100}>",
                $"<http://ex.org/property{i % 10}>",
                $"\"value{i}\"",
                validFrom,
                validTo
            );
        }
    }
}

/// <summary>
/// Benchmarks for temporal query operations
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class TemporalQueryBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;
    private DateTimeOffset _baseTime;
    private const int DataSize = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bench_temporal_query_{Guid.NewGuid():N}");
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);

        _store = new QuadStore(_dbPath);
        _baseTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Pre-populate with temporal data
        for (int i = 0; i < DataSize; i++)
        {
            var validFrom = _baseTime.AddDays(i);
            var validTo = _baseTime.AddDays(i + 365);

            _store.Add(
                $"<http://ex.org/entity{i % 100}>",
                $"<http://ex.org/property{i % 10}>",
                $"\"value{i}\"",
                validFrom,
                validTo
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

    [Benchmark(Description = "Point-in-time query (as-of)")]
    public long PointInTimeQuery()
    {
        long count = 0;
        var queryTime = _baseTime.AddDays(180);

        var results = _store.QueryAsOf(
            "<http://ex.org/entity50>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            queryTime
        );

        while (results.MoveNext())
        {
            count++;
            var triple = results.Current;
            _ = triple.Object.Length;
        }

        return count;
    }

    [Benchmark(Description = "Temporal range query")]
    public long TemporalRangeQuery()
    {
        long count = 0;
        var rangeStart = _baseTime.AddDays(100);
        var rangeEnd = _baseTime.AddDays(200);

        var results = _store.QueryChanges(
            rangeStart,
            rangeEnd,
            "<http://ex.org/entity50>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext())
        {
            count++;
        }

        return count;
    }

    [Benchmark(Description = "Evolution query (all versions)")]
    public long EvolutionQuery()
    {
        long count = 0;

        var results = _store.QueryEvolution(
            "<http://ex.org/entity50>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext())
        {
            count++;
        }

        return count;
    }

    [Benchmark(Description = "Current state query")]
    public long CurrentStateQuery()
    {
        long count = 0;

        var results = _store.QueryCurrent(
            "<http://ex.org/entity50>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        while (results.MoveNext())
        {
            count++;
            var triple = results.Current;
            _ = triple.Object.Length;
        }

        return count;
    }
}
