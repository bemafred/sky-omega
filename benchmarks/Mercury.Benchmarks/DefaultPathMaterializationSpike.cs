using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// ADR-047 spike — Tier A, the materialization risk. TreeJoinExecutor MATERIALIZES the BGP result as a
/// <c>List&lt;MaterializedRow&gt;</c>; the old default path STREAMS its operators. For a query with a LARGE
/// intermediate reduced to a small final result, the tree holds the whole intermediate in memory where the old path
/// streams it into the aggregate. Here: COUNT over a 1000×1000 = 1,000,000-row self-join (K subjects sharing one
/// object), from 1000 triples of input. This measures the peak-allocation gap — the cutover's memory risk at scale,
/// and the one place a naive cutover could OOM where the old path does not.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class DefaultPathMaterializationSpike
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    private const int K = 1000; // K subjects share one object ⇒ K×K-row join intermediate
    private const string Query = "SELECT (COUNT(*) AS ?c) WHERE { ?a <urn:p> ?x . ?b <urn:p> ?x }";

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("materialization-spike");
        tempPath.MarkOwnership();
        _dbPath = tempPath;
        _store = new QuadStore(_dbPath);

        _store.BeginBatch();
        for (int i = 0; i < K; i++)
            _store.AddCurrentBatched($"<urn:s{i}>", "<urn:p>", "<urn:o>"); // all share <urn:o>
        _store.CommitBatch();

        long expected = (long)K * K;
        long old = CountResult(SparqlEngine.Query(_store, Query));
        long tree = CountResult(SparqlEngine.QueryViaTreeForDifferential(_store, Query));
        if (old != expected || tree != expected)
            throw new InvalidOperationException($"correctness gate failed: expected {expected}, old={old}, tree={tree}");
    }

    private static long CountResult(QueryResult r)
    {
        if (r.Rows is not { Count: 1 }) return -1;
        var c = r.Rows[0].GetValueOrDefault("c") ?? "";
        int q1 = c.IndexOf('"'), q2 = q1 >= 0 ? c.IndexOf('"', q1 + 1) : -1;
        return q1 >= 0 && q2 > q1 && long.TryParse(c.AsSpan(q1 + 1, q2 - q1 - 1), out var n) ? n : -1;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Baseline = true, Description = "old path (streams the join into COUNT)")]
    public long OldPath() => CountResult(SparqlEngine.Query(_store, Query));

    [Benchmark(Description = "tree (materializes the 1M-row intermediate)")]
    public long TreeMaterialized() => CountResult(SparqlEngine.QueryViaTreeForDifferential(_store, Query));
}
