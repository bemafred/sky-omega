// Attach/detach lifecycle, ported from PoC probes 05/06 as engine code. Composes the
// validated pieces: DbgShim (native attach flow) -> ICorDebug RCW (consume, source-gen COM)
// -> ManagedCallbackHost (receive, [UnmanagedCallersOnly] vtable) -> DebugActiveProcess ->
// ICorDebugController (Continue/Detach). The StrategyBasedComWrappers is held as a substrate
// singleton (finding 13). Phase 1 validates attach + callback delivery + clean teardown;
// the continue-loop and stepping are Phase 2 (ADR-006).

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SkyOmega.DrHook.Engine.Interop;

namespace SkyOmega.DrHook.Engine;

/// <summary>An attached managed-debugging session over a target .NET process. Dispose detaches
/// and releases all native resources.</summary>
public sealed class DebugSession : IDisposable, IMemberResolver
{
    /// <summary>Maximum recursion depth for object/array inspection (ENG-STK-1).
    /// Mercury-aligned with <c>SparqlParser.DefaultMaxDepth</c>. At ~2 KB per level across
    /// mutually-recursive <c>FieldEnumerator</c> / <c>ArrayInspector</c> / <c>Variables.GetChildren</c>,
    /// 10 levels = ~20 KB stack — well under macOS-secondary 512 KB and Windows 1 MB defaults.
    /// Callers requesting deeper are clamped to this value. (Future: emit <c>EngineAnomaly</c>
    /// when clamping — pending the anomaly-capture infrastructure landing in Phase 1.)
    /// Inheritance-chain walking (<c>GetBase</c> within one level) is iterative and does NOT
    /// count against this budget. See finding 55.</summary>
    public const int MaxInspectionDepth = 10;

    private static readonly ComWrappers Wrappers = new StrategyBasedComWrappers();

    private readonly DbgShim _dbgShim;
    private readonly CallbackPump _pump;
    private readonly ManagedCallbackHost _callback;
    private readonly ICorDebug _cordbg;
    private readonly ICorDebugController _controller;
    private readonly IDebugEventSink _sink;
    private nint _pUnknown;
    private nint _pProcess;
    private int _detached;  // 0 = attached, 1 = detached; atomic via Interlocked.Exchange (ENG-DS-1)
    private int _disposed;  // 0 = live, 1 = disposed; atomic via Interlocked.Exchange (ENG-DS-1)
    private readonly bool _ownsTarget;  // true = Owned (substrate kills target on Dispose); false = Borrowed (detach-leave-running). Probe 44 + finding 64.
    private readonly Process? _targetProcess;  // non-null when _ownsTarget — substrate holds Process handle, kills internally on Dispose. Closes MCH-RE-2 (finding 63).

    /// <summary>Settle window between Process.Kill and Quiesce/Detach. Empirically ~50–100 ms
    /// on macOS-arm64 lets mscordbi's RC event thread notice the exit before substrate teardown.</summary>
    private const int KillSettleMs = 100;

    /// <summary>Settle window when target died externally — between death detection and Detach.
    /// Empirically ~200 ms on macOS-arm64 lets mscordbi's RC event thread complete exit-work-item
    /// processing (ExitProcess delivery, RC state teardown) so Detach unwinds against a fully-
    /// quiesced mscordbi rather than racing it. Finding 66 / Probe 47.</summary>
    private const int ExitWorkSettleMs = 200;

    /// <summary>Settle window for Borrowed sessions before HasExited death check. The target
    /// may have been killed externally microseconds before Dispose ran (Probe 44 phase C kill-
    /// coincident scenario); Process.HasExited can lag the actual exit by tens of milliseconds
    /// (OS reaping / waitpid). 50 ms gives HasExited time to propagate before the death-detection
    /// routing decision. Finding 66 / Probe 47.</summary>
    private const int BorrowedDeathCheckSettleMs = 50;

    /// <summary>Default natural-exit wait for Owned-path Dispose (ADR-008 / finding 68 Stage 1).
    /// 2000 ms is ~50× the empirically-observed worst case for well-behaved SIGTERM response on
    /// macOS-arm64 (probes 49/50/52/53: 12-45 ms). Generous enough to absorb host-load variance
    /// and longer-cleanup targets, tight enough that misbehaving targets surface quickly via the
    /// TargetStuckAtDispose anomaly path. Configurable per-session via AttachAndOwn / Launch
    /// overload; this is the fallback when caller doesn't specify.</summary>
    public static readonly TimeSpan DefaultNaturalExitTimeout = TimeSpan.FromMilliseconds(2000);

    /// <summary>Default brief grace for <see cref="Abandon"/> (ADR-008 / finding 68 Decision 3).
    /// 200 ms catches even handler-equipped well-behaved targets per finding 68 probes 49/50,
    /// while keeping Abandon's total budget sub-second. Callers override via the Abandon argument
    /// if they want even less waiting (rare) or more (use the discipline-default Dispose path
    /// instead).</summary>
    public static readonly TimeSpan DefaultAbandonBriefGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>Per-session natural-exit timeout for Owned Dispose. Defaults to
    /// <see cref="DefaultNaturalExitTimeout"/>. Configurable via the AttachAndOwn / Launch
    /// overload that accepts a TimeSpan.</summary>
    private readonly TimeSpan _naturalExitTimeout;

    // Active breakpoints: owned (module, function, breakpoint) ICorDebug pointers kept alive so
    // the breakpoint stays bound, alongside a public BreakpointInfo carrying the assigned id and
    // descriptor for listing/removal. Released on Dispose or via Remove/ClearBreakpoints.
    private sealed record BreakpointEntry(
        BreakpointInfo Info, nint Module, nint Function, nint Breakpoint,
        BreakpointPolicy? Policy = null)
    {
        /// <summary>Running hit count, incremented on each callback for this breakpoint. Mutable;
        /// only the pump's worker thread touches it (the worker is single-threaded — no lock needed).</summary>
        public int HitCount { get; set; }
    }
    private readonly List<BreakpointEntry> _breakpoints = new();
    private int _nextBreakpointId;

    // Armed exception filters: consulted by WaitForStop on Exception stops. No native resources to
    // release; purely consumer-side state.
    private readonly List<ExceptionFilterInfo> _exceptionFilters = new();
    private int _nextExceptionFilterId;

    // Substrate-internal per-filter state — Policy + HitCount. Kept parallel to _exceptionFilters
    // (rather than extending the public ExceptionFilterInfo record) because Policy/HitCount are
    // implementation details consumers of ListExceptionFilters shouldn't see.
    // ADR-010 Increment 2c (exception-filter side).
    private sealed class ExceptionFilterState
    {
        public BreakpointPolicy? Policy { get; init; }
        public int HitCount { get; set; }
    }
    private readonly Dictionary<int, ExceptionFilterState> _exceptionFilterState = new();

    // Per-module Portable PDB readers, opened on demand for source mapping; disposed on Dispose.
    private readonly Dictionary<string, SymbolReader?> _symbols = new(StringComparer.Ordinal);

    private DebugSession(int processId, DbgShim dbgShim, CallbackPump pump, ManagedCallbackHost callback,
                         ICorDebug cordbg, ICorDebugController controller, IDebugEventSink sink,
                         nint pUnknown, nint pProcess, bool ownsTarget, Process? targetProcess,
                         TimeSpan naturalExitTimeout)
    {
        ProcessId = processId;
        _dbgShim = dbgShim;
        _pump = pump;
        _callback = callback;
        _cordbg = cordbg;
        _controller = controller;
        _sink = sink;
        _pUnknown = pUnknown;
        _pProcess = pProcess;
        _ownsTarget = ownsTarget;
        _targetProcess = targetProcess;
        _naturalExitTimeout = naturalExitTimeout;
    }

    /// <summary>OS process id of the attached target.</summary>
    public int ProcessId { get; }

    /// <summary>True when the substrate launched the target (Owned — caller handles kill-first
    /// before Dispose); false when the substrate attached to an existing target (Attached —
    /// Dispose does detach-leave-running). Probe 44 design.</summary>
    public bool OwnsTarget => _ownsTarget;

