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
        public List<EngineAnomaly> Anomalies { get; } = new();
        public void OnEvent(string name) { lock (Events) Events.Add(name); }
        public void OnAnomaly(EngineAnomaly anomaly) { lock (Anomalies) Anomalies.Add(anomaly); }
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

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
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

        // The handler runs on the pump's worker thread. Kind and thread are published together, as one object,
        // so the test never sees the kind of a call without its thread (two separate writes raced on arm64).
        Tuple<ResumeKind, nint>? seen = null;
        pump.Start((kind, thread) => { Volatile.Write(ref seen, Tuple.Create(kind, thread)); return 0; }, () => { });

        // Park at a stop carrying a specific thread pointer, then step.
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0xABCD, 0);
        Assert.NotNull(pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        pump.StepOver();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref seen)?.Item1 == ResumeKind.StepOver, TimeSpan.FromSeconds(2)),
            "the step kind should reach the resume handler");
        Assert.Equal((nint)0xABCD, Volatile.Read(ref seen)!.Item2); // the stop thread is handed to the handler (for the stepper)
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

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Pause, stop!.Reason);
        Assert.Equal(1, Volatile.Read(ref pauseCalls));       // pause handler fired
        Assert.Equal(0, Volatile.Read(ref continues));        // not yet resumed

        pump.Resume();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 1, TimeSpan.FromSeconds(2)),
            "worker should Continue exactly once after Resume from the Pause stop");
    }

    // ADR-016 6.3: Abandon kills a target held at a stop without asking for a pause, which would queue behind that stop.
    // It reads IsParked: true from the moment a stop is published until the command that resumes it, at every kind of
    // stop, and false while the debuggee runs.
    [Fact]
    public void IsParked_HoldsFromAStopsPublicationUntilItsResume()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        int continues = 0;
        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { });

        Assert.False(pump.IsParked); // running: nothing published

        pump.OnCallback(CallbackKind.Informational, "LoadModule", 0, 0, 0);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 1, TimeSpan.FromSeconds(2)));
        Assert.False(pump.IsParked); // an informational callback is continued, never held

        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0xABCD, 0);
        Assert.NotNull(pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(pump.IsParked); // a caller that has taken the stop reads it held

        pump.Resume();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 2, TimeSpan.FromSeconds(2)));
        Assert.False(pump.IsParked);

        pump.RequestPause();
        Assert.Equal(StopReason.Pause, pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)?.Reason);
        Assert.True(pump.IsParked); // a Pause stop holds it the same way

        pump.Resume();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 3, TimeSpan.FromSeconds(2)));
        Assert.False(pump.IsParked);
    }

    [Fact]
    public void IsParked_IsFalse_OnceDisposeReleasesAParkedWorker()
    {
        var sink = new RecordingSink();
        var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0, 0);
        Assert.NotNull(pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(pump.IsParked);

        // Dispose completes the resume queue: the parked Take throws, and the worker ends. Dispose waits for that only up
        // to its own bound (2 s), and on a loaded runner the worker woke later (macOS x64 CI, 2026-11-21, run
        // 44918126706: the test took 2.25 s and IsParked was still true), so the release is waited for here.
        pump.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !pump.IsParked, TimeSpan.FromSeconds(10)), "The parked worker was not released.");
    }

    // ADR-016 6.6: at a detach the session synchronizes the target with its own Stop, then asks the pump to continue
    // until the detach. The worker continues the stop it is parked at, and every callback that still comes, stopping
    // ones included, without publishing a stop. Until 2026-11-24 the pump was disposed before the Stop, and a callback in
    // between was dropped without a Continue (finding 102).
    [Fact]
    public void ContinueUntilDetached_ContinuesTheParkedStop_AndEveryLaterCallback_PublishingNoStop()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        int continues = 0;
        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { });

        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0xABCD, 0);
        Assert.Equal(StopReason.Breakpoint, pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)?.Reason);
        Assert.True(pump.IsParked);
        Assert.Equal(0, Volatile.Read(ref continues));

        pump.ContinueUntilDetached();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 1 && !pump.IsParked, TimeSpan.FromSeconds(2)),
            "the stop the worker was parked at should be continued, once");

        // A callback that comes before the detach: continued, counted with the events, and no stop is published for it.
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0xABCD, 0);
        pump.OnCallback(CallbackKind.Informational, "CreateThread", 0, 0, 0);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 3, TimeSpan.FromSeconds(2)),
            "each callback after the request should be continued");
        Assert.Null(pump.WaitForStop(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.False(pump.IsParked);
        lock (sink.Events) Assert.Equal(new[] { "Breakpoint", "CreateThread" }, sink.Events.ToArray());
        Assert.Empty(sink.Anomalies);
    }

    // ADR-016 6.6: while the session removes its breakpoints and detaches, the worker binds no pending breakpoint at a
    // module load, holds at none, and serves no pause request; it only continues.
    [Fact]
    public void ContinueUntilDetached_BindsNoBreakpoint_HoldsAtNoModuleLoad_AndServesNoPause()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        int continues = 0, pauses = 0, binds = 0, holds = 0;
        pump.Start(
            (_, _) => { Interlocked.Increment(ref continues); return 0; },
            () => Interlocked.Increment(ref pauses),
            moduleHold: _ => { Interlocked.Increment(ref holds); return true; },
            moduleLoad: _ => Interlocked.Increment(ref binds));

        pump.ContinueUntilDetached();
        pump.OnCallback(CallbackKind.Informational, "LoadModule", 0, 0, 0);
        pump.RequestPause();
        pump.OnCallback(CallbackKind.Informational, "ExitThread", 0, 0, 0);

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref continues) == 2, TimeSpan.FromSeconds(2)),
            "the module load and the thread's exit should each be continued");
        Assert.Null(pump.WaitForStop(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.Equal(0, Volatile.Read(ref binds));
        Assert.Equal(0, Volatile.Read(ref holds));
        Assert.Equal(0, Volatile.Read(ref pauses));
        Assert.Equal(2, Volatile.Read(ref continues));
    }

    // ADR-016 6.4: a CTRL+C or CTRL+BREAK that reaches a debugged Windows target is passed on to it (ControlCTrap is
    // answered S_FALSE), and the operator is told with a ConsoleControlEvent anomaly. The event is continued like any
    // other informational one, and no other event draws the anomaly.
    [Fact]
    public void ControlCTrap_IsReportedAsAConsoleControlEvent_AndContinued()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int continues = 0;
        pump.OnCallback(CallbackKind.Informational, "CreateThread", 0, 0, 0);
        pump.OnCallback(CallbackKind.Informational, "ControlCTrap", 0, 0, 0);
        pump.OnCallback(CallbackKind.Informational, "ExitThread", 0, 0, 0);

        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { });
        pump.Dispose();

        Assert.Equal(3, continues);
        Assert.Equal(new[] { "CreateThread", "ControlCTrap", "ExitThread" }, sink.Events.ToArray());
        EngineAnomaly reported = Assert.Single(sink.Anomalies);
        Assert.Equal(AnomalyKind.ConsoleControlEvent, reported.Kind);
        Assert.Equal("ControlCTrap", reported.Operation);
    }

    [Fact]
    public void ExitProcess_WakesAWaiter_WithProcessExited()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        pump.Start((_, _) => 0, () => { });
        pump.OnCallback(CallbackKind.Informational, "ExitProcess", 0, 0, 0);

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
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

        Assert.Null(pump.WaitForStop(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
    }

    // A debugger worker cancels the wait of the request it runs when its end is asked for (ADR-016 6.7, finding 116).
    // A wait that ignored the token would return null after its minute instead of throwing, so the throw is the evidence;
    // no elapsed time is asserted.
    [Fact]
    public void ACancelledWait_ThrowsOperationCanceled_AndIsNoLongerMarkedWaiting()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });
        using var ending = new CancellationTokenSource();
        ending.CancelAfter(TimeSpan.FromMilliseconds(200));

        Assert.Throws<OperationCanceledException>(() => pump.WaitForStop(TimeSpan.FromMinutes(1), ending.Token));
        Assert.False(pump.IsWaiting);
    }

    [Fact]
    public void AStopThatHasCome_IsReturned_ThoughTheWaitIsCancelled()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0x1234, 0);
        using var ending = new CancellationTokenSource();
        ending.Cancel();

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromMinutes(1), ending.Token);
        Assert.Equal(StopReason.Breakpoint, stop?.Reason);
    }

    [Fact]
    public void ALossThatHasCome_IsReturned_ThoughTheWaitIsCancelled()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });
        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        Assert.True(SpinWait.SpinUntil(() => pump.LostStop is not null, TimeSpan.FromSeconds(2)), "the loss should be recorded");
        using var ending = new CancellationTokenSource();
        ending.Cancel();

        StopInfo? stop = pump.WaitForStop(TimeSpan.FromMinutes(1), ending.Token);
        Assert.Equal(StopReason.DebuggerLost, stop?.Reason);
    }

    // DebuggerError (ICorDebugManagedCallback.DebuggerError): "debugging services have been disabled due to an error";
    // only ICorDebugProcess::GetID may be called afterwards, "all other methods, including ICorDebug::Terminate, should
    // not be called". The resume and pause handlers stand in for the native calls (Continue, Stop): once the loss
    // arrives neither may run again. The error values below are the test's own, not a real CLR error.

    [Fact]
    public void DebuggerError_IsNotContinued_AndNoLaterCallbackIsContinued()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int continues = 0;
        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { });

        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        pump.OnCallback(CallbackKind.Informational, "CreateThread", 0, 0, 0);
        pump.Dispose(); // joins the worker: everything it was going to do is done

        Assert.NotNull(stop);
        Assert.Equal(StopReason.DebuggerLost, stop!.Reason);
        Assert.Equal(0, Volatile.Read(ref continues));        // neither the DebuggerError nor the later callback was continued
        Assert.Contains("DebuggerError", sink.Events);
        Assert.DoesNotContain("CreateThread", sink.Events);   // a later callback is not handled at all
    }

    [Fact]
    public void DebuggerError_CarriesBothCodes_InTheStopAndTheAnomaly()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });

        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.NotNull(stop);
        Assert.Equal(new DebuggerErrorInfo(unchecked((int)0x8013FFFF), 42u), stop!.DebuggerError);
        Assert.True(stop.EndsSession);
        EngineAnomaly anomaly = Assert.Single(sink.Anomalies);
        Assert.Equal(AnomalyKind.DebuggerError, anomaly.Kind);
        Assert.Equal("0x8013FFFF", anomaly.Context!["errorHR"]);
        Assert.Equal("42", anomaly.Context["errorCode"]);
    }

    [Fact]
    public void DebuggerError_WhileParkedAtAStop_TheCallersResumeDoesNotContinue()
    {
        // A command at a stop is what times out into DebuggerError, so the worker is parked at that stop when it comes.
        // The caller's Resume would otherwise be a Continue.
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int continues = 0;
        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { });
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0x1234, 0);
        StopInfo? atBreakpoint = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        StopInfo? lost = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        pump.Resume();
        pump.Dispose(); // joins the worker

        Assert.Equal(StopReason.Breakpoint, atBreakpoint?.Reason);
        Assert.Equal(StopReason.DebuggerLost, lost?.Reason);
        Assert.Equal(0, Volatile.Read(ref continues));
    }

    [Fact]
    public void DebuggerLost_IsReturnedAtOnce_ByEveryLaterWait()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });

        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        StopInfo? first = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        StopInfo? second = pump.WaitForStop(TimeSpan.Zero, TestContext.Current.CancellationToken);
        StopInfo? third = pump.WaitForStop(TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(StopReason.DebuggerLost, first?.Reason);
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Same(first, pump.LostStop);
    }

    [Fact]
    public void DebuggerError_ThenPauseRequest_ThePauseHandlerIsNotCalled()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);

        int pauses = 0;
        pump.Start((_, _) => 0, () => Interlocked.Increment(ref pauses));

        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        StopInfo? lost = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        pump.RequestPause();
        pump.Dispose(); // joins the worker

        Assert.Equal(StopReason.DebuggerLost, lost?.Reason);
        Assert.Equal(0, Volatile.Read(ref pauses)); // Stop is a native call too
    }

    [Fact]
    public void A_SecondDebuggerError_IsNotRecordedAgain()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { });

        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFE), 0, 43);
        StopInfo? stop = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(42u, stop?.DebuggerError?.ErrorCode); // the first error is the one kept
        Assert.Single(sink.Anomalies);
    }

    // The session's own verdict (ADR-016 6.8): the target has ended, as the operating system shows it, and mscordbi has
    // delivered no ExitProcess within 2 s while the pump dispatches. targetEnded stands in for the OS read
    // (DebugSession.TargetHasExited). The bound is the real one, so these tests wait it out.

    [Fact]
    public void ExitNotDelivered_WithinTheBound_TheWaitsCallTheDebuggerLost_AndNothingIsContinuedAfter()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        int continues = 0;
        pump.Start((_, _) => { Interlocked.Increment(ref continues); return 0; }, () => { }, targetEnded: () => true);

        // Short waits, as a driver that shows the target running after 250 ms makes them: the bound runs across them.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        StopInfo? stop = null;
        while (stop is null && clock.Elapsed < TimeSpan.FromSeconds(30))
            stop = pump.WaitForStop(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        TimeSpan calledAt = clock.Elapsed;
        pump.OnCallback(CallbackKind.Informational, "CreateThread", 0, 0, 0);
        pump.OnCallback(CallbackKind.DebuggerError, "DebuggerError", 0, 0, unchecked((int)0x8013FFFF), 0, 42);
        StopInfo? later = pump.WaitForStop(TimeSpan.Zero, TestContext.Current.CancellationToken);
        pump.Dispose(); // joins the worker

        Assert.Equal(StopReason.DebuggerLost, stop?.Reason);
        Assert.True(stop!.ExitNotDelivered);
        Assert.Null(stop.DebuggerError);
        Assert.True(stop.EndsSession);
        // Never before the bound. How soon after it depends on the machine's load: 2.0 to 2.1 s on omega, 5.2 s on a loaded
        // macOS x64 runner (CI 44923993832, 2026-11-21), so the test waits up to 30 s for the verdict and bounds only the
        // early side.
        Assert.True(calledAt >= CallbackPump.ExitDeliveryBound, $"the debugger was called lost after {calledAt}, before the bound");
        Assert.Same(stop, later);                        // the first loss is the one kept: a later DebuggerError does not replace it
        Assert.Same(stop, pump.LostStop);
        Assert.Equal(0, Volatile.Read(ref continues));   // the later callback was not continued
        EngineAnomaly anomaly = Assert.Single(sink.Anomalies);
        Assert.Equal(AnomalyKind.ExitNotDelivered, anomaly.Kind);
        Assert.Equal("2000", anomaly.Context!["boundMs"]);
    }

    [Fact]
    public void ExitProcess_WithinTheBound_IsProcessExited_AndNoLossIsCalledAfterIt()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { }, targetEnded: () => true);

        Assert.Null(pump.WaitForStop(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken)); // the end is seen; the exit has not come yet
        pump.OnCallback(CallbackKind.Informational, "ExitProcess", 0, 0, 0);
        StopInfo? exited = pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        StopInfo? afterwards = pump.WaitForStop(CallbackPump.ExitDeliveryBound + TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Assert.Equal(StopReason.ProcessExited, exited?.Reason);
        Assert.Null(afterwards);      // the exit was delivered, so a wait after it is not a loss
        Assert.Null(pump.LostStop);
        Assert.Empty(sink.Anomalies);
    }

    [Fact]
    public void ATargetHeldAtAStop_IsNotCalledLost_ThoughItHasEnded()
    {
        // mscordbi delivers the exit of a target held at a stop only after the stop's Continue, so a wait meanwhile
        // proves nothing about the debugger.
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        bool ended = false;
        pump.Start((_, _) => 0, () => { }, targetEnded: () => Volatile.Read(ref ended));
        pump.OnCallback(CallbackKind.BreakpointHit, "Breakpoint", 0, 0x1234, 0);
        Assert.Equal(StopReason.Breakpoint, pump.WaitForStop(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)?.Reason);

        Volatile.Write(ref ended, true);
        StopInfo? held = pump.WaitForStop(CallbackPump.ExitDeliveryBound + TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Assert.Null(held);
        Assert.Null(pump.LostStop);
        Assert.Empty(sink.Anomalies);
    }

    [Fact]
    public void ARunningTarget_IsNotCalledLost()
    {
        var sink = new RecordingSink();
        using var pump = new CallbackPump(sink);
        pump.Start((_, _) => 0, () => { }, targetEnded: () => false);

        Assert.Null(pump.WaitForStop(CallbackPump.ExitDeliveryBound + TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
        Assert.Null(pump.LostStop);
        Assert.Empty(sink.Anomalies);
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
