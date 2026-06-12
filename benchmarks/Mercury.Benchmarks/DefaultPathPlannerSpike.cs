using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// ADR-047 spike — the load-bearing question: does a selectivity-PLANNED tree match the old (planned) default path,
/// and how much does planning matter for the tree? A 2-pattern join with PESSIMAL source order: the first pattern is
/// high-cardinality (50,000 <urn:p> triples), the second is selective (5 <urn:rare> triples). Source-order nested-loop
/// scans the 50,000-row outer set; a selectivity reorder runs the 5-row pattern first, so ~5 outer iterations.
///
/// Three paths over the SAME query:
///   - OldPath        : the shipping default-path executor (QueryPlanner reorders — the baseline).
///   - TreeUnplanned  : TreeJoinExecutor in source order (no planning) — the cost of cutting over naively.
///   - TreePlanned    : TreeJoinExecutor with the QueryPlanner selectivity reorder (ADR-047's proposed design).
///
/// Hypothesis: TreeUnplanned ≫ OldPath (source order is pessimal); TreePlanned ≈ OldPath (planning closes the gap).
/// GlobalSetup gates correctness — all three must return the same 5 rows (a BGP reorder is correctness-neutral).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class DefaultPathPlannerSpike
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    private const string Query = "SELECT ?s ?x WHERE { ?s <urn:p> ?o . ?o <urn:rare> ?x }";

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("planner-spike");
        tempPath.MarkOwnership();
        _dbPath = tempPath;
        _store = new QuadStore(_dbPath);

        _store.BeginBatch();
        for (int i = 0; i < 50_000; i++)
            _store.AddCurrentBatched($"<urn:s{i}>", "<urn:p>", $"<urn:o{i}>");   // high-cardinality
        for (int i = 0; i < 5; i++)
            _store.AddCurrentBatched($"<urn:o{i}>", "<urn:rare>", $"<urn:x{i}>"); // selective
        _store.CommitBatch();
        _store.Checkpoint(); // collect predicate statistics so the planner has real cardinalities (5000 vs 5)

        // Correctness gate: the selectivity reorder is correctness-neutral, so all three paths must agree.
        int old = SparqlEngine.Query(_store, Query).Rows?.Count ?? -1;
        int unplanned = SparqlEngine.QueryViaTreeForDifferential(_store, Query, reorderBgp: false).Rows?.Count ?? -1;
        int planned = SparqlEngine.QueryViaTreeForDifferential(_store, Query, reorderBgp: true).Rows?.Count ?? -1;
        if (old != 5 || unplanned != 5 || planned != 5)
            throw new InvalidOperationException($"correctness gate failed: old={old} unplanned={unplanned} planned={planned} (expected 5)");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Baseline = true, Description = "old path (QueryPlanner)")]
    public int OldPath() => SparqlEngine.Query(_store, Query).Rows!.Count;

    [Benchmark(Description = "tree, source order (unplanned)")]
    public int TreeUnplanned() => SparqlEngine.QueryViaTreeForDifferential(_store, Query, reorderBgp: false).Rows!.Count;

    [Benchmark(Description = "tree, selectivity reorder (planned)")]
    public int TreePlanned() => SparqlEngine.QueryViaTreeForDifferential(_store, Query, reorderBgp: true).Rows!.Count;
}
