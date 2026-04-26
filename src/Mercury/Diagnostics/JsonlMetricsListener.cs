using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Emits Mercury observability events as one JSON object per line. Implements the
/// <see cref="IObservabilityListener"/> umbrella (ADR-035 Decision 1) plus the legacy
/// <see cref="IQueryMetricsListener"/> / <see cref="IRebuildMetricsListener"/> surfaces
/// for back-compat. Every record carries <c>schema_version</c> (Decision 4) and
/// <c>kind</c> (event vs state).
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe via a coarse lock around each record. Listeners may be invoked on producer
/// threads — the lock ensures JSONL records never interleave.
/// </para>
/// <para>
/// Periodic state-emission timer (Decision 5): when a non-zero <c>stateEmissionInterval</c>
/// is supplied, a background <see cref="System.Threading.Timer"/> fires every interval and
/// invokes registered <see cref="StateProducer"/> callbacks. Producers emit through the
/// listener's normal record paths.
/// </para>
/// <para>
/// Schema version is the contract with downstream JSONL consumers (<c>jq</c>, Grafana, etc).
/// Increment <see cref="SchemaVersion"/> only with a documented migration note.
/// </para>
/// </remarks>
public sealed class JsonlMetricsListener : IObservabilityListener, IQueryMetricsListener, IRebuildMetricsListener, IDisposable
{
    /// <summary>Current JSONL record schema version. Bump only with documented migration.</summary>
    public const string SchemaVersion = "1";

    private readonly StreamWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();
    private readonly List<StateProducer> _stateProducers = new();
    private readonly Timer? _stateTimer;
    private bool _disposed;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>Producer callback invoked on each state-emission tick.</summary>
    public delegate void StateProducer(JsonlMetricsListener listener);

