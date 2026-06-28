namespace SkyOmega.DrHook.Engine;

/// <summary>Which channel a <see cref="DebugStateDelta"/> carries — the four facets of
/// <see cref="IDebugEventSink"/> unified into one ordered stream.</summary>
public enum DebugStateDeltaKind
{
    /// <summary>An ICorDebug lifecycle callback, identified by name (<see cref="IDebugEventSink.OnEvent"/>) —
    /// e.g. "CreateProcess", "LoadModule", "Breakpoint", "StepComplete". This is the position-change signal:
    /// a connected view re-requests a <see cref="DebugStateSnapshot"/> when one arrives.</summary>
    Event,

    /// <summary>A logpoint record or condition fault (<see cref="IDebugEventSink.OnLog"/>).</summary>
    Log,

    /// <summary>A substrate-correctness anomaly (<see cref="IDebugEventSink.OnAnomaly"/>).</summary>
    Anomaly,

    /// <summary>A chunk of LAUNCHED-debuggee console output (<see cref="IDebugEventSink.OnConsoleOutput"/>).</summary>
    Console,

    /// <summary>The operator's verbatim stated hypothesis at a navigation or inspection step
    /// (<see cref="IDebugEventSink.OnHypothesis"/>) — the prediction half of the (hypothesis, observation)
    /// braid (ADR-012 D4 / Phase 3). Append-only: a correction is a later delta, never a mutation.</summary>
    Hypothesis,
}

/// <summary>One element of the live "delta stream" face of the ADR-012 surface-agnostic model — a single
/// envelope over the four <see cref="IDebugEventSink"/> channels so the WHOLE event stream is one ordered,
/// serializable sequence (Phase 2 publishes it over the transport; a connected view interleaves it after the
/// <see cref="DebugStateSnapshot"/> to stay current). Exactly one payload field is non-null, selected by
/// <see cref="Kind"/>. <see cref="At"/> is the channel record's own timestamp where it has one
/// (log / anomaly / console); for a bare <see cref="IDebugEventSink.OnEvent"/> name — which carries no
/// timestamp of its own — it is the receipt time stamped by the tap.</summary>
public sealed record DebugStateDelta(
    DateTimeOffset At,
    DebugStateDeltaKind Kind,
    string? EventName = null,
    LogRecord? Log = null,
    EngineAnomaly? Anomaly = null,
    ConsoleOutputRecord? Console = null,
    HypothesisRecord? Hypothesis = null)
{
    /// <summary>A lifecycle-event delta. <paramref name="at"/> is the receipt time (OnEvent has no timestamp).</summary>
    public static DebugStateDelta ForEvent(DateTimeOffset at, string name) => new(at, DebugStateDeltaKind.Event, EventName: name);

    /// <summary>A log delta, timestamped by the record's own <see cref="LogRecord.TimestampUtc"/>.</summary>
    public static DebugStateDelta ForLog(LogRecord record) => new(record.TimestampUtc, DebugStateDeltaKind.Log, Log: record);

    /// <summary>An anomaly delta, timestamped by the record's own <see cref="EngineAnomaly.CapturedAt"/>.</summary>
    public static DebugStateDelta ForAnomaly(EngineAnomaly anomaly) => new(anomaly.CapturedAt, DebugStateDeltaKind.Anomaly, Anomaly: anomaly);

    /// <summary>A console delta, timestamped by the record's own <see cref="ConsoleOutputRecord.CapturedAt"/>.</summary>
    public static DebugStateDelta ForConsole(ConsoleOutputRecord record) => new(record.CapturedAt, DebugStateDeltaKind.Console, Console: record);

    /// <summary>A hypothesis delta — the operator's verbatim prediction, timestamped by the record's own
    /// <see cref="HypothesisRecord.CapturedAt"/>. The observation it predicts is the snapshot / delta that
    /// follows it in the stream (per-stop pairing by timeline position).</summary>
    public static DebugStateDelta ForHypothesis(HypothesisRecord record) => new(record.CapturedAt, DebugStateDeltaKind.Hypothesis, Hypothesis: record);
}

/// <summary>Which kind of prediction a <see cref="HypothesisRecord"/> carries — the lens a view uses to pair
/// and nest it. <see cref="Navigation"/> predicts WHERE execution goes (stated on continue / step / launch /
/// attach / pause); it pairs with the resulting stop. <see cref="Inspection"/> predicts the CONTENTS of a
/// value (stated on locals / expand); it nests under the current stop — the richest reasoning-window
/// (ADR-012 Phase 3).</summary>
public enum HypothesisLens
{
    Navigation,
    Inspection,
}

/// <summary>The operator's verbatim stated hypothesis at a debug step — the prediction half of the
/// (hypothesis, observation) braid (ADR-012 D4 / Phase 3). Carried into the engine as a sink event
/// (<see cref="IDebugEventSink.OnHypothesis"/>) from the MCP boundary, where every state-changing /
/// inspecting tool already takes a <c>hypothesis</c>. <see cref="Text"/> is verbatim — the operator's exact
/// words; the point is to observe the reasoning, not a paraphrase. Immutable / append-only: a correction is
/// a NEW record, never an edit (editing would erase the wrong→right repair, which is the learning signal).</summary>
public sealed record HypothesisRecord(DateTimeOffset CapturedAt, string Text, HypothesisLens Lens);
