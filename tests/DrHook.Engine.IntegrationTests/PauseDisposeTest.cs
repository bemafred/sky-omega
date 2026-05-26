// Layer 1 — INTEGRATION TEST for probe 44 phase B (Pause-stop + Dispose-without-Resume).
//
// Promoted to integration tests under ADR-008 Increment 4 / Phase 8 mass promotion.
//
// Substrate-correctness hypothesis (per probe 44 phase B / finding 59): when the caller
// pauses the target via session.Pause(), the worker classifies the PauseRequest, calls
// _pauseHandler (controller.Stop), publishes a Pause stop, parks at _resume.Take. If
// the caller then Disposes WITHOUT consuming the stop (WaitForStop) and WITHOUT
// Resuming, the substrate must:
//   - Emit exactly 1 WorkerSilentBreak anomaly (honest signal: worker exited via
//     _resume.Take catch when CompleteAdding fired)
//   - NOT crash
//   - NOT emit WorkerException (would mean _pauseHandler / _resumeHandler threw)
//
// With ADR-008 Increment 1's new substrate: after the worker unparks, Dispose proceeds
// to Owned-path Stage 1 SIGTERM. The paused target may or may not respond to SIGTERM
// (mscordbi state during pause is implementation-dependent). If SIGTERM times out,
// Stage 2 SIGKILL fires and TargetStuckAtDispose anomaly surfaces — that's expected
// substrate behavior, not a bug.

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
public sealed class PauseDisposeTest
{
    private sealed class CollectingSink : IDebugEventSink
    {
        private readonly object _lock = new();
        private readonly List<EngineAnomaly> _anomalies = new();
        private int _eventCount;

        public int EventCount => Volatile.Read(ref _eventCount);
        public IReadOnlyList<EngineAnomaly> Anomalies
        {
            get { lock (_lock) { return _anomalies.ToArray(); } }
        }

        public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
        public void OnAnomaly(EngineAnomaly a) { lock (_lock) { _anomalies.Add(a); } }
    }

    [TestMethod]
    public void AttachAndOwn_MtpTarget_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly()
    {
        string targetExe = IntegrationTargetPaths.MtpTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP target exe not found at {targetExe}.");

        // Use a longer naturalExitTimeout — Stage 1 SIGTERM against a paused target may
        // need extra time. If it times out, Stage 2 SIGKILL fires which is expected.
        TimeSpan naturalExitTimeout = TimeSpan.FromMilliseconds(1000);

        using Process bootstrap = TargetSpawn.Mtp(targetExe);
        try
        {
            int pid = TargetSpawn.ExtractPid(bootstrap, TimeSpan.FromSeconds(30));
            var sink = new CollectingSink();

            using (DebugSession session = DebugSession.AttachAndOwn(pid, sink, naturalExitTimeout))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200));

                // Request Pause; wait for Pause stop to arrive.
                session.Pause();
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(3));
                Assert.IsNotNull(stop, "Pause stop did not arrive within 3s.");
                Assert.AreEqual(StopReason.Pause, stop!.Reason);

                // Dispose WITHOUT consuming the stop / resuming. Worker is parked at
                // _resume.Take. CompleteAdding unparks → WorkerSilentBreak anomaly.
            }

            // Substrate-correctness assertions:
            EngineAnomaly[] workerSilentBreaks = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerSilentBreak).ToArray();
            Assert.AreEqual(1, workerSilentBreaks.Length,
                $"Expected exactly 1 WorkerSilentBreak (worker parked at _resume.Take when CompleteAdding fired); got {workerSilentBreaks.Length}.");

            EngineAnomaly[] workerExceptions = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException).ToArray();
            Assert.AreEqual(0, workerExceptions.Length,
                $"Expected 0 WorkerException; got {workerExceptions.Length}. _pauseHandler or _resumeHandler threw — substrate bug.");

            // Increment 3 discipline assertion. Target may have exited via Stage 1 SIGTERM
            // or Stage 2 SIGKILL; either way, it must be gone shortly after Dispose.
            bool exitedNaturally = bootstrap.WaitForExit(5000);
            Assert.IsTrue(exitedNaturally,
                "MTP target did not exit within 5s after Dispose — Layer 1 discipline violation OR substrate's two-stage escalation didn't terminate target.");
        }
        finally
        {
            try { if (!bootstrap.HasExited) bootstrap.Kill(entireProcessTree: true); } catch { }
        }
    }

    [TestMethod]
    public void AttachAndOwn_VstestTestHost_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly()
    {
        string targetProject = IntegrationTargetPaths.VstestTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"VSTest target csproj not found at {targetProject}.");

        TimeSpan naturalExitTimeout = TimeSpan.FromMilliseconds(1000);

        using Process dotnetTest = TargetSpawn.Vstest(targetProject);
        try
        {
            int testHostPid = TargetSpawn.ExtractPid(dotnetTest, TimeSpan.FromSeconds(60));
            var sink = new CollectingSink();

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, sink, naturalExitTimeout))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));

                session.Pause();
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(3));
                Assert.IsNotNull(stop, "Pause stop did not arrive within 3s.");
                Assert.AreEqual(StopReason.Pause, stop!.Reason);
            }

            EngineAnomaly[] workerSilentBreaks = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerSilentBreak).ToArray();
            Assert.AreEqual(1, workerSilentBreaks.Length,
                $"Expected exactly 1 WorkerSilentBreak; got {workerSilentBreaks.Length}.");

            EngineAnomaly[] workerExceptions = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException).ToArray();
            Assert.AreEqual(0, workerExceptions.Length,
                $"Expected 0 WorkerException; got {workerExceptions.Length}.");

            bool exitedNaturally = dotnetTest.WaitForExit(10000);
            Assert.IsTrue(exitedNaturally,
                "VSTest dotnet-test bootstrap did not exit within 10s after Dispose — Layer 1 discipline violation.");
        }
        finally
        {
            try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
        }
    }
}
