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
/// fresh one. While a request runs the supervisor asks the worker how it is every <see cref="HealthEvery"/>, and
/// discards it (ADR-016 6.8) when it is suspected, when it calls its debugger lost, or when it is gone before it
/// answers: the worker is killed, an owned target still running under the same identity is ended through the
/// operating system, a borrowed one is left as it is, the pending tool call is answered with what is known, and the
/// anomalies say what happened. A session tool asked for with no session open also goes to a fresh worker, which answers it as the
/// session answers it. When drhook-mcp ends, each worker's stdin closes: a session still open is ended as
/// <c>drhook_stop</c> ends it, and a target left running is left running.</summary>
public sealed class Supervisor : IDisposable
{
    /// <summary>How long drhook-mcp's own end waits for its workers to end their sessions.</summary>
    private static readonly TimeSpan WorkersEndBound = TimeSpan.FromSeconds(10);

    /// <summary>How often a worker running a request is asked how it is (ADR-016 6.8).</summary>
    private static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(5);

    /// <summary>How long a health request may go unanswered before the worker is suspected (ADR-016 6.8). The worker's
    /// stdin thread answers it at once; the bound is to be checked against measured round trips before it is kept.</summary>
    private static readonly TimeSpan HealthBound = TimeSpan.FromSeconds(5);

    /// <summary>How long a request's debugger work may run before the worker is suspected: 6.8's bound. Waiting for the
    /// target (a stop, an evaluation's end) is not debugger work; each such wait has its own timeout.</summary>
    private static readonly TimeSpan DebuggerWorkBound = TimeSpan.FromSeconds(30);

    private readonly string _workerDirectory;
    private readonly Journal _journal;
    private readonly BoundedAnomalySink _anomalies = new(BoundedAnomalySink.SessionCapacity);
    private readonly BoundedLogSink _logs = new(BoundedLogSink.SessionCapacity);
    private readonly BoundedConsoleSink _console = new(BoundedConsoleSink.SessionCapacity);
    private readonly IDebugEventSink _records;
    private readonly SemaphoreSlim _oneRequestAtATime = new(1, 1);
    private WorkerProcess? _inSession;
    private readonly List<WorkerProcess> _holding = new();

