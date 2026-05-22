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
    private Thread? _worker;
    private nint _stopThread;     // ICorDebugThread at the current stop — handed to the resume handler (for stepping)
    private bool _disposed;

    public CallbackPump(IDebugEventSink userSink) => _userSink = userSink;

    /// <summary>The ICorDebugThread at the current stop (0 if never stopped). Set by the worker
    /// before it publishes the stop; visible to a caller after <see cref="WaitForStop"/> returns
    /// (the stop-queue hand-off establishes the happens-before). For inspection (frames/vars).</summary>
    public nint StopThread => _stopThread;

    /// <summary>Enqueue side — invoked by the callback thunks on mscordbi's event thread.
    /// Returns immediately so the event thread is never blocked.</summary>
    public void OnCallback(CallbackKind kind, string name, nint appDomain, nint thread, int detail)
    {
        try
        {
            _events.Add(new CallbackEvent(kind, name, appDomain, thread, detail));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutdown race: the queue stopped accepting or was disposed while a late callback
            // was still arriving. Drop it — the session is tearing down.
        }
    }

    /// <summary>Begin draining. Call once the process controller exists (after
    /// DebugActiveProcess). <paramref name="resume"/> resumes the debuggee for a given
    /// <see cref="ResumeKind"/> and stop thread — for a step it sets up the stepper, then
    /// Continues — and returns the Continue HRESULT.</summary>
    public void Start(Func<ResumeKind, nint, int> resume)
    {
        _resumeHandler = resume;
        _worker = new Thread(Pump) { IsBackground = true, Name = "DrHook.CallbackPump" };
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
        foreach (CallbackEvent e in _events.GetConsumingEnumerable())
        {
            if (e.Kind == CallbackKind.Informational)
            {
                _userSink.OnEvent(e.Name);
                if (e.Name == "ExitProcess")
                    _stops.TryAdd(new StopInfo(StopReason.ProcessExited)); // wake any waiter
                _resumeHandler!(ResumeKind.Continue, 0);
            }
            else
            {
                // STOPPING: leave the debuggee synchronized, surface the stop, and block until
                // the caller resumes. The process produces no new callbacks while stopped, so
                // the queue behind this event is empty until we resume.
                _stopThread = e.Thread;
                _stops.Add(e.Kind == CallbackKind.Exception
                    ? new StopInfo(StopReason.Exception, (ExceptionStopKind)e.Detail)
                    : new StopInfo(MapReason(e.Kind)));
                ResumeKind kind;
                try { kind = _resume.Take(); }
                catch (InvalidOperationException) { break; } // disposed while parked at a stop
                // For a step, the handler creates the stepper on _stopThread before Continuing;
                // its completion arrives as a StepComplete stop.
                _resumeHandler!(kind, _stopThread);
            }
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
        if (_disposed) return;
        _disposed = true;
        _events.CompleteAdding();
        _resume.CompleteAdding(); // unblock a worker parked at a stop (Take throws → loop breaks)
        _worker?.Join(TimeSpan.FromSeconds(2));
        _events.Dispose();
        _resume.Dispose();
        _stops.Dispose();
    }
}
