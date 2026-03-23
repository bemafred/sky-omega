using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace SkyOmega.DrHook.Diagnostics;

/// <summary>
/// Captures and SUMMARIZES live observations from a running .NET process via EventPipe.
///
/// Cross-LLM refinements applied:
///   #1 Signal summarization — collapsed stacks, thread state overview, anomaly flags.
///      Raw EventPipe output overwhelms LLM context. Summaries are diagnosis-ready.
///   #2 Code version anchoring — assembly version captured with every observation.
///      Grounds the observation in a specific code state. Prevents bitemporal desync.
///   #3 Hypothesis field — consumer states expectations BEFORE inspecting.
///      Captures the delta between expectation and reality. Without a prior expectation,
///      an observation has no epistemic value — it's just data.
///
/// EventPipe is the runtime's native cross-platform tracing mechanism.
/// DOTNET_DiagnosticPorts exposes it via IPC — no vsdbg, no proprietary tooling.
/// </summary>
public sealed class StackInspector
{
    private static readonly IReadOnlyList<EventPipeProvider> DefaultProviders =
    [
        new EventPipeProvider(
            "Microsoft-DotNETCore-SampleProfiler",
            System.Diagnostics.Tracing.EventLevel.Informational,
            (long)ClrTraceEventParser.Keywords.None),

        new EventPipeProvider(
            "Microsoft-Windows-DotNETRuntime",
            System.Diagnostics.Tracing.EventLevel.Warning,
            (long)(ClrTraceEventParser.Keywords.Exception |
                   ClrTraceEventParser.Keywords.GC |
                   ClrTraceEventParser.Keywords.Contention))
    ];

    public async Task<ObservationSnapshot> CaptureAsync(int pid, int durationMs, string hypothesis, CancellationToken ct)
    {
        // Capture code version for anchoring
        string assemblyVersion = "unknown";
        string processName = "unknown";
        try
        {
            var proc = Process.GetProcessById(pid);
            processName = proc.ProcessName;
            assemblyVersion = proc.MainModule?.FileVersionInfo.FileVersion ?? "unknown";
        }
        catch
        {
            // Process may not be accessible — continue with unknowns
        }

        var client = new DiagnosticsClient(pid);

        var threadSamples = new Dictionary<int, int>();        // threadId → sample count
        var exceptions = new List<ExceptionEvent>();
        var gcEvents = new List<GcEvent>();
        var contentionCount = 0;

        using var session = client.StartEventPipeSession(DefaultProviders, requestRundown: false);
        using var source = new EventPipeEventSource(session.EventStream);

        source.Clr.ExceptionStart += e =>
        {
            exceptions.Add(new ExceptionEvent(e.TimeStamp, e.ExceptionType, e.ExceptionMessage, e.ThreadID));
        };

        source.Clr.GCStart += e =>
        {
            gcEvents.Add(new GcEvent(e.TimeStamp, e.Depth, e.Reason.ToString()));
        };

        source.Clr.ContentionStart += _ =>
        {
            Interlocked.Increment(ref contentionCount);
        };

        source.Dynamic.All += e =>
        {
            if (e.ProviderName == "Microsoft-DotNETCore-SampleProfiler")
            {
                var tid = e.ThreadID;
                threadSamples[tid] = threadSamples.GetValueOrDefault(tid, 0) + 1;
            }
        };

        // Capture for the specified duration
        var processTask = Task.Run(() => source.Process(), ct);
        await Task.Delay(durationMs, ct);
        session.Stop();
        await processTask;

        // ─── Build summary (refinement #1: summarization) ──────────────

        var totalSamples = threadSamples.Values.Sum();
        var hotThread = threadSamples.Count > 0
            ? threadSamples.MaxBy(kv => kv.Value)
            : new KeyValuePair<int, int>(0, 0);

        var anomalies = new List<string>();

        // Anomaly: single thread consuming >80% of samples (possible infinite loop)
        if (totalSamples > 10 && hotThread.Value > totalSamples * 0.8)
            anomalies.Add($"HOTSPOT: Thread {hotThread.Key} consumed {hotThread.Value}/{totalSamples} samples ({100.0 * hotThread.Value / totalSamples:F0}%) — possible tight loop or spin-wait");

        // Anomaly: high GC pressure
        if (gcEvents.Count > 5)
            anomalies.Add($"GC_PRESSURE: {gcEvents.Count} GC events in {durationMs}ms — possible allocation storm");

        // Anomaly: contention
        if (contentionCount > 0)
            anomalies.Add($"CONTENTION: {contentionCount} lock contention events — possible deadlock risk");

        // Anomaly: unhandled exceptions
        if (exceptions.Count > 0)
            anomalies.Add($"EXCEPTIONS: {exceptions.Count} exception(s) thrown: {string.Join(", ", exceptions.Select(e => e.Type).Distinct())}");

        // Anomaly: no activity at all
        if (totalSamples == 0 && exceptions.Count == 0 && gcEvents.Count == 0)
            anomalies.Add("IDLE: No activity captured — process may be idle, blocked, or observation window too short");

        return new ObservationSnapshot(
            Pid: pid,
            ProcessName: processName,
            AssemblyVersion: assemblyVersion,
            CapturedAt: DateTime.UtcNow,
            DurationMs: durationMs,
            Hypothesis: hypothesis,
            ThreadCount: threadSamples.Count,
            TotalSamples: totalSamples,
            ThreadSummary: threadSamples.Select(kv =>
                new ThreadSummary(kv.Key, kv.Value, 100.0 * kv.Value / Math.Max(totalSamples, 1))).ToList(),
            Exceptions: exceptions,
            GcEvents: gcEvents,
            ContentionCount: contentionCount,
            Anomalies: anomalies);
    }
}