    /// <summary>Attach the native ICorDebug engine to a running .NET process and register the
    /// managed callback. On macOS/ARM64 this needs no debug entitlement (finding 13).</summary>
    /// <exception cref="DebugEngineException">An ICorDebug step failed.</exception>
    /// <summary>Attach to an existing process as a BORROWED session — substrate does not
    /// own the target's lifecycle (no kill on Dispose). Dispose detaches and leaves the target
    /// running, OR — if the target died externally between Attach and Dispose (OS kill, user
    /// force-quit, OOM, crash, container shutdown) — short-circuits mscordbi protocol
    /// operations to avoid racing exit-work-item processing (Probe 47 / finding 66). Use case:
    /// production observation, attaching to processes spawned by external orchestration
    /// (NCrunch, IDE test runners, user-launched apps). The Process handle is acquired for
    /// death-detection ONLY — substrate never calls Process.Kill on it.</summary>
    /// <exception cref="ArgumentException">No process with that id is running.</exception>
    public static DebugSession Attach(int processId, IDebugEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        // Acquire Process handle for DEATH-DETECTION (Probe 47 / finding 66).
        // Substrate does NOT take ownership of kill for Borrowed — the handle is consulted
        // in Dispose to short-circuit mscordbi protocol ops when the target has died externally.
        Process targetProcess = Process.GetProcessById(processId);
        DbgShim dbgShim = DbgShim.Load();
        nint pUnknown = 0;
        try
        {
            ThrowIfFailed(dbgShim.CreateCordbForProcess(processId, out pUnknown), "CreateDebuggingInterfaceFromVersion");
            // Borrowed sessions don't use naturalExitTimeout (substrate doesn't terminate them).
            // Pass DefaultNaturalExitTimeout for field initialization symmetry only.
            return FromCordbg(dbgShim, sink, processId, pUnknown, ownsTarget: false, targetProcess: targetProcess, naturalExitTimeout: DefaultNaturalExitTimeout);
        }
        catch
        {
            if (pUnknown != 0) Marshal.Release(pUnknown);
            dbgShim.Dispose();
            targetProcess.Dispose();
            throw;
        }
    }

    /// <summary>Attach to an existing process as an OWNED session — substrate takes
    /// lifecycle ownership of the target. Substrate acquires the Process handle via
    /// <see cref="Process.GetProcessById(int)"/> and kills the target before Dispose's
    /// substrate teardown. Use case: integration tests that needed to spawn the target
    /// externally for orchestration reasons (MTP <c>--debug</c> attach handshake, VSTest
    /// VSTEST_HOST_DEBUG stdout parsing) — once they have the PID, they hand ownership
    /// here and the substrate enforces the kill-first protocol on Dispose. The caller's
    /// spawn-time Process handle (if held) can be Disposed immediately after this call
    /// — substrate holds its own. Finding 64.</summary>
    /// <exception cref="ArgumentException">No process with that id is running.</exception>
    /// <param name="naturalExitTimeout">Optional per-session override for the natural-exit
    /// wait in Dispose's Stage 1 SIGTERM (ADR-008 / finding 68). Default: 2000 ms
    /// (<see cref="DefaultNaturalExitTimeout"/>). Set higher for targets with legitimate
    /// long-cleanup needs; setting lower is rarely useful — use <see cref="Abandon"/> with
    /// a custom briefGrace for the fast-escalation case.</param>
    public static DebugSession AttachAndOwn(int processId, IDebugEventSink sink, TimeSpan? naturalExitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        // Acquire Process handle BEFORE dbgshim attach — if the target exited between
        // caller's spawn and now, fail fast with ArgumentException rather than mid-attach.
        Process targetProcess = Process.GetProcessById(processId);
        DbgShim dbgShim = DbgShim.Load();
        nint pUnknown = 0;
        try
        {
            ThrowIfFailed(dbgShim.CreateCordbForProcess(processId, out pUnknown), "CreateDebuggingInterfaceFromVersion");
            return FromCordbg(dbgShim, sink, processId, pUnknown, ownsTarget: true, targetProcess: targetProcess,
                naturalExitTimeout: naturalExitTimeout ?? DefaultNaturalExitTimeout);
        }
        catch
        {
            if (pUnknown != 0) Marshal.Release(pUnknown);
            dbgShim.Dispose();
            targetProcess.Dispose();
            throw;
        }
    }

    /// <summary>Launch a .NET process under debug control via dbgshim's RegisterForRuntimeStartup
    /// flow: spawn suspended → register a startup callback → resume → await the callback. On return,
    /// the debugger is attached BEFORE managed code has run, so a <c>Debugger.Break()</c> at the top
    /// of <c>Main</c> surfaces as the first stop (finding 42). Backs <c>drhook_step_run</c> in the
    /// MCP rewrite. <paramref name="program"/> is typically the <c>dotnet</c> host with the target
    /// DLL as an argument.</summary>
    /// <exception cref="DebugEngineException">The launch failed or the runtime didn't initialize.</exception>
    /// <summary>Launch a .NET process under debug control via dbgshim's RegisterForRuntimeStartup
    /// flow as an OWNED session. Substrate spawns the target, acquires its <see cref="Process"/>
    /// handle, and kills the target before Dispose's substrate teardown. Use case: drhook_step_run
    /// MCP tool, file-based probes that launch their target. Finding 64 (substrate-owned
    /// lifecycle); callers no longer manage kill-first themselves.</summary>
    /// <exception cref="DebugEngineException">The launch failed or the runtime didn't initialize.</exception>
    /// <param name="naturalExitTimeout">See <see cref="AttachAndOwn"/>'s parameter doc.</param>
    public static DebugSession Launch(string program, IReadOnlyList<string> args, string? workingDirectory, IDebugEventSink sink, TimeSpan? naturalExitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(sink);
        string commandLine = BuildCommandLine(program, args);
        DbgShim dbgShim = DbgShim.Load();
        nint pUnknown = 0;
        Process? targetProcess = null;
        try
        {
            ThrowIfFailed(
                dbgShim.LaunchWithDebugger(commandLine, workingDirectory, TimeSpan.FromSeconds(30), out uint pid, out pUnknown),
                "DbgShim.LaunchWithDebugger");
            // Acquire Process handle for the just-launched target so Dispose can kill-first
            // internally. If the target somehow exited between LaunchWithDebugger and now,
            // we proceed with targetProcess=null — Dispose's kill is best-effort.
            try { targetProcess = Process.GetProcessById((int)pid); } catch (ArgumentException) { /* exited already */ }
            return FromCordbg(dbgShim, sink, (int)pid, pUnknown, ownsTarget: true, targetProcess: targetProcess,
                naturalExitTimeout: naturalExitTimeout ?? DefaultNaturalExitTimeout);
        }
        catch
        {
            if (pUnknown != 0) Marshal.Release(pUnknown);
            dbgShim.Dispose();
            targetProcess?.Dispose();
            throw;
        }
    }

