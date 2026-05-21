// The continue-loop (ADR-006 Phase 2; netcoredbg's CallbacksQueue pattern, finding 12).
//
// ICorDebug callbacks are delivered one at a time on mscordbi's event thread, and each
// synchronizes (stops) the debuggee — the runtime will not dispatch the next callback until
// the debugger calls ICorDebugController.Continue. Calling Continue reentrantly from inside
// the callback is fragile, so we decouple: the callback (on the event thread) ENQUEUES the
// event and returns S_OK immediately; a dedicated worker thread drains the queue, surfaces
// each event to the user's sink, then calls Continue to release the process for the next
// callback. This is the foundation for stepping — a "stopping" event (breakpoint hit, step
// complete) will later skip the Continue and surface a stop to the caller (Phase 2 increment 2).
//
// CallbackPump is itself the IDebugEventSink handed to ManagedCallbackHost (its OnEvent is the
// enqueue, called on the event thread); the user's sink is invoked from the worker thread.

using System.Collections.Concurrent;

namespace SkyOmega.DrHook.Engine;

internal sealed class CallbackPump : IDebugEventSink, IDisposable
{
    private readonly BlockingCollection<string> _events = new();
    private readonly IDebugEventSink _userSink;
    private Func<int>? _continue;
    private Thread? _worker;
    private bool _disposed;

    public CallbackPump(IDebugEventSink userSink) => _userSink = userSink;

    /// <summary>Enqueue side — invoked by the callback thunks on mscordbi's event thread.
    /// Returns immediately so the event thread is not blocked.</summary>
    public void OnEvent(string name)
    {
        try
        {
            _events.Add(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutdown race: the queue stopped accepting (CompleteAdding) or was disposed while
            // a late callback was still arriving on mscordbi's event thread. Drop it — the
            // session is tearing down and will detach from its current state.
        }
    }

    /// <summary>Begin draining. Call once the process controller exists (after
    /// DebugActiveProcess). <paramref name="continueProcess"/> resumes the debuggee so the
    /// next callback can fire.</summary>
    public void Start(Func<int> continueProcess)
    {
        _continue = continueProcess;
        _worker = new Thread(Pump) { IsBackground = true, Name = "DrHook.CallbackPump" };
        _worker.Start();
    }

    private void Pump()
    {
        // GetConsumingEnumerable blocks until an event is available and ends when
        // CompleteAdding is called and the queue drains. IDebugEventSink.OnEvent must not
        // throw (interface contract) — a throwing user sink will terminate the loop.
        foreach (string name in _events.GetConsumingEnumerable())
        {
            _userSink.OnEvent(name);
            _continue!();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _events.Dispose();
    }
}