    /// <summary>Wrap an existing stream; caller retains ownership unless <paramref name="leaveOpen"/> is false.</summary>
    public JsonlMetricsListener(Stream stream, bool leaveOpen = true, TimeSpan? stateEmissionInterval = null)
    {
        // AutoFlush=true so a missed explicit Dispose still leaves the file complete on disk.
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: leaveOpen) { AutoFlush = true };
        _ownsWriter = !leaveOpen;
        _stateTimer = StartTimerIfRequested(stateEmissionInterval);
    }

    /// <summary>Open or append to a file.</summary>
    public JsonlMetricsListener(string path, TimeSpan? stateEmissionInterval = null)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = true };
        _ownsWriter = true;
        _stateTimer = StartTimerIfRequested(stateEmissionInterval);
    }

    private Timer? StartTimerIfRequested(TimeSpan? interval)
    {
        if (interval is not { } i || i <= TimeSpan.Zero) return null;
        return new Timer(StateTimerTick, null, i, i);
    }

    /// <summary>Register a producer invoked on each state-emission tick.</summary>
    public void RegisterStateProducer(StateProducer producer)
    {
        if (producer is null) throw new ArgumentNullException(nameof(producer));
        lock (_gate)
        {
            if (_disposed) return;
            _stateProducers.Add(producer);
        }
    }

    private void StateTimerTick(object? _)
    {
        StateProducer[] snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            if (_stateProducers.Count == 0) return;
            snapshot = _stateProducers.ToArray();
        }
        foreach (var producer in snapshot)
        {
            try { producer(this); }
            catch { /* swallow — observability must never break the producer */ }
        }
    }

    public void OnQueryMetrics(in QueryMetrics metrics)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "query", "event", metrics.Timestamp);
            json.WriteString("profile", metrics.Profile.ToString());
            json.WriteString("kind", metrics.Kind.ToString());
            json.WriteNumber("parse_ms", metrics.ParseTime.TotalMilliseconds);
            json.WriteNumber("exec_ms", metrics.ExecutionTime.TotalMilliseconds);
            json.WriteNumber("rows", metrics.RowsReturned);
            json.WriteBoolean("success", metrics.Success);
            if (metrics.ErrorMessage is not null)
                json.WriteString("error", metrics.ErrorMessage);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnRebuildPhase(in RebuildPhaseMetrics phase)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "rebuild_phase", "event", phase.Timestamp);
            json.WriteString("index", phase.IndexName);
            json.WriteNumber("entries", phase.EntriesProcessed);
            json.WriteNumber("elapsed_ms", phase.Elapsed.TotalMilliseconds);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnRebuildProgress(in RebuildProgressMetrics progress)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "rebuild_progress", "event", progress.Timestamp);
            json.WriteString("phase_name", progress.PhaseName);
            json.WriteString("sub_phase", progress.SubPhase);
            json.WriteNumber("entries_processed", progress.EntriesProcessed);
            json.WriteNumber("estimated_total", progress.EstimatedTotal);
            json.WriteNumber("rate_per_sec", progress.RatePerSecond);
            json.WriteNumber("gc_heap_bytes", progress.GcHeapBytes);
            json.WriteNumber("rss_bytes", progress.WorkingSetBytes);
            json.WriteNumber("elapsed_ms", progress.Elapsed.TotalMilliseconds);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnRebuildComplete(RebuildMetrics summary)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "rebuild_complete", "event", summary.Timestamp);
            json.WriteString("profile", summary.Profile.ToString());
            json.WriteNumber("total_ms", summary.TotalElapsed.TotalMilliseconds);
            json.WriteNumber("phase_count", summary.Phases.Count);
            json.WriteBoolean("no_op", summary.WasNoOp);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnLoadProgress(in LoadProgressMetrics progress)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "load_progress", "event", progress.Timestamp);
            json.WriteNumber("triples", progress.TriplesLoaded);
            json.WriteNumber("elapsed_ms", progress.Elapsed.TotalMilliseconds);
            json.WriteNumber("triples_per_sec", progress.TriplesPerSecond);
            json.WriteNumber("recent_triples_per_sec", progress.RecentTriplesPerSecond);
            json.WriteNumber("gc_heap_bytes", progress.GcHeapBytes);
            json.WriteNumber("rss_bytes", progress.WorkingSetBytes);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnGcEvent(in GcEvent ev)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "gc", "event", ev.Timestamp);
            json.WriteNumber("generation", ev.Generation);
            json.WriteNumber("pause_ms", ev.PauseDuration.TotalMilliseconds);
            json.WriteNumber("heap_after_bytes", ev.HeapSizeAfterBytes);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnLohDelta(in LohDeltaEvent ev)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "loh_delta", "event", ev.Timestamp);
            json.WriteNumber("delta_bytes", ev.DeltaBytes);
            json.WriteNumber("total_allocated_bytes", ev.TotalAllocatedBytes);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnRssState(in RssState state)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "rss", "state", state.Timestamp);
            json.WriteNumber("rss_bytes", state.WorkingSetBytes);
            json.WriteNumber("private_bytes", state.PrivateMemoryBytes);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnDiskFreeState(in DiskFreeState state)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "disk_free", "state", state.Timestamp);
            json.WriteString("path", state.Path);
            json.WriteNumber("free_bytes", state.FreeBytes);
            json.WriteNumber("total_bytes", state.TotalBytes);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnAtomInternRate(in AtomInternRate rate)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "atom_intern_rate", "state", rate.Timestamp);
            json.WriteNumber("cumulative_intern", rate.CumulativeIntern);
            json.WriteNumber("rate_per_sec", rate.RatePerSecond);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnAtomLoadFactor(in AtomLoadFactor lf)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "atom_load_factor", "state", lf.Timestamp);
            json.WriteNumber("atom_count", lf.AtomCount);
            json.WriteNumber("bucket_count", lf.BucketCount);
            json.WriteNumber("load_factor", lf.LoadFactor);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnAtomProbeDistance(in AtomProbeDistance dist)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "atom_probe_distance", "state", dist.Timestamp);
            json.WriteNumber("p50", dist.P50);
            json.WriteNumber("p95", dist.P95);
            json.WriteNumber("p99", dist.P99);
            json.WriteNumber("p999", dist.P999);
            json.WriteNumber("max", dist.Max);
            json.WriteNumber("samples", dist.SampleCount);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnAtomRehash(in AtomRehashEvent ev)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "atom_rehash", "event", ev.Timestamp);
            json.WriteNumber("old_buckets", ev.OldBucketCount);
            json.WriteNumber("new_buckets", ev.NewBucketCount);
            json.WriteNumber("duration_ms", ev.Duration.TotalMilliseconds);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnAtomFileGrowth(in AtomFileGrowthEvent ev)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "atom_file_growth", "event", ev.Timestamp);
            json.WriteString("path", ev.FilePath);
            json.WriteNumber("old_length", ev.OldLengthBytes);
            json.WriteNumber("new_length", ev.NewLengthBytes);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnScopeEnter(long scopeId, long parentScopeId, string name, DateTimeOffset timestamp)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "scope_enter", "event", timestamp);
            json.WriteNumber("scope_id", scopeId);
            json.WriteNumber("parent_scope_id", parentScopeId);
            json.WriteString("name", name);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnScopeExit(long scopeId, TimeSpan duration, DateTimeOffset timestamp)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHeader(json, "scope_exit", "event", timestamp);
            json.WriteNumber("scope_id", scopeId);
            json.WriteNumber("duration_ms", duration.TotalMilliseconds);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    private static void WriteHeader(Utf8JsonWriter json, string phase, string recordKind, DateTimeOffset timestamp)
    {
        json.WriteStartObject();
        json.WriteString("schema_version", SchemaVersion);
        json.WriteString("phase", phase);
        json.WriteString("record_kind", recordKind);
        json.WriteString("ts", timestamp.ToString("o"));
    }

    private void WriteBufferedLine(MemoryStream buffer)
    {
        var json = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(json);
        }
    }

    /// <summary>
    /// Write a caller-serialized JSON line under the listener's lock. Lets external
    /// producers (e.g. the CLI's load-progress path) share the listener's writer so every
    /// record goes through one lock and one buffer. Caller is responsible for including
    /// <c>schema_version</c> and <c>kind</c> if downstream consumers require them.
    /// </summary>
    public void WriteLine(string jsonLine)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(jsonLine);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        _stateTimer?.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
            if (_ownsWriter)
                _writer.Dispose();
        }
    }
}
