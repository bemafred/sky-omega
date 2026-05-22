namespace SkyOmega.DrHook.Engine;

/// <summary>What a breakpoint hit does once its gates pass: surface a stop the caller controls
/// (<see cref="All"/>), or log-and-keep-running without surfacing (<see cref="None"/>). The axis
/// that turns a breakpoint into a logpoint (finding 33). Per-thread suspend is a future variant.</summary>
public enum SuspendPolicy
{
    /// <summary>Surface the stop — the debuggee stays synchronized for inspection until resumed.</summary>
    All,
    /// <summary>Don't surface — auto-resume after running the policy's action (the logpoint corner).</summary>
    None,
}

/// <summary>How a hit count is compared to a threshold (finding 33). Doubles as logpoint sampling.</summary>
public enum HitCountMode
{
    /// <summary>Fire only on exactly the Nth hit.</summary>
    Equals,
    /// <summary>Fire on the Nth hit and every hit after.</summary>
    AtLeast,
    /// <summary>Fire on every Nth hit (a multiple of N).</summary>
    Multiple,
}

/// <summary>A hit-count gate: fire only when the running hit count satisfies <see cref="Mode"/> /
/// <see cref="Value"/>. Maps to VS "Hit Count", VS Code <c>hitCondition</c>, Rider "pass count".</summary>
public sealed record HitCountGate(HitCountMode Mode, int Value)
{
    /// <summary>Whether a hit at the 1-based <paramref name="hitCount"/> passes this gate.</summary>
    public bool Admits(int hitCount) => Mode switch
    {
        HitCountMode.Equals => hitCount == Value,
        HitCountMode.AtLeast => hitCount >= Value,
        HitCountMode.Multiple => Value > 0 && hitCount % Value == 0,
        _ => true,
    };
}

/// <summary>A structured log event emitted by a logpoint (or a condition fault) — the engine produces
/// these and hands them to <see cref="IDebugEventSink.OnLog"/>; the host chooses the destination
/// (ring buffer / file / Mercury), per finding 35. <see cref="IsFault"/> marks a diagnostic (a
/// condition that couldn't be evaluated) rather than ordinary logpoint output.</summary>
public sealed record LogRecord(DateTimeOffset TimestampUtc, string Message, bool IsFault = false);

/// <summary>How a breakpoint hit is handled — the four-axis model of finding 33 as one composed
/// object: GATES (<see cref="Condition"/>, <see cref="HitCount"/>; all must pass to fire), an ACTION
/// (<see cref="LogMessage"/> — render + emit when set), and a SUSPEND policy (<see cref="Suspend"/>).
/// A conditional breakpoint and a logpoint are two configurations of this one type — composed
/// capabilities, not a mode flag:
/// <list type="bullet">
///   <item>conditional breakpoint = <c>Condition</c> + <c>Suspend.All</c>;</item>
///   <item>logpoint = <c>LogMessage</c> + <c>Suspend.None</c>;</item>
///   <item>conditional logpoint = <c>Condition</c> + <c>LogMessage</c> + <c>Suspend.None</c>;</item>
///   <item>log-and-break = <c>LogMessage</c> + <c>Suspend.All</c>.</item>
/// </list>
/// The <see cref="Condition"/> and <see cref="LogMessage"/> delegates are evaluated against a snapshot
/// of the frame; the C#-expression front end (Roslyn) lives above the engine. A condition that THROWS
/// is treated as "couldn't evaluate" — it surfaces a <see cref="StopReason.ConditionError"/> rather
/// than being silently treated as false (finding 35).</summary>
public sealed record BreakpointPolicy(
    Func<IEvalContext, bool>? Condition = null,
    HitCountGate? HitCount = null,
    Func<IEvalContext, string>? LogMessage = null,
    SuspendPolicy Suspend = SuspendPolicy.All);
