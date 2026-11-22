// EngineAnomaly — typed-record substrate-anomaly capture, the loop-closing mechanism for
// unknown unknowns (per ADR-007 Phase 1 + feedback_surprises_are_substrate_grade.md).
//
// An EngineAnomaly is structured evidence about a substrate-correctness invariant that wasn't
// upheld. NOT a logger message; NOT a generic exception wrapper. Each Kind is a named substrate
// concept whose appearance in the wild is promotable to probe → finding → fix (the discipline
// loop). The seed sites were identified by findings 53 + 54 + 55:
//
//   - LateCallback             — CallbackPump.OnCallback catch after CompleteAdding/Dispose
//   - WorkerSilentBreak        — CallbackPump.Pump _resume.Take catch (worker exits w/o resume completion)
//   - WorkerException          — CallbackPump.Pump worker died with an unhandled exception
//   - HostRecoveryFailed       — ManagedCallbackHost.HostOf returned null (MCH-1 silent failure)
//   - StartupContextLost       — DbgShim.StartupCallbackThunk null parameter / wrong handle target
//   - UnexpectedHResult        — Quiesce / Detach / Terminate / Marshal.Release non-success HR
//   - DepthClamped             — DebugSession.GetLocals/GetArguments clamped to MaxInspectionDepth
//   - UnexpectedCleanupException — EngineSteppingSession.CleanupSession swallowed an exception
//   - TargetStuckAtDispose     — an owned target still ran when its exit request's bound expired; left running (ADR-016 6.2)
//   - TargetIsHostChild        — Attach/AttachAndOwn target is a direct OS child of the debugger host (finding 87)
//   - UiThreadUnresponsive     — after a visual capture + detach, the target's UI thread did not drain a queued job
//   - GoneTargetAlive          — Detach said the target is gone (terminated / neutered), but it is still running
//   - DebuggerError            — mscordbi disabled debugging services for the process (ICorDebugManagedCallback.DebuggerError)
//
// New kinds are added as new substrate surprises surface. The discipline rule: each kind is a
// named concept, not "Generic / Other" — and each emission site documents what would have to be
// true for the anomaly to NOT fire (the "expected" delta).

using System.Collections.Generic;

namespace SkyOmega.DrHook.Engine;

/// <summary>The taxonomy of substrate-correctness invariants that EngineAnomaly captures.
/// Extensible — each new in-the-wild surprise becomes a new kind via probe + finding (per
/// ADR-007 Phase 1 discipline). Avoid catch-all "Other" entries; if a condition doesn't fit
/// an existing kind, the discipline is to add a named kind for it.</summary>
public enum AnomalyKind
{
    /// <summary>An mscordbi callback arrived after the pump's queue was CompleteAdding'd or
    /// Disposed — caught by <c>CallbackPump.OnCallback</c>'s ObjectDisposedException /
    /// InvalidOperationException catch. Expected when teardown races a late callback; surfaced
    /// so the rate / kind of late callbacks is visible (a high rate suggests detach contract
    /// violation — C-DRAIN-CB / C-DRAIN-EXIT per finding 54).</summary>
    LateCallback,

    /// <summary>The pump worker exited via the <c>_resume.Take()</c> catch — the queue was
    /// completed while the worker was parked at a stop. Expected at clean teardown; anomalous
    /// if it happens with stops pending that the caller never consumed (suggests the caller
    /// disposed without resuming).</summary>
    WorkerSilentBreak,

    /// <summary>The pump worker's <c>_resumeHandler</c> threw an unhandled exception. Probe 45
    /// target. Worker dies and future WaitForStop blocks indefinitely; the anomaly is the
    /// substrate's signal that the session is non-recoverable.</summary>
    WorkerException,

    /// <summary>ManagedCallbackHost.HostOf returned null — GCHandle dereference failed,
    /// indicating MCH-1 (Dispose racing in-flight callback) is active. The callback is dropped.
    /// Capture site requires a static fallback sink (deferred — the substrate state is
    /// compromised when this fires; can't go back through a session-tied sink).</summary>
    HostRecoveryFailed,

    /// <summary>DbgShim.StartupCallbackThunk fired with a null parameter or a GCHandle whose
    /// target was not a StartupContext. DBG-1 territory (Unregister synchronization
    /// undocumented). Capture site requires a static fallback sink (deferred for the same
    /// reason as HostRecoveryFailed).</summary>
    StartupContextLost,

    /// <summary>An ICorDebug call (Stop / Detach / Terminate / Marshal.Release on a refcounted
    /// pointer) returned a non-success HRESULT. Today the engine discards these; with the
    /// anomaly surface, the HRESULT + operation become structured evidence. Caller can decide
    /// whether to escalate or proceed; substrate doesn't suppress the underlying side effects.</summary>
    UnexpectedHResult,

