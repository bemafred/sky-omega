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

    /// <summary>The pump worker's <c>_resumeHandler</c> threw an unhandled exception. Probe 44
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
