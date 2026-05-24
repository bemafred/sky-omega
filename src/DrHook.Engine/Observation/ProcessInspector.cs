// Observation facade (ADR-006 Phase 1 / Tier 1). Wraps the substrate-admitted
// Microsoft.Diagnostics.NETCore.Client (Layer 1, Diagnostic IPC) for .NET-process discovery,
// plus System.Diagnostics.Process (BCL) for live metrics. Consumers depend on this facade's
// types, not the NuGet directly — the "thin facade" from ADR-006's Integration section, which
// keeps the admitted dependency at one seam and the surface testable. Managed-assembly
// enumeration via EventPipe (Layer 2 / TraceEvent) is a follow-up; this covers process
// discovery + Tier-1 metrics + native module list (Process.Modules).
//
// DBG-PI-1 (finding 59 follow-up): DiagnosticsClient.GetPublishedProcesses hangs on macOS
// against a target that was attached + detached recently (mscordbi/runtime state takes time
// to settle after Detach; GetPublishedProcesses queries the runtime via diagnostic IPC and
// can block indefinitely during this settle window). Substrate fix: bound the call with a
// timeout and treat timeout as "discovery failed — no processes visible right now." Callers
// asking "is this PID alive?" should use System.Diagnostics.Process.HasExited; ProcessInspector
// answers a narrower question (.NET process advertising a diagnostic port).

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>Default bound on <c>DiagnosticsClient.GetPublishedProcesses</c> — protects
    /// callers from indefinite hangs on macOS when querying immediately after a debugger
    /// Detach (DBG-PI-1 / finding 59). Two seconds is empirically long enough for the
    /// runtime's diagnostic-port advertisement to settle after Detach; longer hangs almost
    /// always indicate the call won't return.</summary>
    public const int DefaultDiscoveryTimeoutMs = 2000;

    /// <summary>OS process ids of .NET processes advertising a diagnostic port
    /// (the runtime's own publication mechanism, via the admitted DiagnosticsClient).
    /// Bounded by <see cref="DefaultDiscoveryTimeoutMs"/>; returns empty if the underlying
    /// call hangs past the budget (a recently-detached macOS target is the typical case).</summary>
    public static IReadOnlyList<int> ListDotnetProcessIds()
        => GetPublishedProcessesBounded().ToList();

    /// <summary>True if <paramref name="pid"/> is a .NET process with a diagnostic port.
    /// Bounded by <see cref="DefaultDiscoveryTimeoutMs"/>; returns false on timeout (discovery
    /// inconclusive — callers needing process liveness should use <see cref="Process.HasExited"/>).</summary>
    public static bool IsDotnetProcess(int pid)
        => GetPublishedProcessesBounded().Contains(pid);

    /// <summary>List discovered .NET processes with basic identity. Processes that exit
    /// between discovery and inspection are skipped. Bounded by
    /// <see cref="DefaultDiscoveryTimeoutMs"/>; returns empty on timeout.</summary>
    public static IReadOnlyList<DotnetProcess> ListDotnetProcesses()
    {
        List<DotnetProcess> result = new();
        foreach (int pid in GetPublishedProcessesBounded())
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

    // DBG-PI-1: bound the raw DiagnosticsClient call so a recently-detached macOS target
    // can't hang the caller indefinitely. Task.Run + WaitAsync(timeout) returns either the
    // result or throws TimeoutException; we treat timeout as "no processes visible." The
    // backing Task may still be running after timeout (orphan); on the rare occasion it
    // completes much later, the result is discarded. Acceptable for occasional discovery.
    private static IEnumerable<int> GetPublishedProcessesBounded()
    {
        try
        {
            return Task.Run(() => DiagnosticsClient.GetPublishedProcesses())
                .WaitAsync(TimeSpan.FromMilliseconds(DefaultDiscoveryTimeoutMs))
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            return Array.Empty<int>();
        }
    }
}