    /// <summary>A caller-supplied inspection depth exceeded <see cref="DebugSession.MaxInspectionDepth"/>
    /// (ENG-STK-1 from finding 55). The depth was clamped to MaxInspectionDepth; the inspection
    /// proceeds with the clamped value. Indicates the caller has incorrect expectations or
    /// pathological input.</summary>
    DepthClamped,

    /// <summary>An exception escaped a substrate cleanup path that swallows-by-design today
    /// (EngineSteppingSession.CleanupSession's <c>try { } catch { }</c> around Kill or Dispose).
    /// With the anomaly surface, the cleanup-suppressed exception becomes diagnostic evidence
    /// rather than silent loss.</summary>
    UnexpectedCleanupException,

    /// <summary>An owned target that Dispose had detached from and asked to exit (SIGTERM, on Windows CTRL+BREAK) was still running when
    /// <c>NaturalExitTimeout</c> expired. It is left running, un-debugged, and reported
    /// (<c>DebugSession.TargetLeftRunning</c>) for the client to end with a deliberate kill: Dispose never kills as
    /// cleanup (ADR-016 6.2; until then ADR-008's Decision 1 escalated to SIGKILL here). The target did not honour
    /// its exit request (an eternal loop, a handler that cancels the signal, deadlocked async work): an actionable
    /// signal about the target, not a fault of the debugger.</summary>
    TargetStuckAtDispose,

    /// <summary>Attach / AttachAndOwn was handed a target whose OS parent is the debugger host itself
    /// (finding 87). The CoreCLR PAL inside the in-process debug components polls
    /// <c>wait4(pid, WNOHANG)</c> for exit detection and REAPS the target when it is our child; if the
    /// host also started it via <see cref="System.Diagnostics.Process.Start()"/>, the BCL's own child
    /// reaper then finds it gone — <c>Process.WaitForExit</c> never observes the exit, and at the next
    /// SIGCHLD the runtime calls <c>Environment.FailFast("Error while reaping child. errno = 10")</c>,
    /// terminating the host. Two reapers on one pid; whichever polls first wins. Not fatal by itself
    /// (a child spawned via posix_spawn is untracked by the BCL and safe), so it is surfaced, not
    /// refused: a host must not debug a target it started via System.Diagnostics.Process.</summary>
    TargetIsHostChild,

    /// <summary>After a <c>drhook_capture_visual</c> session, the target was left running (detach) but its UI
    /// thread did not execute a job queued on its dispatcher within the drain window — the UI thread is HUNG
    /// (managed or native), the 1-in-41 post-capture hang (ADR-012 Q8). Detected by
    /// <see cref="DispatcherDrainSentinel"/> (probe 88: an idle app drains within the first 50 ms poll; managed
    /// and native hangs never drain). Actionable: the app beachballs — sample it (<c>sample &lt;pid&gt; 3</c>)
    /// BEFORE killing it; the stuck stack is the only path to the root cause.</summary>
    UiThreadUnresponsive,

    /// <summary><c>ICorDebugController.Detach</c> returned CORDBG_E_PROCESS_TERMINATED or CORDBG_E_OBJECT_NEUTERED,
    /// both meaning the target is gone, but the target was still running when the OS was asked, after a short
    /// confirmation wait (finding 102: once on zbook, Linux x64, a borrowed detach of a live target drew
    /// TERMINATED and the target ran on). The debugger is disconnected; the target's state is not known to be
    /// right. Actionable: check that the target is running normally, not held.</summary>
    GoneTargetAlive,

    /// <summary>mscordbi sent <c>ICorDebugManagedCallback.DebuggerError</c>: "debugging services have been disabled
    /// due to an error". Documented: only <c>ICorDebugProcess::GetID</c> may be called afterwards, "all other methods,
    /// including ICorDebug::Terminate, should not be called", and the process is ended with operating-system
    /// facilities. The session is lost (<see cref="StopReason.DebuggerLost"/>): it makes no further native call, and
    /// its teardown keeps every reference mscordbi handed it (no <c>Release</c>) and the callback object mscordbi
    /// holds, for the rest of this process. Context: <c>errorHR</c>, <c>errorCode</c>. Actionable: the error codes
    /// name the CLR's reason; an owned target is killed at teardown, a borrowed one is left as it is.</summary>
    DebuggerError,

    /// <summary>A session was detached before mscordbi had dispatched its <c>CreateProcess</c> callback (finding 110):
    /// the teardown waited its bound for it and went on. For an attach, mscordbi queues the "fake attach events" on its
    /// event thread with the process lock released, and a Detach that neuters the process meanwhile crashed the host
    /// once (SIGSEGV in <c>ShimProcess::QueueFakeAssemblyAndModuleEvent</c>). Context: <c>pid</c>, <c>waitMs</c>.
    /// Actionable: the session ended before its target ever synchronized with the debugger; if the host crashes after
    /// this anomaly, the crash is that race.</summary>
    DetachBeforeAttachEvents,