    /// <summary>A supervisor whose workers start from <paramref name="workerDirectory"/>, where drhook-worker is built
    /// (worker/ in drhook-mcp's own directory), and which journals its discards and kills in
    /// <paramref name="journalPath"/> (journal.ndjson in DrHook's directory, ADR-016 6.9).</summary>
    public Supervisor(string workerDirectory, string journalPath)
    {
        _workerDirectory = workerDirectory;
        _journal = new Journal(journalPath);
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
                if (worker is null)
                {
                    RaiseWorkerDiscarded(0, tool, failure!, "unknown", (0, false, "unknown", false), journal: null);
                    return new WorkerAnswer(OperatorJson.Error(failure!), null, null, WorkerProtocol.Ended);
                }
                if (tool == "drhook_launch") worker.Program = (string?)args["program"];
            }
            if (tool == "drhook_kill") JournalTheKill(worker, args);
            return Settle(worker, tool, await Supervise(worker, tool, args));
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
        JournalTheKill(holder, args);
        return Settle(holder, "drhook_kill", await Supervise(holder, "drhook_kill", args));
    }

    // Every drhook_kill is journaled before it is sent: the session's Abandon, or the kill of a target drhook_stop left
    // running (ADR-016 6.9). The reason is the operator's hypothesis, which drhook_kill records as the anomaly's value.
    private void JournalTheKill(WorkerProcess worker, JsonObject args) => _journal.Write("drhook_kill", new JsonObject
    {
        ["workerPid"] = worker.Pid,
        ["reason"] = (string?)args["hypothesis"],
        ["target"] = TargetFacts(worker),
    });

    // The target as the journal records it: identity, the program's path, the last reported state. No arguments, no
    // environment, no values.
    private static JsonObject? TargetFacts(WorkerProcess worker) => worker.Target is { } t
        ? new JsonObject
        {
            ["pid"] = t.Pid,
            ["start"] = t.Start,
            ["owned"] = t.Owned,
            ["program"] = worker.Program,
            ["lastKnown"] = worker.LastState,
        }
        : null;

    // The request, watched: every HealthEvery the worker is asked how it is. It is suspected when it does not answer
    // within HealthBound, or when its debugger work has run past DebuggerWorkBound, and then discarded at once.
    private async Task<WorkerAnswer> Supervise(WorkerProcess worker, string tool, JsonObject args)
    {
        Task<WorkerAnswer> pending = worker.CallAsync(tool, args);
        while (true)
        {
            if (await Task.WhenAny(pending, Task.Delay(HealthEvery)) == pending) return await pending;
            JsonNode? health = await worker.HealthAsync(HealthBound);
            if (pending.IsCompleted) return await pending;
            string? suspicion = health is null
                ? $"it did not answer a health request within {HealthBound.TotalSeconds:0} s"
                : (string?)health["phase"] == WorkerProtocol.DebuggerWork && (long?)health["phaseMs"] > DebuggerWorkBound.TotalMilliseconds
                    ? $"its debugger work in {tool} had run for {(long)health["phaseMs"]! / 1000.0:0.#} s, past {DebuggerWorkBound.TotalSeconds:0} s"
                    : null;
            if (suspicion is null) continue;
            _anomalies.OnAnomaly(new EngineAnomaly(DateTimeOffset.UtcNow, AnomalyKind.WorkerSuspected, "drhook-mcp", "Supervise",
                Observed: $"the debugger worker (pid {worker.Pid}) running {tool}: {suspicion}",
                Expected: "a health answer at once, and debugger work within its bound (ADR-016 6.8)",
                Context: new Dictionary<string, string>
                {
                    ["workerPid"] = worker.Pid.ToString(CultureInfo.InvariantCulture), ["tool"] = tool, ["reason"] = suspicion,
                }));
            return Discard(worker, tool, $"suspected: {suspicion}", "unknown");
        }
    }

    // What becomes of a worker after its answer: it stays in session, it holds a target left running, it is asked to
    // exit, or it is discarded (its debugger is lost, or it ended before it answered). Returns the answer to give.
    private WorkerAnswer Settle(WorkerProcess worker, string tool, WorkerAnswer answer)
    {
        switch (answer.Session)
        {
            case WorkerProtocol.Active:
                _inSession = worker;
                return answer;
            case WorkerProtocol.Holding:
                if (_inSession == worker) _inSession = null;
                lock (_holding) if (!_holding.Contains(worker)) _holding.Add(worker);
                return answer;
            case WorkerProtocol.Lost:
                // The worker answered as the session does; its debugger cannot be trusted with another call, so it is
                // discarded rather than asked to exit (ADR-016 6.8).
                Discard(worker, tool, $"it called its debugger lost ({answer.LostCause})", answer.LostCause ?? "unknown");
                return answer;
            case WorkerProcess.Gone:
                return Discard(worker, tool, "it ended before it answered", "unknown");
            case WorkerProcess.Discarded:
                return answer; // the discard has dealt with the worker already
            default:
                if (_inSession == worker) _inSession = null;
                lock (_holding) _holding.Remove(worker);
                if (!worker.End())
                    RaiseWorkerDiscarded(worker.Pid, tool, $"it did not end within {WorkerProcess.ExitBound.TotalSeconds:0} s of its exit request, and was killed", "unknown", (0, false, "unknown", false), journal: null);
                return answer;
        }
    }

    // The discard (ADR-016 6.7): the worker is killed and waited for; an owned target still running under the same
    // identity is ended through the operating system, a borrowed one is left as it is; the pending call is answered
    // with the session's outcome, the target's state as the operating system shows it, and the cause as the evidence
    // shows it; the anomalies say what happened. The next drhook_launch or drhook_attach starts a fresh worker.
    private WorkerAnswer Discard(WorkerProcess worker, string tool, string reason, string cause)
    {
        if (_inSession == worker) _inSession = null;
        lock (_holding) _holding.Remove(worker);
        string? lastState = worker.LastState;
        string journaled = _journal.Write("discard", new JsonObject
        {
            ["workerPid"] = worker.Pid,
            ["tool"] = tool,
            ["reason"] = reason,
            ["cause"] = cause,
            ["target"] = TargetFacts(worker),
        });
        worker.KillAndWait();

        (int Pid, bool Owned, string State, bool EndedByDrHook) target = (0, false, "unknown", false);
        if (worker.Target is { } named)
        {
            bool? ended = worker.TargetHasEnded();
            bool endedByDrHook = false;
            if (ended == false && named.Owned)
            {
                _journal.Write("kill after discard", new JsonObject
                {
                    ["workerPid"] = worker.Pid, ["pid"] = named.Pid, ["start"] = named.Start, ["program"] = worker.Program,
                });
                worker.KillTarget();
                ended = worker.TargetHasEnded();
                endedByDrHook = ended == true;
            }
            target = (named.Pid, named.Owned, ended switch { true => "ended", false => "running", _ => "unknown" }, endedByDrHook);
        }
        worker.Dispose();
        RaiseWorkerDiscarded(worker.Pid, tool, reason, cause, target, journaled);
        bool leftHeld = !target.Owned && target.State == "running" && !OperatingSystem.IsWindows()
                        && lastState is not null && lastState.StartsWith("stopped", StringComparison.Ordinal);
        if (leftHeld)
            _anomalies.OnAnomaly(new EngineAnomaly(DateTimeOffset.UtcNow, AnomalyKind.TargetLeftHeld, "drhook-mcp", "Discard",
                Observed: $"the borrowed target {target.Pid} was {lastState} when its worker was discarded",
                Expected: "a borrowed target left running, not held",
                Context: new Dictionary<string, string>
                {
                    ["pid"] = target.Pid.ToString(CultureInfo.InvariantCulture), ["lastState"] = lastState!,
                }));

        var result = new JsonObject
        {
            ["status"] = "workerDiscarded",
            ["operation"] = tool,
            ["reason"] = reason,
            ["cause"] = cause,
            ["workerPid"] = worker.Pid,
        };
        if (target.Pid != 0)
            result["target"] = new JsonObject
            {
                ["pid"] = target.Pid,
                ["owned"] = target.Owned,
                ["state"] = target.State,
                ["endedByDrHook"] = target.EndedByDrHook,
                ["lastKnown"] = lastState,
            };
        string targetSays = target.Pid == 0 ? "The session had no target yet."
            : target.EndedByDrHook ? $"Its target (pid {target.Pid}, owned) was ended through the operating system."
            : target.Owned ? $"Its target (pid {target.Pid}, owned) is {target.State}."
            : leftHeld ? $"Its target (pid {target.Pid}, borrowed) was held at a stop and is left so: on this platform it stays frozen, and cannot be debugged again until it restarts (finding 106)."
            : $"Its target (pid {target.Pid}, borrowed) is left as it is: {target.State}.";
        result["prompt"] = $"The debugger worker was discarded during {tool}, so the session is over: {reason}. {targetSays} " +
                           "Drain drhook_drain_anomalies for the record. The next drhook_launch or drhook_attach starts a fresh worker.";
        return new WorkerAnswer(result.ToJsonString(OperatorJson.Options), null, null, WorkerProcess.Discarded);
    }

    // The discard's anomaly carries its journal entry, so the operator can consolidate it into Mercury (ADR-016 6.9).
    private void RaiseWorkerDiscarded(int workerPid, string tool, string reason, string cause, (int Pid, bool Owned, string State, bool EndedByDrHook) target, string? journal)
    {
        var context = new Dictionary<string, string>
        {
            ["workerPid"] = workerPid.ToString(CultureInfo.InvariantCulture),
            ["tool"] = tool,
            ["reason"] = reason,
            ["cause"] = cause,
            ["pid"] = target.Pid.ToString(CultureInfo.InvariantCulture),
            ["owned"] = target.Owned ? "true" : "false",
            ["targetState"] = target.State,
        };
        if (journal is not null) context["journal"] = journal;
        _anomalies.OnAnomaly(new EngineAnomaly(DateTimeOffset.UtcNow, AnomalyKind.WorkerDiscarded, "drhook-mcp", "Discard",
            Observed: $"the debugger worker (pid {workerPid}) running {tool} was discarded: {reason}",
            Expected: "a worker that answers, and ends when asked (ADR-016 6.7, 6.8)",
            Context: context));
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
