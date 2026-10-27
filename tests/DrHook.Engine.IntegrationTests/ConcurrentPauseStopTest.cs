// Layer 1 — INTEGRATION TEST for probe 43 (concurrent PauseRequest + STOPPING events).
//
// Promoted to integration tests under ADR-008 Increment 4c / Phase 8 mass promotion.
//
// Substrate-correctness hypothesis (per probe 43 / finding 58): the pump worker's
// single-consumer FIFO queue serialises concurrent PauseRequest + STOPPING events
// correctly. PauseRequest arrives via session.Pause() (caller thread); STOPPING events
// (Exception) arrive via mscordbi RC event thread. Both push to the same BlockingCollection
// _events queue; the worker processes them in FIFO order. A Pause stop eventually surfaces
// even amid a STOPPING event flood — substrate doesn't drop or reorder.
//
// At integration scale: target's RunThrowCatchLoop throws + catches in a tight loop,
// generating Exception (STOPPING) callbacks. Substrate attaches; arms a no-match exception filter
// (auto-resumes all Exception stops via substrate's WaitForStop loop); runs to a breakpoint inside
// the loop, so the flood is known to be running; calls session.Pause(); awaits Pause stop
// arrival. Assertion: Pause stop arrives within reasonable budget despite STOPPING flood.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class ConcurrentPauseStopTest
{
    private sealed class NullSink : IDebugEventSink
    {
        public void OnEvent(string name) { }
    }

    [TestMethod]
    public void AttachAndOwn_MtpTarget_PauseDuringStoppingFlood_PauseStopSurfaces()
    {
        RunConcurrentPauseStopScenario_Mtp("RunThrowCatchLoop");
    }

    private static void RunConcurrentPauseStopScenario_Mtp(string methodFilter)
    {
        string targetExe = IntegrationTargetPaths.MtpTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP target exe not found at {targetExe}.");

        using Process bootstrap = TargetSpawn.Mtp(targetExe, methodFilter: methodFilter);
        try
        {
            int pid = TargetSpawn.ExtractPid(bootstrap, TimeSpan.FromSeconds(30));

            using (DebugSession session = DebugSession.AttachAndOwn(pid, new NullSink()))
            {
                AssertPauseStopSurfacesUnderStoppingFlood(session, IntegrationTargetPaths.MtpTargetSource());
            }

            bool exitedNaturally = bootstrap.WaitForExit(5000);
            Assert.IsTrue(exitedNaturally,
                "MTP target did not exit naturally within 5s after Dispose.");
        }
        finally
        {
            try { if (!bootstrap.HasExited) bootstrap.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>The substrate-correctness assertion shared by both MTP + VSTest variants: a Pause stop surfaces
    /// while the target's throw/catch loop floods the pump with STOPPING (Exception) callbacks.
    ///
    /// The flood must actually be running when Pause is requested, so sync on the target, never on the clock
    /// (finding 94). This used to settle 100 ms after the attach and Pause: an attach that landed early (the
    /// VSTest PID is printed before the testhost is up, finding 91) paused the testhost before the loop, so the
    /// assertion passed without a flood, and Dispose then SIGTERMed the testhost mid-handshake, leaving
    /// vstest.console waiting out its connection timeout (the bootstrap missed its 10 s exit, ~1 in 20 runs on
    /// omega-ubuntu and under host load on omega). Now: Pause for a stop however early the attach landed, arm a
    /// breakpoint on the loop's throw (FLOOD_RUNNING_HERE), run to it, then Pause mid-flood.</summary>
    private static void AssertPauseStopSurfacesUnderStoppingFlood(DebugSession session, string targetSource)
    {
        // Arm no-match exception filter — substrate's WaitForStop auto-resumes Exception
        // stops via the filter loop. Pause and Break stops surface unaltered.
        session.ArmExceptionFilter("NoSuchTypeWillMatch");

        int markerLine = Array.FindIndex(File.ReadAllLines(targetSource), l => l.Contains("FLOOD_RUNNING_HERE", StringComparison.Ordinal)) + 1;
        Assert.IsTrue(markerLine > 0, $"FLOOD_RUNNING_HERE marker not found in {targetSource}.");

        session.Pause();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(stop, "No stop within 5s of the first Pause.");
        int breakpointId = session.SetBreakpointAtLine(Path.GetFileName(targetSource), markerLine);
        Assert.AreNotEqual(0, breakpointId, $"Failed to set a breakpoint at {Path.GetFileName(targetSource)}:{markerLine}.");
        do
        {
            session.Resume();
            stop = session.WaitForStop(TimeSpan.FromSeconds(30));
            Assert.IsNotNull(stop, "The throw/catch loop's breakpoint was not hit within 30s.");
        }
        while (stop!.Reason is StopReason.Break or StopReason.Pause);
        Assert.AreEqual(StopReason.Breakpoint, stop.Reason);
        Assert.IsTrue(session.RemoveBreakpoint(breakpointId), "Could not remove the throw/catch loop's breakpoint.");

        // Resume into the flood (~50 throws over ~500 ms) and request a Pause while it runs.
        session.Resume();
        session.Pause();
        stop = session.WaitForStop(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(stop, "No stop arrived within 5s after Pause request — substrate FIFO serialization may have dropped the request.");
        Assert.AreEqual(StopReason.Pause, stop!.Reason,
            $"Expected Pause stop; got {stop.Reason}. Substrate didn't serialize Pause correctly amid STOPPING flood.");

        // Resume past Pause — let target proceed to natural exit.
        session.Resume();
    }

    [TestMethod]
    public void AttachAndOwn_VstestTestHost_PauseDuringStoppingFlood_PauseStopSurfaces()
    {
        string targetProject = IntegrationTargetPaths.VstestTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"VSTest target csproj not found at {targetProject}.");

        using VstestRun run = TargetSpawn.Vstest(targetProject, methodFilter: "RunThrowCatchLoop");
        Process dotnetTest = run.Shell;
        try
        {
            int testHostPid = TargetSpawn.ExtractPid(dotnetTest, TimeSpan.FromSeconds(60));

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, new NullSink()))
            {
                AssertPauseStopSurfacesUnderStoppingFlood(session, IntegrationTargetPaths.VstestTargetSource());
            }

            // 10s budget matching other VSTest tests: the [Fact] is past the handshake (its loop's breakpoint
            // was hit), so the bootstrap's exit does not wait on vstest.console's connection timeout.
            bool exitedNaturally = dotnetTest.WaitForExit(10000);
            Assert.IsTrue(exitedNaturally,
                "VSTest dotnet-test bootstrap did not exit naturally within 10s.");
        }
        finally
        {
            run.KillGroup(); // finding 90: the group kill also reaches a testhost orphaned while waiting for a debugger
        }
    }
}
