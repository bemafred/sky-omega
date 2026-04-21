using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-030 Decision 5: Reference profile now uses Cognitive's bulk/rebuild split —
/// inline writes are GSPO-only, <see cref="QuadStore.RebuildSecondaryIndexes"/>
/// populates GPOS and the trigram index from a single GSPO scan. These tests lock
/// down the invariants: state transitions, primary-to-secondary row-count equivalence,
/// trigram inclusion only for literal objects, predicate-bound SPARQL correctness.
/// </summary>
public class ReferenceRebuildTests : IDisposable
{
    private readonly string _testDir;

    public ReferenceRebuildTests()
    {
        var tempPath = TempPath.Test("refrebuild");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private async Task<QuadStore> BulkLoadReferenceAsync(string dir, string nt, bool bulkMode = true)
    {
        Directory.CreateDirectory(dir);
        var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference, BulkMode = bulkMode });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(nt));
        await RdfEngine.LoadStreamingAsync(store, stream, RdfFormat.NTriples);
        return store;
    }

    [Fact]
    public async Task BulkMode_AfterLoad_IndexStateIsPrimaryOnly()
    {
        var dir = Path.Combine(_testDir, "primary_only");
        using var store = await BulkLoadReferenceAsync(dir,
            "<http://ex/a> <http://ex/p> <http://ex/b> .\n");

        // ADR-030 Decision 5: Reference + BulkMode enters PrimaryOnly until rebuild runs.
        Assert.Equal(StoreIndexState.PrimaryOnly, store.IndexState);
    }

    [Fact]
    public async Task Rebuild_PrimaryOnlyToReady_StateTransitionsCorrectly()
    {
        var dir = Path.Combine(_testDir, "state_transition");
        using var store = await BulkLoadReferenceAsync(dir,
            "<http://ex/a> <http://ex/p> <http://ex/b> .\n");

        Assert.Equal(StoreIndexState.PrimaryOnly, store.IndexState);
        store.RebuildSecondaryIndexes();
        Assert.Equal(StoreIndexState.Ready, store.IndexState);
    }

    [Fact]
    public async Task Rebuild_GposRowCount_MatchesGspo()
    {
        // After rebuild, GPOS must contain exactly the same entries as GSPO — just
        // remapped to predicate-first ordering. Uniqueness invariant (Decision 7)
        // guarantees no duplicates get created by the remap.
        var dir = Path.Combine(_testDir, "gpos_count");
        const string nt = """
        <http://ex/a> <http://ex/p1> <http://ex/b> .
        <http://ex/a> <http://ex/p2> <http://ex/c> .
        <http://ex/b> <http://ex/p1> <http://ex/c> .
        <http://ex/a> <http://ex/p1> <http://ex/c> .
        """;
        using var store = await BulkLoadReferenceAsync(dir, nt);

        store.RebuildSecondaryIndexes();

        Assert.Equal(4, store.GetStatistics().QuadCount);
        // All four SPARQL query patterns resolve correctly post-rebuild.
        var allResult = SparqlEngine.Query(store,
            "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }");
        Assert.True(allResult.Success);
        Assert.Contains("4", allResult.Rows![0].Values.First());

        var predResult = SparqlEngine.Query(store,
            "SELECT ?s ?o WHERE { ?s <http://ex/p1> ?o }");
        Assert.Equal(3, predResult.Rows!.Count);
    }

    [Fact]
    public async Task Rebuild_Trigram_IndexesLiteralObjectsOnly()
    {
        var dir = Path.Combine(_testDir, "trigram");
        const string nt = """
        <http://ex/s1> <http://ex/name> "Alice" .
        <http://ex/s2> <http://ex/name> "Bob" .
        <http://ex/s1> <http://ex/knows> <http://ex/s2> .
        """;
        using var store = await BulkLoadReferenceAsync(dir, nt);

        // Capture trigram phase metrics.
        var listener = new TestRebuildListener();
        store.RebuildMetricsListener = listener;
        store.RebuildSecondaryIndexes();

        var trigramPhase = listener.Phases.First(p => p.IndexName == "Trigram");
        Assert.NotEqual(default, trigramPhase);
        // Two literal objects ("Alice", "Bob"); the IRI object is not indexed.
        Assert.Equal(2, trigramPhase.EntriesProcessed);
    }

    [Fact]
    public async Task Rebuild_EmitsGposAndTrigramPhases()
    {
        // ADR-030 Phase 2 parallel rebuild fires OnRebuildPhase from consumer threads
        // concurrently — emission order is non-deterministic. Assert the set.
        var dir = Path.Combine(_testDir, "phase_order");
        using var store = await BulkLoadReferenceAsync(dir,
            "<http://ex/a> <http://ex/p> \"x\" .\n");

        var listener = new TestRebuildListener();
        store.RebuildMetricsListener = listener;
        store.RebuildSecondaryIndexes();

        Assert.Equal(2, listener.Phases.Count);
        var names = new System.Collections.Generic.HashSet<string>(
            listener.Phases.Select(p => p.IndexName));
        Assert.Contains("GPOS", names);
        Assert.Contains("Trigram", names);
        Assert.Single(listener.Summaries);
        Assert.False(listener.Summaries.First().WasNoOp);
    }

    [Fact]
    public async Task RebuildTwice_IsIdempotent()
    {
        // Calling rebuild twice should not double-count entries — Decision 7
        // uniqueness invariant handles the duplicates on the second pass.
        var dir = Path.Combine(_testDir, "idempotent");
        using var store = await BulkLoadReferenceAsync(dir,
            """
            <http://ex/a> <http://ex/p> <http://ex/b> .
            <http://ex/a> <http://ex/q> "lit" .
            """);

        store.RebuildSecondaryIndexes();
        var firstCount = SparqlEngine.Query(store,
            "SELECT (COUNT(*) AS ?n) WHERE { ?s <http://ex/p> ?o }").Rows![0].Values.First();

        store.RebuildSecondaryIndexes();
        var secondCount = SparqlEngine.Query(store,
            "SELECT (COUNT(*) AS ?n) WHERE { ?s <http://ex/p> ?o }").Rows![0].Values.First();

        Assert.Equal(firstCount, secondCount);
    }

    [Fact]
    public async Task ParallelRebuild_LargeDataset_QueryEquivalent()
    {
        // Correctness stress for ADR-030 Phase 2: many triples, small channel capacity
        // via back-pressure, verify every SPARQL pattern returns expected results. If
        // the broadcast/consumer plumbing had a race (lost quads, duplicates, corrupt
        // page splits under concurrency), this test would fail with wrong row counts.
        var dir = Path.Combine(_testDir, "parallel_stress");
        Directory.CreateDirectory(dir);

        // Build a dataset with mixed IRI and literal objects so GPOS + Trigram both
        // see non-trivial work. 2000 triples across 10 predicates, half literals.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            var predicate = $"<http://ex/p{i % 10}>";
            var subject = $"<http://ex/s{i}>";
            var obj = (i % 2 == 0) ? $"\"lit-{i}\"" : $"<http://ex/o{i}>";
            sb.AppendLine($"{subject} {predicate} {obj} .");
        }

        using var store = await BulkLoadReferenceAsync(dir, sb.ToString());
        store.RebuildSecondaryIndexes();

        // Full scan
        var all = SparqlEngine.Query(store, "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }");
        Assert.True(all.Success);
        Assert.Contains("2000", all.Rows![0].Values.First());

        // Predicate-bound (through GPOS) — 10 predicates × 200 subjects = 200 each
        var perPredicate = SparqlEngine.Query(store,
            "SELECT (COUNT(*) AS ?n) WHERE { ?s <http://ex/p5> ?o }");
        Assert.True(perPredicate.Success);
        Assert.Contains("200", perPredicate.Rows![0].Values.First());

        // Subject-bound (through GSPO)
        var perSubject = SparqlEngine.Query(store,
            "SELECT ?p ?o WHERE { <http://ex/s123> ?p ?o }");
        Assert.True(perSubject.Success);
        Assert.Single(perSubject.Rows!);
    }

    [Fact]
    public async Task Rebuild_AcrossReopen_PreservesQueryability()
    {
        var dir = Path.Combine(_testDir, "reopen");
        const string nt = """
        <http://ex/a> <http://ex/p> <http://ex/b> .
        <http://ex/c> <http://ex/p> <http://ex/d> .
        """;

        // Create, load, rebuild — all in one session.
        using (var store = await BulkLoadReferenceAsync(dir, nt))
        {
            store.RebuildSecondaryIndexes();
        }

        // Reopen — state must be Ready (rebuild is durable) and queries still work.
        using var reopened = new QuadStore(dir);
        Assert.Equal(StoreIndexState.Ready, reopened.IndexState);

        var result = SparqlEngine.Query(reopened,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o }");
        Assert.True(result.Success);
        Assert.Equal(2, result.Rows!.Count);
    }

    private sealed class TestRebuildListener : IRebuildMetricsListener
    {
        // Thread-safe: parallel rebuild fires OnRebuildPhase from consumer threads.
        public readonly System.Collections.Concurrent.ConcurrentBag<RebuildPhaseMetrics> Phases = new();
        public readonly System.Collections.Concurrent.ConcurrentBag<RebuildMetrics> Summaries = new();
        public void OnRebuildPhase(in RebuildPhaseMetrics phase) => Phases.Add(phase);
        public void OnRebuildComplete(RebuildMetrics summary) => Summaries.Add(summary);
    }
}
