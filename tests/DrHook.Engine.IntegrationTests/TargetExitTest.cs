// Layer 1 — INTEGRATION TEST: a session's end says how the target ended, from the operating system's record.
//
// Before 2026-11-07 every exit was reported as "The debuggee exited (ran to completion)", with no exit code, whatever
// ended the target: an exit code of 3 and a SIGKILL read the same. drhook_stop of an owned target said "target
// gracefully terminated" without knowing. The engine had the status and discarded it (waitpid(pid, out int _, 0)).
//
// Each test drives the real MCP-layer session (EngineSteppingSession) against the snapshot target: launch to a marker
// line, then end the session the way the test names, and read "targetExit" from the result.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Mcp;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class TargetExitTest
{
    [TestMethod]
    public async Task Continue_ToTheTargetsExit_ReportsItsExitCode()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("EXIT_HERE", StringComparison.Ordinal)) + 1;
        Assert.IsTrue(markerLine > 0, "'EXIT_HERE' marker not found in the snapshot target source.");
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        using var session = new EngineSteppingSession();
        int pid = 0;
        try
        {
            string launch = await session.LaunchAsync(dotnet, new[] { "exec", targetDll, "exit-with", "3" }, cwd: null,
                source, markerLine, hypothesis: "stops at Environment.Exit(3)", env: null, CancellationToken.None);
            using (JsonDocument launchDoc = JsonDocument.Parse(launch))
            {
                Assert.AreEqual("breakpoint", launchDoc.RootElement.GetProperty("stoppedReason").GetString(), $"launch: {launch}");
                pid = launchDoc.RootElement.GetProperty("pid").GetInt32();
            }

            string ended = await session.ContinueAsync("the target exits with code 3", waitForBreakpoint: true, CancellationToken.None);

            using JsonDocument endedDoc = JsonDocument.Parse(ended);
            JsonElement root = endedDoc.RootElement;
            Assert.AreEqual("processExited", root.GetProperty("stoppedReason").GetString(), $"continue: {ended}");
            Assert.IsTrue(root.TryGetProperty("targetExit", out JsonElement exit), $"no targetExit in: {ended}");
            Assert.AreEqual("exited", exit.GetProperty("kind").GetString(), $"targetExit: {exit}");
            Assert.AreEqual(3, exit.GetProperty("code").GetInt32(), $"targetExit: {exit}");
            string prompt = root.GetProperty("prompt").GetString()!;
            StringAssert.Contains(prompt, "exited with code 3", $"prompt: {prompt}");
            Assert.IsFalse(prompt.Contains("ran to completion", StringComparison.Ordinal), $"prompt claims what nothing showed: {prompt}");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }

    [TestMethod]
    public async Task Continue_ToTheTargetsKill_ReportsWhatKilledIt()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("KILL_HERE", StringComparison.Ordinal)) + 1;
        Assert.IsTrue(markerLine > 0, "'KILL_HERE' marker not found in the snapshot target source.");
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        using var session = new EngineSteppingSession();
        int pid = 0;
        try
        {
            string launch = await session.LaunchAsync(dotnet, new[] { "exec", targetDll, "kill-self" }, cwd: null,
                source, markerLine, hypothesis: "stops at Process.Kill()", env: null, CancellationToken.None);
            using (JsonDocument launchDoc = JsonDocument.Parse(launch))
            {
                Assert.AreEqual("breakpoint", launchDoc.RootElement.GetProperty("stoppedReason").GetString(), $"launch: {launch}");
                pid = launchDoc.RootElement.GetProperty("pid").GetInt32();
            }

            string ended = await session.ContinueAsync("the target kills itself", waitForBreakpoint: true, CancellationToken.None);

            using JsonDocument endedDoc = JsonDocument.Parse(ended);
            JsonElement root = endedDoc.RootElement;
            Assert.AreEqual("processExited", root.GetProperty("stoppedReason").GetString(), $"continue: {ended}");
            Assert.IsTrue(root.TryGetProperty("targetExit", out JsonElement exit), $"no targetExit in: {ended}");
            string prompt = root.GetProperty("prompt").GetString()!;
            if (OperatingSystem.IsWindows())
            {
                // TerminateProcess(handle, -1): an exit code, not a signal.
                Assert.AreEqual("exited", exit.GetProperty("kind").GetString(), $"targetExit: {exit}");
                Assert.AreEqual(-1, exit.GetProperty("code").GetInt32(), $"targetExit: {exit}");
            }
            else
            {
                Assert.AreEqual("killed", exit.GetProperty("kind").GetString(), $"targetExit: {exit}");
                Assert.AreEqual(9, exit.GetProperty("signal").GetInt32(), $"targetExit: {exit}");
                Assert.AreEqual("SIGKILL", exit.GetProperty("signalName").GetString(), $"targetExit: {exit}");
                StringAssert.Contains(prompt, "killed by signal 9 (SIGKILL)", $"prompt: {prompt}");
            }
            Assert.IsFalse(prompt.Contains("ran to completion", StringComparison.Ordinal), $"prompt claims what nothing showed: {prompt}");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }

    [TestMethod]
    public async Task Stop_OfAnOwnedTarget_ReportsHowItEnded()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("SNAPSHOT_HERE", StringComparison.Ordinal)) + 1;
        Assert.IsTrue(markerLine > 0, "'SNAPSHOT_HERE' marker not found in the snapshot target source.");
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        using var session = new EngineSteppingSession();
        int pid = 0;
        try
        {
            string launch = await session.LaunchAsync(dotnet, new[] { "exec", targetDll }, cwd: null,
                source, markerLine, hypothesis: "stops in Worker.Compute", env: null, CancellationToken.None);
            using (JsonDocument launchDoc = JsonDocument.Parse(launch))
            {
                Assert.AreEqual("breakpoint", launchDoc.RootElement.GetProperty("stoppedReason").GetString(), $"launch: {launch}");
                pid = launchDoc.RootElement.GetProperty("pid").GetInt32();
            }

            string stopped = await session.StopAsync("the owned target is asked to exit and does", CancellationToken.None);

            using JsonDocument stoppedDoc = JsonDocument.Parse(stopped);
            JsonElement root = stoppedDoc.RootElement;
            Assert.AreEqual("stopped", root.GetProperty("status").GetString(), $"stop: {stopped}");
            Assert.IsTrue(root.TryGetProperty("targetExit", out JsonElement exit), $"no targetExit in: {stopped}");
            string kind = exit.GetProperty("kind").GetString()!;
            Assert.AreNotEqual("unknown", kind, $"an owned target's end is DrHook's to know: {exit}");
            string prompt = root.GetProperty("prompt").GetString()!;
            Assert.IsFalse(prompt.Contains("gracefully terminated", StringComparison.Ordinal), $"prompt claims what nothing showed: {prompt}");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }
}
