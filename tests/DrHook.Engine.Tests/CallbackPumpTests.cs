// In-process tests for the continue-loop (ADR-006 Phase 2). The pump's contract is pure
// producer/consumer plumbing — independent of mscordbi — so the test plays both roles in
// process: it enqueues events the way the callback thunks would (on the "event thread") and
// supplies the Continue delegate the way DebugSession wires controller.Continue. This is the
// Phase 2 advance over Phase 1: many events flow, and each one is matched by exactly one
// Continue, instead of a single callback that then wedges on a reentrant resume.
//
// Determinism: Dispose() calls CompleteAdding then Joins the worker, and
// GetConsumingEnumerable drains every buffered item before it ends — so once Dispose returns,
// the worker has provably processed the whole queue and the Join barrier publishes its writes
// to the asserting thread. No sleeps, no polling.

using System.Threading;
using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class CallbackPumpTests
{
    private sealed class RecordingSink : IDebugEventSink
    {
        public List<string> Events { get; } = new();
        public void OnEvent(string name) => Events.Add(name);
    }

    [Fact]
    public void Pump_DrainsBacklogEnqueuedBeforeStart_InOrder()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        // Callbacks can arrive between SetManagedHandler and pump.Start — they queue up.
        pump.OnEvent("CreateProcess");
        pump.OnEvent("CreateAppDomain");
        pump.OnEvent("LoadModule");

        pump.Start(() => 0);
        pump.Dispose(); // CompleteAdding + Join: the backlog is fully drained on return.

        Assert.Equal(new[] { "CreateProcess", "CreateAppDomain", "LoadModule" }, sink.Events.ToArray());
    }

    [Fact]
    public void Pump_DrainsEventsEnqueuedAfterStart()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        pump.Start(() => 0);

        // Steady state: events arrive on the event thread while the worker is already draining.
        pump.OnEvent("LoadModule");
        pump.OnEvent("LoadClass");

        pump.Dispose();

        Assert.Equal(new[] { "LoadModule", "LoadClass" }, sink.Events.ToArray());
    }

    [Fact]
    public void Pump_CallsContinue_OncePerEvent()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int continues = 0;
        pump.OnEvent("CreateProcess");
        pump.OnEvent("CreateThread");
        pump.OnEvent("LoadModule");

        pump.Start(() => { Interlocked.Increment(ref continues); return 0; });
        pump.Dispose();

        // Each non-stopping callback must be released so the next one can fire — exactly once.
        Assert.Equal(3, continues);
        Assert.Equal(3, sink.Events.Count);
    }

    [Fact]
    public void OnEvent_AfterDispose_IsDroppedWithoutThrowing()
    {
        var sink = new RecordingSink();
        var pump = new CallbackPump(sink);

        pump.Start(() => 0);
        pump.Dispose();

        // A late callback arriving after shutdown (process detaches from its stopped state)
        // must not throw back into the native event thread.
        pump.OnEvent("ExitProcess");

        Assert.DoesNotContain("ExitProcess", sink.Events);
    }
}
