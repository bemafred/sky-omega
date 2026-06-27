using System.Globalization;
using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Engine.Transport;

/// <summary>Maps the internal debug-state model (<see cref="DebugStateSnapshot"/> / <see cref="DebugStateDelta"/>
/// and their facets) to the shared wire DTOs (<c>DrHook.Wire</c>) and produces one NDJSON line per message via
/// <see cref="WireCodec"/>. This mapping lives in the engine because only the engine knows the domain types;
/// the wire DTOs + codec are the shared contract (DrHook.Wire), referenced by both the server here and every
/// view client. The projection deliberately decouples the wire from the domain records (which can be reshaped
/// without breaking views) and sidesteps <c>System.Text.Json</c> polymorphism (BreakpointInfo subtypes) and the
/// <c>object?</c> of <see cref="LocalValue.RawValue"/>.</summary>
public static class DebugStateWireMapper
{
    /// <summary>Serialize a snapshot message to a single newline-terminated NDJSON line.</summary>
    public static string SnapshotLine(DebugStateSnapshot snapshot, long seq)
        => WireCodec.Serialize(new WireMessage("snapshot", seq, Snapshot: ToWire(snapshot)));

    /// <summary>Serialize a delta message to a single newline-terminated NDJSON line.</summary>
    public static string DeltaLine(DebugStateDelta delta, long seq)
        => WireCodec.Serialize(new WireMessage("delta", seq, Delta: ToWire(delta)));

    public static WireSnapshot ToWire(DebugStateSnapshot s) => new(
        s.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
        new WireSession(s.Session.ProcessId, s.Session.OwnsTarget, s.Session.RuntimeMajor,
            s.Session.IsDetached, s.Session.IsDisposed, s.Session.Execution.ToString()),
        new WirePosition(
            s.Position.Stop?.Reason.ToString(),
            s.Position.ExceptionTypeName,
            s.Position.CallStack.Select(ToWire).ToArray(),
            s.Position.Locals.Select(ToWire).ToArray(),
            s.Position.Arguments.Select(ToWire).ToArray()),
        s.Breakpoints.Select(ToWire).ToArray(),
        s.ExceptionFilters.Select(f => new WireExceptionFilter(f.Info.Id, f.HitCount, f.Info.TypeName, f.Info.PhaseFilter.ToString())).ToArray(),
        new WireStreams(s.Console.Records.Count, s.Console.Dropped, s.Logs.Records.Count, s.Logs.Dropped, s.Anomalies.Anomalies.Count, s.Anomalies.Dropped));

    public static WireDelta ToWire(DebugStateDelta d) => d.Kind switch
    {
        DebugStateDeltaKind.Event => new WireDelta("event", At(d.At), Event: d.EventName),
        DebugStateDeltaKind.Log => new WireDelta("log", At(d.At), LogMessage: d.Log?.Message, LogFault: d.Log?.IsFault),
        DebugStateDeltaKind.Anomaly => new WireDelta("anomaly", At(d.At), AnomalyKind: d.Anomaly?.Kind.ToString(), AnomalyObserved: d.Anomaly?.Observed),
        DebugStateDeltaKind.Console => new WireDelta("console", At(d.At), ConsoleStream: d.Console?.Stream.ToString(), ConsoleText: d.Console?.Text),
        _ => new WireDelta("unknown", At(d.At)),
    };

    private static WireFrame ToWire(FrameLocation f) => new(f.Display, f.File, f.Line);

    private static WireVar ToWire(LocalValue l) => new(l.Name, l.ElementType, l.StringValue ?? Render(l.RawValue));
    private static WireVar ToWire(ArgumentValue a) => new(a.Name, a.ElementType, a.StringValue ?? Render(a.RawValue));

    private static WireBreakpoint ToWire(BreakpointStatus b) => b.Info switch
    {
        LineBreakpointInfo line => new WireBreakpoint(b.Info.Id, b.HitCount, "line", line.FilePath, line.Line, null, null),
        FunctionBreakpointInfo fn => new WireBreakpoint(b.Info.Id, b.HitCount, "function", null, null, fn.TypeName, fn.MethodName),
        _ => new WireBreakpoint(b.Info.Id, b.HitCount, "unknown", null, null, null, null),
    };

    private static string At(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
    private static string? Render(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
}
