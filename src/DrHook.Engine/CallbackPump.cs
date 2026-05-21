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
//     and blocks until the caller resumes via DebugSession.Resume. This is the keystone for
//     breakpoints and stepping: stepping setup (creating an ICorDebugStepper on _stopThread
//     before the resume-Continue) lands in the next increment.
//
// CallbackPump is the IManagedCallbackSink the host enqueues into; the user's IDebugEventSink
// receives the informational stream from the worker thread. Stop/resume form a rendezvous over
// two blocking queues so the single worker remains the only caller of Continue.

using System.Collections.Concurrent;

namespace SkyOmega.DrHook.Engine;

internal sealed class CallbackPump : IManagedCallbackSink, IDisposable
{
    private enum ResumeAction { Continue }

    private readonly BlockingCollection<CallbackEvent> _events = new();
    private readonly BlockingCollection<ResumeAction> _resume = new();
    private readonly BlockingCollection<StopInfo> _stops = new();
    private readonly IDebugEventSink _userSink;
    private Func<int>? _continue;
    private Thread? _worker;
    private nint _stopThread;     // ICorDebugThread at the current stop — for stepping (next increment)
    private bool _disposed;

    public CallbackPump(IDebugEventSink userSink) => _userSink = userSink;

    /// <summary>Enqueue side — invoked by the callback thunks on mscordbi's event thread.
    /// Returns immediately so the event thread is never blocked.</summary>
    public void OnCallback(CallbackKind kind, string name, nint appDomain, nint thread)
    {
        try
        {
            _events.Add(new CallbackEvent(kind, name, appDomain, thread));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutdown race: the queue stopped accepting or was disposed while a late callback
            // was still arriving. Drop it — the session is tearing down.
        }
    }

    /// <summary>Begin draining. Call once the process controller exists (after
    /// DebugActiveProcess). <paramref name="continueProcess"/> resumes the debuggee.</summary>
    public void Start(Func<int> continueProcess)
    {
        _continue = continueProcess;
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
    public void Resume()
    {
        try
        {
            _resume.Add(ResumeAction.Continue);
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
                _continue!();
            }
            else
            {
                // STOPPING: leave the debuggee synchronized, surface the stop, and block until
                // the caller resumes. The process produces no new callbacks while stopped, so
                // the queue behind this event is empty until we Continue.
                _stopThread = e.Thread;
                _stops.Add(new StopInfo(MapReason(e.Kind)));
                ResumeAction action;
                try { action = _resume.Take(); }
                catch (InvalidOperationException) { break; } // disposed while parked at a stop
                _ = action; // only Continue today; stepping actions added next increment
                _continue!();
            }
        }
    }

    private static StopReason MapReason(CallbackKind kind) => kind switch
    {
        CallbackKind.BreakpointHit => StopReason.Breakpoint,
        CallbackKind.StepComplete => StopReason.Step,
        CallbackKind.Break => StopReason.Break,
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
