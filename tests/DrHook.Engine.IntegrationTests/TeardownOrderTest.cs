// Layer 1 — INTEGRATION TEST: drhook_stop of a launched target detaches the debugger BEFORE it asks the target to exit
// (finding 110).
//
// Until 2026-11-16 the owned teardown released the stop and sent SIGTERM with every breakpoint active and the debugger
// attached. A target looping through its breakpoint could hit it, or end a thread, while it died; mscordbi's event thread
// then waited for ever on a read of the dead target and held the DAC lock, so every later attach and launch in the
// process hung (finding 100's cascade). That needs a loaded machine to show: on macbook under CPU load 15 of 40 runs of
// this suite hung. This test checks the order itself, which needs no load: the target's own handler for its exit
// request (SIGTERM, on Windows CTRL+BREAK, ADR-016 6.4) records whether a managed debugger was still attached when the
// request came.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Worker;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class TeardownOrderTest
{
    [TestMethod]
    public async Task Stop_OfAnOwnedTargetAtABreakpoint_DetachesBeforeAskingTheTargetToExit()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("SNAPSHOT_HERE", StringComparison.Ordinal)) + 1;
        Assert.IsTrue(markerLine > 0, "'SNAPSHOT_HERE' marker not found in the snapshot target source.");
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        string report = Path.Combine(Path.GetTempPath(), $"drhook-teardown-order-{Guid.NewGuid():N}.txt");

        using var session = new EngineSteppingSession(new CompositeEventSink());
        int pid = 0;
        try
        {
            // The breakpoint is on a line the target passes every 50 ms: the case in which the old order let the
            // released target hit it again while it was being ended.
            string launch = await session.LaunchAsync(dotnet, new[] { "exec", targetDll, "report-attached-on-exit-request", report },
                cwd: null, source, markerLine, hypothesis: "stops in Worker.Compute", env: null, CancellationToken.None);
            using (JsonDocument launchDoc = JsonDocument.Parse(launch))
            {
                Assert.AreEqual("breakpoint", launchDoc.RootElement.GetProperty("stoppedReason").GetString(), $"launch: {launch}");
                pid = launchDoc.RootElement.GetProperty("pid").GetInt32();
            }

            string stopped = await session.StopAsync("the debugger detaches, then the running target is asked to exit", CancellationToken.None);

            // StopAsync returns after the target has ended, and the handler writes before the target ends.
            Assert.IsTrue(File.Exists(report),
                $"The target's handler for its exit request wrote nothing: the target was not running when it was asked to exit, or it was killed. stop: {stopped}");
            Assert.AreEqual("attached=False", File.ReadAllText(report).Trim(),
                "The target was asked to exit while the debugger was still attached (finding 110's order is detach, then the request).");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
            try { File.Delete(report); } catch { /* best effort */ }
        }
    }
}
