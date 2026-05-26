// Layer 3 — MTP integration target test (finding 61 vocabulary; ADR-008 Increment 3 redesign).
//
// One [TestMethod] that gives the integration-test layer a real-shaped MTP exe to
// debug. The signaling mechanism is MTP-native:
//
//   When the integration test launches this exe with `--debug`, MTP prints
//   "Waiting for debugger to attach... Process Id: NNNN, Name: ..." on stdout and
//   blocks until Debugger.IsAttached becomes true. The integration test parses the
//   PID, calls DebugSession.Attach/AttachAndOwn, and MTP then proceeds to run this
//   [TestMethod].
//
// LIFECYCLE DISCIPLINE (ADR-008 / finding 67 / Increment 3): finite observable work
// then natural exit. Previously Thread.Sleep(30s) — Layer 1 violator (target required
// substrate kill or caller kill to terminate within reasonable test budget). Now: brief
// Thread.Start/Join work (~500 ms) generates a small handful of mscordbi callbacks for
// substrate observation, then the [TestMethod] returns. MTP's test orchestration then
// finishes naturally — testhost reports results, exits cleanly. Substrate's Dispose
// observes the natural exit via Stage 1 SIGTERM and finding 66 death-detection (or
// the target may already be on its way out by the time Dispose runs).
//
// The integration test asserts natural exit via Process.WaitForExit(timeout) after
// session.Dispose() — substrate-aligned discipline validation, not an arbitrary
// timeout-and-kill pattern.

using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DrHook.Engine.IntegrationTargets.Mtp;

[TestClass]
public sealed class IdleTarget
{
    [TestMethod]
    public void RunBriefObservableWork()
    {
        // MTP's --debug option handled the wait-for-debugger handshake before
        // this method was invoked. Debugger.IsAttached is true here (or the
        // launcher chose not to use --debug, which still works).
        //
        // Brief observable work: 10 Thread.Start/Join × ~50 ms = ~500 ms total.
        // Generates CreateThread + ExitThread mscordbi callbacks per iteration
        // for substrate observation, then this [TestMethod] returns naturally.
        // MTP completes test reporting; testhost exits naturally.
        for (int i = 0; i < 10; i++)
        {
            Thread t = new(static () => Thread.Sleep(20)) { IsBackground = true };
            t.Start();
            t.Join();
            Thread.Sleep(30);
        }
    }

    // ADR-008 Increment 4c (probe 43 promotion): generates STOPPING (Exception) mscordbi
    // callbacks via throw/catch in a tight loop. Pairs with ConcurrentPauseStopTest which
    // attaches during this method's execution and tests substrate's pump serialization of
    // concurrent PauseRequest + STOPPING events.
    //
    // Selected via MTP --filter "FullyQualifiedName~RunThrowCatchLoop" so existing
    // Phase-8a tests (which assert zero WorkerSilentBreak — no STOPPING events) are
    // unaffected.
    [TestMethod]
    public void RunThrowCatchLoop()
    {
        // Bounded throw/catch loop. Each iteration produces a STOPPING (Exception)
        // callback. Total natural runtime ~500 ms.
        for (int i = 0; i < 50; i++)
        {
            try { throw new InvalidOperationException("drhook-integration-stopping"); }
            catch { /* first-chance Exception callback */ }
            Thread.Sleep(10);
        }
    }
}
