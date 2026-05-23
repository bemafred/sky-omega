namespace SkyOmega.DrHook.Engine;

/// <summary>An armed exception filter — registered once via
/// <see cref="DebugSession.ArmExceptionFilter"/> and consulted by <see cref="DebugSession.WaitForStop"/>
/// when an <see cref="StopReason.Exception"/> stop arrives. Non-matching exceptions are auto-resumed
/// silently; matching exceptions surface. With NO filters armed the behavior is unchanged — every
/// exception stop surfaces (backwards-compat with probes 26/27). Filters are AND-compositions of two
/// criteria (type name × phase); having multiple filters armed is OR across them.</summary>
public sealed record ExceptionFilterInfo(int Id, string TypeName, ExceptionStopKind PhaseFilter)
{
    /// <summary>Match wildcard for <see cref="TypeName"/> — accepts any thrown type.</summary>
    public const string AnyType = "*";

    /// <summary>Whether this filter admits an exception whose runtime type is
    /// <paramref name="actualType"/> and was raised at phase <paramref name="actualPhase"/>. Type
    /// match is exact (no subclass walk yet — a future refinement); phase matches when the filter's
    /// <see cref="PhaseFilter"/> is <see cref="ExceptionStopKind.None"/> (wildcard) or equals the
    /// actual phase.</summary>
    public bool Matches(string actualType, ExceptionStopKind actualPhase)
        => (TypeName == AnyType || TypeName == actualType)
        && (PhaseFilter == ExceptionStopKind.None || PhaseFilter == actualPhase);
}
