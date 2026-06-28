// EngineSteppingSession — the BCL-only replacement for SteppingSessionManager (the netcoredbg-
// DAP-backed one in src/DrHook/Stepping/). Same async surface; consumers (DrHookTools
// and through it the MCP wire) see the same JSON-shaped responses. Backed entirely by
// SkyOmega.DrHook.Engine.DebugSession.
//
// One session at a time (the engine model — DI-injected as singleton). drhook_attach / drhook_launch
// create the session; drhook_detach ends it; other tools require IsActive.
//
// JSON shapes preserved at the TOP-LEVEL key level (status, operation, step, currentState,
// stoppedReason, hypothesis, prompt, metrics). Inner shapes (currentState, variables) carry
// engine-derived data — richer than the old DAP-via-JSON shape, but the top-level keys are
// compatible so existing MCP consumers don't break. The `metrics` block is currently a stub
// (the EventPipe-based gathering used by the old session manager is a separate concern that the
// snapshot tool already serves directly).
//
// Phase 3 close (finding 51): retires the netcoredbg dependency from the stepping path. Snapshot
// + processes tools still use DrHook (EventPipe — independent of netcoredbg DAP); retiring that
// project entirely is a follow-on cleanup.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Interop;
using SkyOmega.DrHook.Engine.Transport;
using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Mcp;

