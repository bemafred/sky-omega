using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// ADR-062 D7: what a row costs through each index's cursor, the one read of an index. The cursor is moved by an
/// interface call per row (<c>IQuadIndex.Advance</c>). Each benchmark walks all 50,000 entries of one index and sums
/// one atom of each row, with no decoding, so the call is as large a part of the row as it can be. Measured against
/// each index's own enumerator before ADR-062 Step 6 removed them: the call cost 0.5 to 0.8 ns a row (ADR-062, "The
/// call, measured at the index").
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class IndexCursorBenchmarks
{
    private const int Rows = 50_000;

    private string _path = null!;
    private TemporalQuadIndex _cognitive = null!;
    private VersionedQuadIndex _graph = null!;
    private ReferenceQuadIndex _reference = null!;
    private MinimalQuadIndex _minimal = null!;
    private TemporalQuery _remembered;

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("index-cursor");
        tempPath.MarkOwnership();
        _path = tempPath;
        Directory.CreateDirectory(_path);

        _cognitive = new TemporalQuadIndex(Path.Combine(_path, "cognitive.tdb"), null, 64L << 20);
        _graph = new VersionedQuadIndex(Path.Combine(_path, "graph.tdb"), null, 64L << 20);
        _reference = new ReferenceQuadIndex(Path.Combine(_path, "reference.tdb"), null, 64L << 20);
        _minimal = new MinimalQuadIndex(Path.Combine(_path, "minimal.tdb"), null, 64L << 20);

        for (long row = 1; row <= Rows; row++)
        {
            _cognitive.BeginPeriod(0, row, 101, 201, memorizedAt: 1000);
            _graph.NextVersion(0, row, 101, 201, 0);
            _reference.AddRaw(0, row, 101, 201);
            _minimal.NextVersion(row, 101, 201, 0);
        }

        _remembered = new TemporalQuery { Type = TemporalQueryType.AsOf, AsOfTime = 2000 };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cognitive.Dispose();
        _graph.Dispose();
        _reference.Dispose();
        _minimal.Dispose();
        if (Directory.Exists(_path))
            Directory.Delete(_path, true);
    }

    [Benchmark(Description = "Cognitive")]
    public long Cognitive()
    {
        long sum = 0;
        var rows = _cognitive.Query(-1, -1, -1, -1, _remembered);
        while (rows.MoveNext()) sum += rows.Primary;
        return sum;
    }

    [Benchmark(Description = "Graph")]
    public long Graph()
    {
        long sum = 0;
        var rows = _graph.Query(-1, -1, -1, -1);
        while (rows.MoveNext()) sum += rows.Primary;
        return sum;
    }

    [Benchmark(Description = "Reference")]
    public long Reference()
    {
        long sum = 0;
        var rows = _reference.Query(-1, -1, -1, -1);
        while (rows.MoveNext()) sum += rows.Primary;
        return sum;
    }

    [Benchmark(Description = "Minimal")]
    public long Minimal()
    {
        long sum = 0;
        var rows = _minimal.Query(-1, -1, -1);
        while (rows.MoveNext()) sum += rows.Primary;
        return sum;
    }
}
