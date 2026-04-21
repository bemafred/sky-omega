using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

/// <summary>
/// ADR-030 Phase 1: measurement infrastructure tests. Verifies the listener contract
/// (fires on every query/rebuild, no-op default is harmless), the JSONL serialization
/// round-trip, and Reference-profile rebuild's no-op summary.
/// </summary>
public class MetricsListenerTests : IDisposable
{
    private readonly string _testDir;

    public MetricsListenerTests()
    {
        var tempPath = TempPath.Test("metrics");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private sealed class CapturingQueryListener : IQueryMetricsListener
    {
        public readonly List<QueryMetrics> Captured = new();
        public void OnQueryMetrics(in QueryMetrics metrics) => Captured.Add(metrics);
    }

    private sealed class CapturingRebuildListener : IRebuildMetricsListener
    {
        public readonly List<RebuildPhaseMetrics> Phases = new();
        public readonly List<RebuildMetrics> Summaries = new();
        public void OnRebuildPhase(in RebuildPhaseMetrics phase) => Phases.Add(phase);
        public void OnRebuildComplete(RebuildMetrics summary) => Summaries.Add(summary);
    }

    [Fact]
    public void NullListener_QueryAndRebuild_ExecuteNormally()
    {
        var dir = Path.Combine(_testDir, "noop");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        Assert.Null(store.QueryMetricsListener);
        Assert.Null(store.RebuildMetricsListener);

        store.AddCurrent("s", "p", "o");
        var result = SparqlEngine.Query(store, "SELECT ?s WHERE { ?s ?p ?o }");
        Assert.True(result.Success);
        // No exception, no listener state to observe.
    }

    [Fact]
    public void QueryListener_CapturesEveryQuery_WithCorrectFields()
    {
        var dir = Path.Combine(_testDir, "query");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        var listener = new CapturingQueryListener();
        store.QueryMetricsListener = listener;

        store.AddCurrent("a", "b", "c");
        store.AddCurrent("d", "e", "f");

        SparqlEngine.Query(store, "SELECT ?s ?p ?o WHERE { ?s ?p ?o }");
        SparqlEngine.Query(store, "ASK { ?s ?p ?o }");
        SparqlEngine.Query(store, "SELECT ?s WHERE { ?s <not-a-predicate> ?o }");

        Assert.Equal(3, listener.Captured.Count);

        Assert.Equal(QueryMetricsKind.Select, listener.Captured[0].Kind);
        Assert.True(listener.Captured[0].Success);
        Assert.Equal(2, listener.Captured[0].RowsReturned);
        Assert.Equal(StoreProfile.Cognitive, listener.Captured[0].Profile);

        Assert.Equal(QueryMetricsKind.Ask, listener.Captured[1].Kind);
        Assert.Equal(1, listener.Captured[1].RowsReturned);

        Assert.Equal(QueryMetricsKind.Select, listener.Captured[2].Kind);
        Assert.Equal(0, listener.Captured[2].RowsReturned);
    }

    [Fact]
    public void QueryListener_ParseError_CapturesFailureWithMessage()
    {
        var dir = Path.Combine(_testDir, "parse_err");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        var listener = new CapturingQueryListener();
        store.QueryMetricsListener = listener;

        SparqlEngine.Query(store, "SELECT ?s WHERE {{{ broken");

        Assert.Single(listener.Captured);
        Assert.False(listener.Captured[0].Success);
        Assert.NotNull(listener.Captured[0].ErrorMessage);
    }

    [Fact]
    public void RebuildListener_Cognitive_EmitsPerPhaseAndSummary()
    {
        var dir = Path.Combine(_testDir, "rebuild_cog");
        Directory.CreateDirectory(dir);

        // Bulk-load a small dataset into a PrimaryOnly Cognitive store, then rebuild
        // with a listener attached.
        using var store = new QuadStore(dir, null, null, new StorageOptions { BulkMode = true });
        store.BeginBatch();
        store.AddCurrentBatched("s1", "p", "\"literal one\"");
        store.AddCurrentBatched("s2", "p", "\"literal two\"");
        store.AddCurrentBatched("s3", "p", "\"literal three\"");
        store.CommitBatch();
        store.FlushToDisk();

        var listener = new CapturingRebuildListener();
        store.RebuildMetricsListener = listener;
        store.RebuildSecondaryIndexes();

        // Expected phases: GPOS, GOSP, TGSP, Trigram
        Assert.Equal(4, listener.Phases.Count);
        Assert.Equal("GPOS", listener.Phases[0].IndexName);
        Assert.Equal("GOSP", listener.Phases[1].IndexName);
        Assert.Equal("TGSP", listener.Phases[2].IndexName);
        Assert.Equal("Trigram", listener.Phases[3].IndexName);
        Assert.All(listener.Phases, p => Assert.True(p.EntriesProcessed >= 0));

        // Summary fires exactly once with profile and per-phase records.
        Assert.Single(listener.Summaries);
        Assert.Equal(StoreProfile.Cognitive, listener.Summaries[0].Profile);
        Assert.Equal(4, listener.Summaries[0].Phases.Count);
        Assert.False(listener.Summaries[0].WasNoOp);
    }

    [Fact]
    public async Task RebuildListener_Reference_EmitsNoOpSummary()
    {
        var dir = Path.Combine(_testDir, "rebuild_ref");
        Directory.CreateDirectory(dir);

        using var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference });
        using var nt = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "<http://ex/a> <http://ex/p> <http://ex/b> .\n"));
        await RdfEngine.LoadStreamingAsync(store, nt, RdfFormat.NTriples);

        var listener = new CapturingRebuildListener();
        store.RebuildMetricsListener = listener;
        store.RebuildSecondaryIndexes();

        // Reference rebuild emits exactly one summary with WasNoOp=true, zero phases.
        Assert.Empty(listener.Phases);
        Assert.Single(listener.Summaries);
        Assert.True(listener.Summaries[0].WasNoOp);
        Assert.Equal(StoreProfile.Reference, listener.Summaries[0].Profile);
        Assert.Equal(TimeSpan.Zero, listener.Summaries[0].TotalElapsed);
    }

    [Fact]
    public void JsonlMetricsListener_QueryRoundTrip()
    {
        using var buffer = new MemoryStream();
        var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        var metrics = new QueryMetrics(
            Timestamp: new DateTimeOffset(2026, 04, 20, 12, 0, 0, TimeSpan.Zero),
            Profile: StoreProfile.Reference,
            Kind: QueryMetricsKind.Select,
            ParseTime: TimeSpan.FromMilliseconds(7.5),
            ExecutionTime: TimeSpan.FromMilliseconds(42.1),
            RowsReturned: 123,
            Success: true,
            ErrorMessage: null);

        jsonl.OnQueryMetrics(in metrics);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var line = reader.ReadLine();
        Assert.NotNull(line);

        using var doc = JsonDocument.Parse(line!);
        var root = doc.RootElement;
        Assert.Equal("query", root.GetProperty("phase").GetString());
        Assert.Equal("Reference", root.GetProperty("profile").GetString());
        Assert.Equal("Select", root.GetProperty("kind").GetString());
        Assert.Equal(123, root.GetProperty("rows").GetInt64());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void JsonlMetricsListener_RebuildPhaseAndSummary_Distinguishable()
    {
        using var buffer = new MemoryStream();
        var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        var phase = new RebuildPhaseMetrics(
            Timestamp: new DateTimeOffset(2026, 04, 20, 12, 0, 0, TimeSpan.Zero),
            IndexName: "GPOS",
            EntriesProcessed: 1000,
            Elapsed: TimeSpan.FromSeconds(5));
        jsonl.OnRebuildPhase(in phase);

        var summary = new RebuildMetrics(
            Timestamp: new DateTimeOffset(2026, 04, 20, 12, 0, 10, TimeSpan.Zero),
            Profile: StoreProfile.Cognitive,
            TotalElapsed: TimeSpan.FromSeconds(20),
            Phases: new[] { phase },
            WasNoOp: false);
        jsonl.OnRebuildComplete(summary);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var line1 = reader.ReadLine();
        var line2 = reader.ReadLine();

        using var doc1 = JsonDocument.Parse(line1!);
        using var doc2 = JsonDocument.Parse(line2!);

        Assert.Equal("rebuild_phase", doc1.RootElement.GetProperty("phase").GetString());
        Assert.Equal("GPOS", doc1.RootElement.GetProperty("index").GetString());
        Assert.Equal(1000, doc1.RootElement.GetProperty("entries").GetInt64());

        Assert.Equal("rebuild_complete", doc2.RootElement.GetProperty("phase").GetString());
        Assert.Equal("Cognitive", doc2.RootElement.GetProperty("profile").GetString());
        Assert.Equal(1, doc2.RootElement.GetProperty("phase_count").GetInt32());
        Assert.False(doc2.RootElement.GetProperty("no_op").GetBoolean());
    }

    [Fact]
    public void JsonlMetricsListener_ErrorMessage_Serialized()
    {
        using var buffer = new MemoryStream();
        var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        jsonl.OnQueryMetrics(new QueryMetrics(
            Timestamp: DateTimeOffset.UtcNow,
            Profile: StoreProfile.Cognitive,
            Kind: QueryMetricsKind.Select,
            ParseTime: TimeSpan.FromMilliseconds(1),
            ExecutionTime: TimeSpan.Zero,
            RowsReturned: 0,
            Success: false,
            ErrorMessage: "parse error: unexpected '{'"));
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var line = reader.ReadLine();
        using var doc = JsonDocument.Parse(line!);

        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("parse error: unexpected '{'", doc.RootElement.GetProperty("error").GetString());
    }
}
