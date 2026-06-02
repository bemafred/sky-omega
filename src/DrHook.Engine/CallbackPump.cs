// The continue-loop with stopping-event control (ADR-006 Phase 2; netcoredbg CallbacksQueue
// pattern, finding 12).
//
// ICorDebug callbacks arrive one at a time on mscordbi's event thread, and each synchronizes
// (stops) the debuggee until the debugger calls Continue. The callback thunks enqueue + return
// S_OK; a dedicated worker thread drains the queue:
//
//   - INFORMATIONAL callbacks (CreateThread, LoadModule, …) are surfaced to the user sink and
//     auto-continued — the firehose the continue-loop validated in increment 1.
//   - STOPPING callbacks (breakpoint hit, step complete, Debugger.Break) are NOT auto-continued.
//     The worker publishes a StopInfo, leaves the debuggee SYNCHRONIZED (frozen for inspection),
//     and blocks until the caller resumes. Resume carries a ResumeKind: plain Continue, or a
//     step — for which the resume handler creates an ICorDebugStepper on _stopThread (captured
//     here) before the Continue, and the step's completion arrives as a StepComplete stop.
//
// CallbackPump is the IManagedCallbackSink the host enqueues into; the user's IDebugEventSink
// receives the informational stream from the worker thread. Stop/resume form a rendezvous over
// two blocking queues so the single worker remains the only caller of Continue.

using System.Collections.Concurrent;

namespace SkyOmega.DrHook.Engine;

/// <summary>How the caller resumes a stopped debuggee: a plain continue, or a step (whose
/// completion arrives as a <see cref="StopReason.Step"/> stop).</summary>
internal enum ResumeKind { Continue, StepInto, StepOver, StepOut }

internal sealed class CallbackPump : IManagedCallbackSink, IDisposable
{
    private readonly BlockingCollection<CallbackEvent> _events = new();
    private readonly BlockingCollection<ResumeKind> _resume = new();
    private readonly BlockingCollection<StopInfo> _stops = new();
    private readonly IDebugEventSink _userSink;
    private Func<ResumeKind, nint, int>? _resumeHandler;
    private Action? _pauseHandler;
    private Func<nint, bool>? _moduleHoldHandler; // ADR-011 Layer 2: one-shot entry-module hold on launch (null = no hold)
    private Thread? _worker;
    private nint _stopThread;            // ICorDebugThread at the current stop — handed to the resume handler (for stepping)
    private nint _lastBreakpointPointer; // ICorDebugBreakpoint at the most recent BreakpointHit (0 otherwise) — for caller-thread policy lookup
    private int _disposed;               // 0 = live, 1 = disposed; atomic via Interlocked.Exchange (ENG-CP-1)

    public CallbackPump(IDebugEventSink userSink) => _userSink = userSink;

    /// <summary>The ICorDebugThread at the current stop (0 if never stopped). Set by the worker
    /// before it publishes the stop; visible to a caller after <see cref="WaitForStop"/> returns
    /// (the stop-queue hand-off establishes the happens-before). For inspection (frames/vars).</summary>
    public nint StopThread => _stopThread;

    /// <summary>The ICorDebugBreakpoint pointer for the most recent <see cref="CallbackKind.BreakpointHit"/>
    /// stop (0 if the most recent stop was not a breakpoint hit). Set by the worker on each BreakpointHit
    /// before the stop is published; visible to a caller after <see cref="WaitForStop"/> returns. Used by
    /// the caller-thread breakpoint-policy evaluation in <see cref="DebugSession.WaitForStop"/> to look up
    /// the entry that fired (ADR-010 Increment 2c, refactored to caller-thread evaluation so policy
    /// conditions can themselves trigger func-eval without deadlocking the pump worker).</summary>
    public nint LastBreakpointPointer => _lastBreakpointPointer;

