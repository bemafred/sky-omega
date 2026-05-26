// Layer 1 — INTEGRATION TEST for probe 45 (Worker-thread exception path).
//
// Promoted to integration tests under ADR-008 Increment 4 / Phase 8 mass promotion.
// Pattern follows Increment 3's discipline-aligned natural-exit pattern (finding 71):
//   - AttachAndOwn (substrate-managed lifecycle)
//   - Target's [TestMethod]/[Fact] does brief observable work + natural exit
//   - Post-Dispose WaitForExit assertion validates Layer 1 discipline
//
// Substrate-correctness hypothesis (per probe 45): a user IDebugEventSink that throws
// from OnEvent must be caught by the pump worker's outer try/catch (EA-4 / CallbackPump.cs)
// and surfaced as a WorkerException anomaly via OnAnomaly. The worker exits cleanly;
// future WaitForStop returns null (substrate doesn't lie about state); Dispose completes
// without throwing.
//
// At integration scale: target's RunBriefObservableWork generates CreateThread + ExitThread
// callbacks per iteration. First OnEvent on the ThrowingSink fires the injection. Substrate's
// outer catch fires WorkerException. Test asserts: exactly 1 WorkerException, WaitForStop
// returns null cleanly, Dispose doesn't throw, target exits naturally.

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
public sealed class WorkerExceptionTest
{
    /// <summary>Throws on first OnEvent invocation; captures OnAnomaly into a thread-safe
    /// list. OnAnomaly MUST NOT throw (IDebugEventSink contract); only OnEvent injects.</summary>
    private sealed class ThrowingSink : IDebugEventSink
    {
        private readonly object _lock = new();
        private readonly List<EngineAnomaly> _anomalies = new();
        private int _onEventCalls;
        private int _throwsArmed = 1;

        public int OnEventCalls => Volatile.Read(ref _onEventCalls);
        public IReadOnlyList<EngineAnomaly> Anomalies
        {
            get { lock (_lock) { return _anomalies.ToArray(); } }
        }

        public void OnEvent(string name)
        {
            Interlocked.Increment(ref _onEventCalls);
            if (Interlocked.Exchange(ref _throwsArmed, 0) == 1)
                throw new InvalidOperationException($"probe-45 integration injection on first OnEvent ('{name}')");
        }

        public void OnAnomaly(EngineAnomaly anomaly)
        {
            lock (_lock) { _anomalies.Add(anomaly); }
        }
    }

    [TestMethod]
    public void AttachAndOwn_MtpTarget_ThrowingSink_WorkerExceptionAnomalyFires()
    {
        string targetExe = IntegrationTargetPaths.MtpTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP target exe not found at {targetExe}.");

        using Process bootstrap = TargetSpawn.Mtp(targetExe);
        try
        {
            int pid = TargetSpawn.ExtractPid(bootstrap, TimeSpan.FromSeconds(30));
            var sink = new ThrowingSink();

            using (DebugSession session = DebugSession.AttachAndOwn(pid, sink))
            {
                // Wait up to 5s for the throw + WorkerException anomaly surfacing.
                // RunBriefObservableWork generates ~10 OnEvent calls over ~500 ms;
                // first one triggers the injection.
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(50);
                    if (sink.Anomalies.Any(a => a.Kind == AnomalyKind.WorkerException)) break;
                }

                Assert.IsTrue(sink.OnEventCalls > 0,
                    $"Sink's OnEvent never fired ({sink.OnEventCalls} calls) — target didn't generate Informational callbacks for substrate to deliver.");

                EngineAnomaly[] workerExceptions = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException).ToArray();
                Assert.AreEqual(1, workerExceptions.Length,
                    $"Expected exactly 1 WorkerException; got {workerExceptions.Length}. Substrate did not catch + surface the throw correctly.");

                // WaitForStop after worker death must return null cleanly (no false stops).
                Stopwatch waitSw = Stopwatch.StartNew();
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(1));
                waitSw.Stop();
                Assert.IsNull(stop, "WaitForStop returned a stop after worker died — substrate state corruption.");
                Assert.IsTrue(waitSw.ElapsedMilliseconds >= 900,
                    $"WaitForStop returned too quickly ({waitSw.ElapsedMilliseconds}ms) — substrate is lying about state.");
            }
            // Dispose ran at end of using; would have thrown above if broken.

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
    public void AttachAndOwn_VstestTestHost_ThrowingSink_WorkerExceptionAnomalyFires()
    {
        string targetProject = IntegrationTargetPaths.VstestTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"VSTest target csproj not found at {targetProject}.");

        using Process dotnetTest = TargetSpawn.Vstest(targetProject);
        try
        {
            int testHostPid = TargetSpawn.ExtractPid(dotnetTest, TimeSpan.FromSeconds(60));
            var sink = new ThrowingSink();

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, sink))
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(50);
                    if (sink.Anomalies.Any(a => a.Kind == AnomalyKind.WorkerException)) break;
                }

                Assert.IsTrue(sink.OnEventCalls > 0,
                    $"Sink's OnEvent never fired ({sink.OnEventCalls} calls).");

                EngineAnomaly[] workerExceptions = sink.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException).ToArray();
                Assert.AreEqual(1, workerExceptions.Length,
                    $"Expected exactly 1 WorkerException; got {workerExceptions.Length}.");

                Stopwatch waitSw = Stopwatch.StartNew();
                StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(1));
                waitSw.Stop();
                Assert.IsNull(stop);
                Assert.IsTrue(waitSw.ElapsedMilliseconds >= 900);
            }

            bool exitedNaturally = dotnetTest.WaitForExit(10000);
            Assert.IsTrue(exitedNaturally,
                "VSTest dotnet-test bootstrap did not exit naturally within 10s after Dispose.");
        }
        finally
        {
            try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
        }
    }
}