public sealed record ExceptionEvent(DateTime Timestamp, string Type, string Message, int ThreadId);
public sealed record GcEvent(DateTime Timestamp, int Generation, string Reason);
public sealed record ThreadSummary(int ThreadId, int SampleCount, double Percentage);

public sealed record ObservationSnapshot(
    int Pid,
    string ProcessName,
    string AssemblyVersion,
    DateTime CapturedAt,
    int DurationMs,
    string Hypothesis,
    int ThreadCount,
    int TotalSamples,
    List<ThreadSummary> ThreadSummary,
    List<ExceptionEvent> Exceptions,
    List<GcEvent> GcEvents,
    int ContentionCount,
    List<string> Anomalies)
{
    public string ToJson() => JsonSerializer.Serialize(new JsonObject
    {
        // ── Context (anchoring) ──
        ["pid"]             = Pid,
        ["processName"]     = ProcessName,
        ["assemblyVersion"] = AssemblyVersion,
        ["capturedAt"]      = CapturedAt.ToString("O"),
        ["durationMs"]      = DurationMs,

        // ── Epistemic (hypothesis) ──
        ["hypothesis"]      = Hypothesis,

        // ── Summary (not raw data) ──
        ["summary"] = new JsonObject
        {
            ["threadCount"]     = ThreadCount,
            ["totalSamples"]    = TotalSamples,
            ["exceptionCount"]  = Exceptions.Count,
            ["gcEventCount"]    = GcEvents.Count,
            ["contentionCount"] = ContentionCount,
        },

        // ── Thread detail (top 5 by activity) ──
        ["threads"] = new JsonArray(ThreadSummary
            .OrderByDescending(t => t.SampleCount)
            .Take(5)
            .Select(t => (JsonNode)new JsonObject
            {
                ["threadId"]    = t.ThreadId,
                ["samples"]     = t.SampleCount,
                ["percentage"]  = Math.Round(t.Percentage, 1)
            }).ToArray()),

        // ── Exceptions (all — these are high-signal) ──
        ["exceptions"] = new JsonArray(Exceptions.Select(e => (JsonNode)new JsonObject
        {
            ["timestamp"] = e.Timestamp.ToString("O"),
            ["type"]      = e.Type,
            ["message"]   = e.Message,
            ["threadId"]  = e.ThreadId
        }).ToArray()),

        // ── Anomalies (the diagnosis-ready signal) ──
        ["anomalies"] = new JsonArray(Anomalies.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()),

        // ── Delta prompt ──
        ["deltaPrompt"] = Anomalies.Count > 0
            ? $"Anomalies detected. Compare with hypothesis: \"{Hypothesis}\". What is the delta?"
            : $"No anomalies. Does this confirm or challenge your hypothesis: \"{Hypothesis}\"?"

    }, new JsonSerializerOptions { WriteIndented = true });
}
