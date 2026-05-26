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
// generating Exception (STOPPING) callbacks. Substrate attaches; receives Break stop from
// --debug startup; arms a no-match exception filter (auto-resumes all Exception stops via
// substrate's WaitForStop loop); Resumes past Break; calls session.Pause(); awaits Pause
// stop arrival. Assertion: Pause stop arrives within reasonable budget despite STOPPING flood.

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
                AssertPauseStopSurfacesUnderStoppingFlood(session);
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

    /// <summary>The substrate-correctness assertion shared by both MTP + VSTest variants.
    /// Arm exception filter (so Exception stops auto-resume); allow target to make progress
    /// in the throw/catch loop; PauseRequest; assert a Pause stop arrives despite the
    /// STOPPING flood. Consumes any initial Break stop transparently — substrate-correctness
    /// hypothesis is about Pause-stop-surfacing-under-STOPPING-flood, not the initial
    /// stop type (which differs between MTP --debug and VSTest VSTEST_HOST_DEBUG).</summary>
    private static void AssertPauseStopSurfacesUnderStoppingFlood(DebugSession session)
    {
        // Arm no-match exception filter — substrate's WaitForStop auto-resumes Exception
        // stops via the filter loop. Pause and Break stops surface unaltered.
        session.ArmExceptionFilter("NoSuchTypeWillMatch");

        // Brief settle — target makes progress (Break stop fires if runner uses
        // Debugger.Break(); Exception stops from throw/catch fire and auto-resume).
        Thread.Sleep(100);

        // Issue PauseRequest while target is mid-flood.
        session.Pause();

        // Drain pending stops until Pause arrives. With filter armed, Exception stops
        // auto-resume — only Break and Pause surface. Break may queue ahead of Pause
        // (substrate FIFO order); consume it via Resume.
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(stop, "No stop arrived within 5s after Pause request — substrate FIFO serialization may have dropped the request.");
        if (stop!.Reason == StopReason.Break)
        {
            // MTP --debug startup Break stop — consume and await Pause.
            session.Resume();
            stop = session.WaitForStop(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(stop, "Pause stop did not arrive within 5s after consuming Break.");
        }
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

        using Process dotnetTest = TargetSpawn.Vstest(targetProject, methodFilter: "RunThrowCatchLoop");
        try
        {
            int testHostPid = TargetSpawn.ExtractPid(dotnetTest, TimeSpan.FromSeconds(60));

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, new NullSink()))
            {
                AssertPauseStopSurfacesUnderStoppingFlood(session);
            }

            // 10s budget matching other VSTest tests. Earlier iterations of this test
            // needed 30s because the original WaitForStop pattern (expecting Break first)
            // failed assertion in VSTest variant — substrate's Dispose then got tangled in
            // vstest tree teardown. After refactoring to arm filter first + drain
            // transparently (AssertPauseStopSurfacesUnderStoppingFlood), success path runs
            // in <1s; 10s is comfortable.
            bool exitedNaturally = dotnetTest.WaitForExit(10000);
            Assert.IsTrue(exitedNaturally,
                "VSTest dotnet-test bootstrap did not exit naturally within 10s.");
        }
        finally
        {
            try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
        }
    }
}
