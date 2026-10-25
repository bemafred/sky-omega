using System.Text.Json;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine.Observation;

namespace SkyOmega.DrHook.Engine.Diagnostics;

/// <summary>
/// Discovers running .NET processes available for diagnostic attachment.
/// Delegates to <see cref="ProcessInspector.ListDotnetProcesses"/> (the runtime's own diagnostic-port advertisement).
/// Captures assembly version for code anchoring (cross-LLM refinement #2).
/// </summary>
public sealed class ProcessAttacher
{
    public Task<DotNetProcessList> ListDotNetProcessesAsync(CancellationToken ct)
    {
        // One listing for the engine and the MCP tool (finding 93): the same per-process handling and the
        // same bounded discovery (DBG-PI-1).
        var entries = ProcessInspector.ListDotnetProcesses()
            .Select(p => new DotNetProcessEntry(
                p.Pid,
                p.Name,
                p.MainModulePath ?? "unknown",
                p.FileVersion ?? "unknown",
                p.ExecutableDeleted))
            .ToList();

        return Task.FromResult(new DotNetProcessList(entries));
    }
}

/// <summary>One <c>drhook_processes</c> row. <see cref="ExecutableDeleted"/>: the process runs a build whose file has
/// since been deleted or replaced, so <see cref="Version"/> is "unknown" (finding 93).</summary>
public sealed record DotNetProcessEntry(int Pid, string Name, string Path, string Version, bool ExecutableDeleted = false);

public sealed record DotNetProcessList(List<DotNetProcessEntry> Processes)
{
    public string ToJson() => JsonSerializer.Serialize(new JsonObject
    {
        ["processes"] = new JsonArray(Processes.Select(p => (JsonNode)new JsonObject
        {
            ["pid"]     = p.Pid,
            ["name"]    = p.Name,
            ["path"]    = p.Path,
            ["version"] = p.Version,
            ["executableDeleted"] = p.ExecutableDeleted
        }).ToArray()),
        ["count"] = Processes.Count
    }, new JsonSerializerOptions { WriteIndented = true });
}