    /// <summary>The target has ended, as the operating system shows it, and mscordbi delivered no <c>ExitProcess</c>
    /// within <see cref="DebugSession.ExitDeliveryBound"/> (2 s) while the debugger was dispatching callbacks (ADR-016
    /// 6.8). Once a target was gone, <c>ProcessExited</c> came within 175 ms or not within 30 s (findings 104, 105): a
    /// request mscordbi made while the target was alive is never answered, and nothing notices the death. The session
    /// calls its debugger lost (<see cref="StopReason.DebuggerLost"/>, <see cref="StopInfo.ExitNotDelivered"/>) and
    /// makes no further native call, as after <see cref="DebuggerError"/>. Context: <c>waitedMs</c>, <c>boundMs</c>.
    /// Actionable: the debugger process cannot debug again safely; drhook-mcp discards its worker.</summary>
    ExitNotDelivered,

    /// <summary>drhook-mcp suspects a debugger worker (ADR-016 6.8): it did not answer a health request within its
    /// bound, its debugger work ran past its bound, or it was not ready, or did not end, in time. Raised by drhook-mcp,
    /// not the worker. Context: <c>workerPid</c>, <c>tool</c>, <c>reason</c>. Actionable: the worker is discarded at
    /// once (<see cref="WorkerDiscarded"/>); a worker that was only slow is lost with its session.</summary>
    WorkerSuspected,

    /// <summary>drhook-mcp discarded a debugger worker (ADR-016 6.7, 6.8): killed it, ended an owned target still alive
    /// under the same identity through the operating system, and left a borrowed one as it was. Context:
    /// <c>workerPid</c>, <c>reason</c>, <c>cause</c> (lost, DebuggerError, ExitNotDelivered or unknown), and the
    /// target's <c>pid</c>, <c>owned</c> and <c>targetState</c> as the operating system shows it afterwards.
    /// Actionable: the session is over; the next drhook_launch or drhook_attach starts a fresh worker.</summary>
    WorkerDiscarded,

    /// <summary>A borrowed target was held at a stop when its worker was discarded. On Linux and macOS it stays held,
    /// alive and frozen, and cannot be debugged again until it restarts (finding 106); on Windows it ended with the
    /// worker. Context: <c>pid</c>, <c>lastState</c>. Actionable: restart the target, or end it.</summary>
    TargetLeftHeld,
}

/// <summary>A structured record of a substrate-correctness anomaly. Surfaced through
/// <see cref="IDebugEventSink.OnAnomaly"/> alongside the existing <c>OnEvent</c> / <c>OnLog</c>
/// channels — same drain pattern, separate concern. Consumers (typically the MCP layer or a
/// test harness) drain anomalies via a buffered sink (see <see cref="BoundedAnomalySink"/>).
///
/// The discipline rule (per <c>feedback_surprises_are_substrate_grade.md</c>): anomalies are NOT
/// generic exception wrappers — each <see cref="AnomalyKind"/> is a named substrate concept with
/// a documented observed-vs-expected delta. New kinds are added when new surprises appear.</summary>
/// <param name="CapturedAt">Wall-clock time of the anomaly's detection (UTC).</param>
/// <param name="Kind">The taxonomic <see cref="AnomalyKind"/>.</param>
/// <param name="Thread">Logical thread of detection: <c>"mscordbi"</c>, <c>"pump-worker"</c>,
/// <c>"mcp-request"</c>, <c>"dbgshim-startup"</c>, <c>"eventpipe"</c>, or another logical name.
/// Critical because finding 53's contracts are cross-thread; an anomaly's meaning depends on
/// which thread observed it.</param>
/// <param name="Operation">What the substrate was trying to do — e.g. <c>"Stop"</c>,
/// <c>"_resumeHandler"</c>, <c>"GetLocals"</c>, <c>"CleanupSession.Kill"</c>.</param>
/// <param name="Observed">What actually happened — the surprise. E.g. <c>"HRESULT 0x80131c12"</c>,
/// <c>"depth=1000 requested"</c>, <c>"exception: InvalidCastException"</c>.</param>
/// <param name="Expected">What should have happened, if known. Empty string if the substrate
/// cannot articulate the expectation (in which case the anomaly is a candidate for promoting
/// to a probe that characterises the expectation).</param>
/// <param name="Context">Kind-specific fields (e.g. for UnexpectedHResult: <c>{"hresult":"0x..."}</c>;
/// for DepthClamped: <c>{"requested":"1000","clamped":"10"}</c>). Null if no extra fields apply.</param>
public sealed record EngineAnomaly(
    DateTimeOffset CapturedAt,
    AnomalyKind Kind,
    string Thread,
    string Operation,
    string Observed,
    string Expected,
    IReadOnlyDictionary<string, string>? Context = null);