public sealed class EngineSteppingSession : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private DebugSession? _session;
    private string _sessionHypothesis = "";
    private string _targetVersion = "unknown";
    private int _stepCount;
    private int _targetPid;
    // _launchedProcess removed (finding 64) — DebugSession.Launch now acquires its own
    // Process handle and kill-firsts internally on Dispose. EngineSteppingSession no
    // longer manages target lifecycle directly.
    // breakpoint id maps (so remove-by-natural-key resolves to engine ids)
    private readonly Dictionary<string, int> _lineBreakpoints = new();       // key = "file:line"
    private readonly Dictionary<string, int> _functionBreakpoints = new();   // key = "Type.Method"
    private readonly Dictionary<string, int> _exceptionFilters = new();      // key = "id={N}" (composite from Increment 6 Deliverable 1)

    // Per-breakpoint-id presentation Specs — the string form the agent originally supplied. The
    // substrate compiles Spec → BreakpointPolicy (delegates); the MCP layer keeps the Spec around
    // so list/inspection tools can show the agent what they configured rather than opaque
    // delegate references. ADR-010 Increment 6 deliverable 2.
    private readonly Dictionary<int, BreakpointPolicySpec> _policySpecs = new();

    // Per-id discriminator so the by-ID remove tool can dispatch to the right substrate path
    // (source/function breakpoints share the substrate's _breakpoints registry and RemoveBreakpoint;
    // exception filters live in _exceptionFilters and use RemoveExceptionFilter). Substrate assigns
    // separate id sequences for the two kinds, so an id alone is ambiguous — the MCP layer carries
    // the kind. ADR-010 Increment 6 deliverable 3.
    private enum BreakpointKind { Source, Function, Exception }
    private readonly Dictionary<int, BreakpointKind> _breakpointKinds = new();

    // EA-5: substrate-anomaly buffer drained by drhook_drain_anomalies (per ADR-007 Phase 1).
    // Cross-session — anomalies accumulate until the consumer drains; capacity 256 (~128 KB max).
    private readonly BoundedAnomalySink _anomalies = new(capacity: 256);

    // ADR-011 D3: logpoint output (previously DROPPED at the MCP layer — BoundedLogSink existed but
    // was never wired) and launched-debuggee console output each get a bounded buffer, drained by
    // drhook_drain_log / drhook_drain_console. _sink fans every channel to them — the surface-agnostic
    // seam (D5): the substrate emits to one sink; Mira views will add their own consumers later.
    private readonly BoundedLogSink _logs = new(capacity: 512);
    private readonly BoundedConsoleSink _console = new(capacity: 512);

    // ADR-012 Phase 2: the debug-state transport — a 4th sink on the composite. It observes the live delta
    // stream via the fan-out (no-ops while not running), and the session driver pushes a fresh snapshot after
    // each stop (PublishTransportSnapshot). Human-launched views connect to the rendezvous socket. Started per
    // session (StartTransport, on attach/launch), stopped in CleanupSession; killing a view never affects the
    // session, and the driver owns snapshot capture so there is no transport<->stepping race.
    private readonly DebugStateServer _transport = new(WireRendezvous.DefaultSocketPath());
    private readonly IDebugEventSink _sink;

    public EngineSteppingSession()
    {
        _sink = new CompositeEventSink(_anomalies, _logs, _console, _transport);
    }

    /// <summary>Drain the substrate-anomaly buffer as a structured JSON envelope. Anomalies
    /// array is newest-last; dropped count reports records lost to capacity since last drain
    /// (the substrate's honesty marker — no silent loss). Backing for drhook_drain_anomalies.
    /// Public to allow direct invocation from validation probes (e.g. Probe 41).</summary>
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

    /// <summary>Drain the logpoint-output buffer (ADR-011 D3 — previously dropped at the MCP layer)
    /// as a JSON envelope. Records newest-last; dropped reports records lost to capacity. Backs drhook_drain_log.</summary>
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

    public bool IsActive => _session is not null;

    // ─── ADR-012 Phase 2 transport plumbing ─────────────────────────────────────────────────────

    /// <summary>Start the debug-state transport for this session — BEST-EFFORT. A bind failure (stale socket,
    /// permissions) must NEVER break debugging: surface it as an anomaly (drained by drhook_drain_anomalies)
    /// and continue without a live view.</summary>
    private void StartTransport()
    {
        try
        {
            _transport.Start();
        }
        catch (Exception ex)
        {
            _anomalies.OnAnomaly(new EngineAnomaly(DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request",
                "DebugStateServer.Start",
                Observed: $"{ex.GetType().Name}: {ex.Message}",
                Expected: "transport listener bound to the rendezvous socket",
                Context: null));
        }
    }

    /// <summary>Push a fresh self-contained snapshot to connected views after a stop. BEST-EFFORT — a transport
    /// fault must never break a debug response. The DRIVER (this MCP request thread) owns the capture, so views
    /// only ever receive immutable snapshots/deltas: there is no transport↔stepping race (ADR-012 Phase 2). The
    /// stream tails are read non-destructively (Peek) so the drain tools still see them.</summary>
    private void PublishTransportSnapshot(StopInfo? stop)
    {
        if (_session is null || !_transport.IsRunning) return;
        try { _transport.PublishSnapshot(_session.CaptureState(stop, _console.Peek(), _logs.Peek(), _anomalies.Peek())); }
        catch { /* transport is best-effort; never break a debug response */ }
    }

    // ─── Session lifecycle ────────────────────────────────────────────────────────────────────

    public Task<string> AttachAsync(int pid, string sourceFile, int line, string hypothesis, CancellationToken ct)
    {
        if (IsActive) return Task.FromResult(Error("A stepping session is already active. Use drhook_stop first."));
        ResetOutputBuffers(); // new session — drains reflect only this session

        try
        {
            try { _targetVersion = Process.GetProcessById(pid).MainModule?.FileVersionInfo.FileVersion ?? "unknown"; }
            catch { _targetVersion = "unknown"; }

            _sessionHypothesis = hypothesis;
            _stepCount = 0;
            _targetPid = pid;

            _session = DebugSession.Attach(pid, _sink);
            StartTransport(); // a view can now connect; it gets its first snapshot at the first stop below
            // Setup stop: the engine breaks on attach (Debugger.IsAttached transition). Drain it.
            StopInfo? setup = _session.WaitForStop(TimeSpan.FromSeconds(10));

            // The target must be synchronized before ICorDebug inspection (SetBreakpointAtLine ->
            // EnumerateAppDomains). If the attached process exited or never broke within the window,
            // enumerating on an unsynchronized/exited CordbProcess faults inside mscordbi and crashes
            // the server. Guard: return a clean status rather than crash.
            if (setup is null)
            {
                CleanupSession();
                return Task.FromResult(Error(
                    $"The attached process did not reach a synchronized stop within 10s, so the breakpoint at {sourceFile}:{line} could not be armed safely. The target may be running without yielding a debugger stop."));
            }
            if (setup.Reason == StopReason.ProcessExited)
                return Task.FromResult(RenderProcessExited("attach",
                    $"The process exited during attach setup, before the breakpoint at {sourceFile}:{line} could be armed. Drain drhook_drain_log / drhook_drain_console for any output; start the next session with drhook_launch or drhook_attach.",
                    hypothesis));

            // Set the initial breakpoint and run to it.
            int id = TrackSourceBreakpoint(sourceFile, line);
            if (id == 0) return Task.FromResult(Error($"Could not set breakpoint at {sourceFile}:{line}."));

            _session.Resume();
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) return Task.FromResult(Error("Timed out waiting for the breakpoint to hit."));
            if (stop.Reason == StopReason.ProcessExited)
                return Task.FromResult(RenderProcessExited("attach",
                    $"The process exited before reaching the breakpoint at {sourceFile}:{line} — it completed or threw before that line executed. Drain drhook_drain_log / drhook_drain_console for any output; start the next session with drhook_launch or drhook_attach.",
                    hypothesis));

            PublishTransportSnapshot(stop);

            JsonObject result = new()
            {
                ["status"] = "attached",
                ["lockedRuntime"] = DebugSession.ProcessRuntimeMajor is { } major ? $".NET {major}" : "unspecified",
                ["pid"] = pid,
                ["assemblyVersion"] = _targetVersion,
                ["breakpoint"] = new JsonObject { ["file"] = sourceFile, ["line"] = line },
                ["hypothesis"] = hypothesis,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
                ["prompt"] = $"Attached and stopped at breakpoint. Compare state with hypothesis: \"{hypothesis}\""
            };
            return Task.FromResult(Render(result));
        }
        catch (Exception ex)
        {
            CleanupSession();
            return Task.FromResult(Error($"Failed to launch stepping session: {ex.Message}"));
        }
    }

    public Task<string> LaunchAsync(
        string program, string[] args, string? cwd,
        string sourceFile, int line, string hypothesis,
        Dictionary<string, string>? env,
        CancellationToken ct)
    {
        if (IsActive) return Task.FromResult(Error("A stepping session is already active. Use drhook_stop first."));
        ResetOutputBuffers(); // new session — drains reflect only this session (buffers survive a session's END, reset at its START)

        _sessionHypothesis = hypothesis;
        _targetVersion = "launched";
        _stepCount = 0;

        try
        {
            // ADR-011 Layer 2: tell the engine the entry assembly so it holds at that module's load
            // (modules loaded, before main) and we arm the breakpoint there — so launch works on
            // targets that don't self-stop (no Debugger.Break needed). env overrides are merged onto
            // the inherited environment by the POSIX spawn (BuildChildEnv) — Owned/launch only.
            _session = DebugSession.Launch(program, args, cwd, _sink, entryModule: DeriveEntryModule(program, args), env: env);
            StartTransport(); // a view can now connect; it gets its first snapshot at the first stop below

            // Capture the launched PID for status reporting. DebugSession.Launch (finding 64)
            // now owns the Process handle internally and kill-firsts on Dispose — no separate
            // _launchedProcess management here.
            _targetPid = _session.ProcessId;

            // Setup stop (Debugger.Break() in user code, or the attach-before-main initial stop).
            StopInfo? setup = _session.WaitForStop(TimeSpan.FromSeconds(10));

            // The process MUST be synchronized (stopped) before any ICorDebug inspection. If it ran
            // to completion or never stopped within the setup window, SetBreakpointAtLine ->
            // EnumerateAppDomains would run on an exited/unsynchronized CordbProcess and fault inside
            // mscordbi (null-deref at 0x2a8), taking the whole server down. A debugger must not crash
            // because the debuggee finished — return a clean status instead.
            if (setup is null)
            {
                CleanupSession();
                return Task.FromResult(Error(
                    $"The launched process did not stop before the breakpoint at {sourceFile}:{line} could be armed — it ran past the 10s setup window without an early stop (e.g. Debugger.Break, or a breakpoint armed at the initial stop). Arming a breakpoint at the attach-before-main stop for any target is pending substrate work; for now launch a target that halts early."));
            }
            if (setup.Reason == StopReason.ProcessExited)
                return Task.FromResult(RenderProcessExited("launch",
                    $"The process exited during launch setup, before the breakpoint at {sourceFile}:{line} could be armed — it completed or threw within the 10s setup window. Drain drhook_drain_log / drhook_drain_console for any output; start the next session with drhook_launch or drhook_attach.",
                    hypothesis));

            int id = TrackSourceBreakpoint(sourceFile, line);
            if (id == 0) { CleanupSession(); return Task.FromResult(Error($"Could not set breakpoint at {sourceFile}:{line}.")); }

            _session.Resume();
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) { CleanupSession(); return Task.FromResult(Error("Timed out waiting for the breakpoint to hit.")); }
            if (stop.Reason == StopReason.ProcessExited)
                return Task.FromResult(RenderProcessExited("launch",
                    $"The process exited before reaching the breakpoint at {sourceFile}:{line} — it completed or threw before that line executed. Drain drhook_drain_log / drhook_drain_console for any output; start the next session with drhook_launch or drhook_attach.",
                    hypothesis));

            PublishTransportSnapshot(stop);

            JsonObject result = new()
            {
                ["status"] = "launched",
                ["lockedRuntime"] = DebugSession.ProcessRuntimeMajor is { } major ? $".NET {major}" : "unspecified",
                ["program"] = program,
                ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["assemblyVersion"] = _targetVersion,
                ["pid"] = _targetPid,
                ["breakpoint"] = new JsonObject { ["file"] = sourceFile, ["line"] = line },
                ["hypothesis"] = hypothesis,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
                ["prompt"] = $"Launched and stopped at breakpoint. Compare state with hypothesis: \"{hypothesis}\""
            };
            return Task.FromResult(Render(result));
        }
        catch (Exception ex)
        {
            CleanupSession();
            return Task.FromResult(Error($"Failed to launch program: {ex.Message}"));
        }
    }

    // drhook_stop — the normal session end. Owned: Dispose's ADR-008 graceful SIGTERM→(2s)→SIGKILL.
    // Borrowed: Dispose detaches, target left running. Dispatches internally via DebugSession._ownsTarget.
    public Task<string> StopAsync(string hypothesis, CancellationToken ct)
    {
        if (!IsActive) return Task.FromResult(Error("No active stepping session."));

        bool owned = _session!.OwnsTarget;
        JsonObject summary = new()
        {
            ["status"] = "stopped",
            ["hypothesis"] = hypothesis,
            ["mode"] = owned ? "owned" : "borrowed",
            ["disposition"] = owned
                ? "Owned: target asked to exit gracefully (SIGTERM → SIGKILL if stuck, ADR-008)"
                : "Borrowed: detached, target left running",
            ["totalSteps"] = _stepCount,
            ["sessionHypothesis"] = _sessionHypothesis,
            ["assemblyVersion"] = _targetVersion,
            ["prompt"] = $"Session ended after {_stepCount} steps ({(owned ? "Owned: target gracefully terminated" : "Borrowed: target left running")}). " +
                         $"Did the observations confirm or challenge the hypothesis: \"{_sessionHypothesis}\"?"
        };

        CleanupSession();
        return Task.FromResult(Render(summary));
    }

    // drhook_detach — disconnect and LEAVE THE TARGET RUNNING (deliberate). Borrowed: Dispose's Borrowed
    // path detaches without killing. Owned (F-010-2, probes 62/62b): DetachLeaveRunning runs the clean
    // detach (deactivate breakpoints -> Quiesce -> Detach -> Terminate), skipping the kill the Owned
    // Dispose branch issues; the launched target reparents (launchd/PPID=1 on macOS) and keeps running.
    public Task<string> DetachAsync(string hypothesis, CancellationToken ct)
    {
        if (!IsActive) return Task.FromResult(Error("No active stepping session."));

        bool owned = _session!.OwnsTarget;
        if (owned)
        {
            // Owned leave-running: explicitly detach-without-kill. Sets DebugSession._disposed, so the
            // CleanupSession -> Dispose below is the idempotent no-op + state reset (the KillAsync pattern).
            try { _session.DetachLeaveRunning(); }
            catch (Exception ex) { return Task.FromResult(Error($"Detach (leave-running) failed: {ex.GetType().Name}: {ex.Message}")); }
        }

        JsonObject summary = new()
        {
            ["status"] = "detached",
            ["hypothesis"] = hypothesis,
            ["mode"] = owned ? "owned" : "borrowed",
            ["disposition"] = owned
                ? "detached, launched target left running un-debugged (substrate reaps it when it exits; reparents to launchd only if the debugger exits first)"
                : "detached, attached target left running un-debugged",
            ["totalSteps"] = _stepCount,
            ["sessionHypothesis"] = _sessionHypothesis,
            ["assemblyVersion"] = _targetVersion,
            ["prompt"] = $"Detached after {_stepCount} steps; the {(owned ? "launched" : "attached")} target keeps running. " +
                         $"Did the observations confirm or challenge the hypothesis: \"{_sessionHypothesis}\"?"
        };
        CleanupSession();
        return Task.FromResult(Render(summary));
    }

    // drhook_kill — forced termination (anomaly path). Owned: DebugSession.Abandon (SIGTERM brief-grace → SIGKILL
    // → teardown, ADR-008). Borrowed: NOT YET — substrate doesn't own an attached target's lifecycle (F-010-1).
    public Task<string> KillAsync(string hypothesis, CancellationToken ct)
    {
        if (!IsActive) return Task.FromResult(Error("No active stepping session."));

        bool owned = _session!.OwnsTarget;
        int steps = _stepCount, pid = _targetPid;
        string hyp = _sessionHypothesis, ver = _targetVersion;
        try { _session.Abandon(); }
        catch (Exception ex) { return Task.FromResult(Error($"Kill (Abandon) failed: {ex.GetType().Name}: {ex.Message}")); }
        CleanupSession(); // Abandon already disposed; CleanupSession's Dispose is the idempotent no-op + state reset.

        return Task.FromResult(Render(new JsonObject
        {
            ["status"] = "killed",
            ["hypothesis"] = hypothesis,
            ["mode"] = owned ? "owned" : "borrowed",
            ["pid"] = pid,
            ["disposition"] = owned
                ? "forcibly terminated (SIGTERM brief-grace → SIGKILL, DebugSession.Abandon)"
                : "forcibly terminated (SIGKILL — Borrowed force, F-010-1)",
            ["totalSteps"] = steps,
            ["sessionHypothesis"] = hyp,
            ["assemblyVersion"] = ver,
            ["prompt"] = $"Target {pid} force-killed after {steps} steps — reason: \"{hypothesis}\". " +
                         $"drhook_kill is an anomaly path; a well-behaved target ends via drhook_stop."
        }));
    }

    // ─── Step operations ──────────────────────────────────────────────────────────────────────

    public Task<string> StepOverAsync(string? hypothesis, CancellationToken ct) => StepOperation("stepOver", s => s.StepOver(), hypothesis);
    public Task<string> StepIntoAsync(string? hypothesis, CancellationToken ct) => StepOperation("stepIn", s => s.StepInto(), hypothesis);
    public Task<string> StepOutAsync(string? hypothesis, CancellationToken ct)  => StepOperation("stepOut", s => s.StepOut(), hypothesis);

    public Task<string> ContinueAsync(string? hypothesis, bool waitForBreakpoint, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session. Use drhook_launch or drhook_attach first."));
        EmitHypothesis(hypothesis, HypothesisLens.Navigation);
        try
        {
            _session.Resume();
            if (!waitForBreakpoint)
            {
                return Task.FromResult(Render(new JsonObject
                {
                    ["operation"] = "continue",
                    ["status"] = "running",
                    ["assemblyVersion"] = _targetVersion,
                    ["prompt"] = "Process resumed without waiting. Use drhook_pause to interrupt or drhook_continue with waitForBreakpoint=true to block on a stop."
                }));
            }
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) return Task.FromResult(Error("Timed out waiting for a stop."));

            // The debuggee can run to completion during continue (e.g. a suspend=none logpoint that
            // fires to the end of the program) — report the exit + tear down rather than walk a dead
            // process for a frame (finding 77).
            if (stop.Reason == StopReason.ProcessExited)
                return Task.FromResult(RenderProcessExited("continue",
                    "The debuggee exited (ran to completion) — no further stops, the session is over. Drain drhook_drain_log / drhook_drain_console for any final output; start the next session with drhook_launch or drhook_attach.",
                    hypothesis));

            PublishTransportSnapshot(stop);

            JsonObject result = new()
            {
                ["operation"] = "continue",
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["prompt"] = hypothesis is not null
                ? $"Continued to a stop. Compare state with hypothesis: \"{hypothesis}\""
                : "Continued to a stop. Describe what you observe at the new location.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"Continue failed: {ex.Message}")); }
    }

    public Task<string> PauseAsync(string? hypothesis, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        EmitHypothesis(hypothesis, HypothesisLens.Navigation);
        try
        {
            _session.Pause();
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromSeconds(10));
            if (stop is null) return Task.FromResult(Error("Pause timed out — the engine did not surface a stop within budget."));
            if (stop.Reason == StopReason.ProcessExited)
                return Task.FromResult(RenderProcessExited("pause",
                    "The debuggee exited before the pause could synchronize — no stop to report; the session is over. Drain drhook_drain_log / drhook_drain_console for any final output; start the next session with drhook_launch or drhook_attach.",
                    hypothesis));

            PublishTransportSnapshot(stop);

            JsonObject result = new()
            {
                ["operation"] = "pause",
                ["assemblyVersion"] = _targetVersion,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
                ["metrics"] = StubMetrics(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["prompt"] = "Execution interrupted. Inspect with drhook_locals or step through.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"Pause failed: {ex.Message}")); }
    }

    private Task<string> StepOperation(string operationName, Action<DebugSession> step, string? hypothesis)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session. Use drhook_launch or drhook_attach first."));
        EmitHypothesis(hypothesis, HypothesisLens.Navigation);
        try
        {
            _stepCount++;
            step(_session);
            StopInfo? stop = _session.WaitForStop(TimeSpan.FromMinutes(2));
            if (stop is null) return Task.FromResult(Error("Step did not complete within budget."));
            if (stop.Reason == StopReason.ProcessExited)
                return Task.FromResult(RenderProcessExited(operationName,
                    $"The debuggee exited (ran to completion) during {operationName} — no further stops, the session is over. Drain drhook_drain_log / drhook_drain_console for any final output; start the next session with drhook_launch or drhook_attach.",
                    hypothesis));

            PublishTransportSnapshot(stop);

            JsonObject result = new()
            {
                ["operation"] = operationName,
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["stoppedReason"] = MapStopReason(stop),
                ["currentState"] = BuildCurrentState(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["metrics"] = StubMetrics();
            result["prompt"] = hypothesis is not null
                ? $"Step {_stepCount} complete ({operationName}). Compare state with hypothesis: \"{hypothesis}\""
                : $"Step {_stepCount} complete ({operationName}). Describe what you observe.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"{operationName} failed: {ex.Message}")); }
    }

    // ─── Breakpoints ─────────────────────────────────────────────────────────────────────────

    public Task<string> SetBreakpointAsync(string sourceFile, int line, string? condition, int? hitCount, string? logMessage, string? suspend, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));

        BreakpointPolicy? policy = null;
        BreakpointPolicySpec? spec = null;
        if (condition is not null || hitCount is not null || logMessage is not null || suspend is not null)
        {
            spec = new BreakpointPolicySpec(
                Condition: condition,
                HitCount: hitCount is { } n ? new HitCountGate(HitCountMode.Equals, n) : null,
                LogMessage: logMessage,
                Suspend: ParseSuspend(suspend));
            try { policy = _session.Compile(spec); }
            catch (Exception ex) { return Task.FromResult(Error($"Policy compile failed: {ex.GetType().Name}: {ex.Message}")); }
        }

        int id = TrackSourceBreakpoint(sourceFile, line, policy, spec);
        if (id == 0) return Task.FromResult(Error($"Could not set breakpoint at {sourceFile}:{line} — module/PDB unavailable or no sequence point at that line."));

        JsonObject result = new()
        {
            ["status"] = "added",
            ["type"] = "source",
            ["file"] = sourceFile,
            ["line"] = line,
            ["id"] = id,
        };
        if (spec is not null)
        {
            JsonObject policyJson = new();
            if (condition is not null)  policyJson["condition"] = condition;
            if (hitCount is not null)   policyJson["hitCount"] = hitCount.Value;
            if (logMessage is not null) policyJson["logMessage"] = logMessage;
            if (suspend is not null)    policyJson["suspend"] = ParseSuspend(suspend).ToString();
            result["policy"] = policyJson;
        }
        result["prompt"] = $"Breakpoint added at {sourceFile}:{line} (id={id}{(policy is not null ? ", policy attached" : "")}). Use drhook_continue to run to it.";
        return Task.FromResult(Render(result));
    }

    public Task<string> SetFunctionBreakpointAsync(string functionName, string? condition, int? hitCount, string? logMessage, string? suspend, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));

        BreakpointPolicy? policy = null;
        BreakpointPolicySpec? spec = null;
        if (condition is not null || hitCount is not null || logMessage is not null || suspend is not null)
        {
            spec = new BreakpointPolicySpec(
                Condition: condition,
                HitCount: hitCount is { } n ? new HitCountGate(HitCountMode.Equals, n) : null,
                LogMessage: logMessage,
                Suspend: ParseSuspend(suspend));
            try { policy = _session.Compile(spec); }
            catch (Exception ex) { return Task.FromResult(Error($"Policy compile failed: {ex.GetType().Name}: {ex.Message}")); }
        }

        (string moduleSubstr, string typeName, string methodName) = SplitFunction(functionName);
        int id = _session.SetBreakpoint(moduleSubstr, typeName, methodName, policy);
        if (id == 0) return Task.FromResult(Error($"Could not resolve function '{functionName}' (looked for type '{typeName}', method '{methodName}' in module '{moduleSubstr}')."));
        _functionBreakpoints[functionName] = id;
        _breakpointKinds[id] = BreakpointKind.Function;
        if (spec is not null) _policySpecs[id] = spec;

        JsonObject result = new()
        {
            ["status"] = "added",
            ["type"] = "function",
            ["function"] = functionName,
            ["id"] = id,
        };
        if (spec is not null)
        {
            JsonObject policyJson = new();
            if (condition is not null)  policyJson["condition"] = condition;
            if (hitCount is not null)   policyJson["hitCount"] = hitCount.Value;
            if (logMessage is not null) policyJson["logMessage"] = logMessage;
            if (suspend is not null)    policyJson["suspend"] = ParseSuspend(suspend).ToString();
            result["policy"] = policyJson;
        }
        result["prompt"] = $"Function breakpoint added at {functionName} entry (id={id}{(policy is not null ? ", policy attached" : "")}).";
        return Task.FromResult(Render(result));
    }

    public Task<string> SetExceptionBreakpointAsync(string typeName, string? phase, string? condition, int? hitCount, string? logMessage, string? suspend, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        ArgumentNullException.ThrowIfNull(typeName);

        ExceptionStopKind phaseFilter = ParsePhase(phase);

        // Build a BreakpointPolicy only if at least one policy field was supplied; otherwise the
        // filter arms with no policy (legacy backward-compat for type-only filters).
        BreakpointPolicy? policy = null;
        BreakpointPolicySpec? spec = null;
        string? policyError = null;
        if (condition is not null || hitCount is not null || logMessage is not null || suspend is not null)
        {
            spec = new BreakpointPolicySpec(
                Condition: condition,
                HitCount: hitCount is { } n ? new HitCountGate(HitCountMode.Equals, n) : null,
                LogMessage: logMessage,
                Suspend: ParseSuspend(suspend));
            try { policy = _session.Compile(spec); }
            catch (Exception ex) { policyError = $"{ex.GetType().Name}: {ex.Message}"; }
        }

        if (policyError is not null)
            return Task.FromResult(Error($"Policy compile failed: {policyError}"));

        int id = _session.ArmExceptionFilter(typeName, phaseFilter, policy);

        // Track by ID-prefixed key so multiple distinct configurations of the same typeName don't
        // clash in the dictionary. The canonical identifier for removal is the substrate-returned
        // ID (surfaced in the prompt); Increment 6 deliverable 3 will switch the removal tool to
        // accept ID directly.
        _exceptionFilters[$"id={id}"] = id;
        _breakpointKinds[id] = BreakpointKind.Exception;
        if (spec is not null) _policySpecs[id] = spec;

        JsonObject result = new()
        {
            ["status"] = "added",
            ["type"] = "exception",
            ["typeName"] = typeName,
            ["phase"] = phaseFilter.ToString(),
            ["id"] = id,
        };
        if (policy is not null)
        {
            JsonObject policyJson = new();
            if (condition is not null)  policyJson["condition"] = condition;
            if (hitCount is not null)   policyJson["hitCount"] = hitCount.Value;
            if (logMessage is not null) policyJson["logMessage"] = logMessage;
            if (suspend is not null)    policyJson["suspend"] = ParseSuspend(suspend).ToString();
            result["policy"] = policyJson;
        }
        result["prompt"] = $"Exception filter armed (id={id}, type=\"{typeName}\", phase={phaseFilter}" +
            (policy is not null ? ", policy attached" : "") +
            $"). Use drhook_break_remove with this id to remove.";

        return Task.FromResult(Render(result));
    }

    /// <summary>Parse the MCP-facing phase string into a substrate <see cref="ExceptionStopKind"/>.
    /// Accepts the substrate's own enum names with kebab-case spelling, plus the DAP alias
    /// 'user-unhandled' (mapped to <see cref="ExceptionStopKind.Unhandled"/> per DAP convention).
    /// Unknown / null / 'any' map to <see cref="ExceptionStopKind.None"/> (substrate wildcard).</summary>
    private static ExceptionStopKind ParsePhase(string? phase) => phase?.ToLowerInvariant() switch
    {
        null or "" or "any"      => ExceptionStopKind.None,
        "first-chance"           => ExceptionStopKind.FirstChance,
        "user-first-chance"      => ExceptionStopKind.UserFirstChance,
        "catch-handler-found"    => ExceptionStopKind.CatchHandlerFound,
        "unhandled"              => ExceptionStopKind.Unhandled,
        "user-unhandled"         => ExceptionStopKind.Unhandled, // DAP alias
        _                        => ExceptionStopKind.None,
    };

    private static SuspendPolicy ParseSuspend(string? suspend) => suspend?.ToLowerInvariant() switch
    {
        "none" => SuspendPolicy.None,
        _      => SuspendPolicy.All,
    };

    /// <summary>Remove a breakpoint or exception filter by its substrate-assigned id. Dispatches
    /// to the correct substrate path (source/function via <see cref="DebugSession.RemoveBreakpoint"/>;
    /// exception via <see cref="DebugSession.RemoveExceptionFilter"/>) based on the per-id kind
    /// tracker, and prunes the matching MCP-layer dictionary entry alongside the policy spec.
    /// ADR-010 Increment 6 deliverable 3 — by-ID canonical, replacing the polymorphic
    /// <c>(file+line | functionName | filter)</c> dispatch.</summary>
    public Task<string> RemoveByIdAsync(int id, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        if (!_breakpointKinds.TryGetValue(id, out BreakpointKind kind))
            return Task.FromResult(Error($"No breakpoint with id={id} is tracked at the MCP layer. Use drhook_break_list to discover armed ids."));

        bool removed;
        string typeLabel;
        JsonObject result;
        switch (kind)
        {
            case BreakpointKind.Source:
                removed = _session.RemoveBreakpoint(id);
                typeLabel = "source";
                string? sourceKey = FindKey(_lineBreakpoints, id);
                if (sourceKey is not null) _lineBreakpoints.Remove(sourceKey);
                result = new JsonObject { ["status"] = removed ? "removed" : "stale", ["type"] = typeLabel, ["id"] = id };
                if (sourceKey is not null)
                {
                    int colon = sourceKey.LastIndexOf(':');
                    if (colon > 0)
                    {
                        result["file"] = sourceKey[..colon];
                        if (int.TryParse(sourceKey[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ln)) result["line"] = ln;
                    }
                }
                break;
            case BreakpointKind.Function:
                removed = _session.RemoveBreakpoint(id);
                typeLabel = "function";
                string? funcKey = FindKey(_functionBreakpoints, id);
                if (funcKey is not null) _functionBreakpoints.Remove(funcKey);
                result = new JsonObject { ["status"] = removed ? "removed" : "stale", ["type"] = typeLabel, ["id"] = id };
                if (funcKey is not null) result["function"] = funcKey;
                break;
            case BreakpointKind.Exception:
                removed = _session.RemoveExceptionFilter(id);
                typeLabel = "exception";
                string? excKey = FindKey(_exceptionFilters, id);
                if (excKey is not null) _exceptionFilters.Remove(excKey);
                result = new JsonObject { ["status"] = removed ? "removed" : "stale", ["type"] = typeLabel, ["id"] = id };
                break;
            default:
                return Task.FromResult(Error($"Unknown breakpoint kind for id={id}."));
        }

        _breakpointKinds.Remove(id);
        _policySpecs.Remove(id);

        if (!removed)
        {
            // The MCP layer thought we knew about this id but the substrate didn't have it. This
            // is a stale-tracking situation (session was cleared at substrate level without going
            // through the MCP path, or the kind tracker drifted). Report honestly.
            result["prompt"] = $"id={id} was tracked at MCP layer but the substrate had no matching {typeLabel} entry. MCP-layer state pruned; verify with drhook_break_list.";
        }
        return Task.FromResult(Render(result));
    }

    private static string? FindKey(Dictionary<string, int> map, int id)
    {
        foreach ((string key, int value) in map)
            if (value == id) return key;
        return null;
    }

    public string ListBreakpoints()
    {
        if (_session is null) return Error("No active stepping session.");

        // Source + function breakpoints are pattern-matched out of substrate's ListBreakpoints —
        // the engine is authoritative for both descriptor and hit count. The MCP layer's policy-spec
        // dictionary supplies the string form for policy display.
        JsonArray source = new();
        JsonArray function = new();
        foreach (BreakpointInfo info in _session.ListBreakpoints())
        {
            int hits = _session.GetBreakpointHits(info.Id);
            JsonObject? policyJson = _policySpecs.TryGetValue(info.Id, out BreakpointPolicySpec? spec) ? RenderPolicy(spec) : null;
            switch (info)
            {
                case LineBreakpointInfo line:
                    JsonObject sourceEntry = new()
                    {
                        ["id"] = line.Id,
                        ["file"] = line.FilePath,
                        ["line"] = line.Line,
                        ["module"] = line.ModuleSubstring,
                        ["hits"] = hits,
                    };
                    if (policyJson is not null) sourceEntry["policy"] = policyJson;
                    source.Add(sourceEntry);
                    break;
                case FunctionBreakpointInfo func:
                    JsonObject funcEntry = new()
                    {
                        ["id"] = func.Id,
                        ["function"] = $"{func.TypeName}.{func.MethodName}",
                        ["module"] = func.ModuleSubstring,
                        ["hits"] = hits,
                    };
                    if (policyJson is not null) funcEntry["policy"] = policyJson;
                    function.Add(funcEntry);
                    break;
            }
        }

        // Exception filters: substrate's ListExceptionFilters has the canonical {id, typeName,
        // phase}; hit count is per-filter via GetExceptionFilterHits.
        JsonArray exception = new();
        foreach (ExceptionFilterInfo filter in _session.ListExceptionFilters())
        {
            int hits = _session.GetExceptionFilterHits(filter.Id);
            JsonObject entry = new()
            {
                ["id"] = filter.Id,
                ["typeName"] = filter.TypeName,
                ["phase"] = filter.PhaseFilter.ToString(),
                ["hits"] = hits,
            };
            if (_policySpecs.TryGetValue(filter.Id, out BreakpointPolicySpec? spec))
            {
                JsonObject? policyJson = RenderPolicy(spec);
                if (policyJson is not null) entry["policy"] = policyJson;
            }
            exception.Add(entry);
        }

        return Render(new JsonObject
        {
            ["status"] = "ok",
            ["source"] = source,
            ["function"] = function,
            ["exception"] = exception,
            ["count"] = source.Count + function.Count + exception.Count,
        });
    }

    /// <summary>Render a <see cref="BreakpointPolicySpec"/> as JSON for breakpoint-list output —
    /// the string form an agent supplied at Arm time, not the engine-compiled delegate form. Returns
    /// null if the spec has no policy fields (the breakpoint arms with no policy).</summary>
    private static JsonObject? RenderPolicy(BreakpointPolicySpec spec)
    {
        if (spec.Condition is null && spec.HitCount is null && spec.LogMessage is null && spec.Suspend == SuspendPolicy.All)
            return null;
        JsonObject json = new();
        if (spec.Condition is not null)  json["condition"] = spec.Condition;
        if (spec.HitCount is { } gate)   json["hitCount"] = new JsonObject { ["mode"] = gate.Mode.ToString(), ["value"] = gate.Value };
        if (spec.LogMessage is not null) json["logMessage"] = spec.LogMessage;
        json["suspend"] = spec.Suspend.ToString();
        return json;
    }

    public Task<string> ClearBreakpointsAsync(string? category, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));

        int sourceCleared = 0, functionCleared = 0, exceptionCleared = 0;
        bool clearSource    = category is null || category == "source";
        bool clearFunction  = category is null || category == "function";
        bool clearException = category is null || category == "exception";

        if (clearSource)
        {
            foreach (int id in _lineBreakpoints.Values) { if (_session.RemoveBreakpoint(id)) sourceCleared++; _policySpecs.Remove(id); }
            _lineBreakpoints.Clear();
        }
        if (clearFunction)
        {
            foreach (int id in _functionBreakpoints.Values) { if (_session.RemoveBreakpoint(id)) functionCleared++; _policySpecs.Remove(id); }
            _functionBreakpoints.Clear();
        }
        if (clearException)
        {
            foreach (int id in _exceptionFilters.Values) { if (_session.RemoveExceptionFilter(id)) exceptionCleared++; _policySpecs.Remove(id); }
            _exceptionFilters.Clear();
        }

        return Task.FromResult(Render(new JsonObject
        {
            ["status"] = "cleared",
            ["category"] = category ?? "all",
            ["sourceCleared"] = sourceCleared,
            ["functionCleared"] = functionCleared,
            ["exceptionCleared"] = exceptionCleared,
        }));
    }

    // ─── Variables ────────────────────────────────────────────────────────────────────────────

    public Task<string> InspectVariablesAsync(int depth, string? hypothesis, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        EmitHypothesis(hypothesis, HypothesisLens.Inspection);
        try
        {
            IReadOnlyList<LocalValue> locals = _session.GetLocals(depth);
            IReadOnlyList<ArgumentValue> args = _session.GetArguments(depth);

            JsonArray vars = new();
            foreach (LocalValue l in locals)
                vars.Add(LocalValueToJson(l));
            foreach (ArgumentValue a in args)
                vars.Add(ArgumentValueToJson(a));

            JsonObject result = new()
            {
                ["step"] = _stepCount,
                ["assemblyVersion"] = _targetVersion,
                ["variableCount"] = vars.Count,
                ["variables"] = vars,
                ["metrics"] = StubMetrics(),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["prompt"] = hypothesis is not null
                ? $"Step {_stepCount} variables inspected. Compare values with hypothesis: \"{hypothesis}\""
                : $"Step {_stepCount} variables inspected. Describe what you observe.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"Variable inspection failed: {ex.Message}")); }
    }

    /// <summary>Lazy-expand one level of a variable at the current stop (drhook_expand). <paramref name="target"/>
    /// is a local name, "this", or an argument's real name (the declared parameter name drhook_locals
    /// shows; "argN" still resolves for frames whose metadata didn't resolve); <paramref name="path"/>
    /// is the child node names to walk into first (field names or "[i]"). Bounded per call — the
    /// navigable substitute for deep inspection.</summary>
    public Task<string> ExpandAsync(string target, IReadOnlyList<string> path, string? hypothesis, CancellationToken ct)
    {
        if (_session is null) return Task.FromResult(Error("No active stepping session."));
        EmitHypothesis(hypothesis, HypothesisLens.Inspection);
        try
        {
            // Resolve the target to an argument by its real name ("this" or a declared parameter name,
            // as drhook_locals now shows), then by the positional argN fallback (frames with no
            // resolvable metadata), else treat it as a local. Keeps drhook_expand consistent with the
            // displayed names: expanding by the shown name routes to the right argument index.
            IReadOnlyList<ArgumentValue> args = _session.GetArguments();
            int argIndex = -1;
            for (int i = 0; i < args.Count; i++)
                if (string.Equals(args[i].Name, target, StringComparison.Ordinal)) { argIndex = i; break; }
            if (argIndex < 0 && target.StartsWith("arg", StringComparison.Ordinal)
                && int.TryParse(target.AsSpan(3), out int parsed) && parsed >= 0 && parsed < args.Count)
                argIndex = parsed;

            IReadOnlyList<FieldValue> children = argIndex >= 0
                ? _session.ExpandArgument(argIndex, path)
                : _session.ExpandLocal(target, path);

            JsonObject result = new()
            {
                ["target"] = target,
                ["path"] = new JsonArray(path.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
                ["childCount"] = children.Count,
                ["children"] = FieldsToJson(children),
            };
            if (hypothesis is not null) result["hypothesis"] = hypothesis;
            result["prompt"] = children.Count == 0
                ? "No children (null reference, primitive, or unresolved path). Check target/path against drhook_locals."
                : "Expanded one level. To go deeper, append a child name (or '[i]') to the path and call drhook_expand again.";
            return Task.FromResult(Render(result));
        }
        catch (Exception ex) { return Task.FromResult(Error($"Expand failed: {ex.Message}")); }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────

    // The launch breakpoint is armed at the entry assembly's load (ADR-011 Layer 2 hold-gate).
    // Identify the entry assembly: the first .dll among the args (dotnet exec X.dll / dotnet X.dll),
    // else the program's own stem (a native apphost). Returns the simple name the engine matches the
    // loaded module against; an unmatched value just means no hold fires (the launch guard covers it).
    private static string? DeriveEntryModule(string program, string[] args)
    {
        foreach (string a in args)
            if (a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFileNameWithoutExtension(a);
        return System.IO.Path.GetFileNameWithoutExtension(program);
    }

    // A new session's drains must reflect only that session. The console / log / anomaly buffers
    // intentionally survive a session's END for a final drain (see RenderProcessExited), so reset
    // them at the next session's START. Finding: console output bled across sessions (2026-06-02 dogfood).
    private void ResetOutputBuffers()
    {
        _console.Reset();
        _logs.Reset();
        _anomalies.Reset();
    }

    /// <summary>The response for a stop-handler whose target exited instead of reaching the
    /// expected stop — ran to completion during continue/step/pause, or exited before a
    /// launch/attach breakpoint. Tears down the now-dead session (IsActive clears, so the next
    /// launch/attach works) while the adapter-level log / console / anomaly buffers survive for a
    /// final drain. No frame is read; there is none after exit (finding 77). <paramref name="note"/>
    /// is the per-operation prompt; <paramref name="operation"/> labels which call surfaced the exit.</summary>
    private string RenderProcessExited(string operation, string note, string? hypothesis)
    {
        string version = _targetVersion;
        CleanupSession();
        JsonObject exited = new()
        {
            ["operation"] = operation,
            ["assemblyVersion"] = version,
            ["stoppedReason"] = "processExited",
            ["prompt"] = note,
        };
        if (hypothesis is not null) exited["hypothesis"] = hypothesis;
        return Render(exited);
    }

    private JsonObject BuildCurrentState()
    {
        if (_session is null) return new JsonObject();
        IReadOnlyList<string> frames = _session.GetStackFrames();
        return new JsonObject
        {
            ["topFrame"] = frames.Count > 0 ? frames[0] : "(no frame)",
            ["stack"] = new JsonArray(frames.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        };
    }

    private static string MapStopReason(StopInfo stop) => stop.Reason switch
    {
        StopReason.Breakpoint => "breakpoint",
        StopReason.Step => "step",
        StopReason.Break => "debuggerBreak",
        StopReason.Exception => $"exception:{stop.ExceptionKind}",
        StopReason.EvalComplete => "evalComplete",
        StopReason.EvalException => "evalException",
        StopReason.Pause => "pause",
        StopReason.ConditionError => "conditionError",
        StopReason.ProcessExited => "processExited",
        _ => stop.Reason.ToString().ToLowerInvariant(),
    };

    private static JsonObject StubMetrics()
        => new() { ["note"] = "metrics moved to drhook_snapshot (EventPipe) — this field is preserved for shape compat" };

    private static JsonNode LocalValueToJson(LocalValue l)
    {
        JsonObject node = new()
        {
            ["scope"] = "local",
            ["name"] = l.Name,
            ["elementType"] = $"0x{l.ElementType:X2}",
        };
        if (l.RawValue is { } raw) node["value"] = RawToJson(raw);
        if (l.StringValue is not null) node["string"] = l.StringValue;
        if (l.TypeName is not null) node["typeName"] = l.TypeName;
        if (l.HasChildren) node["hasChildren"] = true;
        if (l.Fields is { Count: > 0 } fs) node["fields"] = FieldsToJson(fs);
        return node;
    }

    private static JsonNode ArgumentValueToJson(ArgumentValue a)
    {
        JsonObject node = new()
        {
            ["scope"] = "argument",
            ["name"] = a.Name,
            ["elementType"] = $"0x{a.ElementType:X2}",
        };
        if (a.RawValue is { } raw) node["value"] = RawToJson(raw);
        if (a.StringValue is not null) node["string"] = a.StringValue;
        if (a.TypeName is not null) node["typeName"] = a.TypeName;
        if (a.HasChildren) node["hasChildren"] = true;
        if (a.Fields is { Count: > 0 } fs) node["fields"] = FieldsToJson(fs);
        return node;
    }

    private static JsonArray FieldsToJson(IReadOnlyList<FieldValue> fields)
    {
        JsonArray arr = new();
        foreach (FieldValue f in fields)
        {
            JsonObject node = new()
            {
                ["name"] = f.Name,
                ["elementType"] = $"0x{f.ElementType:X2}",
            };
            if (f.RawValue is { } raw) node["value"] = RawToJson(raw);
            if (f.StringValue is not null) node["string"] = f.StringValue;
            if (f.TypeName is not null) node["typeName"] = f.TypeName;
            if (f.HasChildren) node["hasChildren"] = true;
            if (f.Fields is { Count: > 0 } sub) node["fields"] = FieldsToJson(sub);
            arr.Add(node);
        }
        return arr;
    }

    // JsonValue.Create(object) forces resolver-based serialization (the value's static type is
    // object), which throws against the resolver-less render options ("JsonSerializerOptions ...
    // must specify a TypeInfoResolver setting before being marked as read-only") — the bug that
    // broke drhook_locals. The CLR-typed RawValue is already reified to its boxed primitive
    // (Interop.Variables.ReifyPrimitive), so dispatch to the typed JsonValue.Create overloads
    // (built-in converters, no resolver) — the same path every other primitive in the response
    // takes. Pointers / non-primitives fall back to their string representation.
    private static JsonNode? RawToJson(object raw) => raw switch
    {
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        byte v => JsonValue.Create(v),
        sbyte v => JsonValue.Create(v),
        short v => JsonValue.Create(v),
        ushort v => JsonValue.Create(v),
        int v => JsonValue.Create(v),
        uint v => JsonValue.Create(v),
        long v => JsonValue.Create(v),
        ulong v => JsonValue.Create(v),
        float v => JsonValue.Create(v),
        double v => JsonValue.Create(v),
        decimal v => JsonValue.Create(v),
        char c => JsonValue.Create(c.ToString()),
        _ => JsonValue.Create(raw.ToString() ?? raw.GetType().Name),
    };

    private static (string Module, string Type, string Method) SplitFunction(string functionName)
    {
        // Accept "Namespace.Type.Method", "Type.Method", or a bare "Method" (last-segment fallback).
        int lastDot = functionName.LastIndexOf('.');
        if (lastDot < 0) return ("", "", functionName);
        string method = functionName[(lastDot + 1)..];
        string typePart = functionName[..lastDot];
        int prevDot = typePart.LastIndexOf('.');
        if (prevDot < 0) return ("", typePart, method);
        string typeName = typePart;          // full "Namespace.Type"
        string module  = typePart[..prevDot]; // best-effort module guess from the namespace prefix
        return (module, typeName, method);
    }

    /// <summary>Set a source-line breakpoint AND register it across the MCP tracking maps as ONE
    /// atomic operation — the single registration point every source breakpoint goes through (the
    /// launch + attach initial bp, and drhook_break_source). Keeping the maps in lockstep here makes
    /// drift structurally impossible: a source bp cannot be set without also recording its kind. The
    /// drift this collapses was a real bug — the launch/attach initial bp registered in
    /// <c>_lineBreakpoints</c> but not <c>_breakpointKinds</c>, so <c>RemoveByIdAsync</c> (kind-gated)
    /// rejected an id that <c>ListBreakpoints</c> showed (the ADR-013 finale finding). Returns the
    /// substrate id, or 0 if the breakpoint could not be set (the caller renders its own 0 message).</summary>
    private int TrackSourceBreakpoint(string sourceFile, int line, BreakpointPolicy? policy = null, BreakpointPolicySpec? spec = null)
    {
        int id = _session!.SetBreakpointAtLine(sourceFile, line, policy);
        if (id == 0) return 0;
        _lineBreakpoints[KeyLine(sourceFile, line)] = id;
        _breakpointKinds[id] = BreakpointKind.Source;
        if (spec is not null) _policySpecs[id] = spec;
        return id;
    }

    private static string KeyLine(string file, int line) => $"{file}:{line.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Emit the operator's verbatim hypothesis into the debug-state stream as the prediction half
    /// of the (hypothesis, observation) braid (ADR-012 Phase 3). Stated BEFORE the operation, so it lands in
    /// the timeline immediately before the observation it predicts; no-op for an empty hypothesis or before a
    /// session exists. <c>_sink</c> is the <see cref="CompositeEventSink"/> (transport + bounded sinks), so
    /// this reaches every connected view via the transport's delta broadcast. Append-only by construction —
    /// a correction is the next emitted hypothesis, never an edit.</summary>
    private void EmitHypothesis(string? hypothesis, HypothesisLens lens)
    {
        if (_session is null || string.IsNullOrWhiteSpace(hypothesis)) return;
        _sink.OnHypothesis(new HypothesisRecord(DateTimeOffset.UtcNow, hypothesis, lens));
    }

    private static string Error(string message)
        => Render(new JsonObject { ["status"] = "error", ["message"] = message });

    // ToJsonString on the JsonNode tree avoids the reflection-based serializer (which is
    // disabled when the host runs trimmed/AOT — e.g., the .NET 10 file-based-app context used
    // by Probe 41). Equivalent output for the JsonObject inputs we use.
    private static string Render(JsonObject obj) => obj.ToJsonString(Indented);

    private void CleanupSession()
    {
        // Stop the transport FIRST — close the listener + drop view connections before the session is torn
        // down (ADR-012: session-end drops the views; a view drop never affects the session). Best-effort.
        try { _transport.Stop(); } catch { /* never let a transport fault block session cleanup */ }

        if (_session is not null)
        {
            // Finding 64 — substrate-enforced lifecycle: DebugSession.Dispose handles the
            // kill-first protocol internally for Owned (Launched) sessions. Was previously
            // a manual Kill-then-Dispose dance in this method; substrate now owns the
            // ordering. CleanupSession is just a Dispose + bookkeeping reset.
            try
            {
                _session.Dispose();
            }
            catch (Exception ex)
            {
                // EA capture (UnexpectedCleanupException): DebugSession.Dispose threw.
                // The substrate's Interlocked gates (ENG-DS-1) and idempotent native frees mean
                // most exception paths are now structurally avoided, but anything that escapes
                // surfaces here rather than silently disappearing.
                _anomalies.OnAnomaly(new EngineAnomaly(
                    DateTimeOffset.UtcNow, AnomalyKind.UnexpectedCleanupException, "mcp-request",
                    "CleanupSession.SessionDispose",
                    Observed: $"{ex.GetType().Name}: {ex.Message}",
                    Expected: "DebugSession.Dispose completes cleanly",
                    Context: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().FullName ?? ex.GetType().Name }));
            }
            _session = null;
        }
        _lineBreakpoints.Clear();
        _functionBreakpoints.Clear();
        _exceptionFilters.Clear();
        _stepCount = 0;
        _targetPid = 0;
        _targetVersion = "unknown";
        _sessionHypothesis = "";
    }

    public void Dispose()
    {
        CleanupSession();
        _transport.Dispose();
    }
}
