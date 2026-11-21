// Layer 1 — INTEGRATION TEST: the ordinary and the extraordinary session end, as ADR-016 Step 6 decided them.
//
// 6.2 (replacing ADR-008's Decision 1): drhook_stop of a launched target detaches, then asks it to exit. A target that
// refuses is left running and reported, never killed as cleanup; drhook_kill then ends it after the session is gone,
// once it has checked that the PID still names that process. Windows has no exit request yet (6.4), so a running
// launched target is left running there at once.
//
// 6.3 (replacing ADR-008's Decision 3): drhook_kill stops the target first and kills it while stopped, so no request of
// the debugger's is in flight when it dies (findings 104, 105). A target held at a stop is stopped already; a running
// one is paused. Afterwards the debugger in the same process must still work.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Interop;
using SkyOmega.DrHook.Worker;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class SessionEndTest
{
    private const string EntryModule = "DrHookSnapshotTarget";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Stop_OfALaunchedTargetThatRefusesToExit_LeavesItRunning_AndKillEndsIt()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        int markerLine = MarkerLine(source, "SNAPSHOT_HERE");
        string report = Path.Combine(Path.GetTempPath(), $"drhook-left-running-{Guid.NewGuid():N}.txt");
        // POSIX: a target whose SIGTERM handler cancels the signal. Windows has no exit request, so any running target.
        string[] args = OperatingSystem.IsWindows()
            ? new[] { "exec", targetDll }
            : new[] { "exec", targetDll, "ignore-sigterm", report };

        using var session = new EngineSteppingSession(new CompositeEventSink());
        int pid = 0;
        try
        {
            string launch = await session.LaunchAsync(Dotnet(), args, cwd: null, source, markerLine,
                hypothesis: "stops in Worker.Compute", env: null, CancellationToken.None);
            pid = LaunchedPid(launch);
            long? start = ProcessLifetime.StartOf(pid);   // read while it runs; null on Windows

            string stopped = await session.StopAsync("the target refuses its exit request and is left running", CancellationToken.None);
            using (JsonDocument doc = JsonDocument.Parse(stopped))
            {
                JsonElement root = doc.RootElement;
                Assert.AreEqual("stopped", root.GetProperty("status").GetString(), $"stop: {stopped}");
                Assert.IsTrue(root.TryGetProperty("targetLeftRunning", out JsonElement left) && left.GetBoolean(), $"stop: {stopped}");
                Assert.AreEqual(pid, root.GetProperty("pid").GetInt32(), $"stop: {stopped}");
                Assert.IsFalse(root.TryGetProperty("targetExit", out _), $"a target left running has no end to report yet: {stopped}");
                StringAssert.Contains(root.GetProperty("prompt").GetString(), "drhook_kill", $"stop: {stopped}");
            }
            Assert.IsFalse(HasEnded(pid, start), $"Target {pid} should still run after drhook_stop left it running.");
            if (!OperatingSystem.IsWindows())
            {
                // The request reached the target after the detach, and the target refused it.
                Assert.IsTrue(File.Exists(report), "The target's SIGTERM handler wrote nothing: the exit request never reached it.");
                Assert.AreEqual("attached=False", File.ReadAllText(report).Trim(), "The exit request reached the target while the debugger was attached.");
            }

            string killed = await session.KillAsync("the target refused to exit; end it", leftRunningPid: null, CancellationToken.None);
            using (JsonDocument doc = JsonDocument.Parse(killed))
            {
                JsonElement root = doc.RootElement;
                Assert.AreEqual("killed", root.GetProperty("status").GetString(), $"kill: {killed}");
                Assert.AreEqual(pid, root.GetProperty("pid").GetInt32(), $"kill: {killed}");
                Assert.AreEqual("detached", root.GetProperty("killedWhile").GetString(), $"kill: {killed}");
                AssertKilled(root.GetProperty("targetExit"), killed);
            }
            Assert.IsTrue(HasEnded(pid, start), $"Target {pid} should have ended after drhook_kill.");
            Assert.IsTrue(Reaped(pid), $"Target {pid} was left a zombie of this process after drhook_kill.");

            // Nothing is left to end, so a second kill without a session says so.
            string again = await session.KillAsync("nothing is left", leftRunningPid: null, CancellationToken.None);
            StringAssert.Contains(again, "no target that drhook_stop left running", $"second kill: {again}");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
            try { File.Delete(report); } catch { /* best effort */ }
        }
    }

    // Since finding 110 an owned stop detaches before the target ends, so mscordbi's PAL, which reaps only a child it
    // still debugs (finding 87), no longer reaped it: every launched target drhook_stop ended stayed a zombie of the
    // DrHook process (zbook, 2026-11-20). The session now reaps it once its end has been read.
    [TestMethod]
    public async Task Stop_OfALaunchedTargetThatExits_LeavesNoZombie()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows has no zombies, and no exit request yet: a running launched target is left running.");
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();

        using var session = new EngineSteppingSession(new CompositeEventSink());
        int pid = 0;
        try
        {
            string launch = await session.LaunchAsync(Dotnet(), new[] { "exec", targetDll }, cwd: null, source,
                MarkerLine(source, "SNAPSHOT_HERE"), hypothesis: "stops in Worker.Compute", env: null, CancellationToken.None);
            pid = LaunchedPid(launch);

            string stopped = await session.StopAsync("the target has no SIGTERM handler, so it ends on the request", CancellationToken.None);
            using JsonDocument doc = JsonDocument.Parse(stopped);
            Assert.AreEqual("killed", doc.RootElement.GetProperty("targetExit").GetProperty("kind").GetString(), $"stop: {stopped}");
            Assert.AreEqual(15, doc.RootElement.GetProperty("targetExit").GetProperty("signal").GetInt32(), $"stop: {stopped}");
            Assert.IsTrue(Reaped(pid), $"Target {pid} was left a zombie of this process after drhook_stop.");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }

    [TestMethod]
    public async Task Kill_AtABreakpointStop_KillsTheTargetWhileItIsStopped()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();

        using var session = new EngineSteppingSession(new CompositeEventSink());
        int pid = 0;
        try
        {
            string launch = await session.LaunchAsync(Dotnet(), new[] { "exec", targetDll }, cwd: null, source,
                MarkerLine(source, "SNAPSHOT_HERE"), hypothesis: "stops in Worker.Compute", env: null, CancellationToken.None);
            pid = LaunchedPid(launch);
            long? start = ProcessLifetime.StartOf(pid);   // read while it is held at the breakpoint; null on Windows

            string killed = await session.KillAsync("held at a breakpoint: killed while stopped", leftRunningPid: null, CancellationToken.None);
            using JsonDocument doc = JsonDocument.Parse(killed);
            JsonElement root = doc.RootElement;
            Assert.AreEqual("killed", root.GetProperty("status").GetString(), $"kill: {killed}");
            Assert.AreEqual("stopped", root.GetProperty("killedWhile").GetString(), $"kill: {killed}");
            AssertKilled(root.GetProperty("targetExit"), killed);
            Assert.IsTrue(HasEnded(pid, start), $"Target {pid} should have ended after drhook_kill.");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }

    [TestMethod]
    public void Abandon_OfARunningTarget_StopsItFirst_AndTheNextLaunchWorks()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");

        var sink = new NullSink();
        DebugSession first = DebugSession.Launch(Dotnet(), new[] { targetDll }, workingDirectory: null, sink: sink, entryModule: EntryModule);
        int pid = first.ProcessId;
        try
        {
            Assert.AreEqual(StopReason.EntryModuleLoaded, first.WaitForStop(TimeSpan.FromSeconds(20))?.Reason, "the entry-module hold");
            first.Resume();          // no breakpoint: the target runs its loop
            Thread.Sleep(500);
            Assert.IsNull(first.WaitForStop(TimeSpan.Zero), "the target should be running, not at a stop");

            var clock = Stopwatch.StartNew();
            AbandonOutcome outcome = first.Abandon();
            TestContext.WriteLine($"Abandon of a running target: {outcome}, {clock.ElapsedMilliseconds} ms (stop bound {DebugSession.DefaultAbandonStopBound.TotalMilliseconds} ms)");
            Assert.AreEqual(AbandonOutcome.KilledWhileStopped, outcome, "A running target that answers a pause is stopped before the kill.");

            TargetExit? exit = first.TargetExitWithin(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(exit, "The killed target's end was not read within 5 s.");
            if (OperatingSystem.IsWindows()) Assert.AreEqual(TargetExitKind.Exited, exit!.Kind, exit.Description);
            else
            {
                // The OS record, or the kill DrHook sent when mscordbi collected that record first (sentByDrHook).
                Assert.AreEqual(TargetExitKind.Killed, exit!.Kind, exit.Description);
                Assert.AreEqual(9, exit.Signal, exit.Description);
            }
        }
        finally
        {
            try { first.Dispose(); } catch { /* idempotent */ }
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        // The debugger in this process must still launch and stop a target.
        DebugSession second = DebugSession.Launch(Dotnet(), new[] { targetDll }, workingDirectory: null, sink: sink, entryModule: EntryModule);
        int secondPid = second.ProcessId;
        try
        {
            Assert.AreEqual(StopReason.EntryModuleLoaded, second.WaitForStop(TimeSpan.FromSeconds(20))?.Reason,
                "After the kill, the next launch in the same process should reach its entry-module hold.");
        }
        finally
        {
            try { second.Abandon(); } catch { /* best effort */ }
            try { Process.GetProcessById(secondPid).Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    // A kill always reads as one. For a target killed while the debugger is attached, mscordbi's PAL can collect the OS
    // record before DrHook's wait reads it (finding 87; once in about 20 kills at a breakpoint on zbook, 2026-11-20).
    // DrHook then reports the signal it sent, marked sentByDrHook (TargetExit.OrTheKillDrHookSent).
    private static void AssertKilled(JsonElement exit, string result)
    {
        if (OperatingSystem.IsWindows())
        {
            // TerminateProcess: an exit code, not a signal (TargetExitTest).
            Assert.AreEqual("exited", exit.GetProperty("kind").GetString(), $"targetExit: {result}");
        }
        else
        {
            Assert.AreEqual("killed", exit.GetProperty("kind").GetString(), $"targetExit: {result}");
            Assert.AreEqual(9, exit.GetProperty("signal").GetInt32(), $"targetExit: {result}");
        }
    }

    // Whether the target has ended, read as DebugSession.TargetHasExited reads it. Linux and macOS: by its PID and its
    // start time (ProcessLifetime), so a zombie, ended and not yet reaped, has ended, and a process that took the PID later
    // is not the target. Process.HasExited is no reading there: for a process this one did not Process.Start it reports a
    // zombie as running (probe 106), which failed this test on macOS (CI 44801380410), where the reap had not yet run.
    // Windows has no zombies, and its Process reads the end.
    private static bool HasEnded(int pid, long? start)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (start is not long started) throw new InvalidOperationException($"The start time of target {pid} was not read while it ran.");
            return ProcessLifetime.HasEnded(pid, started) ?? throw new InvalidOperationException($"The state of process {pid} could not be read.");
        }
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    // True once no process has the PID within 2 s: the target was reaped, not left a zombie. `ps` prints the state of a
    // zombie (Z) and nothing for a PID with no process, on Linux and macOS alike. True on Windows, which has no zombies.
    private static bool Reaped(int pid)
    {
        if (OperatingSystem.IsWindows()) return true;
        var waited = Stopwatch.StartNew();
        while (true)
        {
            using Process ps = Process.Start(new ProcessStartInfo("ps", $"-o stat= -p {pid}") { RedirectStandardOutput = true })!;
            string state = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            if (state.Length == 0) return true;
            if (waited.ElapsedMilliseconds > 2000) return false;
            Thread.Sleep(50);
        }
    }

    private static int LaunchedPid(string launch)
    {
        using JsonDocument doc = JsonDocument.Parse(launch);
        Assert.AreEqual("breakpoint", doc.RootElement.GetProperty("stoppedReason").GetString(), $"launch: {launch}");
        return doc.RootElement.GetProperty("pid").GetInt32();
    }

    private static int MarkerLine(string source, string token)
    {
        int line = Array.FindIndex(File.ReadAllLines(source), l => l.Contains(token, StringComparison.Ordinal)) + 1;
        Assert.IsTrue(line > 0, $"'{token}' marker not found in the snapshot target source.");
        return line;
    }

    private static string Dotnet()
    {
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private sealed class NullSink : IDebugEventSink
    {
        public void OnEvent(string name) { }
        public void OnAnomaly(EngineAnomaly anomaly) { }
    }
}
