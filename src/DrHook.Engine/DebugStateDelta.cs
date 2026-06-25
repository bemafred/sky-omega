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
    ConsoleOutputRecord? Console = null)
{
    /// <summary>A lifecycle-event delta. <paramref name="at"/> is the receipt time (OnEvent has no timestamp).</summary>
    public static DebugStateDelta ForEvent(DateTimeOffset at, string name) => new(at, DebugStateDeltaKind.Event, EventName: name);

    /// <summary>A log delta, timestamped by the record's own <see cref="LogRecord.TimestampUtc"/>.</summary>
    public static DebugStateDelta ForLog(LogRecord record) => new(record.TimestampUtc, DebugStateDeltaKind.Log, Log: record);

    /// <summary>An anomaly delta, timestamped by the record's own <see cref="EngineAnomaly.CapturedAt"/>.</summary>
    public static DebugStateDelta ForAnomaly(EngineAnomaly anomaly) => new(anomaly.CapturedAt, DebugStateDeltaKind.Anomaly, Anomaly: anomaly);

    /// <summary>A console delta, timestamped by the record's own <see cref="ConsoleOutputRecord.CapturedAt"/>.</summary>
    public static DebugStateDelta ForConsole(ConsoleOutputRecord record) => new(record.CapturedAt, DebugStateDeltaKind.Console, Console: record);
}
