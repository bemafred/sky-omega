using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// The store's clock, set by the benchmark: a write's times are readings of it (ADR-061 I11), and no writer states one.
/// </summary>
internal sealed class BenchmarkClock : TimeProvider
{
    public DateTimeOffset Reading;
    public override DateTimeOffset GetUtcNow() => Reading;
}

/// <summary>
/// Benchmarks for temporal quad storage operations: each fact is memorized at its own clock reading, a day apart.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class TemporalWriteBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;
    private readonly BenchmarkClock _clock = new() { Reading = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) };

    [Params(1_000, 10_000)]
    public int TripleCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("temporal-write");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath, null, null, new StorageOptions { Clock = _clock });
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
        for (int i = 0; i < TripleCount; i++)
        {
            _clock.Reading = _clock.Reading.AddDays(1);
            _store.AddCurrent(
                $"<http://ex.org/entity{i % 100}>",
                $"<http://ex.org/property{i % 10}>",
                $"\"value{i}\""
            );
        }
    }
}

/// <summary>
/// Benchmarks for the reads of memories (ADR-061 D6, ADR-062 D2). Value i is memorized at day i and forgotten at day
/// i + 365, each day one clock reading.
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
        var tempPath = TempPath.Benchmark("temporal-query");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _baseTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new BenchmarkClock { Reading = _baseTime };
        _store = new QuadStore(_dbPath, null, null, new StorageOptions { Clock = clock });

        // Pre-populate: at day d, value d is memorized and value d - 365 is forgotten
        for (int day = 0; day < DataSize + 365; day++)
        {
            clock.Reading = _baseTime.AddDays(day);
            _store.BeginBatch();
            if (day < DataSize)
                _store.AddCurrentBatched($"<http://ex.org/entity{day % 100}>", $"<http://ex.org/property{day % 10}>", $"\"value{day}\"");
            if (day >= 365)
            {
                int i = day - 365;
                _store.DeleteCurrentBatched($"<http://ex.org/entity{i % 100}>", $"<http://ex.org/property{i % 10}>", $"\"value{i}\"");
            }
            _store.CommitBatch();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "What was remembered at a time (two reads, ADR-061 Q10)")]
    public long RememberedAtATimeQuery()
    {
        long count = 0;
        var justAfter = _baseTime.AddDays(180).AddMilliseconds(1);

        // Memorized before the time and remembered, and memorized before it and forgotten since it
        var remembered = _store.QueryMemories("<http://ex.org/entity50>", default, default, memorizedBefore: justAfter);
        while (remembered.MoveNext())
        {
            count++;
            _ = remembered.Current.Object.Length;
        }
        remembered.Dispose();
        var forgottenSince = _store.QueryMemories("<http://ex.org/entity50>", default, default,
            memorizedBefore: justAfter, forgottenSince: justAfter);
        while (forgottenSince.MoveNext())
        {
            count++;
            _ = forgottenSince.Current.Object.Length;
        }
        forgottenSince.Dispose();

        return count;
    }

    [Benchmark(Description = "Memories overlapping a range (two reads)")]
    public long OverlappingARangeQuery()
    {
        long count = 0;
        var rangeStart = _baseTime.AddDays(100);
        var rangeEnd = _baseTime.AddDays(200);

        // Memorized before the range ends, and remembered or forgotten after it starts
        var remembered = _store.QueryMemories("<http://ex.org/entity50>", default, default, memorizedBefore: rangeEnd);
        while (remembered.MoveNext())
            count++;
        remembered.Dispose();
        var forgottenAfter = _store.QueryMemories("<http://ex.org/entity50>", default, default,
            memorizedBefore: rangeEnd, forgottenSince: rangeStart.AddMilliseconds(1));
        while (forgottenAfter.MoveNext())
            count++;
        forgottenAfter.Dispose();

        return count;
    }

    [Benchmark(Description = "Every period (INCLUDING FORGOTTEN)")]
    public long EveryPeriodQuery()
    {
        long count = 0;

        var results = _store.QueryMemories("<http://ex.org/entity50>", default, default, includingForgotten: true);
        while (results.MoveNext())
            count++;
        results.Dispose();

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
