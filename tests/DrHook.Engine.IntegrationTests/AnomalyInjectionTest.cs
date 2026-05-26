// Layer 1 — INTEGRATION TEST for probe 41 (anomaly injection: DepthClamped via GetLocals(depth>10)).
//
// Promoted to integration tests under ADR-008 Increment 4b / Phase 8 mass promotion.
//
// Substrate-correctness hypothesis (per probe 41 / finding 56): when caller invokes
// session.GetLocals(depth=N) with N > DebugSession.MaxInspectionDepth (=10), substrate
// clamps the depth to 10 and emits an AnomalyKind.DepthClamped anomaly with
// requested=N / clamped=10 context. The full anomaly path is exercised:
//   per-request capture (DebugSession.GetLocals call site) →
//   IDebugEventSink.OnAnomaly → caller's drain.
//
// At integration scale: substrate attaches, target runner fires a Debugger.Break-style
// stop (MTP --debug and VSTest VSTEST_HOST_DEBUG=1 both halt testhost via Debugger.Break,
// which surfaces to the substrate as StopReason.Break). Test calls GetLocals(depth=999)
// at the Break stop — substrate emits anomaly with requested=999/clamped=10 context,
// test resumes (so target can natural-exit), Dispose. Any stop with a valid _stopThread
// suffices for this scenario — DepthClamped fires on the REQUEST, not on the locals'
// actual depth.

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
public sealed class AnomalyInjectionTest
{
    private sealed class CollectingSink : IDebugEventSink
    {
        private readonly object _lock = new();
        private readonly List<EngineAnomaly> _anomalies = new();
        public IReadOnlyList<EngineAnomaly> Anomalies
        {
            get { lock (_lock) { return _anomalies.ToArray(); } }
        }
        public void OnEvent(string name) { }
        public void OnAnomaly(EngineAnomaly a) { lock (_lock) { _anomalies.Add(a); } }
    }

    [TestMethod]
    public void AttachAndOwn_MtpTarget_GetLocalsExcessiveDepth_DepthClampedAnomalyFires()
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
                // MTP --debug fires a Debugger.Break() at startup; that surfaces as a
                // Break stop. Any stop with a valid _stopThread suffices for the
                // GetLocals(depth=999) injection — the anomaly fires on the REQUEST.
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(stop, "Stop did not arrive within 5s.");
                Assert.IsTrue(stop!.Reason == StopReason.Break || stop.Reason == StopReason.Breakpoint,
                    $"Expected Break or Breakpoint stop; got {stop.Reason}.");

                // Trigger DepthClamped: ask for depth=999, substrate clamps to
                // MaxInspectionDepth=10 and emits anomaly.
                _ = session.GetLocals(depth: 999);

                // Resume so target can complete its [TestMethod] and exit naturally.
                session.Resume();
            }

            // Substrate-correctness assertion: exactly 1 DepthClamped anomaly with the expected context.
            EngineAnomaly[] depthClamped = sink.Anomalies.Where(a => a.Kind == AnomalyKind.DepthClamped).ToArray();
            Assert.AreEqual(1, depthClamped.Length,
                $"Expected exactly 1 DepthClamped anomaly; got {depthClamped.Length}. " +
                $"All anomalies: [{string.Join(", ", sink.Anomalies.Select(a => a.Kind.ToString()))}]");

            EngineAnomaly a = depthClamped[0];
            Assert.IsTrue(a.Context is not null && a.Context.ContainsKey("requested"),
                "DepthClamped anomaly missing 'requested' context.");
            Assert.AreEqual("999", a.Context!["requested"],
                $"DepthClamped 'requested' context = {a.Context["requested"]}; expected '999'.");

            // Layer 1 discipline assertion.
            bool exitedNaturally = bootstrap.WaitForExit(5000);
            Assert.IsTrue(exitedNaturally,
                "MTP target did not exit naturally within 5s after Dispose.");
        }
        finally
        {
            try { if (!bootstrap.HasExited) bootstrap.Kill(entireProcessTree: true); } catch { }
        }
    }

    [TestMethod]
    public void AttachAndOwn_VstestTestHost_GetLocalsExcessiveDepth_DepthClampedAnomalyFires()
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
                // VSTEST_HOST_DEBUG=1 halts testhost via Debugger.Break() → Break stop.
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(stop, "Stop did not arrive within 5s.");
                Assert.IsTrue(stop!.Reason == StopReason.Break || stop.Reason == StopReason.Breakpoint,
                    $"Expected Break or Breakpoint stop; got {stop.Reason}.");

                _ = session.GetLocals(depth: 999);

                session.Resume();
            }

            EngineAnomaly[] depthClamped = sink.Anomalies.Where(a => a.Kind == AnomalyKind.DepthClamped).ToArray();
            Assert.AreEqual(1, depthClamped.Length,
                $"Expected exactly 1 DepthClamped anomaly; got {depthClamped.Length}. " +
                $"All anomalies: [{string.Join(", ", sink.Anomalies.Select(a => a.Kind.ToString()))}]");

            Assert.AreEqual("999", depthClamped[0].Context!["requested"]);

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
