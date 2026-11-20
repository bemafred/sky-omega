// Layer 1 — INTEGRATION TEST (finding 61 vocabulary; refactored finding 64; redesigned ADR-008 Increment 3).
//
// Substrate validation for AttachAndOwn against an MTP-shaped target using MTP's
// --debug as the attach-handshake mechanism.
//
// Lifecycle (ADR-008 / finding 67):
//   - Substrate's AttachAndOwn takes lifecycle ownership of the target Process for
//     finding-64 race protection (substrate-owned kill ordering).
//   - Substrate's Dispose now (Increment 1, finding 69) performs Stage 1 SIGTERM-then-
//     wait-for-natural-exit, with Stage 2 SIGKILL fallback only against discipline
//     violators. CoreCLR's default SIGTERM disposition exits the target cleanly.
//   - Target's [TestMethod] (Increment 2, finding 70) does brief observable work
//     then returns naturally; MTP completes test reporting; testhost exits.
//   - Test asserts natural exit via Process.WaitForExit(timeout) AFTER Dispose —
//     validates Layer 1 discipline at the integration-test layer.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class AttachDisposeTest
{
    private sealed class NullSink : IDebugEventSink
    {
        public void OnEvent(string name) { }
    }

    [TestMethod]
    public void AttachAndOwn_MtpTarget_BriefWork_NaturalExit()
    {
        string targetExe = IntegrationTargetPaths.MtpTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP integration target exe not found at {targetExe}.");

        using Process bootstrap = TargetSpawn.Mtp(targetExe, methodFilter: "RunBriefObservableWork");
        try
        {
            int pid = TargetSpawn.ExtractPid(bootstrap, TimeSpan.FromSeconds(30));

            using (DebugSession session = DebugSession.AttachAndOwn(pid, new NullSink()))
            {
                // Into the [TestMethod] first (TheWork): an attach that ends while MTP's --debug loop still waits leaves
                // the target waiting for ever. This test slept 200 ms here, which passed only because Dispose then
                // ended the waiting target, with SIGTERM or (on Windows) the kill.
                TheWork.RunTo(session, IntegrationTargetPaths.MtpTargetSource());
                session.Resume();

                // session.Dispose() runs at end of using: it detaches, then (Linux, macOS) sends SIGTERM and waits
                // NaturalExitTimeout. Windows has no exit request yet (ADR-016 6.4): the target, mid-[TestMethod], is
                // left running and finishes its work by itself.
            }

            // Increment 3 discipline assertion: bootstrap (the OS Process) MUST have
            // exited within a reasonable post-Dispose window. The [TestMethod]'s remaining
            // work and MTP's test-reporting teardown total well under 5 seconds.
            bool exitedNaturally = bootstrap.WaitForExit(5000);
            Assert.IsTrue(exitedNaturally,
                "MTP target did not exit naturally within 5s after Dispose — Layer 1 discipline violation " +
                "(substrate may have left target stuck, OR target is mis-implemented).");
        }
        finally
        {
            // Defensive fallback only — if natural-exit assertion succeeded, no-op.
            try { if (!bootstrap.HasExited) bootstrap.Kill(entireProcessTree: true); } catch { }
        }
    }
}
