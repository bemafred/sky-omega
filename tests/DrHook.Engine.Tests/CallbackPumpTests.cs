// In-process tests for the continue-loop with stopping control (ADR-006 Phase 2). The pump's
// contract is producer/consumer plumbing independent of mscordbi, so the test plays both
// roles: it enqueues callbacks the way the host thunks would (via IManagedCallbackSink) and
// supplies the Continue delegate the way DebugSession wires controller.Continue.
//
// Two behaviors are covered: INFORMATIONAL callbacks drain + auto-continue one-for-one (the
// increment-1 firehose), and STOPPING callbacks (breakpoint/step/break) suppress the Continue,
// surface a StopInfo via WaitForStop, and resume only on Resume — the increment-2 keystone.
//
// Determinism: Dispose() completes both queues and Joins the worker, and GetConsumingEnumerable
// drains every buffered item before ending, so once Dispose returns the worker has provably
// processed the queue and the Join barrier publishes its writes. No sleeps for the drain tests;
// the stopping tests SpinUntil on a bounded deadline for the cross-thread resume.

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

        pump.OnCallback(CallbackKind.Informational, "CreateProcess", 0, 0, 0);
        pump.OnCallback(CallbackKind.Informational, "CreateAppDomain", 0, 0, 0);
        pump.OnCallback(CallbackKind.Informational, "LoadModule", 0, 0, 0);

        pump.Start((_, _) => 0, () => { });
        pump.Dispose();

        Assert.Equal(new[] { "CreateProcess", "CreateAppDomain", "LoadModule" }, sink.Events.ToArray());
    }

    [Fact]
    public void Pump_DrainsEventsEnqueuedAfterStart()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        pump.Start((_, _) => 0, () => { });
        pump.OnCallback(CallbackKind.Informational, "LoadModule", 0, 0, 0);
        pump.OnCallback(CallbackKind.Informational, "LoadClass", 0, 0, 0);
        pump.Dispose();

        Assert.Equal(new[] { "LoadModule", "LoadClass" }, sink.Events.ToArray());
    }

    [Fact]
    public void Pump_AutoContinues_InformationalEvents_OncePerEvent()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int continues = 0;
        pump.OnCallback(CallbackKind.Informational, "CreateProcess", 0, 0, 0);
        pump.OnCallback(CallbackKind.Informational, "CreateThread", 0, 0, 0);
        pump.OnCallback(CallbackKind.Informational, "LoadModule", 0, 0, 0);

        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { });
        pump.Dispose();

        Assert.Equal(3, continues);
        Assert.Equal(3, sink.Events.Count);
    }

    [Fact]
    public void StoppingCallback_SuppressesContinue_AndSurfacesStop_UntilResume()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int continues = 0;
        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { });

        // A breakpoint hit must NOT be auto-continued — the worker parks at the stop.
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0x1234, 0);

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2));
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Breakpoint, stop!.Reason);
        Assert.Equal(0, Volatile.Read(ref continues));      // parked — not resumed
        Assert.DoesNotContain("Breakpoint", sink.Events);   // a stop, not part of the informational firehose

        pump.Resume();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 1, TimeSpan.FromSeconds(2)),
            "worker should Continue exactly once after Resume");
    }

    [Fact]
    public void StepResume_RoutesKindAndStopThread_ToTheResumeHandler()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        ResumeKind seenKind = ResumeKind.Continue;
        nint seenThread = 0;
        pump.Start((kind, thread) => { seenKind = kind; Volatile.Write(ref seenThread, thread); return 0; }, () => { });

        // Park at a stop carrying a specific thread pointer, then step.
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0xABCD, 0);
        Assert.NotNull(pump.WaitForStop(TimeSpan.FromSeconds(2)));

        pump.StepOver();

        Assert.True(SpinWait.SpinUntil(() => seenKind == ResumeKind.StepOver, TimeSpan.FromSeconds(2)),
            "the step kind should reach the resume handler");
        Assert.Equal((nint)0xABCD, Volatile.Read(ref seenThread)); // the stop thread is handed to the handler (for the stepper)
    }

    [Fact]
    public void RequestPause_CallsPauseHandler_SurfacesPauseStop_AndResumeContinuesOnce()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int pauseCalls = 0;
        int continues = 0;
        pump.Start(
            (_, _) => { Interlocked.Increment(ref continues); return 0; },
            () => Interlocked.Increment(ref pauseCalls));

        pump.RequestPause();

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2));
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Pause, stop!.Reason);
        Assert.Equal(1, Volatile.Read(ref pauseCalls));       // pause handler fired
        Assert.Equal(0, Volatile.Read(ref continues));        // not yet resumed

        pump.Resume();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 1, TimeSpan.FromSeconds(2)),
            "worker should Continue exactly once after Resume from the Pause stop");
    }

    [Fact]
    public void ExitProcess_WakesAWaiter_WithProcessExited()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        pump.Start((_, _) => 0, () => { });
        pump.OnCallback(CallbackKind.Informational, "ExitProcess", 0, 0, 0);

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2));
        Assert.NotNull(stop);
        Assert.Equal(StopReason.ProcessExited, stop!.Reason);
    }

    [Fact]
    public void WaitForStop_ReturnsNull_WhenNothingStops()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });

        // Only informational traffic — no stop should ever surface.
        pump.OnCallback(CallbackKind.Informational, "LoadModule", 0, 0, 0);

        Assert.Null(pump.WaitForStop(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void OnCallback_AfterDispose_IsDroppedWithoutThrowing()
    {
        var sink = new RecordingSink();
        var pump = new CallbackPump(sink);

        pump.Start((_, _) => 0, () => { });
        pump.Dispose();

        pump.OnCallback(CallbackKind.Informational, "ExitProcess", 0, 0, 0);

        Assert.DoesNotContain("ExitProcess", sink.Events);
    }
}
