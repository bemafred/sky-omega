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

    [Fact]
    public void ListDotnetProcesses_Self_HasLiveExecutable()
    {
        DotnetProcess self = Assert.Single(ProcessInspector.ListDotnetProcesses(), p => p.Pid == Self);

        Assert.False(self.ExecutableDeleted);
        Assert.NotNull(self.MainModulePath);
        Assert.False(self.MainModulePath!.EndsWith(ProcessIdentity.LinuxDeletedSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void TryRead_ProcessThatIsNotRunning_ReturnsNull()
    {
        // int.MaxValue is far above any pid_max, so no process has it.
        Assert.Null(ProcessIdentity.TryRead(int.MaxValue));
    }

    // Finding 93: Linux reports an unlinked executable as "<path> (deleted)".
    [Theory]
    [InlineData("/opt/app/target (deleted)", true, "/opt/app/target", true)]
    [InlineData("/opt/app/target", true, "/opt/app/target", false)]
    [InlineData("/opt/app/target (deleted)", false, "/opt/app/target (deleted)", false)]   // only Linux uses the suffix
    [InlineData("/opt/app/target (deleted) (deleted)", true, "/opt/app/target (deleted)", true)]
    public void ParseExecutablePath_HandlesLinuxDeletedSuffix(string raw, bool isLinux, string expectedPath, bool expectedDeleted)
    {
        (string? path, bool deleted) = ProcessIdentity.ParseExecutablePath(raw, isLinux);

        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedDeleted, deleted);
    }

    [Fact]
    public void ParseExecutablePath_NoPath_IsNotDeleted()
    {
        Assert.Equal(((string?)null, false), ProcessIdentity.ParseExecutablePath(null, isLinux: true));
    }
}
