// The protocol between drhook-mcp and its debugger worker, drhook-worker (ADR-016 6.7's design, accepted 2026-11-21).
//
// One JSON object per line, on the worker's stdin and stdout. The end of the worker's stdout means the worker is gone;
// its stderr goes to drhook-mcp's stderr. Requests are answered one at a time, in order.
//
// To the worker:
//   {"id":N,"tool":"drhook_...","args":{...}}   run one of the session's tools, with its arguments
//   {"id":N,"health":true}                      answered at once, by the thread that reads stdin, even while a request runs
//   {"exit":true}                               end: a session still open is ended as drhook_stop ends it, a target
//                                               left running is left running; then the worker exits
// From the worker, answers:
//   {"id":N,"result":"<the tool's JSON>","session":S[,"lost":C]}
//   {"id":N,"caption":"...","image":"<PNG, base64>","session":S[,"lost":C]}   the tools that return an image (image may
//                                                                             be absent)
//   S is what the session is after the request: "active"; "holding" (it ended and left an owned target running, which
//   the worker holds for drhook_kill, ADR-016 6.2); "ended"; or "lost": its debugger is lost, for the cause C
//   ("DebuggerError" or "ExitNotDelivered"), and drhook-mcp discards the worker (ADR-016 6.8).
//   {"id":N,"health":{"tool":T,"phase":P,"phaseMs":M,"requestMs":R}}   T the tool running, or absent when idle; P "idle",
//   "waiting for the target" (a stop or an evaluation's end, each bounded by its own timeout) or "debugger work"; M how
//   long it has been in that phase; R how long the request has run.
// From the worker, events, as they happen:
//   {"event":"ready","pid":N,"runtime":"...","protocol":V}
//   {"event":"target","pid":N,"start":T,"owned":B}   a session's target, as soon as the session has one; T its start time
//                                                    in the OS's own unit, absent on Windows (ProcessLifetime)
//   {"event":"state","state":"stopped"|"running"|"ended","reason":R,"where":W}   each stop the session records, and each
//                                                    release; W the top frame of a stop, when it could be read
//   {"event":"anomaly"|"log"|"console", ...}   each record the worker's sinks get, for drhook-mcp's drain buffers
//   {"event":"ended","pid":N}                  the target a holding worker held has ended; the worker then exits
//
// DrHook.Wire's codec does not carry these messages: they carry the tools' own JSON, not Wire's types.

using System.Globalization;
using System.Text.Json.Nodes;

namespace SkyOmega.DrHook.Engine.Worker;

/// <summary>The messages of the protocol between drhook-mcp and drhook-worker (ADR-016 6.7), written and read by the
/// same code on both sides.</summary>
public static class WorkerProtocol
{
    /// <summary>This build's protocol version. drhook-mcp refuses a worker with another. Both come from one build, in
    /// worker/ beside drhook-mcp, so they differ only when a rebuild was done under a running drhook-mcp; a change
    /// reaches DrHook by disconnect, rebuild, reconnect.</summary>
    public const int Version = 1;

    /// <summary>The worker's program, in the worker's own directory.</summary>
    public const string WorkerAssembly = "drhook-worker.dll";

    public const string Active = "active";
    public const string Holding = "holding";
    public const string Ended = "ended";
    public const string Lost = "lost";

    /// <summary>The phases a health answer names (ADR-016 6.8).</summary>
    public const string Idle = "idle";
    public const string WaitingForTarget = "waiting for the target";
    public const string DebuggerWork = "debugger work";

    // ─── To the worker ───────────────────────────────────────────────────────────────────────────────────────────

    public static string Tool(long id, string tool, JsonObject args) =>
        new JsonObject { ["id"] = id, ["tool"] = tool, ["args"] = args }.ToJsonString();

    public static string Exit() => new JsonObject { ["exit"] = true }.ToJsonString();

    public static string Health(long id) => new JsonObject { ["id"] = id, ["health"] = true }.ToJsonString();

