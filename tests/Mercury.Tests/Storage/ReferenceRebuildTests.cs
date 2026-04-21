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

        var trigramPhase = listener.Phases.Find(p => p.IndexName == "Trigram");
        Assert.NotEqual(default, trigramPhase);
        // Two literal objects ("Alice", "Bob"); the IRI object is not indexed.
        Assert.Equal(2, trigramPhase.EntriesProcessed);
    }

    [Fact]
    public async Task Rebuild_EmitsGposAndTrigramPhases_InThatOrder()
    {
        var dir = Path.Combine(_testDir, "phase_order");
        using var store = await BulkLoadReferenceAsync(dir,
            "<http://ex/a> <http://ex/p> \"x\" .\n");

        var listener = new TestRebuildListener();
        store.RebuildMetricsListener = listener;
        store.RebuildSecondaryIndexes();

        Assert.Equal(2, listener.Phases.Count);
        Assert.Equal("GPOS", listener.Phases[0].IndexName);
        Assert.Equal("Trigram", listener.Phases[1].IndexName);
        Assert.Single(listener.Summaries);
        Assert.False(listener.Summaries[0].WasNoOp);
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
        public readonly System.Collections.Generic.List<RebuildPhaseMetrics> Phases = new();
        public readonly System.Collections.Generic.List<RebuildMetrics> Summaries = new();
        public void OnRebuildPhase(in RebuildPhaseMetrics phase) => Phases.Add(phase);
        public void OnRebuildComplete(RebuildMetrics summary) => Summaries.Add(summary);
    }
}
