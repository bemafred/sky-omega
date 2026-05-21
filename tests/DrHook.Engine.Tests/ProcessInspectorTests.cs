// Self-inspection tests for the observation facade — deterministic and CI-safe: the test
// process inspects itself (a live .NET process with a diagnostic port), so no external
// debuggee or dbgshim is needed.

using SkyOmega.DrHook.Engine.Observation;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class ProcessInspectorTests
{
    private static readonly int Self = Environment.ProcessId;

    [Fact]
    public void Snapshot_OfSelf_ReportsLiveMetrics()
    {
        ProcessSnapshot snap = ProcessInspector.Snapshot(Self);

        Assert.Equal(Self, snap.Pid);
        Assert.False(string.IsNullOrEmpty(snap.Name));
        Assert.True(snap.WorkingSetBytes > 0);
        Assert.True(snap.ThreadCount > 0);
        Assert.True(snap.ModuleCount > 0);
        Assert.True(snap.StartTimeUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void IsDotnetProcess_RecognizesSelf()
    {
        // The test host is a .NET process and advertises a diagnostic port.
        Assert.True(ProcessInspector.IsDotnetProcess(Self));
    }

    [Fact]
    public void ListDotnetProcesses_IncludesSelf()
    {
        IReadOnlyList<DotnetProcess> processes = ProcessInspector.ListDotnetProcesses();

        Assert.Contains(processes, p => p.Pid == Self);
    }
}
