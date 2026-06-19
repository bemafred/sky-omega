using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// ADR-047 — the memory bound for must-materialize ORDER BY, the "stream when reducible" half applied to ORDER BY +
/// LIMIT. ORDER BY normally materializes the WHOLE match set, sorts it, then slices the first page. But ORDER BY +
/// LIMIT is a top-N: only the first OFFSET+LIMIT rows survive, so the rest never need to live in memory. The tree
/// streams the BGP into a bounded top-K heap (TreeJoinExecutor.StreamFlatBgpTopK) that retains only OFFSET+LIMIT rows
/// and — via MaterializedRowComparer.CompareBindings — only materializes a row that actually beats the worst kept.
/// Here: ORDER BY ?o LIMIT 10 over N rows. The old path materializes all N MaterializedRows; the folded tree retains
/// 10. This measures the retained-and-allocated gap — the must-materialize memory risk at Reference scale.
/// (Genuine disk spill for ORDER BY *without* LIMIT would not help here: QueryResult.Rows materializes the full
/// result regardless — the result floor — so that case needs streaming result presentation, a separate change.)
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class DefaultPathTopKSpike
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    private const int N = 200_000; // N rows ⇒ old path materializes N, tree keeps OFFSET+LIMIT
    private const string Query = "SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY ?o LIMIT 10";

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("topk-spike");
        tempPath.MarkOwnership();
        _dbPath = tempPath;
        _store = new QuadStore(_dbPath);

        _store.BeginBatch();
        for (int i = 0; i < N; i++)
            _store.AddCurrentBatched($"<urn:s{i}>", "<urn:p>", $"<urn:o{i:D8}>"); // zero-padded ⇒ well-defined lexical order
        _store.CommitBatch();

        string old = FirstO(SparqlEngine.Query(_store, Query));
        string tree = FirstO(SparqlEngine.QueryWithBgpReorder(_store, Query));
        if (old != "<urn:o00000000>" || tree != old)
            throw new InvalidOperationException($"correctness gate failed: old={old}, tree={tree}");
    }

    private static string FirstO(QueryResult r) =>
        r.Rows is { Count: > 0 } ? (r.Rows[0].GetValueOrDefault("o") ?? "") : "";

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Baseline = true, Description = "old path (materializes all N, sorts, slices)")]
    public string OldPath() => FirstO(SparqlEngine.Query(_store, Query));

    [Benchmark(Description = "tree (top-K heap, retains OFFSET+LIMIT — ADR-047)")]
    public string TreeTopK() => FirstO(SparqlEngine.QueryWithBgpReorder(_store, Query));
}
