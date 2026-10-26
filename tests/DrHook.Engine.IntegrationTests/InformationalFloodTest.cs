// Layer 1 — INTEGRATION TEST for probe 42 (Dispose during _resumeHandler under informational flood).
//
// Promoted to integration tests under ADR-008 Increment 4 / Phase 8 mass promotion.
//
// Substrate-correctness hypothesis (per probe 42 redesigned, finding 65): when substrate's
// pump worker is inside _resumeHandler's controller.Continue(0) COM call processing
// Informational callbacks (CreateThread + ExitThread from Thread.Start/Join in target),
// Dispose must tear down cleanly. dispatch-settle (finding 65) prevents the race against
// concurrent mscordbi dispatch. Substrate must NOT emit WorkerException (would mean the
// _resumeHandler body threw across the pump boundary) and NOT emit WorkerSilentBreak (would
// mean stops fired against a no-stops target — substrate classification bug).
//
// At integration scale: target's RunBriefObservableWork generates the informational flood
// (10 × Thread.Start/Join). Substrate attaches, observes during the flood, Disposes mid-flight.
// Assert: zero WorkerException, zero WorkerSilentBreak; target exits naturally.
//
// SYNC ON THE TARGET, NEVER THE CLOCK (finding 94's rule). Both tests used to sleep after attaching
// (200 ms, or 1 s + 500 ms) and assume the flood was then running. On omega-ubuntu (Linux arm64,
// 2026-10-26) the attach beat the VSTest hold, the [Fact] ran, and the testhost exited inside the
// 1 s wait: the test saw ProcessExited and never disposed during a flood at all. Now each test runs
// to a breakpoint on the work loop's TEST_RUNNING_HERE marker, resumes, and disposes on the first
// CreateThread callback after that: the flood has started and is still running.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class InformationalFloodTest
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class CollectingSink : IDebugEventSink
    {
        private readonly object _lock = new();
        private readonly List<EngineAnomaly> _anomalies = new();
        private int _eventCount;
        private int _floodArmed;

        public int EventCount => Volatile.Read(ref _eventCount);
        public IReadOnlyList<EngineAnomaly> Anomalies
        {
            get { lock (_lock) { return _anomalies.ToArray(); } }
        }

        /// <summary>Set by the first CreateThread callback after <see cref="ArmFlood"/>: the work loop's flood has begun.</summary>
        public ManualResetEventSlim FloodStarted { get; } = new();

        /// <summary>Count CreateThread callbacks from here on as the flood (call while stopped in the work loop).</summary>
        public void ArmFlood() => Volatile.Write(ref _floodArmed, 1);

        public void OnEvent(string name)
        {
            Interlocked.Increment(ref _eventCount);
            if (name == "CreateThread" && Volatile.Read(ref _floodArmed) == 1)
                FloodStarted.Set();
        }

        public void OnAnomaly(EngineAnomaly a) { lock (_lock) { _anomalies.Add(a); } }
    }

    [TestMethod]
    public void AttachAndOwn_MtpTarget_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak()
    {
        string targetExe = IntegrationTargetPaths.MtpTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP target exe not found at {targetExe}.");

        using Process bootstrap = TargetSpawn.Mtp(targetExe, methodFilter: "RunBriefObservableWork");
        try
        {
            int pid = TargetSpawn.ExtractPid(bootstrap, TimeSpan.FromSeconds(30));
            var sink = new CollectingSink();

            string source = IntegrationTargetPaths.MtpTargetSource();
            int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("TEST_RUNNING_HERE", StringComparison.Ordinal)) + 1;
            Assert.IsTrue(markerLine > 0, $"TEST_RUNNING_HERE marker not found in {source}.");

            using (DebugSession session = DebugSession.AttachAndOwn(pid, sink))
            {
                // Pause for a stop however early the attach landed, arm the work-loop breakpoint (pending until the
                // target assembly loads), and run to it, resuming any Pause or Break queued ahead of it.
                session.Pause();
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(stop, "No stop within 5s of Pause.");
                int breakpointId = session.SetBreakpointAtLine("IdleTarget.cs", markerLine);
                Assert.AreNotEqual(0, breakpointId, $"Failed to set a breakpoint at IdleTarget.cs:{markerLine}.");
                do
                {
                    session.Resume();
                    stop = session.WaitForStop(TimeSpan.FromSeconds(30));
                    Assert.IsNotNull(stop, "The work-loop breakpoint was not hit within 30s.");
                }
                while (stop!.Reason is StopReason.Break or StopReason.Pause);
                Assert.AreEqual(StopReason.Breakpoint, stop.Reason);
                Assert.IsTrue(session.RemoveBreakpoint(breakpointId), "Could not remove the work-loop breakpoint.");

                // Resume into the flood. The pump now processes its Informational callbacks, calling _resumeHandler
                // per event; Dispose on the first one, mid-flood.
                sink.ArmFlood();
                session.Resume();
                sink.FloodStarted.Wait(TestContext.CancellationToken);
            }

            // Substrate-correctness assertions:
            EngineAnomaly[] workerExceptions = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException).ToArray();
            Assert.AreEqual(0, workerExceptions.Length,
                $"Expected 0 WorkerException; got {workerExceptions.Length}. _resumeHandler body threw through pump boundary — substrate bug.");

            EngineAnomaly[] workerSilentBreaks = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerSilentBreak).ToArray();
            Assert.AreEqual(0, workerSilentBreaks.Length,
                $"Expected 0 WorkerSilentBreak; got {workerSilentBreaks.Length}. Stop fired against no-stops target — substrate classification bug.");

            // Increment 3 discipline assertion.
            bool exitedNaturally = bootstrap.WaitForExit(5000);
            Assert.IsTrue(exitedNaturally,
                "MTP target did not exit naturally within 5s after Dispose — Layer 1 discipline violation.");
        }
        finally
        {
            try { if (!bootstrap.HasExited) bootstrap.Kill(entireProcessTree: true); } catch { }
        }
    }

    [TestMethod]
    public void AttachAndOwn_VstestTestHost_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak()
    {
        string targetProject = IntegrationTargetPaths.VstestTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"VSTest target csproj not found at {targetProject}.");

        using VstestRun run = TargetSpawn.Vstest(targetProject, methodFilter: "RunBriefObservableWork");
        Process dotnetTest = run.Shell;
        try
        {
            int testHostPid = TargetSpawn.ExtractPid(dotnetTest, TimeSpan.FromSeconds(60));
            var sink = new CollectingSink();

            string source = IntegrationTargetPaths.VstestTargetSource();
            int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("TEST_RUNNING_HERE", StringComparison.Ordinal)) + 1;
            Assert.IsTrue(markerLine > 0, $"TEST_RUNNING_HERE marker not found in {source}.");

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, sink))
            {
                // Pause for a stop however early the attach landed (finding 91: an attach that lands first skips the
                // VSTEST_HOST_DEBUG hold's Break, one that lands later gets it), arm the [Fact] breakpoint (pending
                // until the test assembly loads), and run to it, resuming the Pause and any Break queued ahead of it.
                session.Pause();
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(stop, "No stop within 5s of Pause.");
                int breakpointId = session.SetBreakpointAtLine("IdleFact.cs", markerLine);
                Assert.AreNotEqual(0, breakpointId, $"Failed to set a breakpoint at IdleFact.cs:{markerLine}.");
                do
                {
                    session.Resume();
                    stop = session.WaitForStop(TimeSpan.FromSeconds(30));
                    Assert.IsNotNull(stop, "The [Fact] breakpoint was not hit within 30s.");
                }
                while (stop!.Reason is StopReason.Break or StopReason.Pause);
                Assert.AreEqual(StopReason.Breakpoint, stop.Reason);
                Assert.IsTrue(session.RemoveBreakpoint(breakpointId), "Could not remove the [Fact] breakpoint.");

                // Resume into the flood with no stop pending — the no-stops target this test's hypothesis needs —
                // and Dispose on its first CreateThread callback, mid-flood.
                sink.ArmFlood();
                session.Resume();
                sink.FloodStarted.Wait(TestContext.CancellationToken);
            }

            EngineAnomaly[] workerExceptions = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException).ToArray();
            Assert.AreEqual(0, workerExceptions.Length,
                $"Expected 0 WorkerException; got {workerExceptions.Length}.");

            EngineAnomaly[] workerSilentBreaks = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerSilentBreak).ToArray();
            Assert.AreEqual(0, workerSilentBreaks.Length,
                $"Expected 0 WorkerSilentBreak; got {workerSilentBreaks.Length}.");

            bool exitedNaturally = dotnetTest.WaitForExit(10000);
            Assert.IsTrue(exitedNaturally,
                "VSTest dotnet-test bootstrap did not exit naturally within 10s — Layer 1 discipline violation.");
        }
        finally
        {
            run.KillGroup(); // finding 90: the group kill also reaches a testhost orphaned while waiting for a debugger
        }
    }
}
