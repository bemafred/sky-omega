// Layer 1 — INTEGRATION TEST: the debugger worker (ADR-016 6.7's design, accepted 2026-11-21).
//
// drhook-mcp's supervisor runs every session in a drhook-worker of its own, and relays the session's tools to it over
// the worker's stdin and stdout. These tests drive the supervisor as drhook-mcp's tools do, with the worker this
// suite's build carries in worker/ beside drhook-mcp: a session through a worker, whose records reach drhook-mcp's
// drain buffers; a target that ends before its breakpoint; drhook-mcp ending with a session open; and a target
// drhook_stop left running, held by its worker and killed through it. The session's own logic keeps its tests in
// this process (SessionEndTest and the others drive EngineSteppingSession directly).

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine.Interop;
using SkyOmega.DrHook.Mcp;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class WorkerTest
{
    // The worker drhook-mcp's build carries, copied beside drhook-mcp into this suite's output.
    private static readonly string WorkerDirectory = Path.Combine(AppContext.BaseDirectory, "worker");

    [TestMethod]
    public async Task ASession_RunsInAWorkerOfItsOwn_ItsAnomaliesReachTheDrain_AndTheWorkerEndsWithIt()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        using var supervisor = new Supervisor(WorkerDirectory);
        int pid = 0;
        try
        {
            string launch = (await supervisor.CallAsync("drhook_launch", Launch(targetDll, source, "SNAPSHOT_HERE"), CancellationToken.None)).Result!;
            using (JsonDocument doc = JsonDocument.Parse(launch))
            {
                Assert.AreEqual("launched", doc.RootElement.GetProperty("status").GetString(), $"launch: {launch}");
                Assert.AreEqual("breakpoint", doc.RootElement.GetProperty("stoppedReason").GetString(), $"launch: {launch}");
                pid = doc.RootElement.GetProperty("pid").GetInt32();
            }
            int worker = supervisor.SessionWorkerPid ?? throw new AssertFailedException("No worker is in session after the launch.");
            Assert.AreNotEqual(Environment.ProcessId, worker, "The session must run in a worker, not in drhook-mcp.");
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(worker, ProcessParentage.ParentOf(pid), "The target is the worker's child: the worker launched it.");

            // depth 999 is clamped, once for the locals and once for the arguments (probe 41): two DepthClamped
            // anomalies, raised in the worker and forwarded to drhook-mcp's buffer.
            string locals = (await supervisor.CallAsync("drhook_locals", new JsonObject { ["hypothesis"] = "doubled and contribution", ["depth"] = 999 }, CancellationToken.None)).Result!;
            StringAssert.Contains(locals, "\"contribution\"", $"locals: {locals}");
            using (JsonDocument drained = JsonDocument.Parse(supervisor.DrainAnomaliesAsJson()))
            {
                int clamped = 0;
                foreach (JsonElement a in drained.RootElement.GetProperty("anomalies").EnumerateArray())
                    if (a.GetProperty("kind").GetString() == "DepthClamped") clamped++;
                Assert.AreEqual(2, clamped, $"drained: {drained.RootElement}");
            }

            string stopped = (await supervisor.CallAsync("drhook_stop", new JsonObject { ["hypothesis"] = "the session ends" }, CancellationToken.None)).Result!;
            StringAssert.Contains(stopped, "\"status\": \"stopped\"", $"stop: {stopped}");
            if (OperatingSystem.IsWindows())
            {
                // No exit request on Windows yet (ADR-016 6.4): the target is left running, held by its worker.
                string killed = (await supervisor.CallAsync("drhook_kill", new JsonObject { ["hypothesis"] = "left running on Windows" }, CancellationToken.None)).Result!;
                StringAssert.Contains(killed, "\"status\": \"killed\"", $"kill: {killed}");
            }
            Assert.IsNull(supervisor.SessionWorkerPid, "No worker is in session once the session has ended.");
            Assert.IsTrue(Ended(worker, TimeSpan.FromSeconds(10)), $"The worker {worker} should end with its session.");
        }
        finally
        {
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }

    [TestMethod]
    public async Task ATargetThatEndsBeforeItsBreakpoint_ItsConsoleReachesTheDrain_AndItsWorkerEnds()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        string report = Path.Combine(Path.GetTempPath(), $"drhook-worker-console-{Guid.NewGuid():N}.txt");
        using var supervisor = new Supervisor(WorkerDirectory);
        try
        {
            JsonObject args = Launch(targetDll, source, "SNAPSHOT_HERE");
            args["args"] = new JsonArray("exec", targetDll, "console-streams", report);
            string launch = (await supervisor.CallAsync("drhook_launch", args, CancellationToken.None)).Result!;
            StringAssert.Contains(launch, "\"stoppedReason\": \"processExited\"", $"launch: {launch}");
            Assert.IsNull(supervisor.SessionWorkerPid, "The session ended with its target: no worker is in session.");

            string console = supervisor.DrainConsoleAsJson();
            StringAssert.Contains(console, "CONSOLE_ISOLATION_STDOUT", $"console: {console}");
            StringAssert.Contains(console, "CONSOLE_ISOLATION_STDERR", $"console: {console}");
        }
        finally
        {
            try { File.Delete(report); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task DrHookMcpEnding_WithASessionOpen_EndsTheSessionAsStopDoes_AndTheWorkerExits()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        var supervisor = new Supervisor(WorkerDirectory);
        int pid = 0;
        try
        {
            string launch = (await supervisor.CallAsync("drhook_launch", Launch(targetDll, source, "SNAPSHOT_HERE"), CancellationToken.None)).Result!;
            using (JsonDocument doc = JsonDocument.Parse(launch)) pid = doc.RootElement.GetProperty("pid").GetInt32();
            long? start = ProcessLifetime.StartOf(pid); // null on Windows
            int worker = supervisor.SessionWorkerPid!.Value;

            supervisor.Dispose(); // drhook-mcp's end: the worker's stdin closes

            Assert.IsTrue(Ended(worker, TimeSpan.FromSeconds(10)), $"The worker {worker} should exit once drhook-mcp has ended.");
            if (!OperatingSystem.IsWindows())
            {
                // As drhook_stop ends it: detached, then asked to exit; the target has no SIGTERM handler, so it ends.
                var waited = Stopwatch.StartNew();
                while (ProcessLifetime.HasEnded(pid, start!.Value) != true && waited.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);
                Assert.AreEqual(true, ProcessLifetime.HasEnded(pid, start.Value), $"The target {pid} should have ended with its session.");
            }
        }
        finally
        {
            supervisor.Dispose();
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }

    [TestMethod]
    public async Task ATargetStopLeftRunning_IsHeldByItsWorker_AndKillEndsItThroughThatWorker()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        string report = Path.Combine(Path.GetTempPath(), $"drhook-worker-left-running-{Guid.NewGuid():N}.txt");
        using var supervisor = new Supervisor(WorkerDirectory);
        int pid = 0;
        try
        {
            JsonObject args = Launch(targetDll, source, "SNAPSHOT_HERE");
            // POSIX: a target whose SIGTERM handler cancels the signal. Windows has no exit request, so any running target.
            if (!OperatingSystem.IsWindows()) args["args"] = new JsonArray("exec", targetDll, "ignore-sigterm", report);
            string launch = (await supervisor.CallAsync("drhook_launch", args, CancellationToken.None)).Result!;
            using (JsonDocument doc = JsonDocument.Parse(launch)) pid = doc.RootElement.GetProperty("pid").GetInt32();
            long? start = ProcessLifetime.StartOf(pid);
            int worker = supervisor.SessionWorkerPid!.Value;

            string stopped = (await supervisor.CallAsync("drhook_stop", new JsonObject { ["hypothesis"] = "it refuses to exit" }, CancellationToken.None)).Result!;
            StringAssert.Contains(stopped, "\"targetLeftRunning\": true", $"stop: {stopped}");
            Assert.IsNull(supervisor.SessionWorkerPid, "The session has ended.");
            Assert.IsFalse(Ended(worker, TimeSpan.FromMilliseconds(500)), "The worker holds the target left running, so it stays.");

            string killed = (await supervisor.CallAsync("drhook_kill", new JsonObject { ["hypothesis"] = "it refused to exit; end it" }, CancellationToken.None)).Result!;
            using (JsonDocument doc = JsonDocument.Parse(killed))
            {
                Assert.AreEqual("killed", doc.RootElement.GetProperty("status").GetString(), $"kill: {killed}");
                Assert.AreEqual(pid, doc.RootElement.GetProperty("pid").GetInt32(), $"kill: {killed}");
                Assert.AreEqual("detached", doc.RootElement.GetProperty("killedWhile").GetString(), $"kill: {killed}");
            }
            if (start is long started) Assert.AreEqual(true, ProcessLifetime.HasEnded(pid, started), $"The target {pid} should have ended.");
            Assert.IsTrue(Ended(worker, TimeSpan.FromSeconds(10)), $"The worker {worker} should end once its target has.");

            string again = (await supervisor.CallAsync("drhook_kill", new JsonObject { ["hypothesis"] = "nothing is left" }, CancellationToken.None)).Result!;
            StringAssert.Contains(again, "no target that drhook_stop left running", $"second kill: {again}");
        }
        finally
        {
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
            try { File.Delete(report); } catch { /* best effort */ }
        }
    }

    // drhook_launch's arguments as drhook-mcp's tool passes them: dotnet exec the target, with a breakpoint at the line
    // that carries the marker.
    private static JsonObject Launch(string targetDll, string source, string marker)
    {
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        int line = Array.FindIndex(File.ReadAllLines(source), l => l.Contains(marker, StringComparison.Ordinal)) + 1;
        Assert.IsTrue(line > 0, $"'{marker}' marker not found in {source}.");
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return new JsonObject
        {
            ["program"] = dotnet,
            ["args"] = new JsonArray("exec", targetDll),
            ["cwd"] = null,
            ["sourceFile"] = source,
            ["line"] = line,
            ["hypothesis"] = "stops in Worker.Compute",
            ["env"] = null,
        };
    }

    // Whether the process with this PID has ended within the bound. The worker is drhook-mcp's child here as well, the
    // test's: Process reads its end.
    private static bool Ended(int pid, TimeSpan bound)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.WaitForExit(bound);
        }
        catch (ArgumentException) { return true; }
    }
}
