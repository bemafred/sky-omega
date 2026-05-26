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
    public void AttachAndOwn_MtpTarget_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak()
    {
        string targetExe = IntegrationTargetPaths.MtpTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP target exe not found at {targetExe}.");

        using Process bootstrap = TargetSpawn.Mtp(targetExe, methodFilter: "RunBriefObservableWork");
        try
        {
            int pid = TargetSpawn.ExtractPid(bootstrap, TimeSpan.FromSeconds(30));
            var sink = new CollectingSink();

            using (DebugSession session = DebugSession.AttachAndOwn(pid, sink))
            {
                // Brief observation window — target's [TestMethod] starts running
                // Thread.Start/Join iterations. Substrate's pump processes Informational
                // callbacks, calling _resumeHandler per event. Dispose happens mid-flood.
                Thread.Sleep(TimeSpan.FromMilliseconds(200));
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

        using Process dotnetTest = TargetSpawn.Vstest(targetProject, methodFilter: "RunBriefObservableWork");
        try
        {
            int testHostPid = TargetSpawn.ExtractPid(dotnetTest, TimeSpan.FromSeconds(60));
            var sink = new CollectingSink();

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, sink))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
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
            try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
        }
    }
}
