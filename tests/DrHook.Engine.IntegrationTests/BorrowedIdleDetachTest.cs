// Layer 1 — INTEGRATION TEST (finding 89 — the Borrowed detach leak).
//
// A Borrowed session whose only stop is a Pause (an idle target yields no organic stop on attach — the
// drhook_attach Pause-fallback path) must DETACH cleanly, and the target must later be able to exit without
// taking the debugger host down.
//
// Before the fix the Borrowed Dispose branch pre-resumed the target (Continue until S_FALSE) and then called
// Detach against a RUNNING process: mscordbi answered the extra Continue with CORDBG_E_SUPERFLOUS_CONTINUE,
// Detach failed CORDBG_E_PROCESS_NOT_SYNCHRONIZED (0x80131302), Terminate failed ILLEGAL_SHUTDOWN_ORDER
// (0x80131C15), the CordbProcess leaked, and when the target exited mscordbi's ExitProcessWorkItem ran against
// the freed managed callback — SIGSEGV of the host (exit 139). This target reproduced exactly that on the
// pre-fix engine (2026-10-03).
//
// Two assertions: the deterministic one — Detach and Terminate report no UnexpectedHResult; and the
// consequential one — the target is killed and this test host keeps running (a regression SEGVs the whole run).
//
// Topology (finding 87): the target runs under a /bin/sh parent, never as this host's Process.Start child.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class BorrowedIdleDetachTest
{
    [TestMethod]
    public void Attach_PauseStop_Dispose_DetachesCleanly_AndTargetExitLeavesHostAlive()
    {
        if (OperatingSystem.IsWindows()) return; // the /bin/sh topology and the observed failure are POSIX

        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target not found at {targetDll}.");

        // `dotnet <dll> & echo $!; wait $!` — the shell reports the target's pid on stdout and stays its parent.
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("dotnet \"$0\" > /dev/null 2>&1 & echo $!; wait $!");
        startInfo.ArgumentList.Add(targetDll);
        using Process shell = Process.Start(startInfo)!;
        try
        {
            int pid = int.Parse(shell.StandardOutput.ReadLine()!, CultureInfo.InvariantCulture);
            Thread.Sleep(1500); // the target's runtime is up and it is looping in Thread.Sleep — idle to a debugger

            var sink = new BoundedAnomalySink(64);
            using (DebugSession session = DebugSession.Attach(pid, sink))
            {
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(2));
                if (stop is null)
                {
                    session.Pause();
                    stop = session.WaitForStop(TimeSpan.FromSeconds(5));
                }
                Assert.IsNotNull(stop, "No synchronized stop after attach + Pause.");
            }

            EngineAnomaly[] detachFaults = sink.Drain().Anomalies
                .Where(a => a.Kind == AnomalyKind.UnexpectedHResult &&
                            (a.Operation.StartsWith("Detach", StringComparison.Ordinal) ||
                             a.Operation.StartsWith("Terminate", StringComparison.Ordinal) ||
                             a.Operation.StartsWith("Quiesce", StringComparison.Ordinal)))
                .ToArray();
            Assert.AreEqual(0, detachFaults.Length,
                "Borrowed detach did not complete cleanly (finding 89): " +
                string.Join("; ", detachFaults.Select(a => $"{a.Operation}: {a.Observed}")));

            // The target was left running un-debugged; let it die now. With a leaked CordbProcess this SEGVs the host.
            // Reaching the assertion below at all is the survival proof; it also confirms the target really exited.
            Process.GetProcessById(pid).Kill();
            Assert.IsTrue(shell.WaitForExit(5000), "The killed target's shell parent did not exit.");
            Thread.Sleep(1000); // mscordbi's exit processing (where the leak SEGV'd) has run by now
        }
        finally
        {
            try { if (!shell.HasExited) shell.Kill(entireProcessTree: true); } catch { }
        }
    }
}
