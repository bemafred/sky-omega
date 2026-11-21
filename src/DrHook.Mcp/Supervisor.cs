using System.Globalization;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Worker;

namespace SkyOmega.DrHook.Mcp;

/// <summary>drhook-mcp as the supervisor of its debugger workers (ADR-016 6.7's design, accepted 2026-11-21). Every
/// session runs in a drhook-worker of its own, started for it; the supervisor relays the session's tools to it, one
/// request at a time, and keeps what has no debugger in it or outlives a session:
/// <list type="bullet">
/// <item>the anomaly, log and console buffers that the drain tools read. Each worker forwards every record its sinks
/// get; the buffers are reset when a new session starts, so that a session's drains show only it, and they outlive a
/// session's end for its last drain;</item>
/// <item>the workers that hold a target <c>drhook_stop</c> left running (ADR-016 6.2), so that <c>drhook_kill</c> can still
/// end it through its session;</item>
/// <item><c>drhook_processes</c> and <c>drhook_snapshot</c>, which read through EventPipe and load no debugger (in
/// <see cref="DrHookTools"/>).</item>
/// </list>
/// A worker whose session has ended is asked to exit; the next <c>drhook_launch</c> or <c>drhook_attach</c> starts a
/// fresh one. A session tool asked for with no session open also goes to a fresh worker, which answers it as the
/// session answers it. When drhook-mcp ends, each worker's stdin closes: a session still open is ended as
/// <c>drhook_stop</c> ends it, and a target left running is left running.</summary>
public sealed class Supervisor : IDisposable
{
    /// <summary>How long drhook-mcp's own end waits for its workers to end their sessions.</summary>
    private static readonly TimeSpan WorkersEndBound = TimeSpan.FromSeconds(10);

    private readonly string _workerDirectory;
    private readonly BoundedAnomalySink _anomalies = new(BoundedAnomalySink.SessionCapacity);
    private readonly BoundedLogSink _logs = new(BoundedLogSink.SessionCapacity);
    private readonly BoundedConsoleSink _console = new(BoundedConsoleSink.SessionCapacity);
    private readonly IDebugEventSink _records;
    private readonly SemaphoreSlim _oneRequestAtATime = new(1, 1);
    private WorkerProcess? _inSession;
    private readonly List<WorkerProcess> _holding = new();

    /// <summary>A supervisor whose workers start from <paramref name="workerDirectory"/>, where drhook-worker is built
    /// (worker/ in drhook-mcp's own directory).</summary>
    public Supervisor(string workerDirectory)
    {
        _workerDirectory = workerDirectory;
        _records = new CompositeEventSink(_anomalies, _logs, _console);
    }

    /// <summary>The PID of the worker in session, null when no session is open. For the worker's own tests.</summary>
    internal int? SessionWorkerPid => _inSession?.Pid;

    /// <summary>Run a session tool in its worker: the session's, or a fresh one when no session is open. A
    /// <c>drhook_kill</c> with no session open goes to the worker that holds the target it names.</summary>
    public async Task<WorkerAnswer> CallAsync(string tool, JsonObject args, CancellationToken ct)
    {
        await _oneRequestAtATime.WaitAsync(ct);
        try
        {
            if (tool == "drhook_kill" && _inSession is null) return await KillHeldTarget(args);

            WorkerProcess? worker = _inSession;
            if (worker is null)
            {
                if (tool is "drhook_launch" or "drhook_attach") ResetBuffers(); // a new session: its drains show only it
                worker = WorkerProcess.Start(_workerDirectory, _records, out string? failure);
                if (worker is null) return new WorkerAnswer(OperatorJson.Error(failure!), null, null, WorkerProtocol.Ended);
            }
            WorkerAnswer answer = await worker.CallAsync(tool, args);
            Settle(worker, answer.Session);
            return answer;
        }
        finally
        {
            _oneRequestAtATime.Release();
        }
    }

    // drhook_kill with no session open: the target drhook_stop left running, named by its PID when there is more than
    // one (LeftRunningChoice, as the session itself chooses), ended through the worker that holds it.
    private async Task<WorkerAnswer> KillHeldTarget(JsonObject args)
    {
        List<WorkerProcess> holding;
        lock (_holding)
        {
            _holding.RemoveAll(h =>
            {
                if (!h.IsGone && !h.TargetEnded) return false;
                h.Dispose();
                return true;
            });
            holding = new List<WorkerProcess>(_holding);
        }
        LeftRunningChoice choice = LeftRunningChoice.Among(holding.ConvertAll(h => h.TargetPid), (int?)args["pid"]);
        if (choice.Refusal is { } refusal) return new WorkerAnswer(OperatorJson.Error(refusal), null, null, WorkerProtocol.Ended);

        WorkerProcess holder = holding.Find(h => h.TargetPid == choice.Pid)!;
        WorkerAnswer answer = await holder.CallAsync("drhook_kill", args);
        Settle(holder, answer.Session);
        return answer;
    }

    // What becomes of a worker after its answer: it stays in session, it holds a target left running, or it is asked to
    // exit.
    private void Settle(WorkerProcess worker, string session)
    {
        switch (session)
        {
            case WorkerProtocol.Active:
                _inSession = worker;
                break;
            case WorkerProtocol.Holding:
                if (_inSession == worker) _inSession = null;
                lock (_holding) if (!_holding.Contains(worker)) _holding.Add(worker);
                break;
            default:
                if (_inSession == worker) _inSession = null;
                lock (_holding) _holding.Remove(worker);
                worker.End();
                break;
        }
    }

