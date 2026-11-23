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
    private Action<nint>? _moduleLoadHandler;     // full pending-breakpoints: bind pending breakpoints on each LoadModule (null = none)
    private Thread? _worker;
    private nint _stopThread;            // ICorDebugThread at the current stop — handed to the resume handler (for stepping)
    private nint _lastBreakpointPointer; // ICorDebugBreakpoint at the most recent BreakpointHit (0 otherwise) — for caller-thread policy lookup
    private int _disposed;               // 0 = live, 1 = disposed; atomic via Interlocked.Exchange (ENG-CP-1)
    private StopInfo? _lostStop;         // set once, when DebuggerError arrives or the session calls its debugger lost; never cleared
    private int _parked;                 // 1 while the worker holds the debuggee at a published stop (TakeResume)

    /// <summary>True while the worker holds the debuggee at a stop it has published and waits for the command that
    /// resumes it: the process is synchronized, and no callback is being dispatched. A pause requested meanwhile queues
    /// behind the stop and is not served until the stop is resumed, so a caller that must know whether the debuggee
    /// is stopped now (Abandon, ADR-016 6.3) reads this instead of asking for a pause.</summary>
    public bool IsParked => Volatile.Read(ref _parked) == 1;

    // Publish a stop and park at it until the caller's command arrives. The debuggee is held from the moment its callback
    // came, so the flag is raised before the stop is visible: a caller that has taken the stop never reads the worker as
    // not parked. Take throws InvalidOperationException once Dispose completes the queue; the callers handle that as
    // before.
    private ResumeKind ParkAt(StopInfo stop)
    {
        Volatile.Write(ref _parked, 1);
        try
        {
            _stops.Add(stop);
            return _resume.Take();
        }
        finally { Volatile.Write(ref _parked, 0); }
    }

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

    /// <summary>The <see cref="StopReason.DebuggerLost"/> stop once mscordbi has sent <c>DebuggerError</c>, else null.
    /// Set once by the worker and never cleared: from then on the debugger is unusable for this process, and no native
    /// call may be made on it (only <c>ICorDebugProcess::GetID</c> is documented as safe).</summary>
    public StopInfo? LostStop => Volatile.Read(ref _lostStop);

    // Set when mscordbi dispatches the session's CreateProcess callback. Never disposed: a ManualResetEventSlim that
    // was never waited on with a timeout long enough to allocate its kernel handle needs no disposal, and a wait after
    // the pump's Dispose must still work.
    private readonly ManualResetEventSlim _processCreated = new(false);

    /// <summary>Waits until mscordbi has dispatched this session's <c>CreateProcess</c> callback, up to
    /// <paramref name="timeout"/>; true once it has (finding 110). For an attach that callback is the first of the
    /// "fake attach events", and mscordbi dispatches it only after it has finished queueing them all, on its event
    /// thread with the process lock released; a <c>Detach</c> before then can neuter the process under that thread.
    /// A launch gets a real CreateProcess at startup.</summary>
    public bool WaitForProcessCreated(TimeSpan timeout) => _processCreated.Wait(timeout);

    /// <summary>Enqueue side — invoked by the callback thunks on mscordbi's event thread.
    /// Returns immediately so the event thread is never blocked.</summary>
    public void OnCallback(CallbackKind kind, string name, nint appDomain, nint thread, int detail, nint breakpoint = 0, uint errorCode = 0)
    {
        // First, before any disposed check: the teardown waits on this whether or not the pump still runs.
        if (name == "CreateProcess") _processCreated.Set();

        if (kind == CallbackKind.DebuggerError)
        {
            // ICorDebugManagedCallback.DebuggerError: "debugging services have been disabled due to an error". Only
            // ICorDebugProcess::GetID may be called afterwards; "all other methods, including ICorDebug::Terminate,
            // should not be called". That overrides the general rule that each callback must Continue.
            // Published here, on mscordbi's thread, not by the worker: the worker may be parked at a stop (a command at
            // a stop is what times out into DebuggerError) and would not read the queue until the caller resumes, and
            // its resume is a Continue. Managed work only, O(1): no native call.
            var error = new DebuggerErrorInfo(detail, errorCode);
            if (Interlocked.CompareExchange(ref _lostStop, new StopInfo(StopReason.DebuggerLost, DebuggerError: error), null) is not null)
                return; // only the first loss is kept; nothing more is done
            _userSink.OnEvent(name);
            _userSink.OnAnomaly(new EngineAnomaly(
                DateTimeOffset.UtcNow, AnomalyKind.DebuggerError, "mscordbi", "OnCallback (DebuggerError)",
                Observed: $"DebuggerError errorHR 0x{detail:X8}, errorCode {errorCode}: debugging services for the process are disabled",
                Expected: "no DebuggerError (mscordbi keeps debugging services enabled)",
                Context: new Dictionary<string, string>
                {
                    ["errorHR"] = $"0x{detail:X8}",
                    ["errorCode"] = errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));
            WakeOnLoss(new CallbackEvent(kind, name, appDomain, thread, detail, breakpoint, errorCode));
            return;
        }
        try
        {
            _events.Add(new CallbackEvent(kind, name, appDomain, thread, detail, breakpoint, errorCode));
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

    // Once the loss is recorded in _lostStop, wake everything that could otherwise make a native call or wait for one: a
    // caller blocked in WaitForStop, a worker parked at a stop (whose resume would be a Continue; it sees the loss and
    // ends without continuing), and an idle worker (wake is the event that ends its wait). Managed work only, O(1).
    private void WakeOnLoss(CallbackEvent wake)
    {
        try
        {
            _stops.Add(Volatile.Read(ref _lostStop)!);
            _resume.Add(ResumeKind.Continue);
            _events.Add(wake);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Pump already torn down: the loss is still recorded in _lostStop, which is all a later caller reads.
        }
    }

    /// <summary>How long mscordbi is given to deliver <c>ExitProcess</c> once the target has ended, as the operating
    /// system shows it, while the pump dispatches callbacks (ADR-016 6.8). Once the target was gone, <c>ProcessExited</c>
    /// came within 175 ms or not within 30 s (findings 104, 105); within 0 to 138 ms in finding 106, whose worker called
    /// every loss at 2 s and no false one.</summary>
    internal static readonly TimeSpan ExitDeliveryBound = TimeSpan.FromSeconds(2);

    // How often a wait reads the operating system for the target's end.
    private static readonly TimeSpan EndPoll = TimeSpan.FromMilliseconds(100);

    private Func<bool>? _targetEnded; // reads the operating system: has the target ended? Null: the session has no way to know
    private int _exitPublished;       // 1 once the worker has published ProcessExited
    private long _endSeenAt;          // Stopwatch timestamp of the first wait that saw the target ended and nothing delivered; 0 = none

    // The session's own verdict, ADR-016 6.8: the target has ended, as the operating system shows it, and mscordbi has
    // delivered no ExitProcess within ExitDeliveryBound while the pump was dispatching. Finding 105: a request mscordbi
    // started while the target was alive waits for a reply that never comes, and nothing in its PAL notices the death.
    // The debugger is then lost as surely as after DebuggerError, and its teardown would wait in the same request
    // (finding 103), so the loss is recorded the same way and the session makes no further native call. A worker held at
    // a stop is not dispatching: mscordbi waits for its Continue before it delivers the exit, so no verdict is made then.
    // Read on the caller's thread, between waits.
    private StopInfo? LossByUndeliveredExit()
    {
        if (_targetEnded is not { } ended || IsParked || Volatile.Read(ref _exitPublished) == 1 || !ended())
        {
            _endSeenAt = 0;
            return null;
        }
        if (_endSeenAt == 0) { _endSeenAt = System.Diagnostics.Stopwatch.GetTimestamp(); return null; }
        TimeSpan waited = System.Diagnostics.Stopwatch.GetElapsedTime(_endSeenAt);
        if (waited < ExitDeliveryBound) return null;

        if (Interlocked.CompareExchange(ref _lostStop, new StopInfo(StopReason.DebuggerLost, ExitNotDelivered: true), null) is not null)
            return Volatile.Read(ref _lostStop); // a DebuggerError came first; it is the loss kept
        _userSink.OnAnomaly(new EngineAnomaly(
            DateTimeOffset.UtcNow, AnomalyKind.ExitNotDelivered, "mcp-request", "WaitForStop",
            Observed: $"the target has ended, as the operating system shows it, and mscordbi delivered no ExitProcess within {waited.TotalMilliseconds:0} ms",
            Expected: $"ProcessExited within {ExitDeliveryBound.TotalSeconds:0} s of the target's end (findings 104, 106: within 175 ms)",
            Context: new Dictionary<string, string>
            {
                ["waitedMs"] = waited.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                ["boundMs"] = ExitDeliveryBound.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            }));
        WakeOnLoss(new CallbackEvent(CallbackKind.Informational, "ExitNotDelivered", 0, 0, 0));
        return Volatile.Read(ref _lostStop);
    }

    /// <summary>Begin draining. Call once the process controller exists (after
    /// DebugActiveProcess). <paramref name="resume"/> resumes the debuggee for a given
    /// <see cref="ResumeKind"/> and stop thread — for a step it sets up the stepper, then
    /// Continues — and returns the Continue HRESULT. <paramref name="pause"/> synchronizes the
    /// running debuggee (calls <c>ICorDebugController.Stop(0)</c>) when the worker receives a
    /// <see cref="CallbackKind.PauseRequest"/>; both controller operations are owned by this
    /// single worker thread.
    /// <paramref name="targetEnded"/> reads the operating system for the target's end, for the session's own verdict
    /// on a lost debugger (<see cref="ExitDeliveryBound"/>); null when the session has no way to know.</summary>
    public void Start(Func<ResumeKind, nint, int> resume, Action pause, Func<nint, bool>? moduleHold = null, Action<nint>? moduleLoad = null, Func<bool>? targetEnded = null)
    {
        _resumeHandler = resume;
        _pauseHandler = pause;
        _moduleHoldHandler = moduleHold;
        _moduleLoadHandler = moduleLoad;
        _targetEnded = targetEnded;
        // Explicit 1 MB maxStackSize — cross-platform substrate consistency (ENG-STK-2).
        // Defends against platform-default variance (Windows 1 MB / macOS secondary 512 KB /
        // Linux 8 MB) and future JIT/inlining variance across CoreCLR releases. Pump work is
        // ~5 KB worst case; the headroom is for user-sink growth.
        _worker = new Thread(Pump, maxStackSize: 1024 * 1024) { IsBackground = true, Name = "DrHook.CallbackPump" };
        _worker.Start();
    }

    /// <summary>Block until the next stop (or process exit), up to <paramref name="timeout"/>.
    /// Returns null on timeout (the debuggee is still running). A <see cref="StopReason.ProcessExited"/>
    /// result means the session is over. Once the debugger is lost (<see cref="LostStop"/>), returns that stop at
    /// once, every time: nothing else can come. The debugger is lost when mscordbi sends <c>DebuggerError</c>, or when
    /// the target has ended and mscordbi has delivered no <c>ExitProcess</c> within <see cref="ExitDeliveryBound"/>,
    /// which the wait reads from the operating system every <see cref="EndPoll"/>.</summary>
    public StopInfo? WaitForStop(TimeSpan timeout) => WaitForStop(timeout, CancellationToken.None);

    /// <summary>As <see cref="WaitForStop(TimeSpan)"/>, and <paramref name="cancel"/> ends the wait: once it is
    /// cancelled, a wait that finds no stop within its next <see cref="EndPoll"/> throws
    /// <see cref="OperationCanceledException"/>. A stop or a loss that has come is returned first. A debugger worker
    /// cancels the wait of the request it runs when its end is asked for, so that its session ends as drhook_stop ends
    /// it (ADR-016 6.7, finding 116).</summary>
    public StopInfo? WaitForStop(TimeSpan timeout, CancellationToken cancel)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Volatile.Write(ref _waitingChangedAt, started);
        Volatile.Write(ref _waiting, 1);
        try
        {
            return WaitForStopMarked(timeout, started, cancel);
        }
        finally
        {
            Volatile.Write(ref _waiting, 0);
            Volatile.Write(ref _waitingChangedAt, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    private int _waiting;            // 1 while a caller waits in WaitForStop
    private long _waitingChangedAt;  // Stopwatch timestamp of the last start or end of such a wait

    /// <summary>True while a caller waits for the target: for a stop, or for an evaluation's end, each bounded by its
    /// own timeout. Everything else a session does is debugger work. Read from any thread, for drhook-mcp's health
    /// requests (ADR-016 6.8).</summary>
    public bool IsWaiting => Volatile.Read(ref _waiting) == 1;

    /// <summary>The <see cref="System.Diagnostics.Stopwatch"/> timestamp at which the last wait for the target started or
    /// ended; 0 before the first.</summary>
    public long WaitingChangedAt => Volatile.Read(ref _waitingChangedAt);

    private StopInfo? WaitForStopMarked(TimeSpan timeout, long started, CancellationToken cancel)
    {
        while (true)
        {
            if (Volatile.Read(ref _lostStop) is { } lost) return lost;
            TimeSpan remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            TimeSpan slice = remaining < EndPoll ? remaining : EndPoll;
            try
            {
                if (_stops.TryTake(out StopInfo? stop, (int)slice.TotalMilliseconds))
                    return Volatile.Read(ref _lostStop) ?? stop; // a stop taken just as the loss was published yields to it
            }
            catch (ObjectDisposedException)
            {
                return Volatile.Read(ref _lostStop); // session torn down; null unless the debugger was lost
            }
            if (LossByUndeliveredExit() is { } undelivered) return undelivered;
            if (cancel.IsCancellationRequested) throw new OperationCanceledException("The wait for a stop was cancelled before one came.", cancel);
            if (remaining == TimeSpan.Zero) return null;
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
                // The debugger is lost (DebuggerError, published by OnCallback): end without another native call.
                // Checked before every event and again before every handler call below, since the loss can be
                // published on mscordbi's thread at any moment.
                if (Volatile.Read(ref _lostStop) is not null) break;
                if (e.Kind == CallbackKind.Informational)
                {
                    _userSink.OnEvent(e.Name);
                    // ADR-016 6.4: the event was passed on to the target (ManagedCallbackHost answers S_FALSE), and the
                    // operator is told, since what the target does next is its answer to it. Continued below like any
                    // other informational event.
                    if (e.Name == "ControlCTrap")
                        _userSink.OnAnomaly(new EngineAnomaly(
                            DateTimeOffset.UtcNow, AnomalyKind.ConsoleControlEvent, "pump-worker", "ControlCTrap",
                            Observed: "CTRL+C or CTRL+BREAK reached the target while it was debugged; DrHook passed it on to the target",
                            Expected: "the target's own console handlers answer it, or none does and Windows ends the target with exit code 0xC000013A"));
                    if (e.Name == "ExitProcess")
                    {
                        // The debuggee is gone — clear the stale stop-thread pointer. Otherwise
                        // StopThread keeps returning the last stop's now-dead ICorDebugThread, and a
                        // post-exit GetStackFrames walks it past WalkManagedFrames' (pThread == 0)
                        // guard, dereferencing a released thread → NRE (finding 77).
                        _stopThread = 0;
                        Volatile.Write(ref _exitPublished, 1); // before the stop is visible: a waiter that reads none yet makes no verdict
                        _stops.TryAdd(new StopInfo(StopReason.ProcessExited)); // wake any waiter
                        if (Volatile.Read(ref _lostStop) is not null) break;
                        _resumeHandler!(ResumeKind.Continue, 0);
                    }
                    else if (e.Name == "LoadModule")
                    {
                        // Full pending-breakpoints: bind any pending breakpoint whose module just
                        // loaded, before auto-continuing — so a breakpoint in a lazily-loaded module is
                        // armed the instant its module appears, before the process runs past the code.
                        if (Volatile.Read(ref _lostStop) is not null) break;
                        _moduleLoadHandler?.Invoke(e.Breakpoint);
                        // ADR-011 Layer 2 hold-gate: one-shot hold at the launch entry assembly's load
                        // — modules loaded, before main — so the caller can arm breakpoints (bound or
                        // pending) before the process runs. Park like a STOPPING event until resumed.
                        if (_moduleHoldHandler is { } hold && hold(e.Breakpoint))
                        {
                            _stopThread = 0;
                            ResumeKind kind;
                            try { kind = ParkAt(new StopInfo(StopReason.EntryModuleLoaded)); }
                            catch (InvalidOperationException)
                            {
                                _userSink.OnAnomaly(new EngineAnomaly(
                                    DateTimeOffset.UtcNow, AnomalyKind.WorkerSilentBreak, "pump-worker",
                                    "Pump (entry-module hold)",
                                    Observed: "_resume.Take threw with an EntryModuleLoaded hold pending",
                                    Expected: "caller arms the launch breakpoint and resumes"));
                                break;
                            }
                            if (Volatile.Read(ref _lostStop) is not null) break;
                            _resumeHandler!(kind, 0);
                        }
                        else
                        {
                            if (Volatile.Read(ref _lostStop) is not null) break;
                            _resumeHandler!(ResumeKind.Continue, 0);
                        }
                    }
                    else
                    {
                        if (Volatile.Read(ref _lostStop) is not null) break;
                        _resumeHandler!(ResumeKind.Continue, 0);
                    }
                }
                else if (e.Kind == CallbackKind.PauseRequest)
                {
                    // Caller-initiated synchronization. We synchronize the running debuggee here
                    // (the controller's Stop is the sole counterpart of Continue, both owned by this
                    // worker), then surface a Pause stop and park at _resume.Take like any other stop.
                    if (Volatile.Read(ref _lostStop) is not null) break;
                    _pauseHandler!();
                    _stopThread = 0;
                    ResumeKind kind;
                    try { kind = ParkAt(new StopInfo(StopReason.Pause)); }
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
                    if (Volatile.Read(ref _lostStop) is not null) break;
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

                    ResumeKind kind;
                    try { kind = ParkAt(toPublish); }
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
                    if (Volatile.Read(ref _lostStop) is not null) break;
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
