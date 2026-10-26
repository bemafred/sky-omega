// Layer 1 — INTEGRATION TEST for Legacy VSTest path (finding 62; refactored finding 64; redesigned ADR-008 Increment 3).
//
// Validates substrate's AttachAndOwn against a testhost halted by VSTEST_HOST_DEBUG=1.
//
// Lifecycle (ADR-008 / finding 67):
//   - Substrate's AttachAndOwn takes lifecycle ownership of the testhost target for
//     finding-64 race protection (substrate-owned kill ordering).
//   - Substrate's Dispose now (Increment 1, finding 69) performs Stage 1 SIGTERM-then-
//     wait-for-natural-exit, with Stage 2 SIGKILL fallback only against violators.
//     testhost's [Fact] does brief observable work (Increment 2 finding-70-style); xUnit
//     reports test result; testhost exits cleanly; dotnet-test parent + vstest.console
//     cascade.
//   - Test asserts natural exit via Process.WaitForExit(timeout) on the dotnet-test
//     bootstrap AFTER Dispose. The bootstrap's entire process tree (vstest.console +
//     testhost) exits when testhost (its grandchild) exits — that's natural lifecycle,
//     not substrate kill.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class VstestAttachDisposeTest
{
    private sealed class NullSink : IDebugEventSink
    {
        public void OnEvent(string name) { }
    }

    [TestMethod]
    public void AttachAndOwn_VstestTestHost_BriefWork_NaturalExit()
    {
        string targetProject = IntegrationTargetPaths.VstestTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"Legacy VSTest integration target csproj not found at {targetProject}.");

        using VstestRun run = TargetSpawn.Vstest(targetProject, methodFilter: "RunBriefObservableWork");
        Process dotnetTest = run.Shell;
        try
        {
            int testHostPid = TargetSpawn.ExtractPid(dotnetTest, TimeSpan.FromSeconds(60));

            string source = IntegrationTargetPaths.VstestTargetSource();
            int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("TEST_RUNNING_HERE", StringComparison.Ordinal)) + 1;
            Assert.IsTrue(markerLine > 0, $"TEST_RUNNING_HERE marker not found in {source}.");

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, new NullSink()))
            {
                // FINDING 94 — sync on the target's progress, never on the clock. This test used to sleep 500 ms
                // and Dispose, assuming the [Fact] was done by then. It was not: the testhost was still in its
                // vstest.console handshake (or parked at the VSTEST_HOST_DEBUG hold's Break, which nobody
                // resumed), so Dispose's SIGTERM killed it. A kill DURING the handshake leaves vstest.console
                // waiting out its 90 s connection timeout — on elite (4 cores) the 500 ms landed exactly there.
                //
                // Pause for a stop that works however early the attach landed (finding 91), arm a breakpoint on
                // the [Fact]'s first statement (pending until the test assembly loads), then run to it. Its hit
                // proves the handshake is over and the [Fact] is running.
                session.Pause();
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(stop, "No stop within 5s of Pause.");
                int breakpointId = session.SetBreakpointAtLine("IdleFact.cs", markerLine);
                Assert.AreNotEqual(0, breakpointId, $"Failed to set a breakpoint at IdleFact.cs:{markerLine}.");

                // Run to the breakpoint. The hold's Break and the Pause can both queue ahead of it (finding 91's
                // two orderings); each is resumed.
                do
                {
                    session.Resume();
                    stop = session.WaitForStop(TimeSpan.FromSeconds(30));
                    Assert.IsNotNull(stop, "The [Fact] breakpoint was not hit within 30s.");
                }
                while (stop!.Reason is StopReason.Break or StopReason.Pause);
                Assert.AreEqual(StopReason.Breakpoint, stop.Reason);

                // The natural exit this test is about: the [Fact] finishes its brief work, xUnit reports to
                // vstest.console, and the testhost exits by itself while still attached.
                session.Resume();
                stop = session.WaitForStop(TimeSpan.FromSeconds(30));
                Assert.IsNotNull(stop, "The testhost did not exit within 30s of finishing at the breakpoint.");
                Assert.AreEqual(StopReason.ProcessExited, stop!.Reason);

                // session.Dispose() at end of using: the target is already gone, so neither Stage 1 SIGTERM nor
                // Stage 2 SIGKILL has anything to do. No TargetStuckAtDispose anomaly expected.
            }

            // Increment 3 discipline assertion: with the testhost gone after reporting its result, the dotnet-test
            // bootstrap exits naturally — and successfully, because the [Fact] passed.
            bool exitedNaturally = dotnetTest.WaitForExit(10000);
            Assert.IsTrue(exitedNaturally,
                "VSTest dotnet-test bootstrap did not exit naturally within 10s after Dispose — Layer 1 " +
                "discipline violation (testhost may be stuck, or vstest reporting may be hanging).");
            Assert.AreEqual(0, dotnetTest.ExitCode, "dotnet test reported a failure: the [Fact] should have run to completion.");
        }
        finally
        {
            run.KillGroup(); // finding 90: the group kill also reaches a testhost orphaned while waiting for a debugger
        }
    }
}