    /// <summary>Shared post-cordbg setup: cast to <see cref="ICorDebug"/>, build the pump and
    /// callback vtable, register the handler, <c>DebugActiveProcess</c>, start the continue-loop.
    /// Same shape used by Attach and Launch.</summary>
    private static DebugSession FromCordbg(DbgShim dbgShim, IDebugEventSink sink, int processId, nint pUnknown, bool ownsTarget, Process? targetProcess, TimeSpan naturalExitTimeout)
    {
        CallbackPump? pump = null;
        ManagedCallbackHost? callback = null;
        try
        {
            var cordbg = (ICorDebug)Wrappers.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None);
            ThrowIfFailed(cordbg.Initialize(), "ICorDebug.Initialize");

            pump = new CallbackPump(sink);
            callback = new ManagedCallbackHost(pump);
            ThrowIfFailed(cordbg.SetManagedHandler(callback.NativePointer), "ICorDebug.SetManagedHandler");

            ThrowIfFailed(cordbg.DebugActiveProcess((uint)processId, 0, out nint pProcess), "ICorDebug.DebugActiveProcess");
            var controller = (ICorDebugController)Wrappers.GetOrCreateObjectForComInstance(pProcess, CreateObjectFlags.None);

            // Drive the continue-loop now that the process controller exists. The resume handler
            // arms a stepper on the stopped thread for step resumes (no-op for a plain continue),
            // then Continues. The pause handler synchronizes the running debuggee for an AsyncBreak
            // (DebugSession.Pause). Both are routed through the worker so the controller has a single
            // caller. Callbacks enqueued since SetManagedHandler (if any) drain immediately.
            pump.Start(
                (kind, thread) =>
                {
                    Stepping.Arm(thread, kind);
                    return controller.Continue(0);
                },
                () => controller.Stop(0));

            return new DebugSession(processId, dbgShim, pump, callback, cordbg, controller, sink, pUnknown, pProcess, ownsTarget, targetProcess, naturalExitTimeout);
        }
        catch
        {
            pump?.Dispose();
            callback?.Dispose();
            throw;
        }
    }

    /// <summary>Quote a token if it contains spaces/quotes; dbgshim's command-line parser splits on
    /// whitespace with shell-style quoting.</summary>
    private static string BuildCommandLine(string program, IReadOnlyList<string> args)
    {
        System.Text.StringBuilder sb = new();
        sb.Append(Quote(program));
        foreach (string arg in args) { sb.Append(' '); sb.Append(Quote(arg)); }
        return sb.ToString();
    }

    private static string Quote(string s)
        => s.Length == 0 || s.IndexOfAny(new[] { ' ', '"', '\t' }) >= 0
            ? "\"" + s.Replace("\"", "\\\"") + "\""
            : s;

    /// <summary>Block until the debuggee next stops (breakpoint, step complete, exception, or
    /// <c>Debugger.Break</c>), up to <paramref name="timeout"/>. Returns null on timeout (still
    /// running); a <see cref="StopReason.ProcessExited"/> result means the session is over.
    /// While stopped the debuggee is frozen — inspect, then <see cref="Resume"/>.
    ///
    /// When at least one exception filter has been armed via <see cref="ArmExceptionFilter"/>,
    /// non-matching <see cref="StopReason.Exception"/> stops are auto-resumed inside the wait
    /// (the deadline budget is preserved across resumes). With no filters armed every stop surfaces
    /// — the legacy behavior probes 26/27 rely on.</summary>
    public StopInfo? WaitForStop(TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;

            // Breakpoint stops: evaluate the entry's policy on the caller thread (where the pump is
            // free to service func-eval Resume/WaitForStop cycles). Without a policy attached, surface
            // directly — backward compatible. With a policy, the gates / action / suspend decision
            // determines surface, auto-resume, or condition fault. ADR-010 Increment 2c.
            if (stop.Reason == StopReason.Breakpoint)
            {
                BreakpointEntry? entry = FindBreakpointEntry(_pump.LastBreakpointPointer);
                if (entry?.Policy is null) return stop;

                int hits = entry.HitCount;
                PolicyOutcome outcome = EvaluatePolicy(entry.Policy, ref hits);
                entry.HitCount = hits;
                switch (outcome)
                {
                    case PolicyOutcome.Resume: _pump.Resume(); continue;
                    case PolicyOutcome.ConditionFault: return new StopInfo(StopReason.ConditionError);
                    default: return stop;
                }
            }

            // Exception stops: type filter + per-filter policy. Same structure as the breakpoint path
            // above. With no filters armed, every exception stop surfaces.
            if (stop.Reason == StopReason.Exception)
            {
                if (_exceptionFilters.Count == 0) return stop;
                ExceptionFilterInfo? match = FindMatchingExceptionFilter(stop.ExceptionKind);
                if (match is null) { _pump.Resume(); continue; }

                if (_exceptionFilterState.TryGetValue(match.Id, out ExceptionFilterState? state) && state.Policy is not null)
                {
                    int hits = state.HitCount;
                    PolicyOutcome outcome = EvaluatePolicy(state.Policy, ref hits);
                    state.HitCount = hits;
                    switch (outcome)
                    {
                        case PolicyOutcome.Resume: _pump.Resume(); continue;
                        case PolicyOutcome.ConditionFault: return new StopInfo(StopReason.ConditionError);
                        default: return stop;
                    }
                }

                return stop;
            }

            // Other stops (Step, Break, EvalComplete, EvalException, Pause, ProcessExited) surface as-is.
            return stop;
        }
    }

    private ExceptionFilterInfo? FindMatchingExceptionFilter(ExceptionStopKind actualPhase)
    {
        // Walk the exception's full type chain (across modules via ICorDebugType.GetBase, probe 37)
        // so a filter on a base class (e.g. "System.Exception") matches any subclass — finding 47.
        IReadOnlyList<string> chain = Interop.ExceptionInspector.CurrentExceptionTypeChain(_pump.StopThread);
        if (chain.Count == 0) return null;
        foreach (ExceptionFilterInfo f in _exceptionFilters)
            if (f.MatchesChain(chain, actualPhase)) return f;
        return null;
    }

    /// <summary>Resume a stopped debuggee so it runs to the next stop or exit.</summary>
    public void Resume() => _pump.Resume();

    /// <summary>Send a graceful-termination request to the Owned target (SIGTERM on Unix;
    /// Windows path is deferred to ADR-007 Phase 9 / GenerateConsoleCtrlEvent). Wait up to
    /// <paramref name="timeout"/> for the target to exit naturally. Returns true if the target
    /// exited within the window; false if still alive. Does NOT force-kill on timeout — caller
    /// chooses the next action (call <see cref="Abandon"/> to escalate; retry; wait longer).
    ///
    /// <para>Layer 1 discipline primitive per ADR-008 / finding 68. Well-behaved targets respond
    /// to SIGTERM in 12-45 ms on macOS-arm64 (per finding 68 probes 49/50/52/53). Tight CPU
    /// loops with no explicit handler also exit cleanly via CoreCLR's default signal disposition.
    /// Only targets that explicitly catch + ignore SIGTERM (or Cancel=true their handler) will
    /// survive past timeout.</para>
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> if called on a Borrowed (Attach)
    /// session — substrate doesn't own the target's lifecycle for Borrowed sessions, so the
    /// caller is not entitled to request its termination through the substrate.</para></summary>
    /// <exception cref="InvalidOperationException">Called on a Borrowed session.</exception>
    public bool RequestExit(TimeSpan timeout)
    {
        if (!_ownsTarget)
        {
            throw new InvalidOperationException(
                "RequestExit is valid only for Owned sessions (AttachAndOwn / Launch). Borrowed Attach " +
                "sessions don't own the target's lifecycle — the caller does. To terminate a Borrowed " +
                "target, the caller (who owns it) should signal it via their own process-management path.");
        }
        if (_targetProcess is null || _targetProcess.HasExited) return true;

        try
        {
            int rc = Interop.PosixSignals.Kill(_targetProcess.Id, Interop.PosixSignals.SIGTERM);
            if (rc != 0)
            {
                int err = Marshal.GetLastWin32Error();
                _sink.OnAnomaly(new EngineAnomaly(
                    DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request",
                    "RequestExit (libc.kill SIGTERM)",
                    Observed: $"kill returned {rc}, errno {err}",
                    Expected: "0 (signal queued for delivery)",
                    Context: new Dictionary<string, string> { ["pid"] = _targetProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["errno"] = err.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                return _targetProcess.HasExited;
            }
        }
        catch (PlatformNotSupportedException)
        {
            // Windows: GenerateConsoleCtrlEvent path deferred to Phase 9.
            // For now, the caller can fall back to Abandon (Process.Kill).
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request",
                "RequestExit (platform not supported)",
                Observed: $"PosixSignals.Kill threw PlatformNotSupportedException on {RuntimeInformation.OSDescription}",
                Expected: "Unix: SIGTERM via libc.kill",
                Context: new Dictionary<string, string> { ["platform"] = RuntimeInformation.OSDescription }));
            return false;
        }
        catch (Exception ex)
        {
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedCleanupException, "mcp-request",
                "RequestExit (libc.kill)",
                Observed: $"{ex.GetType().Name}: {ex.Message}",
                Expected: "kill returns 0 on success or -1+errno on failure",
                Context: null));
            return _targetProcess.HasExited;
        }

        return _targetProcess.WaitForExit((int)timeout.TotalMilliseconds);
    }

    /// <summary>Forcibly terminate the Owned target after a brief grace period and tear down the
    /// substrate session. Extraordinary measure for targets that violate process lifecycle rules
    /// — eternal loops, missing graceful-shutdown handlers, deadlocks. Internally:
    /// (1) sends SIGTERM, (2) waits <paramref name="briefGrace"/> (default 200 ms,
    /// <see cref="DefaultAbandonBriefGrace"/>), (3) sends SIGKILL if target still alive,
    /// (4) runs <see cref="Dispose"/>'s teardown.
    ///
    /// <para>The brief grace is a final courtesy — well-behaved targets exit in tens of ms on
    /// SIGTERM per finding 68, so 200 ms catches them; misbehaving targets get the non-catchable
    /// SIGKILL within sub-second total budget.</para>
    ///
    /// <para>Use <see cref="Dispose"/> for the discipline-default path — Dispose waits
    /// <see cref="DefaultNaturalExitTimeout"/> (2 seconds) for natural exit. Use Abandon when you
    /// know the target won't end on its own and you've chosen to terminate quickly.</para>
    ///
    /// <para>No-op on already-Disposed sessions. For Borrowed sessions: Abandon's kill semantics
    /// don't apply (substrate doesn't own the target); the call falls through to Dispose's
    /// Borrowed teardown path.</para></summary>
    public void Abandon(TimeSpan? briefGrace = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        TimeSpan grace = briefGrace ?? DefaultAbandonBriefGrace;
        if (_ownsTarget && _targetProcess is not null && !_targetProcess.HasExited)
        {
            // Stage 1: SIGTERM with brief grace — catches well-behaved targets.
            bool exitedNaturally;
            try { exitedNaturally = RequestExit(grace); }
            catch { exitedNaturally = _targetProcess.HasExited; }

            // Stage 2: SIGKILL fallback. The TargetStuckAtDispose anomaly is reserved for
            // Dispose's default path (where the discipline-default 2s timeout was applied);
            // Abandon's deliberate fast-escalation doesn't emit it — the caller opted in.
            if (!exitedNaturally)
            {
                TryKillTargetAndSettle();
            }
        }

        Dispose();
    }

    /// <summary>Interrupt a RUNNING debuggee (AsyncBreak). Synchronizes the process via
    /// <c>ICorDebugController.Stop</c>; the next <see cref="WaitForStop"/> returns a
    /// <see cref="StopReason.Pause"/> stop. Resume via <see cref="Resume"/> like any other stop.
    /// If a callback-driven stop is already in flight, that stop surfaces first and the requested
    /// pause queues behind it (will fire after the user resumes the prior stop).</summary>
    public void Pause() => _pump.RequestPause();

    /// <summary>Run until a breakpoint hit where <paramref name="condition"/> holds (or a
    /// non-breakpoint stop, or timeout). At each breakpoint hit the condition is evaluated against
    /// a snapshot of the frame's locals/arguments; if false, the debuggee is resumed and the wait
    /// continues. This is the conditional-breakpoint mechanism: the breakpoint marks WHERE, the
    /// predicate decides WHETHER. The predicate is a plain delegate — the C#-expression front end
    /// (Roslyn) lives above the engine. Returns null on timeout.</summary>
    public StopInfo? WaitForConditionalStop(Func<IEvalContext, bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;
            if (stop.Reason != StopReason.Breakpoint) return stop; // non-breakpoint stops surface as-is
            IEvalContext context = new EvalContext(GetLocals(), GetArguments());
            if (condition(context)) return stop;
            _pump.Resume(); // condition false — keep running to the next hit (within the deadline)
        }
    }

    /// <summary>Drive a breakpoint with a <see cref="BreakpointPolicy"/> — the unified surface from
    /// findings 33/35. At each breakpoint hit: increment a hit counter, evaluate the gates
    /// (<see cref="BreakpointPolicy.HitCount"/>, then <see cref="BreakpointPolicy.Condition"/>), run
    /// the <see cref="BreakpointPolicy.LogMessage"/> action if present (rendered + emitted as a
    /// <see cref="LogRecord"/> via <see cref="IDebugEventSink.OnLog"/>), and surface or auto-resume per
    /// <see cref="BreakpointPolicy.Suspend"/>. Conditional breakpoint, logpoint, hit-count gate, and
    /// log-and-break all fall out as configurations of the same policy. A condition that THROWS is a
    /// FAULT — never silently false — and surfaces as <see cref="StopReason.ConditionError"/> with a
    /// fault <see cref="LogRecord"/>. Non-breakpoint stops surface as-is. Returns null on timeout (a
    /// pure logpoint, by design, never returns a non-null value — call with a bounded timeout to
    /// drain logs).</summary>
    public StopInfo? WaitForPolicyStop(BreakpointPolicy policy, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(policy);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        int hitCount = 0;
        while (true)
        {
            // Deadline-based: total wall-clock budget, not per-stop. A pure logpoint (Suspend.None,
            // no condition that ever returns) only terminates via this deadline; without it a
            // fast-hitting breakpoint resets the per-call timeout each iteration and the loop never exits.
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;
            if (stop.Reason != StopReason.Breakpoint) return stop;

            switch (EvaluatePolicy(policy, ref hitCount))
            {
                case PolicyOutcome.Resume: _pump.Resume(); continue;
                case PolicyOutcome.ConditionFault: return new StopInfo(StopReason.ConditionError);
                default: return stop; // Surface
            }
        }
    }

    /// <summary>Drive an exception "breakpoint" with a <see cref="BreakpointPolicy"/> — the
    /// exception-location surface from findings 33/35. Exception stops whose type does not match
    /// <paramref name="exceptionTypeName"/> are auto-resumed; matching stops run the policy
    /// (gates, log action, suspend decision), reusing the SAME policy evaluation as
    /// <see cref="WaitForPolicyStop"/>. A condition like <c>"ex.Code == 42"</c> uses the in-flight
    /// exception object (resolve via <see cref="TryEvalCurrentExceptionMember"/>) — the walker
    /// special-cases the <c>ex</c> operand. Returns null on timeout; non-exception stops surface
    /// as-is. The hit counter only counts matching exceptions, not stray first-chance noise.</summary>
    public StopInfo? WaitForExceptionPolicyStop(string exceptionTypeName, BreakpointPolicy policy, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(exceptionTypeName);
        ArgumentNullException.ThrowIfNull(policy);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        int hitCount = 0;
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            StopInfo? stop = _pump.WaitForStop(remaining);
            if (stop is null) return null;
            if (stop.Reason != StopReason.Exception) return stop;

            // Type filter — non-matching exceptions auto-resume without polluting the hit counter.
            if (GetCurrentExceptionTypeName() != exceptionTypeName) { _pump.Resume(); continue; }

            switch (EvaluatePolicy(policy, ref hitCount))
            {
                case PolicyOutcome.Resume: _pump.Resume(); continue;
                case PolicyOutcome.ConditionFault: return new StopInfo(StopReason.ConditionError);
                default: return stop;
            }
        }
    }

    private enum PolicyOutcome { Resume, Surface, ConditionFault }

    /// <summary>Run a stop's policy: gates (hit count + condition) → action (log) → suspend decision.
    /// Shared by <see cref="WaitForPolicyStop"/> and <see cref="WaitForExceptionPolicyStop"/> so the
    /// fault path and best-effort logging behave identically across location kinds.</summary>
    private PolicyOutcome EvaluatePolicy(BreakpointPolicy policy, ref int hitCount)
    {
        hitCount++;
        IEvalContext context = new EvalContext(GetLocals(), GetArguments());

        if (policy.HitCount is { } gate && !gate.Admits(hitCount)) return PolicyOutcome.Resume;

        if (policy.Condition is { } condition)
        {
            bool holds;
            try { holds = condition(context); }
            catch (Exception ex)
            {
                _sink.OnLog(new LogRecord(DateTimeOffset.UtcNow, $"condition fault: {ex.Message}", IsFault: true));
                return PolicyOutcome.ConditionFault;
            }
            if (!holds) return PolicyOutcome.Resume;
        }

        if (policy.LogMessage is { } render)
        {
            string message;
            try { message = render(context); }
            catch (Exception ex) { message = $"<log fault: {ex.Message}>"; }
            _sink.OnLog(new LogRecord(DateTimeOffset.UtcNow, message));
        }

        return policy.Suspend == SuspendPolicy.None ? PolicyOutcome.Resume : PolicyOutcome.Surface;
    }

    /// <summary>Linear-scan lookup of the <see cref="BreakpointEntry"/> whose
    /// <c>ICorDebugBreakpoint</c> pointer matches <paramref name="breakpointPtr"/>. Returns null if
    /// none — used by <see cref="WaitForStop"/>'s caller-thread policy evaluation after the pump
    /// publishes a Breakpoint stop with <see cref="CallbackPump.LastBreakpointPointer"/> set.</summary>
    private BreakpointEntry? FindBreakpointEntry(nint breakpointPtr)
    {
        if (breakpointPtr == 0) return null;
        for (int i = 0; i < _breakpoints.Count; i++)
            if (_breakpoints[i].Breakpoint == breakpointPtr) return _breakpoints[i];
        return null;
    }

    /// <summary>Step into calls from the current stop. Completion surfaces as a
    /// <see cref="StopReason.Step"/> from <see cref="WaitForStop"/>. Valid only while stopped.</summary>
    public void StepInto() => _pump.StepInto();

    /// <summary>Step over calls from the current stop. Completion surfaces as a
    /// <see cref="StopReason.Step"/> from <see cref="WaitForStop"/>. Valid only while stopped.</summary>
    public void StepOver() => _pump.StepOver();

    /// <summary>Step out of the current frame. Completion surfaces as a
    /// <see cref="StopReason.Step"/> from <see cref="WaitForStop"/>. Valid only while stopped.</summary>
    public void StepOut() => _pump.StepOut();

    /// <summary>Names of the modules loaded in the target (process → app domains → assemblies →
    /// modules). Valid only while the debuggee is stopped (after <see cref="WaitForStop"/>) —
    /// ICorDebug enumeration requires the process to be synchronized.</summary>
    public IReadOnlyList<string> EnumerateModules() => RuntimeNavigation.ModuleNames(_pProcess);

    /// <summary>The managed call stack at the current stop, top frame first, as
    /// "Type.Method @ file:line" when a Portable PDB is available (else "Type.Method";
    /// "[external]" for native/internal frames). Valid only while the debuggee is stopped
    /// (after <see cref="WaitForStop"/>).</summary>
    public IReadOnlyList<string> GetStackFrames()
    {
        List<string> result = new();
        foreach (Interop.FrameInfo frame in Frames.WalkManagedFrames(_pump.StopThread))
        {
            SourceLocation? loc = frame.IlOffset >= 0 && frame.ModulePath.Length > 0
                ? SymbolsFor(frame.ModulePath)?.TryGetLine(frame.Token, frame.IlOffset)
                : null;
            result.Add(loc is { } l ? $"{frame.Method} @ {Path.GetFileName(l.File)}:{l.Line}" : frame.Method);
        }
        return result;
    }

    /// <summary>Cached <see cref="SymbolReader"/> for a module (opened once; null if no PDB).</summary>
    private SymbolReader? SymbolsFor(string modulePath)
    {
        if (!_symbols.TryGetValue(modulePath, out SymbolReader? reader))
        {
            reader = SymbolReader.TryOpen(modulePath);
            _symbols[modulePath] = reader;
        }
        return reader;
    }

    /// <summary>Argument values of the active (top) frame at the current stop. Arg 0 is
    /// <c>this</c> for an instance method; <see cref="ArgumentValue.RawValue"/> holds the
    /// primitive bits for generic values, null for object references. When <paramref name="depth"/>
    /// &gt; 0, object args have their <see cref="ArgumentValue.Fields"/> populated by walking
    /// instance fields up the type chain (finding 48); recursive into object-typed fields when
    /// depth &gt; 1. Valid only while stopped.</summary>
    public IReadOnlyList<ArgumentValue> GetArguments(int depth = 0)
    {
        if (depth > MaxInspectionDepth)
        {
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.DepthClamped, "mcp-request", "GetArguments",
                Observed: $"depth={depth} requested",
                Expected: $"depth <= {MaxInspectionDepth}",
                Context: new Dictionary<string, string> { ["requested"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture), ["clamped"] = MaxInspectionDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            depth = MaxInspectionDepth;
        }
        return Variables.ReadActiveFrameArguments(_pump.StopThread, 16, depth);
    }

    /// <summary>Named local variables of the active (top) frame at the current stop — PDB names
    /// (via the module's Portable PDB) paired with values read from the frame. Empty if no PDB.
    /// A local not yet in scope/assigned at the current line surfaces with a null
    /// <see cref="LocalValue.RawValue"/>. When <paramref name="depth"/> &gt; 0, object locals have
    /// their <see cref="LocalValue.Fields"/> populated by walking instance fields up the type
    /// chain (finding 48); recursive into object-typed fields when depth &gt; 1. Valid only while
    /// stopped.</summary>
    public IReadOnlyList<LocalValue> GetLocals(int depth = 0)
    {
        if (depth > MaxInspectionDepth)
        {
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.DepthClamped, "mcp-request", "GetLocals",
                Observed: $"depth={depth} requested",
                Expected: $"depth <= {MaxInspectionDepth}",
                Context: new Dictionary<string, string> { ["requested"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture), ["clamped"] = MaxInspectionDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            depth = MaxInspectionDepth;
        }
        List<Interop.FrameInfo> frames = Frames.WalkManagedFrames(_pump.StopThread);
        if (frames.Count == 0) return Array.Empty<LocalValue>();

        Interop.FrameInfo top = frames[0];
        if (top.ModulePath.Length == 0 || (top.Token >> 24) != 0x06) return Array.Empty<LocalValue>();

        IReadOnlyList<LocalName> names = SymbolsFor(top.ModulePath)?.GetLocalNames(top.Token) ?? Array.Empty<LocalName>();
        return Variables.ReadActiveFrameLocals(_pump.StopThread, names, depth);
    }

    /// <summary>The fully-qualified type name of the exception in flight at the current stop (e.g.
    /// "System.InvalidOperationException"), or null if none. Meaningful at a
    /// <see cref="StopReason.Exception"/> stop; reads <c>ICorDebugThread.GetCurrentException</c> on
    /// the stop thread. Valid only while stopped.</summary>
    public string? GetCurrentExceptionTypeName() => ExceptionInspector.CurrentExceptionTypeName(_pump.StopThread);

    /// <summary>EXPERIMENT (func-eval): evaluate a static, parameterless method in the debuggee at
    /// the current stop and return its value. Creates an eval on the stopped thread, calls the
    /// function, resumes to run it, and waits up to <paramref name="timeout"/> for the
    /// EvalComplete stop. A timeout is the func-eval-deadlock signal. Valid only while stopped.</summary>
    public EvalStatus TryEvalStaticCall(string moduleNameSubstring, string typeName, string methodName,
                                        TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return EvalStatus.SetupFailed;
        try
        {
            uint token = MetadataResolver.ResolveMethodToken(pModule, typeName, methodName);
            if (token == 0) return EvalStatus.SetupFailed;

            nint function = Eval.GetFunction(pModule, token);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;
                try
                {
                    if (!Eval.CallStaticNoArgs(eval, function)) return EvalStatus.SetupFailed;

                    _pump.Resume(); // worker Continues → the eval runs managed code in the debuggee
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) return EvalStatus.TimedOut; // func-eval deadlock (the netcoredbg failure)
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally { RuntimeNavigation.Release(eval); }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(pModule); }
    }

    /// <summary>EXPERIMENT (func-eval breadth): evaluate a static, single-<c>int</c>-argument method
    /// in the debuggee at the current stop. The argument is built as an eval value
    /// (<c>CreateValue</c> + <c>SetValue</c>). On a timeout the eval is aborted
    /// (<c>ICorDebugEval.Abort</c>) before returning. Valid only while stopped.</summary>
    public EvalStatus TryEvalStaticCallInt(string moduleNameSubstring, string typeName, string methodName,
                                           int argument, TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return EvalStatus.SetupFailed;
        try
        {
            uint token = MetadataResolver.ResolveMethodToken(pModule, typeName, methodName);
            if (token == 0) return EvalStatus.SetupFailed;

            nint function = Eval.GetFunction(pModule, token);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;

                nint arg = 0;
                try
                {
                    arg = Eval.CreateInt32(eval, argument);
                    if (arg == 0) return EvalStatus.SetupFailed;
                    if (!Eval.CallWithOneArg(eval, function, arg)) return EvalStatus.SetupFailed;

                    _pump.Resume();
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; } // hung eval — abort it
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally
                {
                    if (arg != 0) RuntimeNavigation.Release(arg);
                    RuntimeNavigation.Release(eval);
                }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(pModule); }
    }

    /// <summary>EXPERIMENT (func-eval breadth): call an instance method/property
    /// <paramref name="declaringTypeName"/>.<paramref name="methodName"/> (resolved in the module
    /// matching <paramref name="declaringModuleSubstring"/>) on the local named
    /// <paramref name="thisLocalName"/> as <c>this</c>. E.g. <c>s.Length</c> = String.get_Length on
    /// the local <c>s</c>. Valid only while stopped.</summary>
    public EvalStatus TryEvalInstanceCall(string thisLocalName, string declaringModuleSubstring,
                                          string declaringTypeName, string methodName,
                                          TimeSpan timeout, out ArgumentValue result)
    {
        result = default;

        // 1. Resolve `this`: find the named local's slot (top frame's PDB) and read its value.
        List<Interop.FrameInfo> frames = Frames.WalkManagedFrames(_pump.StopThread);
        if (frames.Count == 0) return EvalStatus.SetupFailed;
        Interop.FrameInfo top = frames[0];
        if (top.ModulePath.Length == 0 || (top.Token >> 24) != 0x06) return EvalStatus.SetupFailed;

        SymbolReader? symbols = SymbolsFor(top.ModulePath);
        if (symbols is null) return EvalStatus.SetupFailed;
        int slot = -1;
        foreach (LocalName local in symbols.GetLocalNames(top.Token))
            if (local.Name == thisLocalName) { slot = local.Slot; break; }
        if (slot < 0) return EvalStatus.SetupFailed;

        nint thisValue = Variables.GetActiveFrameLocalValue(_pump.StopThread, slot);
        if (thisValue == 0) return EvalStatus.SetupFailed;
        try
        {
            // 2. Resolve the method on its declaring module (may differ from the frame's module).
            nint declModule = RuntimeNavigation.FindModule(_pProcess, declaringModuleSubstring);
            if (declModule == 0) return EvalStatus.SetupFailed;
            try
            {
                uint token = MetadataResolver.ResolveMethodToken(declModule, declaringTypeName, methodName);
                if (token == 0) return EvalStatus.SetupFailed;
                nint function = Eval.GetFunction(declModule, token);
                if (function == 0) return EvalStatus.SetupFailed;
                try
                {
                    nint eval = Eval.CreateEval(_pump.StopThread);
                    if (eval == 0) return EvalStatus.SetupFailed;
                    try
                    {
                        if (!Eval.CallWithOneArg(eval, function, thisValue)) return EvalStatus.SetupFailed; // args[0] = this
                        _pump.Resume();
                        StopInfo? stop = _pump.WaitForStop(timeout);
                        if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; }
                        if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                        if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                        result = Eval.GetResultValue(eval);
                        return EvalStatus.Completed;
                    }
                    finally { RuntimeNavigation.Release(eval); }
                }
                finally { RuntimeNavigation.Release(function); }
            }
            finally { RuntimeNavigation.Release(declModule); }
        }
        finally { RuntimeNavigation.Release(thisValue); }
    }

    /// <summary>EXPERIMENT (general member resolution): call the property getter
    /// <c>thisLocal.member</c> by resolving <c>get_&lt;member&gt;</c> on the local's RUNTIME type —
    /// no hardcoded declaring type/module — and func-evaluating it. Works for plain reference
    /// objects; strings/non-object kinds return SetupFailed (the ICorDebugType path is a follow-on).
    /// Valid only while stopped.</summary>
    public EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        int slot = ResolveLocalSlot(thisLocalName);
        if (slot < 0) return EvalStatus.SetupFailed;

        nint thisValue = Variables.GetActiveFrameLocalValue(_pump.StopThread, slot);
        if (thisValue == 0) return EvalStatus.SetupFailed;
        try
        {
            nint function = MemberResolver.ResolveGetter(thisValue, memberName);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;
                try
                {
                    if (!Eval.CallWithOneArg(eval, function, thisValue)) return EvalStatus.SetupFailed;
                    _pump.Resume();
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; }
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally { RuntimeNavigation.Release(eval); }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(thisValue); }
    }

    /// <summary>EXPERIMENT (func-eval at an exception stop): at a <see cref="StopReason.Exception"/>
    /// stop, call the property getter <c>&lt;member&gt;</c> on the in-flight exception object — its
    /// value comes from <c>ICorDebugThread.GetCurrentException</c>, not a local — by resolving
    /// <c>get_&lt;member&gt;</c> on the exception's RUNTIME type and func-evaluating it. This composes
    /// the exception stop (probe 26) with general member resolution (probe 24); the runtime preserves
    /// the exception across the eval (cordebug.idl). Powers conditional exception breakpoints
    /// (<c>ex.Code == 42</c>). Valid only at an exception stop.</summary>
    public EvalStatus TryEvalCurrentExceptionMember(string memberName, TimeSpan timeout, out ArgumentValue result)
    {
        result = default;
        nint thisValue = Interop.ExceptionInspector.CurrentExceptionValue(_pump.StopThread);
        if (thisValue == 0) return EvalStatus.SetupFailed;
        try
        {
            nint function = MemberResolver.ResolveGetter(thisValue, memberName);
            if (function == 0) return EvalStatus.SetupFailed;
            try
            {
                nint eval = Eval.CreateEval(_pump.StopThread);
                if (eval == 0) return EvalStatus.SetupFailed;
                try
                {
                    if (!Eval.CallWithOneArg(eval, function, thisValue)) return EvalStatus.SetupFailed;
                    _pump.Resume();
                    StopInfo? stop = _pump.WaitForStop(timeout);
                    if (stop is null) { Eval.Abort(eval); return EvalStatus.TimedOut; }
                    if (stop.Reason == StopReason.EvalException) return EvalStatus.ThrewException;
                    if (stop.Reason != StopReason.EvalComplete) return EvalStatus.SetupFailed;

                    result = Eval.GetResultValue(eval);
                    return EvalStatus.Completed;
                }
                finally { RuntimeNavigation.Release(eval); }
            }
            finally { RuntimeNavigation.Release(function); }
        }
        finally { RuntimeNavigation.Release(thisValue); }
    }

    /// <summary>PDB slot of the named local in the active (top) frame, or -1 if not found.</summary>
    private int ResolveLocalSlot(string localName)
    {
        List<Interop.FrameInfo> frames = Frames.WalkManagedFrames(_pump.StopThread);
        if (frames.Count == 0) return -1;

        Interop.FrameInfo top = frames[0];
        if (top.ModulePath.Length == 0 || (top.Token >> 24) != 0x06) return -1;

        SymbolReader? symbols = SymbolsFor(top.ModulePath);
        if (symbols is null) return -1;
        foreach (LocalName local in symbols.GetLocalNames(top.Token))
            if (local.Name == localName) return local.Slot;
        return -1;
    }

    /// <summary>Resolve <paramref name="typeName"/>.<paramref name="methodName"/> in the module
    /// whose name contains <paramref name="moduleNameSubstring"/> to an <c>mdMethodDef</c> token
    /// (0 if not found). Valid only while the debuggee is stopped. The token feeds breakpoint
    /// creation (<c>GetFunctionFromToken</c>).</summary>
    public uint ResolveMethodToken(string moduleNameSubstring, string typeName, string methodName)
        => RuntimeNavigation.ResolveMethodToken(_pProcess, moduleNameSubstring, typeName, methodName);

    /// <summary>Lift a <see cref="BreakpointPolicySpec"/> (string data) into a
    /// <see cref="BreakpointPolicy"/> (engine domain form) by compiling its Condition expression
    /// against this session's member resolver. The substrate's canonical compiler — external
    /// callers (MCP layer, probes using string conditions, transport-form constructors) lift via
    /// this method rather than invoking any parser directly. Compilation logic is substrate-only
    /// (see <see cref="Expressions.CSharpCondition"/>, internal).
    ///
    /// <para><see cref="BreakpointPolicySpec.LogMessage"/> template compilation is not yet
    /// implemented; passing a non-null LogMessage throws <see cref="NotImplementedException"/>
    /// until the template compiler (with <c>{expr}</c> interpolation) lands as a follow-up
    /// to Increment 1.</para></summary>
    public BreakpointPolicy Compile(BreakpointPolicySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.CompileWith(this);
    }

    /// <summary>Set an active breakpoint at the entry of
    /// <paramref name="typeName"/>.<paramref name="methodName"/> in the module whose name
    /// contains <paramref name="moduleNameSubstring"/>. Returns the new breakpoint's id (positive)
    /// on success, <c>0</c> if the method can't be resolved or the breakpoint can't be created.
    /// Pass the id to <see cref="RemoveBreakpoint"/>; <see cref="ListBreakpoints"/> returns it
    /// alongside the <see cref="FunctionBreakpointInfo"/> descriptor. Valid only while the debuggee
    /// is stopped; a hit later surfaces as <see cref="StopReason.Breakpoint"/> from
    /// <see cref="WaitForStop"/>.</summary>
    public int SetBreakpoint(string moduleNameSubstring, string typeName, string methodName, BreakpointPolicy? policy = null)
    {
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return 0;
        try
        {
            uint token = MetadataResolver.ResolveMethodToken(pModule, typeName, methodName);
            if (token == 0) return 0;
            if (!Breakpoints.TryCreate(pModule, token, out nint function, out nint breakpoint)) return 0;

            int id = ++_nextBreakpointId;
            _breakpoints.Add(new BreakpointEntry(
                new FunctionBreakpointInfo(id, moduleNameSubstring, typeName, methodName),
                pModule, function, breakpoint, policy));
            pModule = 0; // ownership moved into _breakpoints — don't release below
            return id;
        }
        finally { if (pModule != 0) RuntimeNavigation.Release(pModule); }
    }

    /// <summary>Set an active breakpoint at a source <paramref name="line"/> in a document whose
    /// name contains <paramref name="fileHint"/>, within the module matching
    /// <paramref name="moduleNameSubstring"/>. Binds via the module's Portable PDB to the nearest
    /// sequence point at or after the line. Returns the new breakpoint's id (positive) on success,
    /// <c>0</c> if no PDB, no matching line, or the breakpoint can't be created. Pass the id to
    /// <see cref="RemoveBreakpoint"/>; <see cref="ListBreakpoints"/> returns it alongside the
    /// <see cref="LineBreakpointInfo"/> descriptor. Valid only while stopped; a hit surfaces as
    /// <see cref="StopReason.Breakpoint"/>.</summary>
    public int SetBreakpointAtLine(string moduleNameSubstring, string fileHint, int line, BreakpointPolicy? policy = null)
    {
        nint pModule = RuntimeNavigation.FindModule(_pProcess, moduleNameSubstring);
        if (pModule == 0) return 0;
        try
        {
            SymbolReader? symbols = SymbolsFor(RuntimeNavigation.ModuleName(pModule));
            if (symbols is null || !symbols.TryFindLine(fileHint, line, out int token, out int ilOffset))
                return 0;
            if (!Breakpoints.TryCreateAtOffset(pModule, (uint)token, (uint)ilOffset, out nint function, out nint breakpoint))
                return 0;

            int id = ++_nextBreakpointId;
            _breakpoints.Add(new BreakpointEntry(
                new LineBreakpointInfo(id, moduleNameSubstring, fileHint, line),
                pModule, function, breakpoint, policy));
            pModule = 0; // ownership moved into _breakpoints
            return id;
        }
        finally { if (pModule != 0) RuntimeNavigation.Release(pModule); }
    }

    /// <summary>The active breakpoints in registration order — id + descriptor (a
    /// <see cref="LineBreakpointInfo"/> or <see cref="FunctionBreakpointInfo"/>). The MCP list /
    /// remove-by-natural-key flows pattern-match on the concrete subtype to recover file/line or
    /// type/method.</summary>
    public IReadOnlyList<BreakpointInfo> ListBreakpoints()
    {
        BreakpointInfo[] result = new BreakpointInfo[_breakpoints.Count];
        for (int i = 0; i < _breakpoints.Count; i++) result[i] = _breakpoints[i].Info;
        return result;
    }

    /// <summary>Deactivate (via <c>ICorDebugBreakpoint.Activate(FALSE)</c>) and release the
    /// breakpoint with <paramref name="id"/>. Returns <c>true</c> if a matching entry was found;
    /// <c>false</c> otherwise. Valid only while stopped.</summary>
    public bool RemoveBreakpoint(int id)
    {
        for (int i = 0; i < _breakpoints.Count; i++)
        {
            if (_breakpoints[i].Info.Id == id)
            {
                BreakpointEntry e = _breakpoints[i];
                Breakpoints.Deactivate(e.Breakpoint);
                RuntimeNavigation.Release(e.Breakpoint);
                RuntimeNavigation.Release(e.Function);
                RuntimeNavigation.Release(e.Module);
                _breakpoints.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Deactivate and release ALL active breakpoints; returns how many were cleared. Valid
    /// only while stopped. <see cref="Dispose"/> releases the refs separately (after the runtime
    /// has been terminated, so deactivation is moot).</summary>
    public int ClearBreakpoints()
    {
        int count = _breakpoints.Count;
        foreach (BreakpointEntry e in _breakpoints)
        {
            Breakpoints.Deactivate(e.Breakpoint);
            RuntimeNavigation.Release(e.Breakpoint);
            RuntimeNavigation.Release(e.Function);
            RuntimeNavigation.Release(e.Module);
        }
        _breakpoints.Clear();
        return count;
    }

    /// <summary>Arm a persistent exception filter. Once armed, <see cref="WaitForStop"/> only
    /// surfaces matching exception stops; non-matching ones are auto-resumed. <paramref name="typeName"/>
    /// is an exact match (or <see cref="ExceptionFilterInfo.AnyType"/> <c>"*"</c> for any type;
    /// subclass walking is a future refinement). <paramref name="phaseFilter"/> defaults to
    /// <see cref="ExceptionStopKind.None"/> meaning "any phase". Returns a positive id; pass to
    /// <see cref="RemoveExceptionFilter"/>.</summary>
    public int ArmExceptionFilter(string typeName, ExceptionStopKind phaseFilter = ExceptionStopKind.None, BreakpointPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        int id = ++_nextExceptionFilterId;
        _exceptionFilters.Add(new ExceptionFilterInfo(id, typeName, phaseFilter));
        if (policy is not null)
            _exceptionFilterState[id] = new ExceptionFilterState { Policy = policy };
        return id;
    }

    /// <summary>The armed exception filters in registration order. With at least one armed,
    /// <see cref="WaitForStop"/> auto-resumes non-matching exception stops.</summary>
    public IReadOnlyList<ExceptionFilterInfo> ListExceptionFilters()
    {
        ExceptionFilterInfo[] result = new ExceptionFilterInfo[_exceptionFilters.Count];
        for (int i = 0; i < _exceptionFilters.Count; i++) result[i] = _exceptionFilters[i];
        return result;
    }

    /// <summary>Remove the exception filter with <paramref name="id"/>. Returns <c>true</c> if a
    /// matching entry was found.</summary>
    public bool RemoveExceptionFilter(int id)
    {
        for (int i = 0; i < _exceptionFilters.Count; i++)
        {
            if (_exceptionFilters[i].Id == id)
            {
                _exceptionFilters.RemoveAt(i);
                _exceptionFilterState.Remove(id);
                return true;
            }
        }
        return false;
    }

    /// <summary>Remove all armed exception filters; returns how many were cleared. After this,
    /// <see cref="WaitForStop"/> reverts to the no-filter behavior (every exception stop surfaces).</summary>
    public int ClearExceptionFilters()
    {
        int count = _exceptionFilters.Count;
        _exceptionFilters.Clear();
        _exceptionFilterState.Clear();
        return count;
    }

    /// <summary>Detach the debugger; the target resumes running without it. Idempotent.</summary>
    public void Detach()
    {
        // Atomic idempotence gate (ENG-DS-1) — concurrent Detach calls don't double-invoke
        // ICorDebugController.Detach (whose behavior on already-detached is undefined per docs).
        if (Interlocked.Exchange(ref _detached, 1) != 0) return;
        int hr = _controller.Detach();
        if (hr < 0)
        {
            // EA capture (UnexpectedHResult): Detach failed. Engine continues teardown — failure
            // here usually means the target already exited or mscordbi is in an inconsistent state.
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request", "Detach",
                Observed: $"HRESULT 0x{hr:X8}",
                Expected: "S_OK",
                Context: new Dictionary<string, string> { ["hresult"] = $"0x{hr:X8}" }));
        }
    }

    /// <summary>Kill the target process before substrate teardown for OWNED sessions
    /// (finding 64). Closes the dispose-then-kill race (drhook-detach-exit-race /
    /// MCH-RE-2) structurally by enforcing the kill-first protocol inside Dispose.
    /// Best-effort: failures to kill (target already exited, permission denied,
    /// orphan-to-init) emit an UnexpectedHResult anomaly but don't block teardown.
    /// Settle window of <see cref="KillSettleMs"/> after Kill gives mscordbi's RC event
    /// thread time to notice the exit before Quiesce/Detach run.</summary>
    private void TryKillTargetAndSettle()
    {
        try
        {
            if (_targetProcess is not null && !_targetProcess.HasExited)
            {
                _targetProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedCleanupException, "mcp-request",
                "TryKillTargetAndSettle",
                Observed: $"{ex.GetType().Name}: {ex.Message}",
                Expected: "target Process.Kill succeeds or target already exited",
                Context: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().FullName ?? ex.GetType().Name }));
        }
        // Settle: let mscordbi's RC event thread process the exit work item before
        // we tear down our own state. Empirically ~50–100 ms on macOS-arm64.
        Thread.Sleep(KillSettleMs);
    }

    /// <summary>Resume the debuggee before Detach (Attached path; Probe 44 / finding 59;
    /// dispatch-settle added per finding 65).
    /// After Quiesce the target is synchronized; mscordbi's Detach for a synchronized target
    /// performs an implicit resume that has been observed to race the exit work item when the
    /// resumed code immediately exits (limit drhook-detach-exit-race / probe 12 evidence).
    /// Explicit Continue here moves the target back to a running state under our control, so
    /// Detach's mscordbi unwind happens against a running (not stopped) target.
    ///
    /// mscordbi's Stop is a COUNTER, not a flag: each Stop call (pauseHandler + Quiesce) is
    /// matched by a Continue. We loop Continue until controller.Continue returns S_FALSE
    /// (target already running), bounded by maxAttempts to prevent infinite loop on a
    /// substrate bug. Without the loop, an unbalanced counter leaves mscordbi internally
    /// still considering the target synchronized after Detach, blocking the next Attach
    /// on the same target with CORDBG_E_DEBUGGER_ALREADY_ATTACHED.
    ///
    /// FINDING 65 — dispatch-settle: between Continue iterations the resumed target may
    /// generate fresh callbacks that mscordbi's RC event thread dispatches via the CCW
    /// (still alive at this point in Dispose). Without settle, the next Continue (or
    /// downstream Detach / Terminate) races concurrent dispatch and SIGSEGVs on
    /// macOS-arm64 under informational-callback flood (probe 42 redesigned). The settle
    /// gives mscordbi's RC thread a window to complete in-flight dispatch before the next
    /// substrate operation. Empirical: 10 ms intra-iteration, 50 ms pre-return — well
    /// inside Dispose budget, comfortably above mscordbi's microsecond-scale dispatch
    /// latency. Settle fires on every exit path (S_FALSE / error / exhausted) since any
    /// can be followed by Detach against in-flight dispatch.</summary>
    private void TryResumeForDetach()
    {
        const int S_FALSE = 1;
        const int maxAttempts = 10;
        const int IntraSettleMs = 10;
        const int FinalSettleMs = 50;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int hr = _controller.Continue(0);
            if (hr == S_FALSE)
            {
                Thread.Sleep(FinalSettleMs); // post-resume dispatch settles before Detach
                return;
            }
            if (hr < 0)
            {
                _sink.OnAnomaly(new EngineAnomaly(
                    DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request",
                    "TryResumeForDetach (Continue loop)",
                    Observed: $"HRESULT 0x{hr:X8} on attempt {attempt + 1}",
                    Expected: "S_OK while counter > 0, then S_FALSE when target released",
                    Context: new Dictionary<string, string> { ["hresult"] = $"0x{hr:X8}", ["attempt"] = (attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                Thread.Sleep(FinalSettleMs); // dying target may still race exit-work against Detach
                return;
            }
            // hr == S_OK: target was synchronized, one step decremented. Let mscordbi
            // dispatch any callbacks the resume produced before issuing the next Continue.
            Thread.Sleep(IntraSettleMs);
        }
        // Reached maxAttempts without S_FALSE — substrate anomaly, likely a counter leak.
        // Defensive settle before falling through to Detach so any final dispatch completes.
        Thread.Sleep(FinalSettleMs);
        _sink.OnAnomaly(new EngineAnomaly(
            DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request",
            "TryResumeForDetach (Continue loop exhausted)",
            Observed: $"controller.Continue returned S_OK {maxAttempts} times without reaching S_FALSE",
            Expected: "S_FALSE within bounded attempts (target running)",
            Context: new Dictionary<string, string> { ["maxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
    }

    /// <summary>Synchronize the target before Detach so mscordbi's RC event thread is not
    /// mid-flush of queued callbacks when Detach tears down the shim (finding 14). Stop blocks
    /// until the process is synchronized; best-effort (a failing HRESULT falls through to
    /// Detach rather than throwing on the dispose path).</summary>
    private void Quiesce()
    {
        int hr = _controller.Stop(0);
        if (hr < 0)
        {
            // EA capture (UnexpectedHResult): Stop failed. Engine continues to Detach as designed
            // (best-effort per the method's existing contract); the anomaly is the substrate
            // signal that the C-DRAIN-CB enforcement may be incomplete.
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request", "Quiesce (Stop)",
                Observed: $"HRESULT 0x{hr:X8}",
                Expected: "S_OK (debuggee synchronized for clean Detach)",
                Context: new Dictionary<string, string> { ["hresult"] = $"0x{hr:X8}" }));
        }
    }

    public void Dispose()
    {
        // Atomic idempotence gate (ENG-DS-1) — concurrent Dispose calls return without
        // double-Terminate, double-Marshal.Release, or racing _breakpoints iteration.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop our worker first: it drives controller.Continue, so it must be joined before
        // we touch the controller for the quiescent detach.
        _pump.Dispose();

        // ADR-008 / finding 68: for OWNED sessions, two-stage discipline-aligned termination.
        // Stage 1: SIGTERM (graceful request, catchable). Wait NaturalExitTimeout for natural
        //          exit. Well-behaved targets exit in tens of ms; tight CPU loops in ~15 ms via
        //          CoreCLR default disposition. Only targets that explicitly ignore SIGTERM
        //          (Cancel=true handler) will survive to Stage 2.
        // Stage 2: TargetStuckAtDispose anomaly + SIGKILL (non-catchable, via existing
        //          TryKillTargetAndSettle / Process.Kill / kernel-mandated exit). The anomaly
        //          surfaces the target's discipline violation as an actionable upstream signal.
        //
        // Finding 64 closed the structural dispose-then-kill race (MCH-RE-2) by making the
        // substrate own the kill operation. Finding 68's empirical SIGTERM evidence motivates
        // the two-stage discipline — substrate trusts well-implemented targets to exit
        // naturally, and only forces the kernel-mandated kill against violators.
        if (_ownsTarget && _targetProcess is not null && !_targetProcess.HasExited)
        {
            // ADR-008 Increment 1b: release any pending mscordbi stop before SIGTERM.
            // The substrate KNOWS from session activity whether the target is currently
            // halted by mscordbi (pump worker parked at _resume.Take until _pump.Dispose's
            // CompleteAdding fired and unparked it via WorkerSilentBreak). Even after the
            // pump worker exits, mscordbi continues to hold the target halted at the
            // hit/break point. SIGTERM against a halted target may be queued but blocked
            // by mscordbi's halt mechanism (probe 56 + Phase 8b AnomalyInjectionTest
            // evidence). Releasing the stop via controller.Continue moves target back to
            // running state — SIGTERM's signal-delivery thread then completes cleanly via
            // CoreCLR default disposition.
            //
            // Reuses TryResumeForDetach (Borrowed-path helper from finding 59/65): if
            // target is already running, first Continue returns S_FALSE — no-op (~50ms
            // for final settle). If halted, Continue releases the stop, target runs, then
            // SIGTERM lands cleanly. Substrate acts on its knowledge of target state.
            TryResumeForDetach();

            bool exitedNaturally = false;
            try { exitedNaturally = RequestExit(_naturalExitTimeout); }
            catch { exitedNaturally = _targetProcess.HasExited; }

            if (!exitedNaturally)
            {
                _sink.OnAnomaly(new EngineAnomaly(
                    DateTimeOffset.UtcNow, AnomalyKind.TargetStuckAtDispose, "mcp-request",
                    "DebugSession.Dispose (Stage 1 SIGTERM timeout)",
                    Observed: $"target {_targetProcess.Id} did not exit within NaturalExitTimeout={_naturalExitTimeout.TotalMilliseconds}ms after SIGTERM",
                    Expected: "target completes its work and exits naturally on SIGTERM (well-implemented process lifecycle)",
                    Context: new Dictionary<string, string>
                    {
                        ["pid"] = _targetProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["timeoutMs"] = _naturalExitTimeout.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
                TryKillTargetAndSettle();
            }
        }
        else if (!_ownsTarget)
        {
            // FINDING 66 follow-up: for Borrowed sessions, the target may have been killed
            // externally microseconds before this Dispose ran. Process.HasExited can lag the
            // actual death by tens of milliseconds (OS hasn't reaped yet / waitpid not
            // collected). A small pre-check settle lets HasExited propagate so the death-
            // detection below makes the correct routing decision. For Owned, KillSettleMs
            // already covered this window in TryKillTargetAndSettle above.
            Thread.Sleep(BorrowedDeathCheckSettleMs);
        }

        // FINDING 66 — target-death detection: if the target has exited (external Kill,
        // user force-quit, OOM killer, target crash, container shutdown; OR substrate's own
        // kill-first above for OWNED), mscordbi's RC event thread is mid-processing the
        // exit-work-item (delivering ExitProcess, dispatching final callbacks, releasing RC
        // state). The substrate's Quiesce/Continue calls from the main thread would race
        // that exit-work and SIGSEGV mscordbi on macOS-arm64 (Probe 44 phase C / Probe 47
        // evidence). Skip the active protocol pushes (Quiesce + TryResumeForDetach) and
        // add an explicit exit-work-completion settle before Detach. Detach itself is still
        // necessary: it releases the CCW reference from mscordbi's side, which is the
        // safety precondition for the later _callback.Dispose (which frees the CCW's
        // native vtable + GCHandle backing memory). Without Detach, a dispatch in flight
        // when _callback.Dispose runs would UAF the freed CCW memory.
        bool targetDead = _targetProcess?.HasExited == true;

        if (targetDead)
        {
            // Dead-target path: let mscordbi's RC event thread complete exit-work-item
            // processing before our Detach unwinds the same internal state. 200 ms is
            // empirically comfortable on macOS-arm64; well above mscordbi's exit-work
            // duration (sub-millisecond for typical processes) and well inside Dispose
            // budget. The KillSettleMs for OWNED above already covered 100 ms of this
            // window; this is additive belt-and-braces for external-death scenarios that
            // bypass that path.
            Thread.Sleep(ExitWorkSettleMs);
            Detach();
        }
        else
        {
            // Live-target path: synchronize before Detach so mscordbi's RC event thread is
            // not mid-flush of queued callbacks when Detach tears down the shim (finding 14).
            // Stop() synchronizes the process: it blocks until any in-flight dispatch
            // completes and the debuggee is halted; Detach from the synchronized state is
            // the probe-05-validated safe path.
            Quiesce();

            if (!_ownsTarget)
            {
                // Borrowed alive: detach-leave-running (Probe 44 / finding 59 / dispatch-settle
                // finding 65). Substrate explicitly resumes so target is RUNNING when mscordbi's
                // Detach unwinds (avoids stopped-at-breakpoint state that widens the exit-race
                // window per probe 12). Target keeps running un-debugged.
                TryResumeForDetach();
            }
            Detach();
        }
        int terminateHr = _cordbg.Terminate();
        if (terminateHr < 0)
        {
            // EA capture (UnexpectedHResult): Terminate failed. Continuing teardown — refs still
            // get released, but the anomaly signals mscordbi state may be inconsistent post-Dispose.
            _sink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.UnexpectedHResult, "mcp-request", "Terminate",
                Observed: $"HRESULT 0x{terminateHr:X8}",
                Expected: "S_OK (ICorDebug fully torn down)",
                Context: new Dictionary<string, string> { ["hresult"] = $"0x{terminateHr:X8}" }));
        }

        // Release our breakpoint refs now that the runtime has dropped the breakpoints. No need to
        // deactivate first — Terminate already invalidated them; ClearBreakpoints is for live removal.
        foreach (BreakpointEntry e in _breakpoints)
        {
            RuntimeNavigation.Release(e.Breakpoint);
            RuntimeNavigation.Release(e.Function);
            RuntimeNavigation.Release(e.Module);
        }
        _breakpoints.Clear();

        foreach (SymbolReader? reader in _symbols.Values) reader?.Dispose();
        _symbols.Clear();

        if (_pProcess != 0) { Marshal.Release(_pProcess); _pProcess = 0; }
        if (_pUnknown != 0) { Marshal.Release(_pUnknown); _pUnknown = 0; }

        _callback.Dispose();
        _dbgShim.Dispose();
        GC.KeepAlive(_cordbg);
        GC.KeepAlive(_controller);

        // Release substrate's Process handle (finding 64). The OS process is already
        // dead (TryKillTargetAndSettle handled it for Owned) or was never substrate's
        // to kill (Borrowed). Dispose just releases the managed handle.
        _targetProcess?.Dispose();
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            throw new DebugEngineException(operation, hr);
    }
}

/// <summary>Raised when an ICorDebug operation returns a failure HRESULT.</summary>
public sealed class DebugEngineException : Exception
{
    public DebugEngineException(string operation, int hresult)
        : base($"{operation} failed (HRESULT 0x{hresult:X8}).")
        => HResult = hresult;
}

/// <summary>Outcome of a function evaluation.</summary>
public enum EvalStatus
{
    /// <summary>The eval completed; the result value was returned.</summary>
    Completed,
    /// <summary>The evaluated code threw an exception.</summary>
    ThrewException,
    /// <summary>No EvalComplete arrived within the timeout — the func-eval deadlocked.</summary>
    TimedOut,
    /// <summary>The eval could not be set up (method unresolved, no stop thread, etc.).</summary>
    SetupFailed,
}

/// <summary>Immutable snapshot of a stop's locals/arguments for a conditional-breakpoint predicate.</summary>
internal sealed class EvalContext : IEvalContext
{
    public EvalContext(IReadOnlyList<LocalValue> locals, IReadOnlyList<ArgumentValue> arguments)
    {
        Locals = locals;
        Arguments = arguments;
    }

    public IReadOnlyList<LocalValue> Locals { get; }
    public IReadOnlyList<ArgumentValue> Arguments { get; }
}