    /// <summary>Enqueue side — invoked by the callback thunks on mscordbi's event thread.
    /// Returns immediately so the event thread is never blocked.</summary>
    public void OnCallback(CallbackKind kind, string name, nint appDomain, nint thread, int detail, nint breakpoint = 0)
    {
        try
        {
            _events.Add(new CallbackEvent(kind, name, appDomain, thread, detail, breakpoint));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutdown race: the queue stopped accepting or was disposed while a late callback
            // was still arriving. Drop it — the session is tearing down.
            // EA capture (LateCallback): surface the rate/kind so detach-contract violations
            // (C-DRAIN-CB / C-DRAIN-EXIT per finding 54) become visible rather than silent.
            _userSink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.LateCallback, "mscordbi", "OnCallback",
                Observed: $"{ex.GetType().Name} after queue {(ex is ObjectDisposedException ? "disposed" : "completed")}",
                Expected: "queue accepting Adds (session live)",
                Context: new Dictionary<string, string> { ["callbackKind"] = kind.ToString(), ["callbackName"] = name }));
        }
    }

    /// <summary>Begin draining. Call once the process controller exists (after
    /// DebugActiveProcess). <paramref name="resume"/> resumes the debuggee for a given
    /// <see cref="ResumeKind"/> and stop thread — for a step it sets up the stepper, then
    /// Continues — and returns the Continue HRESULT. <paramref name="pause"/> synchronizes the
    /// running debuggee (calls <c>ICorDebugController.Stop(0)</c>) when the worker receives a
    /// <see cref="CallbackKind.PauseRequest"/>; both controller operations are owned by this
    /// single worker thread.</summary>
    public void Start(Func<ResumeKind, nint, int> resume, Action pause, Func<nint, bool>? moduleHold = null)
    {
        _resumeHandler = resume;
        _pauseHandler = pause;
        _moduleHoldHandler = moduleHold;
        // Explicit 1 MB maxStackSize — cross-platform substrate consistency (ENG-STK-2).
        // Defends against platform-default variance (Windows 1 MB / macOS secondary 512 KB /
        // Linux 8 MB) and future JIT/inlining variance across CoreCLR releases. Pump work is
        // ~5 KB worst case; the headroom is for user-sink growth.
        _worker = new Thread(Pump, maxStackSize: 1024 * 1024) { IsBackground = true, Name = "DrHook.CallbackPump" };
        _worker.Start();
    }

    /// <summary>Block until the next stop (or process exit), up to <paramref name="timeout"/>.
    /// Returns null on timeout (the debuggee is still running). A <see cref="StopReason.ProcessExited"/>
    /// result means the session is over.</summary>
    public StopInfo? WaitForStop(TimeSpan timeout)
    {
        try
        {
            return _stops.TryTake(out StopInfo? stop, (int)timeout.TotalMilliseconds) ? stop : null;
        }
        catch (ObjectDisposedException)
        {
            return null; // session torn down
        }
    }

    /// <summary>Resume a stopped debuggee. The command is consumed by the worker parked at the
    /// current stop; harmless if called when not stopped (it queues for the next park).</summary>
    public void Resume() => Enqueue(ResumeKind.Continue);

    /// <summary>Step into calls; completion surfaces as a <see cref="StopReason.Step"/> stop.</summary>
    public void StepInto() => Enqueue(ResumeKind.StepInto);

    /// <summary>Step over calls; completion surfaces as a <see cref="StopReason.Step"/> stop.</summary>
    public void StepOver() => Enqueue(ResumeKind.StepOver);

    /// <summary>Step out of the current frame; completion surfaces as a <see cref="StopReason.Step"/> stop.</summary>
    public void StepOut() => Enqueue(ResumeKind.StepOut);

    /// <summary>Request a caller-initiated synchronization (AsyncBreak). The worker calls the
    /// pause handler (<c>ICorDebugController.Stop</c>) and publishes a <see cref="StopReason.Pause"/>
    /// stop; the caller pulls it via <see cref="WaitForStop"/> and resumes via <see cref="Resume"/>
    /// like any other stop.</summary>
    public void RequestPause()
    {
        try
        {
            _events.Add(new CallbackEvent(CallbackKind.PauseRequest, "Pause", 0, 0, 0));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutdown race — pause request dropped.
            _userSink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.LateCallback, "mcp-request", "RequestPause",
                Observed: $"{ex.GetType().Name} after queue {(ex is ObjectDisposedException ? "disposed" : "completed")}",
                Expected: "queue accepting Adds (session live)"));
        }
    }

    private void Enqueue(ResumeKind kind)
    {
        try
        {
            _resume.Add(kind);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Disposed — nothing to resume.
        }
    }

    private void Pump()
    {
        // EA capture (WorkerException, Probe 45 target): wrap the entire pump loop so an
        // exception out of _resumeHandler / _pauseHandler doesn't kill the worker silently.
        // Worker still exits on unhandled exception (session is non-recoverable per finding 53)
        // but the substrate's signal goes out first.
        try
        {
            foreach (CallbackEvent e in _events.GetConsumingEnumerable())
            {
                if (e.Kind == CallbackKind.Informational)
                {
                    _userSink.OnEvent(e.Name);
                    if (e.Name == "ExitProcess")
                    {
                        // The debuggee is gone — clear the stale stop-thread pointer. Otherwise
                        // StopThread keeps returning the last stop's now-dead ICorDebugThread, and a
                        // post-exit GetStackFrames walks it past WalkManagedFrames' (pThread == 0)
                        // guard, dereferencing a released thread → NRE (finding 77).
                        _stopThread = 0;
                        _stops.TryAdd(new StopInfo(StopReason.ProcessExited)); // wake any waiter
                        _resumeHandler!(ResumeKind.Continue, 0);
                    }
                    else if (e.Name == "LoadModule" && _moduleHoldHandler is { } hold && hold(e.Breakpoint))
                    {
                        // ADR-011 Layer 2 hold-gate: hold the process synchronized at the launch entry
                        // assembly's load — modules are loaded but main has not run — so the caller can
                        // arm a launch breakpoint against the entry module. One-shot (the handler
                        // consumes itself). Park like a STOPPING event until the caller resumes.
                        _stopThread = 0;
                        _stops.Add(new StopInfo(StopReason.EntryModuleLoaded));
                        ResumeKind kind;
                        try { kind = _resume.Take(); }
                        catch (InvalidOperationException)
                        {
                            _userSink.OnAnomaly(new EngineAnomaly(
                                DateTimeOffset.UtcNow, AnomalyKind.WorkerSilentBreak, "pump-worker",
                                "Pump (entry-module hold)",
                                Observed: "_resume.Take threw with an EntryModuleLoaded hold pending",
                                Expected: "caller arms the launch breakpoint and resumes"));
                            break;
                        }
                        _resumeHandler!(kind, 0);
                    }
                    else
                    {
                        _resumeHandler!(ResumeKind.Continue, 0);
                    }
                }
                else if (e.Kind == CallbackKind.PauseRequest)
                {
                    // Caller-initiated synchronization. We synchronize the running debuggee here
                    // (the controller's Stop is the sole counterpart of Continue, both owned by this
                    // worker), then surface a Pause stop and park at _resume.Take like any other stop.
                    _pauseHandler!();
                    _stopThread = 0;
                    _stops.Add(new StopInfo(StopReason.Pause));
                    ResumeKind kind;
                    try { kind = _resume.Take(); }
                    catch (InvalidOperationException)
                    {
                        // EA capture (WorkerSilentBreak): a Pause stop was published but the
                        // caller disposed before consuming it. The stop is lost.
                        _userSink.OnAnomaly(new EngineAnomaly(
                            DateTimeOffset.UtcNow, AnomalyKind.WorkerSilentBreak, "pump-worker",
                            "Pump (PauseRequest branch)",
                            Observed: "_resume.Take threw InvalidOperationException with Pause stop pending",
                            Expected: "caller consumes Pause stop and Resumes"));
                        break;
                    }
                    _resumeHandler!(kind, _stopThread);
                }
                else
                {
                    // STOPPING: leave the debuggee synchronized, surface the stop, and block until
                    // the caller resumes. The process produces no new callbacks while stopped, so
                    // the queue behind this event is empty until we resume. Capture the breakpoint
                    // pointer on BreakpointHit so caller-thread policy lookup (ADR-010 Increment 2c,
                    // caller-thread refactor) can map back to the entry.
                    _stopThread = e.Thread;
                    _lastBreakpointPointer = e.Kind == CallbackKind.BreakpointHit ? e.Breakpoint : 0;

                    StopInfo toPublish = e.Kind == CallbackKind.Exception
                        ? new StopInfo(StopReason.Exception, (ExceptionStopKind)e.Detail)
                        : new StopInfo(MapReason(e.Kind));

                    _stops.Add(toPublish);
                    ResumeKind kind;
                    try { kind = _resume.Take(); }
                    catch (InvalidOperationException)
                    {
                        // EA capture (WorkerSilentBreak): a STOPPING stop was published but the
                        // caller disposed before consuming it. The stop is lost.
                        _userSink.OnAnomaly(new EngineAnomaly(
                            DateTimeOffset.UtcNow, AnomalyKind.WorkerSilentBreak, "pump-worker",
                            "Pump (STOPPING branch)",
                            Observed: "_resume.Take threw InvalidOperationException with stop pending",
                            Expected: "caller consumes stop and Resumes",
                            Context: new Dictionary<string, string> { ["stopReason"] = toPublish.Reason.ToString() }));
                        break;
                    }
                    // For a step, the handler creates the stepper on _stopThread before Continuing;
                    // its completion arrives as a StepComplete stop.
                    _resumeHandler!(kind, _stopThread);
                }
            }
        }
        catch (Exception ex)
        {
            _userSink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.WorkerException, "pump-worker", "Pump",
                Observed: $"{ex.GetType().Name}: {ex.Message}",
                Expected: "loop exits cleanly via CompleteAdding or break",
                Context: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().FullName ?? ex.GetType().Name }));
            // Worker dies; future WaitForStop will time out. Session is non-recoverable.
        }
    }

    private static StopReason MapReason(CallbackKind kind) => kind switch
    {
        CallbackKind.BreakpointHit => StopReason.Breakpoint,
        CallbackKind.StepComplete => StopReason.Step,
        CallbackKind.Break => StopReason.Break,
        CallbackKind.EvalComplete => StopReason.EvalComplete,
        CallbackKind.EvalException => StopReason.EvalException,
        _ => StopReason.Break,
    };

    public void Dispose()
    {
        // Atomic idempotence gate (ENG-CP-1) — concurrent Dispose calls return without
        // racing BlockingCollection.Dispose to an ObjectDisposedException.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _events.CompleteAdding();
        _resume.CompleteAdding(); // unblock a worker parked at a stop (Take throws → loop breaks)
        _worker?.Join(TimeSpan.FromSeconds(2));
        _events.Dispose();
        _resume.Dispose();
        _stops.Dispose();
    }
}