    // ─── From the worker: answers ────────────────────────────────────────────────────────────────────────────────

    public static string Result(long id, string result, string session, string? lost) =>
        new JsonObject { ["id"] = id, ["result"] = result, ["session"] = session, ["lost"] = lost }.ToJsonString();

    public static string ImageResult(long id, string caption, byte[]? png, string session, string? lost)
    {
        var answer = new JsonObject { ["id"] = id, ["caption"] = caption, ["session"] = session, ["lost"] = lost };
        if (png is not null) answer["image"] = Convert.ToBase64String(png);
        return answer.ToJsonString();
    }

    public static string HealthAnswer(long id, string? tool, string phase, long phaseMs, long requestMs) => new JsonObject
    {
        ["id"] = id,
        ["health"] = new JsonObject { ["tool"] = tool, ["phase"] = phase, ["phaseMs"] = phaseMs, ["requestMs"] = requestMs },
    }.ToJsonString();

    // ─── From the worker: events ─────────────────────────────────────────────────────────────────────────────────

    public static string Ready(int pid, string runtime) =>
        new JsonObject { ["event"] = "ready", ["pid"] = pid, ["runtime"] = runtime, ["protocol"] = Version }.ToJsonString();

    public static string Target(int pid, long? start, bool owned) =>
        new JsonObject { ["event"] = "target", ["pid"] = pid, ["start"] = start, ["owned"] = owned }.ToJsonString();

    public static string State(string state, string? reason, string? where) =>
        new JsonObject { ["event"] = "state", ["state"] = state, ["reason"] = reason, ["where"] = where }.ToJsonString();

    public static string TargetEnded(int pid) =>
        new JsonObject { ["event"] = "ended", ["pid"] = pid }.ToJsonString();

    public static string Anomaly(EngineAnomaly a)
    {
        var message = new JsonObject
        {
            ["event"] = "anomaly",
            ["capturedAt"] = a.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = a.Kind.ToString(),
            ["thread"] = a.Thread,
            ["operation"] = a.Operation,
            ["observed"] = a.Observed,
            ["expected"] = a.Expected,
        };
        if (a.Context is { Count: > 0 } context)
        {
            var node = new JsonObject();
            foreach ((string key, string value) in context) node[key] = value;
            message["context"] = node;
        }
        return message.ToJsonString();
    }

    public static string Log(LogRecord r) => new JsonObject
    {
        ["event"] = "log",
        ["timestamp"] = r.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
        ["message"] = r.Message,
        ["isFault"] = r.IsFault,
    }.ToJsonString();

    public static string Console(ConsoleOutputRecord r) => new JsonObject
    {
        ["event"] = "console",
        ["capturedAt"] = r.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
        ["stream"] = r.Stream.ToString(),
        ["text"] = r.Text,
    }.ToJsonString();

    // ─── Reading the records back, in drhook-mcp ─────────────────────────────────────────────────────────────────

    public static EngineAnomaly ReadAnomaly(JsonNode m)
    {
        Dictionary<string, string>? context = null;
        if (m["context"] is JsonObject node)
        {
            context = new Dictionary<string, string>();
            foreach ((string key, JsonNode? value) in node) context[key] = (string?)value ?? "";
        }
        return new EngineAnomaly(
            DateTimeOffset.Parse((string)m["capturedAt"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Enum.Parse<AnomalyKind>((string)m["kind"]!),
            (string)m["thread"]!, (string)m["operation"]!, (string)m["observed"]!, (string)m["expected"]!,
            context);
    }

    public static LogRecord ReadLog(JsonNode m) => new(
        DateTimeOffset.Parse((string)m["timestamp"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        (string)m["message"]!,
        (bool)m["isFault"]!);

    public static ConsoleOutputRecord ReadConsole(JsonNode m) => new(
        DateTimeOffset.Parse((string)m["capturedAt"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Enum.Parse<ConsoleStream>((string)m["stream"]!),
        (string)m["text"]!);
}
