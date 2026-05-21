// Observation facade (ADR-006 Phase 1 / Tier 1). Wraps the substrate-admitted
// Microsoft.Diagnostics.NETCore.Client (Layer 1, Diagnostic IPC) for .NET-process discovery,
// plus System.Diagnostics.Process (BCL) for live metrics. Consumers depend on this facade's
// types, not the NuGet directly — the "thin facade" from ADR-006's Integration section, which
// keeps the admitted dependency at one seam and the surface testable. Managed-assembly
// enumeration via EventPipe (Layer 2 / TraceEvent) is a follow-up; this covers process
// discovery + Tier-1 metrics + native module list (Process.Modules).

using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace SkyOmega.DrHook.Engine.Observation;

/// <summary>A discovered .NET process available for diagnostic attachment.</summary>
public sealed record DotnetProcess(int Pid, string Name, string? MainModulePath, string? FileVersion);

/// <summary>A point-in-time snapshot of a process's BCL-observable metrics.</summary>
public sealed record ProcessSnapshot(
    int Pid,
    string Name,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    DateTime StartTimeUtc,
    TimeSpan TotalProcessorTime,
    int ModuleCount);

/// <summary>Discovers and inspects .NET processes. Layer-1 discovery is the admitted
/// <c>DiagnosticsClient</c>; metrics are BCL <see cref="Process"/>.</summary>
public static class ProcessInspector
{
    /// <summary>OS process ids of .NET processes advertising a diagnostic port
    /// (the runtime's own publication mechanism, via the admitted DiagnosticsClient).</summary>
    public static IReadOnlyList<int> ListDotnetProcessIds()
        => DiagnosticsClient.GetPublishedProcesses().ToList();

    /// <summary>True if <paramref name="pid"/> is a .NET process with a diagnostic port.</summary>
    public static bool IsDotnetProcess(int pid)
        => DiagnosticsClient.GetPublishedProcesses().Contains(pid);

    /// <summary>List discovered .NET processes with basic identity. Processes that exit
    /// between discovery and inspection are skipped.</summary>
    public static IReadOnlyList<DotnetProcess> ListDotnetProcesses()
    {
        List<DotnetProcess> result = new();
        foreach (int pid in DiagnosticsClient.GetPublishedProcesses())
        {
            try
            {
                using Process proc = Process.GetProcessById(pid);
                ProcessModule? mainModule = proc.MainModule;
                result.Add(new DotnetProcess(
                    pid,
                    proc.ProcessName,
                    mainModule?.FileName,
                    mainModule?.FileVersionInfo.FileVersion));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Process exited between discovery and inspection, or its main module is
                // inaccessible — skip it.
            }
        }
        return result;
    }

    /// <summary>Capture a point-in-time metric snapshot of a process.</summary>
    /// <exception cref="ArgumentException">No process with that id is running.</exception>
    public static ProcessSnapshot Snapshot(int pid)
    {
        using Process proc = Process.GetProcessById(pid);
        return new ProcessSnapshot(
            pid,
            proc.ProcessName,
            proc.WorkingSet64,
            proc.PrivateMemorySize64,
            proc.Threads.Count,
            proc.StartTime.ToUniversalTime(),
            proc.TotalProcessorTime,
            proc.Modules.Count);
    }
}
