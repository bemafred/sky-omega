// Whether a process has ended, by PID and start time (ProcessLifetime, the fact behind DebugSession.TargetHasExited).
// CI-safe: the child is spawned with PosixSpawn, as DebugSession.Launch spawns a target, so the BCL does not track it
// and nothing reaps it until the test does: between its death and the reap it is a zombie, the state in which
// Process.HasExited reported a dead target as running (probe 106, 2026-11-07).

using System.Diagnostics;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class ProcessLifetimeTests
{
    [Fact]
    public void AChildThatDied_HasEnded_WhileAZombie_AndAfterItIsReaped_ThoughProcessHasExitedSaysRunning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no zombies; ProcessLifetime answers null there.");

        int rc = PosixSpawn.SpawnSuspendedRedirected("/bin/sleep", ["/bin/sleep", "30"], cwd: null, env: null,
            out int pid, out int stdoutFd, out int stderrFd);
        Assert.Equal(0, rc);
        bool reaped = false;
        try
        {
            PosixSpawn.Continue(pid);
            long? start = ProcessLifetime.StartOf(pid);
            Assert.NotNull(start);
            Assert.False(ProcessLifetime.HasEnded(pid, start.Value));

            using Process held = Process.GetProcessById(pid);   // how DebugSession holds a target
            PosixSpawn.Kill(pid);
            var waited = Stopwatch.StartNew();
            while (ProcessLifetime.HasEnded(pid, start.Value) != true && waited.ElapsedMilliseconds < 5000) Thread.Sleep(10);

            Assert.True(ProcessLifetime.HasEnded(pid, start.Value));
            Assert.Null(ProcessLifetime.StartOf(pid));
            Assert.Equal(0, PosixSpawn.kill(pid, 0));             // still a zombie: nothing has reaped it
            Assert.False(held.HasExited);                         // the defect: a dead, unreaped target reads as running

            PosixSignals.ReapChild(pid);
            reaped = true;
            Assert.True(ProcessLifetime.HasEnded(pid, start.Value));
        }
        finally
        {
            if (!reaped) { PosixSpawn.Kill(pid); PosixSignals.ReapChild(pid); }
            PosixSpawn.Close(stdoutFd);
            PosixSpawn.Close(stderrFd);
        }
    }

    [Fact]
    public void ARunningProcess_HasNotEnded_ButAnotherStartUnderItsPid_Has()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no zombies; ProcessLifetime answers null there.");

        long? start = ProcessLifetime.StartOf(Environment.ProcessId);

        Assert.NotNull(start);
        Assert.False(ProcessLifetime.HasEnded(Environment.ProcessId, start.Value));
        Assert.True(ProcessLifetime.HasEnded(Environment.ProcessId, start.Value + 1));   // a reused PID is another process
    }

    [Fact]
    public void OnWindows_TheAnswerIsTheProcessHandles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        Assert.Null(ProcessLifetime.StartOf(Environment.ProcessId));
        Assert.Null(ProcessLifetime.HasEnded(Environment.ProcessId, 0));
    }
}
