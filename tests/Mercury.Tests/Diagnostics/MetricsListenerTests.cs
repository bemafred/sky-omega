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
    public async Task RebuildListener_Reference_EmitsGposAndTrigramPhases()
    {
        // ADR-030 Decision 5: Reference rebuild now populates GPOS + trigram from
        // the GSPO-only bulk output (mirrors Cognitive's bulk/rebuild split).
        var dir = Path.Combine(_testDir, "rebuild_ref");
        Directory.CreateDirectory(dir);

        using var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference });
        using var nt = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "<http://ex/a> <http://ex/p> \"literal\" .\n"));
        await RdfEngine.LoadStreamingAsync(store, nt, RdfFormat.NTriples);

        var listener = new CapturingRebuildListener();
        store.RebuildMetricsListener = listener;
        store.RebuildSecondaryIndexes();

        // Two phase records (GPOS and Trigram) plus a summary.
        Assert.Equal(2, listener.Phases.Count);
        Assert.Equal("GPOS", listener.Phases[0].IndexName);
        Assert.Equal(1, listener.Phases[0].EntriesProcessed);
        Assert.Equal("Trigram", listener.Phases[1].IndexName);
        Assert.Equal(1, listener.Phases[1].EntriesProcessed); // one literal object

        Assert.Single(listener.Summaries);
        Assert.False(listener.Summaries[0].WasNoOp);
        Assert.Equal(StoreProfile.Reference, listener.Summaries[0].Profile);
        Assert.Equal(2, listener.Summaries[0].Phases.Count);
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
    public async Task JsonlMetricsListener_ConcurrentWrites_NoTornRecords()
    {
        // Parallel rebuild (ADR-030 Phase 5.1.b) will have multiple consumer threads
        // emitting records concurrently. Zero tolerance for torn records — every line
        // in the output must be a complete, parseable JSON object. Spawn a mix of
        // callers hitting every public entry point hard and verify every line parses.
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        const int threadCount = 16;
        const int recordsPerThread = 500;

        var barrier = new System.Threading.Barrier(threadCount);
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < recordsPerThread; i++)
                {
                    switch ((threadId + i) % 4)
                    {
                        case 0:
                            jsonl.OnQueryMetrics(new QueryMetrics(
                                DateTimeOffset.UtcNow, StoreProfile.Cognitive,
                                QueryMetricsKind.Select,
                                TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2),
                                42, true, null));
                            break;
                        case 1:
                            jsonl.OnRebuildPhase(new RebuildPhaseMetrics(
                                DateTimeOffset.UtcNow, "GPOS", 1000, TimeSpan.FromSeconds(1)));
                            break;
                        case 2:
                            jsonl.OnRebuildComplete(new RebuildMetrics(
                                DateTimeOffset.UtcNow, StoreProfile.Reference,
                                TimeSpan.FromSeconds(5),
                                System.Array.Empty<RebuildPhaseMetrics>(), false));
                            break;
                        case 3:
                            // External-producer path — the CLI's WriteMetric uses this
                            // for load-progress records. Must coexist with listener paths.
                            jsonl.WriteLine($"{{\"phase\":\"load\",\"thread\":{threadId},\"i\":{i}}}");
                            break;
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        jsonl.Flush();

        // Every line must be valid JSON. No torn records, no interleaving, no empty lines.
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        int lineCount = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue; // tolerant of trailing newline
            try
            {
                using var doc = JsonDocument.Parse(line);
                Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object);
            }
            catch (JsonException ex)
            {
                Assert.Fail($"Line {lineCount} was not valid JSON: {ex.Message}\nLine: {line}");
            }
            lineCount++;
        }

        // Every call produced exactly one record.
        Assert.Equal(threadCount * recordsPerThread, lineCount);
    }

    [Fact]
    public void JsonlMetricsListener_WriteLine_RoutesExternalRecords()
    {
        // The public WriteLine method lets the CLI share this listener's single
        // writer for its legacy load-progress records — critical for the
        // "one writer per --metrics-out file" contract.
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        jsonl.WriteLine("{\"phase\":\"load\",\"triples\":100000}");
        jsonl.OnQueryMetrics(new QueryMetrics(
            DateTimeOffset.UtcNow, StoreProfile.Cognitive, QueryMetricsKind.Select,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 0, true, null));
        jsonl.WriteLine("{\"phase\":\"load.summary\",\"triples\":100000}");
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var lines = new System.Collections.Generic.List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.Length > 0) lines.Add(line);

        Assert.Equal(3, lines.Count);
        Assert.Contains("\"load\"", lines[0]);
        Assert.Contains("\"query\"", lines[1]);
        Assert.Contains("\"load.summary\"", lines[2]);
    }

    [Fact]
    public void JsonlMetricsListener_AllRecords_CarrySchemaVersionAndRecordKind()
    {
        // ADR-035 Decision 4: every record carries schema_version. Decision 5: every record
        // carries record_kind ("event" | "state"). Round-trip an event-class and a
        // state-class record; both must include both fields.
        using var buffer = new MemoryStream();
        var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        jsonl.OnQueryMetrics(new QueryMetrics(
            DateTimeOffset.UtcNow, StoreProfile.Cognitive, QueryMetricsKind.Select,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 0, true, null));

        jsonl.OnRssState(new RssState(DateTimeOffset.UtcNow, 1_000_000, 800_000));
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        var line1 = reader.ReadLine();
        var line2 = reader.ReadLine();

        using var doc1 = JsonDocument.Parse(line1!);
        using var doc2 = JsonDocument.Parse(line2!);

        Assert.Equal("1", doc1.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("event", doc1.RootElement.GetProperty("record_kind").GetString());
        Assert.Equal("query", doc1.RootElement.GetProperty("phase").GetString());

        Assert.Equal("1", doc2.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("state", doc2.RootElement.GetProperty("record_kind").GetString());
        Assert.Equal("rss", doc2.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public void JsonlMetricsListener_RebuildProgress_RoundTrip()
    {
        using var buffer = new MemoryStream();
        var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true);

        var progress = new RebuildProgressMetrics(
            Timestamp: new DateTimeOffset(2026, 04, 26, 12, 0, 0, TimeSpan.Zero),
            PhaseName: "GPOS",
            SubPhase: "emission",
            EntriesProcessed: 5_000_000,
            EstimatedTotal: 21_300_000_000,
            RatePerSecond: 95_000.0,
            GcHeapBytes: 1_500_000_000,
            WorkingSetBytes: 8_000_000_000,
            Elapsed: TimeSpan.FromMinutes(45));
        jsonl.OnRebuildProgress(in progress);
        jsonl.Flush();

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        using var doc = JsonDocument.Parse(reader.ReadLine()!);
        var root = doc.RootElement;

        Assert.Equal("rebuild_progress", root.GetProperty("phase").GetString());
        Assert.Equal("GPOS", root.GetProperty("phase_name").GetString());
        Assert.Equal("emission", root.GetProperty("sub_phase").GetString());
        Assert.Equal(5_000_000, root.GetProperty("entries_processed").GetInt64());
        Assert.Equal(21_300_000_000, root.GetProperty("estimated_total").GetInt64());
        Assert.Equal(95_000.0, root.GetProperty("rate_per_sec").GetDouble());
    }

    [Fact]
    public void JsonlMetricsListener_PeriodicTimer_FiresRegisteredProducers()
    {
        using var buffer = new MemoryStream();
        using var jsonl = new JsonlMetricsListener(buffer, leaveOpen: true,
            stateEmissionInterval: TimeSpan.FromMilliseconds(50));

        int producerInvocations = 0;
        jsonl.RegisterStateProducer(l =>
        {
            System.Threading.Interlocked.Increment(ref producerInvocations);
            l.OnRssState(new RssState(DateTimeOffset.UtcNow, 1024, 512));
        });

        // Wait for the timer to fire several times.
        System.Threading.Thread.Sleep(300);
        jsonl.Flush();

        Assert.True(producerInvocations >= 2,
            $"expected at least 2 timer fires in 300ms; saw {producerInvocations}");

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, leaveOpen: true);
        int rssRecords = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.Contains("\"phase\":\"rss\"")) rssRecords++;
        Assert.True(rssRecords >= 2, $"expected ≥ 2 rss state records; saw {rssRecords}");
    }

    [Fact]
    public void QuadStore_ObservabilityListener_FansOutWithoutDoubleEmission()
    {
        // ADR-035 Decision 1: same instance registered as both legacy QueryMetricsListener
        // and umbrella ObservabilityListener must NOT receive the event twice. Reference
        // equality check in EmitQueryMetrics gates the second call.
        var dir = Path.Combine(_testDir, "fanout");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        var listener = new CapturingQueryListenerWithUmbrella();
        store.QueryMetricsListener = listener;
        store.ObservabilityListener = listener;

        store.AddCurrent("s", "p", "o");
        SparqlEngine.Query(store, "SELECT ?s WHERE { ?s ?p ?o }");

        // Exactly one capture, even though the same instance is wired to both slots.
        Assert.Equal(1, listener.QueryCount);
    }

    private sealed class CapturingQueryListenerWithUmbrella : IQueryMetricsListener, IObservabilityListener
    {
        public int QueryCount;
        public void OnQueryMetrics(in QueryMetrics metrics)
            => System.Threading.Interlocked.Increment(ref QueryCount);
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
