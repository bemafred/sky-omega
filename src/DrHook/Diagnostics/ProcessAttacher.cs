using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.NETCore.Client;

namespace SkyOmega.DrHook.Diagnostics;

/// <summary>
/// Discovers running .NET processes available for diagnostic attachment.
/// Uses DiagnosticsClient.GetPublishedProcesses() — the runtime's own advertisement mechanism.
/// Captures assembly version for code anchoring (cross-LLM refinement #2).
/// </summary>
public sealed class ProcessAttacher
{
    public Task<DotNetProcessList> ListDotNetProcessesAsync(CancellationToken ct)
    {
        var pids = DiagnosticsClient.GetPublishedProcesses();

        var entries = pids
            .Select(pid =>
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    var mainModule = proc.MainModule;
                    var version = mainModule?.FileVersionInfo.FileVersion ?? "unknown";

                    return new DotNetProcessEntry(
                        pid,
                        proc.ProcessName,
                        mainModule?.FileName ?? "unknown",
                        version);
                }
                catch
                {
                    // Process may have exited between listing and inspection
                    return null;
                }
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        return Task.FromResult(new DotNetProcessList(entries));
    }
}

public sealed record DotNetProcessEntry(int Pid, string Name, string Path, string Version);

public sealed record DotNetProcessList(List<DotNetProcessEntry> Processes)
{
    public string ToJson() => JsonSerializer.Serialize(new JsonObject
    {
        ["processes"] = new JsonArray(Processes.Select(p => (JsonNode)new JsonObject
        {
            ["pid"]     = p.Pid,
            ["name"]    = p.Name,
            ["path"]    = p.Path,
            ["version"] = p.Version
        }).ToArray()),
        ["count"] = Processes.Count
    }, new JsonSerializerOptions { WriteIndented = true });
}
