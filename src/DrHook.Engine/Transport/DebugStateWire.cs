using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyOmega.DrHook.Engine.Transport;

// Wire DTOs — the STABLE on-socket contract every view depends on (ADR-012 Phase 2, Q4 = NDJSON). These
// are deliberately decoupled from the internal domain records (DebugStateSnapshot/DebugStateDelta and
// their facets): the domain types can be renamed/reshaped without breaking views, and the projection
// sidesteps System.Text.Json polymorphism (BreakpointInfo subtypes) and the object? of LocalValue.RawValue.
// One message is one newline-terminated JSON line (NDJSON). camelCase, nulls omitted.

/// <summary>The on-socket envelope. Exactly one of <see cref="Snapshot"/> / <see cref="Delta"/> is set,
/// selected by <see cref="Type"/> (<c>"snapshot"</c> | <c>"delta"</c>). <see cref="Seq"/> is a monotonic
/// per-server sequence so a view can order messages and dedupe the connect-time snapshot if it also
/// arrives via the broadcast.</summary>
public sealed record WireMessage(string Type, long Seq, WireSnapshot? Snapshot = null, WireDelta? Delta = null);

/// <summary>Wire projection of <see cref="DebugStateSnapshot"/> — self-contained current state.</summary>
public sealed record WireSnapshot(string CapturedAt, WireSession Session, WirePosition Position,
    WireBreakpoint[] Breakpoints, WireExceptionFilter[] ExceptionFilters, WireStreams Streams);

public sealed record WireSession(int Pid, bool Owned, int? RuntimeMajor, bool Detached, bool Disposed, string Execution);

public sealed record WirePosition(string? Stop, string? ExceptionType, string? TopFrame,
    string[] CallStack, WireVar[] Locals, WireVar[] Arguments);

public sealed record WireVar(string Name, int ElementType, string? Value);

public sealed record WireBreakpoint(int Id, int HitCount, string Kind, string? File, int? Line, string? Type, string? Method);

public sealed record WireExceptionFilter(int Id, int HitCount, string TypeName, string Phase);

/// <summary>Buffered-stream sizes at snapshot time (counts only — the live content arrives as deltas from
/// connect-time forward; a view shows "N buffered" plus the live stream).</summary>
public sealed record WireStreams(int Console, long ConsoleDropped, int Logs, long LogsDropped, int Anomalies, long AnomaliesDropped);

/// <summary>Wire projection of <see cref="DebugStateDelta"/> — one live event. Only the fields for
/// <see cref="Kind"/> are set.</summary>
public sealed record WireDelta(string Kind, string At, string? Event = null,
    string? LogMessage = null, bool? LogFault = null,
    string? AnomalyKind = null, string? AnomalyObserved = null,
    string? ConsoleStream = null, string? ConsoleText = null);

/// <summary>Maps the internal debug-state model to the wire DTOs and serializes one NDJSON line per
/// message via <see cref="System.Text.Json"/> (BCL — ships in the shared framework, so the transport
/// stays BCL-only). Single-line by construction (no indentation), camelCase, nulls omitted.</summary>
public static class DebugStateWireSerializer
{
    /// <summary>Serialize a snapshot message to a single newline-terminated NDJSON line. SOURCE-GENERATED,
    /// not reflection-based: the repo builds with reflection-based <c>System.Text.Json</c> serialization
    /// disabled (the MCP layer likewise builds JSON via the DOM API, never <c>Serialize&lt;T&gt;</c>), so a
    /// reflection <c>Serialize</c> throws at runtime — and on a background worker thread that throw became a
    /// silent thread death and a multi-minute client hang. Source generation is the AOT-safe path that works
    /// regardless of the reflection switch (and is what the runtime error message itself points to).</summary>
    public static string SnapshotLine(DebugStateSnapshot snapshot, long seq)
        => JsonSerializer.Serialize(new WireMessage("snapshot", seq, Snapshot: ToWire(snapshot)), WireJson.Default.WireMessage) + "\n";

    /// <summary>Serialize a delta message to a single newline-terminated NDJSON line (source-generated).</summary>
    public static string DeltaLine(DebugStateDelta delta, long seq)
        => JsonSerializer.Serialize(new WireMessage("delta", seq, Delta: ToWire(delta)), WireJson.Default.WireMessage) + "\n";

    public static WireSnapshot ToWire(DebugStateSnapshot s) => new(
        s.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
        new WireSession(s.Session.ProcessId, s.Session.OwnsTarget, s.Session.RuntimeMajor,
            s.Session.IsDetached, s.Session.IsDisposed, s.Session.Execution.ToString()),
        new WirePosition(
            s.Position.Stop?.Reason.ToString(),
            s.Position.ExceptionTypeName,
            s.Position.TopFrame,
            s.Position.CallStack.ToArray(),
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

/// <summary>Source-generated JSON for the wire types — NDJSON, camelCase, nulls omitted. Source generation
/// (not reflection) is required: the repo builds with reflection-based <c>System.Text.Json</c> serialization
/// disabled, so generating the metadata at compile time is the supported path. <c>WireMessage</c> roots the
/// whole graph (snapshot + delta facets), so one <c>[JsonSerializable]</c> covers every wire type.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireMessage))]
internal sealed partial class WireJson : JsonSerializerContext { }
