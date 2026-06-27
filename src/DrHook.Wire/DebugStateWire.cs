using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyOmega.DrHook.Wire;

// Wire DTOs — the STABLE on-socket contract shared by the transport server (DrHook.Engine) and every
// visualization client/view (DrHook.Viz and friends). One message = one newline-terminated JSON line
// (NDJSON, ADR-012 Q4), camelCase, nulls omitted. ZERO dependencies, so a view never drags in the engine's
// Roslyn / per-RID libdbgshim / diagnostics packages just to parse JSON. The domain->wire MAPPING lives in
// DrHook.Engine.Transport (it alone knows the domain types); this project is pure wire, referenced by both ends.

/// <summary>The on-socket envelope. Exactly one of <see cref="Snapshot"/> / <see cref="Delta"/> is set,
/// selected by <see cref="Type"/> (<c>"snapshot"</c> | <c>"delta"</c>). <see cref="Seq"/> is a monotonic
/// per-server sequence so a view can order messages and dedupe a connect-time snapshot that also arrives
/// via the broadcast.</summary>
public sealed record WireMessage(string Type, long Seq, WireSnapshot? Snapshot = null, WireDelta? Delta = null);

/// <summary>Self-contained current state — what a view renders with no prior context.</summary>
public sealed record WireSnapshot(string CapturedAt, WireSession Session, WirePosition Position,
    WireBreakpoint[] Breakpoints, WireExceptionFilter[] ExceptionFilters, WireStreams Streams);

public sealed record WireSession(int Pid, bool Owned, int? RuntimeMajor, bool Detached, bool Disposed, string Execution);

public sealed record WirePosition(string? Stop, string? ExceptionType,
    WireFrame[] CallStack, WireVar[] Locals, WireVar[] Arguments);

/// <summary>One call-stack frame on the wire: the <see cref="Display"/> string a text view prints
/// ("Type.Method @ file:line" / "Type.Method" / "[external]") PLUS the structured source location —
/// <see cref="File"/> (the FULL source path, null when unresolved) and <see cref="Line"/> (1-based, null
/// when unresolved). The full path lets a view open the file for a source-on-step rendering (ADR-012
/// Phase 4); before the Phase-2 enrichment the position carried only the abbreviated display string, so a
/// view could show WHERE execution stopped but not open the file. Mirrors how <see cref="WireBreakpoint"/>
/// already carries structured File/Line. The top (innermost) frame is <c>CallStack[0]</c>.</summary>
public sealed record WireFrame(string Display, string? File, int? Line);

/// <summary>One local / argument on the wire: <see cref="Name"/>, the raw CorElementType
/// (<see cref="ElementType"/>), the rendered <see cref="Value"/> (a primitive's text or a string's
/// content; null for an object/array reference or an unavailable value), <see cref="TypeName"/> — the
/// runtime type of a non-null object/array/value-type reference (e.g. <c>Worker</c>; null otherwise) — and
/// <see cref="HasChildren"/>, true when the value is a non-null object/array with expandable members.
/// Together these let a view render a value precisely: a primitive/string by <see cref="Value"/>, an object
/// as its type (<c>{Worker}</c>) from <see cref="TypeName"/>, an object of unresolved type as expandable
/// (<c>{…}</c>) from <see cref="HasChildren"/>, and a null reference as <c>null</c> — never a bare
/// <c>?</c>.</summary>
public sealed record WireVar(string Name, int ElementType, string? Value, bool HasChildren = false, string? TypeName = null);

public sealed record WireBreakpoint(int Id, int HitCount, string Kind, string? File, int? Line, string? Type, string? Method);

public sealed record WireExceptionFilter(int Id, int HitCount, string TypeName, string Phase);

/// <summary>Buffered-stream sizes at snapshot time (counts only — live content arrives as deltas from
/// connect-time forward; a view shows "N buffered" plus the live stream).</summary>
public sealed record WireStreams(int Console, long ConsoleDropped, int Logs, long LogsDropped, int Anomalies, long AnomaliesDropped);

/// <summary>One live event. Only the fields for <see cref="Kind"/> are set.</summary>
public sealed record WireDelta(string Kind, string At, string? Event = null,
    string? LogMessage = null, bool? LogFault = null,
    string? AnomalyKind = null, string? AnomalyObserved = null,
    string? ConsoleStream = null, string? ConsoleText = null);

/// <summary>The NDJSON codec for <see cref="WireMessage"/> — one newline-terminated line per message,
/// SOURCE-GENERATED (not reflection-based: the repo builds with reflection-based <c>System.Text.Json</c>
/// serialization disabled, so a reflection <c>Serialize</c>/<c>Deserialize</c> throws at runtime; source
/// generation is the supported, AOT-safe path). The server serializes with it; clients parse with it.</summary>
public static class WireCodec
{
    /// <summary>Serialize one message to a single newline-terminated NDJSON line.</summary>
    public static string Serialize(WireMessage message)
        => JsonSerializer.Serialize(message, WireJson.Default.WireMessage) + "\n";

    /// <summary>Parse one NDJSON line (trailing newline optional) into a <see cref="WireMessage"/>, or null
    /// if the line is blank. Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static WireMessage? Parse(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length == 0 ? null : JsonSerializer.Deserialize(trimmed, WireJson.Default.WireMessage);
    }
}

/// <summary>Source-generated JSON for the wire types — camelCase, nulls omitted. Source generation (not
/// reflection) is required: reflection-based <c>System.Text.Json</c> serialization is disabled in this repo,
/// so the metadata is generated at compile time. <see cref="WireMessage"/> roots the whole graph (snapshot +
/// delta facets), so one <c>[JsonSerializable]</c> covers every wire type.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireMessage))]
internal sealed partial class WireJson : JsonSerializerContext { }
