// The protocol between drhook-mcp and its debugger worker, drhook-worker (ADR-016 6.7's design, accepted 2026-11-21).
//
// One JSON object per line, on the worker's stdin and stdout. The end of the worker's stdout means the worker is gone;
// its stderr goes to drhook-mcp's stderr. Requests are answered one at a time, in order.
//
// To the worker:
//   {"id":N,"tool":"drhook_...","args":{...}}   run one of the session's tools, with its arguments
//   {"exit":true}                               end: a session still open is ended as drhook_stop ends it, a target
//                                               left running is left running; then the worker exits
// From the worker, answers:
//   {"id":N,"result":"<the tool's JSON>","session":S}
//   {"id":N,"caption":"...","image":"<PNG, base64>","session":S}   for the tools that return an image (image may be absent)
//   S is what the session is after the request: "active", "holding" (it ended and left an owned target running, which
//   the worker holds for drhook_kill, ADR-016 6.2), or "ended".
// From the worker, events, as they happen:
//   {"event":"ready","pid":N,"runtime":"...","protocol":V}
//   {"event":"target","pid":N,"owned":B}       a session's target, once the session has one
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
    /// <summary>This build's protocol version. drhook-mcp refuses a worker with another: the worker follows the build,
    /// and drhook-mcp does not.</summary>
    public const int Version = 1;

    /// <summary>The worker's program, in the worker's own directory.</summary>
    public const string WorkerAssembly = "drhook-worker.dll";

    public const string Active = "active";
    public const string Holding = "holding";
    public const string Ended = "ended";

    // ─── To the worker ───────────────────────────────────────────────────────────────────────────────────────────

    public static string Tool(long id, string tool, JsonObject args) =>
        new JsonObject { ["id"] = id, ["tool"] = tool, ["args"] = args }.ToJsonString();

    public static string Exit() => new JsonObject { ["exit"] = true }.ToJsonString();

    // ─── From the worker: answers ────────────────────────────────────────────────────────────────────────────────

    public static string Result(long id, string result, string session) =>
        new JsonObject { ["id"] = id, ["result"] = result, ["session"] = session }.ToJsonString();

    public static string ImageResult(long id, string caption, byte[]? png, string session)
    {
        var answer = new JsonObject { ["id"] = id, ["caption"] = caption, ["session"] = session };
        if (png is not null) answer["image"] = Convert.ToBase64String(png);
        return answer.ToJsonString();
    }

    // ─── From the worker: events ─────────────────────────────────────────────────────────────────────────────────

    public static string Ready(int pid, string runtime) =>
        new JsonObject { ["event"] = "ready", ["pid"] = pid, ["runtime"] = runtime, ["protocol"] = Version }.ToJsonString();

    public static string Target(int pid, bool owned) =>
        new JsonObject { ["event"] = "target", ["pid"] = pid, ["owned"] = owned }.ToJsonString();

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
