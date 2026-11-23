// Self-inspection tests for the observation facade — deterministic and CI-safe: the test
// process inspects itself (a live .NET process with a diagnostic port), so no external
// debuggee or dbgshim is needed.

using System.Diagnostics;
using SkyOmega.DrHook.Engine.Interop;
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

    // 2026-11-23 on omega, drhook_processes listed three macOS daemons as .NET processes. Their PIDs had diagnostic
    // sockets left by .NET processes that ended without removing them, five days before the daemons started. The
    // runtime names its socket dotnet-diagnostic-<pid>-<key>-socket, where <key> is its start time, so a socket whose
    // key is not the start time of the process under its PID was made by an earlier process.
    [Fact]
    public void ListDotnetProcesses_LeavesOutAProcessUnderAPidWhoseSocketAnEarlierProcessMade()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows publishes a named pipe, which ends with its process: no socket is left behind.");

        // A process that is not .NET, and the socket a killed .NET process would have left under its PID.
        using Process sleep = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        try
        {
            long key = ProcessLifetime.DisambiguationKeyOf(sleep.Id)
                ?? throw new InvalidOperationException($"The start time of sleep, PID {sleep.Id}, could not be read.");
            string leftBehind = Path.Combine(Path.GetTempPath(), $"dotnet-diagnostic-{sleep.Id}-{key - 1}-socket");
            File.WriteAllBytes(leftBehind, []);
            try
            {
                Assert.DoesNotContain(ProcessInspector.ListDotnetProcesses(), p => p.Pid == sleep.Id);
                Assert.False(ProcessInspector.IsDotnetProcess(sleep.Id));
            }
            finally
            {
                File.Delete(leftBehind);
            }
        }
        finally
        {
            sleep.Kill();
            sleep.WaitForExit();
        }
    }

    // The control for the test above: the same construction with the key of the process under the PID is listed, so
    // that test's process is left out for its socket's key, not for being a file or for not being .NET.
    [Fact]
    public void ListDotnetProcesses_ListsAProcessUnderAPidWhoseSocketHasItsOwnStartTime()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows publishes a named pipe, which ends with its process: no socket is left behind.");

        using Process sleep = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        try
        {
            long key = ProcessLifetime.DisambiguationKeyOf(sleep.Id)
                ?? throw new InvalidOperationException($"The start time of sleep, PID {sleep.Id}, could not be read.");
            string itsOwn = Path.Combine(Path.GetTempPath(), $"dotnet-diagnostic-{sleep.Id}-{key}-socket");
            File.WriteAllBytes(itsOwn, []);
            try
            {
                Assert.Contains(ProcessInspector.ListDotnetProcesses(), p => p.Pid == sleep.Id);
                Assert.True(ProcessInspector.IsDotnetProcess(sleep.Id));
            }
            finally
            {
                File.Delete(itsOwn);
            }
        }
        finally
        {
            sleep.Kill();
            sleep.WaitForExit();
        }
    }

    // The key the runtime names a socket with is its start time as DisambiguationKeyOf reads it: the test host's own
    // socket carries it.
    [Fact]
    public void DisambiguationKeyOf_Self_IsTheKeyInTheTestHostsOwnSocket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows names its diagnostic pipe by PID alone.");

        long key = ProcessLifetime.DisambiguationKeyOf(Self)
            ?? throw new InvalidOperationException("The test host's own start time could not be read.");

        Assert.True(File.Exists(Path.Combine(Path.GetTempPath(), $"dotnet-diagnostic-{Self}-{key}-socket")));
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
