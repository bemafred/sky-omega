// Layer 1 — INTEGRATION TEST of the harness itself (Windows): SpawnedProcessJob contains orphans.
//
// Finding 90's shape: during a VSTest run vstest.console can start a second testhost that waits for a debugger forever,
// then exit, orphaning it. A kill of the run's process TREE misses an orphan, because its parent is gone. On POSIX the
// harness kills the run's process group instead; on Windows every spawned process belongs to the harness Job Object
// from its creation, and KillAllButHost kills the job's members.
//
// The test builds the orphan on purpose: cmd starts ping (a grandchild of this host), cmd is killed alone, and ping —
// now parentless — must still be ended by KillAllButHost.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class SpawnedProcessJobTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void OrphanedGrandchild_IsKilledWithTheJob()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("SpawnedProcessJob is the Windows harness; POSIX uses process groups (SpawnedProcessGroups).");

        int[] pingsBefore = Process.GetProcessesByName("PING").Select(p => p.Id).ToArray();

        SpawnedProcessJob.EnsureHostInJob();
        using Process cmd = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1 > nul") { UseShellExecute = false })!;

        // Sync on the target: wait until ping exists, not for a fixed time.
        Process? ping = null;
        while (ping is null)
        {
            TestContext.CancellationToken.ThrowIfCancellationRequested();
            ping = Process.GetProcessesByName("PING").FirstOrDefault(p => !pingsBefore.Contains(p.Id));
            if (ping is null) Thread.Sleep(50);
        }
        using (ping)
        {
            cmd.Kill(entireProcessTree: false);
            cmd.WaitForExit();
            Assert.IsFalse(ping.HasExited, "ping exited with cmd; the orphan this test needs was not created.");

            SpawnedProcessJob.KillAllButHost();

            Assert.IsTrue(ping.WaitForExit(10_000), "The orphaned grandchild survived KillAllButHost: it is not in the job.");
        }
    }
}