    private void ResetBuffers()
    {
        _console.Reset();
        _logs.Reset();
        _anomalies.Reset();
    }

    // ─── The drain tools' buffers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Drain the substrate-anomaly buffer as a structured JSON envelope. Anomalies array is newest-last;
    /// dropped count reports records lost to capacity since last drain (the substrate's honesty marker — no silent
    /// loss). Backing for drhook_drain_anomalies.</summary>
    public string DrainAnomaliesAsJson()
    {
        AnomalyDrainResult drain = _anomalies.Drain();
        JsonArray records = new();
        foreach (EngineAnomaly a in drain.Anomalies)
        {
            JsonObject record = new()
            {
                ["capturedAt"] = a.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
                ["kind"]       = a.Kind.ToString(),
                ["thread"]     = a.Thread,
                ["operation"]  = a.Operation,
                ["observed"]   = a.Observed,
                ["expected"]   = a.Expected,
            };
            if (a.Context is { Count: > 0 } ctx)
            {
                JsonObject contextNode = new();
                foreach ((string k, string v) in ctx) contextNode[k] = v;
                record["context"] = contextNode;
            }
            records.Add(record);
        }

        return Render(new JsonObject
        {
            ["status"]   = "ok",
            ["count"]    = drain.Anomalies.Count,
            ["dropped"]  = drain.Dropped,
            ["capacity"] = _anomalies.Capacity,
            ["anomalies"] = records,
            ["prompt"] = drain.Anomalies.Count == 0 && drain.Dropped == 0
                ? "No anomalies captured since previous drain."
                : $"{drain.Anomalies.Count} anomalies surfaced ({drain.Dropped} dropped to capacity since previous drain). Each is structured evidence of a substrate-correctness invariant that wasn't upheld — review the Kind and observed-vs-expected delta to decide whether to escalate.",
        });
    }

    /// <summary>Drain the debuggee-console buffer (ADR-011 D3) as a JSON envelope. Chunks are
    /// newest-last; dropped reports chunks lost to capacity since the last drain. Backs drhook_drain_console.</summary>
    public string DrainConsoleAsJson()
    {
        ConsoleDrainResult drain = _console.Drain();
        JsonArray records = new();
        foreach (ConsoleOutputRecord r in drain.Records)
            records.Add(new JsonObject
            {
                ["capturedAt"] = r.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
                ["stream"] = r.Stream.ToString(),
                ["text"] = r.Text,
            });
        return Render(new JsonObject
        {
            ["status"] = "ok",
            ["count"] = drain.Records.Count,
            ["dropped"] = drain.Dropped,
            ["capacity"] = _console.Capacity,
            ["output"] = records,
            ["prompt"] = drain.Records.Count == 0 && drain.Dropped == 0
                ? "No debuggee console output captured since the previous drain."
                : $"{drain.Records.Count} console chunk(s) since previous drain ({drain.Dropped} dropped to capacity). Concatenate text in order for the debuggee's stdout/stderr; chunk boundaries are arbitrary (not line-aligned).",
        });
    }

    /// <summary>Drain the logpoint-output buffer (ADR-011 D3) as a JSON envelope. Records newest-last; dropped reports
    /// records lost to capacity. Backs drhook_drain_log.</summary>
    public string DrainLogAsJson()
    {
        DrainResult drain = _logs.Drain();
        JsonArray records = new();
        foreach (LogRecord r in drain.Records)
            records.Add(new JsonObject
            {
                ["timestamp"] = r.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                ["message"] = r.Message,
                ["isFault"] = r.IsFault,
            });
        return Render(new JsonObject
        {
            ["status"] = "ok",
            ["count"] = drain.Records.Count,
            ["dropped"] = drain.Dropped,
            ["capacity"] = _logs.Capacity,
            ["logs"] = records,
            ["prompt"] = drain.Records.Count == 0 && drain.Dropped == 0
                ? "No logpoint records since the previous drain."
                : $"{drain.Records.Count} logpoint record(s) since previous drain ({drain.Dropped} dropped to capacity). Each is a rendered logMessage template from a suspend='none' breakpoint (or a condition fault, isFault=true).",
        });
    }

    private static string Render(JsonObject obj) => obj.ToJsonString(OperatorJson.Options);

    /// <summary>drhook-mcp's end: each worker's stdin closes, so a session still open is ended as drhook_stop ends it
    /// and a target left running is left running. Waits for them up to <see cref="WorkersEndBound"/>, together; a
    /// worker still running then ends by itself.</summary>
    public void Dispose()
    {
        List<WorkerProcess> workers;
        lock (_holding)
        {
            workers = new List<WorkerProcess>(_holding);
            _holding.Clear();
        }
        if (_inSession is { } inSession) workers.Add(inSession);
        _inSession = null;
        Task.WaitAll(workers.Select(w => Task.Run(() => w.CloseAndWait(WorkersEndBound))).ToArray());
        foreach (WorkerProcess w in workers) w.Dispose();
    }
}
