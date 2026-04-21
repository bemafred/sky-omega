using System;
using System.IO;
using System.Text.Json;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Emits <see cref="QueryMetrics"/> and <see cref="RebuildPhaseMetrics"/>/<see cref="RebuildMetrics"/>
/// records as one JSON object per line to a supplied stream. Intended to plug into
/// <c>QuadStore.QueryMetricsListener</c> / <c>QuadStore.RebuildMetricsListener</c> and
/// into the existing <c>--metrics-out</c> CLI path so load progress, query metrics, and
/// rebuild metrics all land in a single JSONL stream ready for <c>jq</c> or ingestion
/// into a time-series DB.
/// </summary>
/// <remarks>
/// Thread-safe via a coarse lock around each record. Listeners are called on the query
/// / rebuild threads — the lock ensures JSONL records never interleave. Close / Dispose
/// flushes the underlying writer.
/// </remarks>
public sealed class JsonlMetricsListener : IQueryMetricsListener, IRebuildMetricsListener, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();
    private bool _disposed;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>Wrap an existing stream. The caller keeps ownership unless <paramref name="leaveOpen"/> is false.</summary>
    public JsonlMetricsListener(Stream stream, bool leaveOpen = true)
    {
        // AutoFlush=true so a missed explicit Dispose (e.g. process exit without a
        // shutdown hook) still leaves the file complete on disk. Per-record flush cost
        // is negligible relative to the query/rebuild work being measured.
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: leaveOpen) { AutoFlush = true };
        _ownsWriter = !leaveOpen;
    }

    /// <summary>Convenience constructor that opens a file in append mode.</summary>
    public JsonlMetricsListener(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = true };
        _ownsWriter = true;
    }

    public void OnQueryMetrics(in QueryMetrics metrics)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("phase", "query");
            json.WriteString("ts", metrics.Timestamp.ToString("o"));
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
            json.WriteStartObject();
            json.WriteString("phase", "rebuild_phase");
            json.WriteString("ts", phase.Timestamp.ToString("o"));
            json.WriteString("index", phase.IndexName);
            json.WriteNumber("entries", phase.EntriesProcessed);
            json.WriteNumber("elapsed_ms", phase.Elapsed.TotalMilliseconds);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
    }

    public void OnRebuildComplete(RebuildMetrics summary)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("phase", "rebuild_complete");
            json.WriteString("ts", summary.Timestamp.ToString("o"));
            json.WriteString("profile", summary.Profile.ToString());
            json.WriteNumber("total_ms", summary.TotalElapsed.TotalMilliseconds);
            json.WriteNumber("phase_count", summary.Phases.Count);
            json.WriteBoolean("no_op", summary.WasNoOp);
            json.WriteEndObject();
        }
        WriteBufferedLine(buffer);
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
    /// Write a caller-serialized JSON line to the underlying stream under the same
    /// lock the listener uses for its own records. Lets external record producers
    /// (e.g. the CLI's load-progress path) share the listener's writer so every
    /// record — query, rebuild-phase, rebuild-complete, load, load.summary — goes
    /// through one lock and one buffer. This is the contract parallel rebuild will
    /// rely on: many concurrent producers, zero torn records.
    /// </summary>
    public void WriteLine(string jsonLine)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine(jsonLine);
        }
    }

    /// <summary>Flush pending records to the underlying stream without closing.</summary>
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
