using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-047 spike diagnostic: the benchmark showed the selectivity reorder is a no-op (planned ≈ unplanned) AND the
/// old path is slow too — so the QueryPlanner is not reordering this pessimal-order query. This probes WHY: it prints
/// the per-pattern cardinality estimate and the order QueryPlanner.OptimizePatternOrder returns. Not an assertion —
/// a characterization print.
/// </summary>
public class PlannerSpikeDiagnosticTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public PlannerSpikeDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
        var tempPath = TempPath.Test("planner-diag");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        _store.BeginBatch();
        for (int i = 0; i < 5000; i++) _store.AddCurrentBatched($"<urn:s{i}>", "<urn:p>", $"<urn:o{i}>");
        for (int i = 0; i < 5; i++) _store.AddCurrentBatched($"<urn:o{i}>", "<urn:rare>", $"<urn:x{i}>");
        _store.CommitBatch();
        _store.Checkpoint(); // collect predicate statistics (CollectPredicateStatistics) so the planner has cardinalities
    }

    public void Dispose() { _store.Dispose(); TempPath.SafeCleanup(_testPath); }

    [Fact]
    public void Diagnose_PlannerReorder()
    {
        const string query = "SELECT ?s ?x WHERE { ?s <urn:p> ?o . ?o <urn:rare> ?x }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var gp = parsed.WhereClause.Pattern;

        _store.AcquireReadLock();
        try
        {
            var planner = new QueryPlanner(_store.Statistics, _store.Atoms);

            var none = new List<int>();
            double c0 = planner.EstimateCardinality(gp.GetPattern(0), query.AsSpan(), none);
            double c1 = planner.EstimateCardinality(gp.GetPattern(1), query.AsSpan(), none);
            int[] order = planner.OptimizePatternOrder(gp, query.AsSpan());

            _output.WriteLine($"pattern count: {gp.PatternCount}");
            _output.WriteLine($"cardinality[0] (?s <urn:p> ?o, 5000 triples): {c0}");
            _output.WriteLine($"cardinality[1] (?o <urn:rare> ?x, 5 triples): {c1}");
            _output.WriteLine($"OptimizePatternOrder => [{string.Join(", ", order)}]   (expect [1, 0] selective-first)");

            // With statistics collected (Checkpoint), the planner sees the real cardinalities and reorders the
            // selective pattern (?o <urn:rare> ?x) before the high-cardinality one (?s <urn:p> ?o). This is the
            // selectivity model the ADR-047 spike reuses to plan the tree; without Checkpoint both estimate the
            // default (1000) and the order is unchanged.
            Assert.True(c0 > c1, $"expected p (5000) more cardinal than rare (5): card0={c0} card1={c1}");
            Assert.Equal(new[] { 1, 0 }, order);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }
}
