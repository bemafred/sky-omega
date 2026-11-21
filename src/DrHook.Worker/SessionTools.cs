using System.Text.Json.Nodes;

namespace SkyOmega.DrHook.Worker;

/// <summary>What a session tool answers: its JSON result, or, for the tools that return an image, a caption and the
/// PNG (null when there is none).</summary>
internal sealed record ToolAnswer(string? Result, string? Caption = null, byte[]? Png = null);

/// <summary>The session's tools as the worker runs them: each request from drhook-mcp, by the tool's MCP name, with the
/// arguments drhook-mcp's tool received (ADR-016 6.7). The results are the session's own, unchanged.</summary>
internal static class SessionTools
{
    public static ToolAnswer Run(EngineSteppingSession session, string tool, JsonObject a)
    {
        CancellationToken none = CancellationToken.None;
        switch (tool)
        {
            case "drhook_launch":
            {
                string[] args = a["args"]!.AsArray().Select(v => (string)v!).ToArray();
                Dictionary<string, string>? env = a["env"] is JsonObject variables
                    ? variables.ToDictionary(v => v.Key, v => (string)v.Value!)
                    : null;
                return new(session.LaunchAsync((string)a["program"]!, args, (string?)a["cwd"], (string)a["sourceFile"]!,
                    (int)a["line"]!, (string)a["hypothesis"]!, env, none).GetAwaiter().GetResult());
            }
            case "drhook_attach":
                return new(session.AttachAsync((int)a["pid"]!, (string)a["sourceFile"]!, (int)a["line"]!,
                    (string)a["hypothesis"]!, none).GetAwaiter().GetResult());
            case "drhook_step_over":
                return new(session.StepOverAsync((string?)a["hypothesis"], none).GetAwaiter().GetResult());
            case "drhook_step_into":
                return new(session.StepIntoAsync((string?)a["hypothesis"], none).GetAwaiter().GetResult());
            case "drhook_step_out":
                return new(session.StepOutAsync((string?)a["hypothesis"], none).GetAwaiter().GetResult());
            case "drhook_continue":
                return new(session.ContinueAsync((string?)a["hypothesis"], (bool)a["waitForBreakpoint"]!, none).GetAwaiter().GetResult());
            case "drhook_pause":
                return new(session.PauseAsync((string?)a["hypothesis"], none).GetAwaiter().GetResult());
            case "drhook_break_source":
                return new(session.SetBreakpointAsync((string)a["sourceFile"]!, (int)a["line"]!, (string?)a["condition"],
                    (int?)a["hitCount"], (string?)a["logMessage"], (string?)a["suspend"], none).GetAwaiter().GetResult());
            case "drhook_break_function":
                return new(session.SetFunctionBreakpointAsync((string)a["functionName"]!, (string?)a["condition"],
                    (int?)a["hitCount"], (string?)a["logMessage"], (string?)a["suspend"], none).GetAwaiter().GetResult());
            case "drhook_break_exception":
                return new(session.SetExceptionBreakpointAsync((string)a["typeName"]!, (string?)a["phase"], (string?)a["condition"],
                    (int?)a["hitCount"], (string?)a["logMessage"], (string?)a["suspend"], none).GetAwaiter().GetResult());
            case "drhook_break_remove":
                return new(session.RemoveByIdAsync((int)a["id"]!, none).GetAwaiter().GetResult());
            case "drhook_break_list":
                return new(session.ListBreakpoints());
            case "drhook_break_clear":
                return new(session.ClearBreakpointsAsync((string?)a["category"], none).GetAwaiter().GetResult());
            case "drhook_locals":
                return new(session.InspectVariablesAsync((int)a["depth"]!, (string?)a["hypothesis"], none).GetAwaiter().GetResult());
            case "drhook_expand":
            {
                string[] path = a["path"]!.AsArray().Select(v => (string)v!).ToArray();
                return new(session.ExpandAsync((string)a["target"]!, path, (string?)a["hypothesis"], none).GetAwaiter().GetResult());
            }
            case "drhook_snapshot_image":
            {
                (byte[]? png, string caption) = session.RenderCurrentStateImage((string?)a["hypothesis"]);
                return new(null, caption, png);
            }
            case "drhook_capture_visual":
            {
                (byte[]? png, string caption) = session.CaptureVisual((string?)a["hypothesis"], (int)a["width"]!, (int)a["height"]!,
                    (string)a["appModule"]!, (string)a["appType"]!, (string)a["currentGetter"]!, (string)a["windowGetterChain"]!,
                    (string)a["gfxModule"]!, (string)a["pixelSizeType"]!, (string)a["bitmapType"]!, (string)a["renderMethod"]!,
                    (string)a["saveMethod"]!);
                return new(null, caption, png);
            }
            case "drhook_stop":
                return new(session.StopAsync((string)a["hypothesis"]!, none).GetAwaiter().GetResult());
            case "drhook_detach":
                return new(session.DetachAsync((string)a["hypothesis"]!, none).GetAwaiter().GetResult());
            case "drhook_kill":
                return new(session.KillAsync((string)a["hypothesis"]!, (int?)a["pid"], none).GetAwaiter().GetResult());
            default:
                return new(Engine.OperatorJson.Error($"The debugger worker has no tool '{tool}'."));
        }
    }
}
