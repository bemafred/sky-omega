// Layer 1 — INTEGRATION TEST: a failed launch says why, and leaves no process behind.
//
// Before 2026-11-07 a launch whose target exited before its runtime started (dotnet exec of a DLL that does not
// exist) failed with only "DbgShim.LaunchWithDebuggerPosix failed (HRESULT 0x80004005)". The muxer's exit code and
// its stderr ("The application to execute does not exist: ...") were discarded with its pipes, and the dead target
// stayed a zombie under the debugger, because the failure path killed it and nothing reaped it. A program that
// cannot be spawned at all also read as E_FAIL, hiding the spawn's errno.
//
// Where a missing program fails differs per platform. macOS and Windows spawn the program itself, so the spawn fails
// with the OS's error. Linux spawns /bin/sh, which stops itself and then execs the program (glibc has no suspended
// spawn; LinuxSpawn), so the spawn succeeds and the exec fails after the release: the shell exits 127 with "not found"
// on stderr (seen 2026-11-07 on Linux arm64 and x64).

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
public sealed class LaunchFailureTest
{
    [TestMethod]
    public async Task Launch_OfATargetThatExitsBeforeItsRuntimeStarts_ReportsItsExitAndStderr_AndReapsIt()
    {
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        string missingDll = Path.Combine(Path.GetTempPath(), $"drhook-missing-{Guid.NewGuid():N}.dll");
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        using var session = new EngineSteppingSession();
        string launch = await session.LaunchAsync(dotnet, new[] { "exec", missingDll }, cwd: null,
            source, line: 1, hypothesis: "the muxer exits at once: the DLL does not exist", env: null, CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(launch);
        JsonElement root = doc.RootElement;
        Assert.AreEqual("error", root.GetProperty("status").GetString(), $"launch: {launch}");
        Assert.IsTrue(root.TryGetProperty("launchFailure", out JsonElement failure), $"no launchFailure in: {launch}");
        JsonElement exit = failure.GetProperty("targetExit");
        Assert.AreEqual("exited", exit.GetProperty("kind").GetString(), $"targetExit: {exit}");
        Assert.AreNotEqual(0, exit.GetProperty("code").GetInt32(), $"targetExit: {exit}");
        StringAssert.Contains(failure.GetProperty("stderr").GetString(), "does not exist", $"launchFailure: {failure}");
        StringAssert.Contains(root.GetProperty("message").GetString(), "does not exist", $"message: {launch}");

        // Reaped: the dead target is not left as a zombie under the debugger (Process.GetProcessById finds a zombie).
        int pid = failure.GetProperty("pid").GetInt32();
        Assert.ThrowsExactly<ArgumentException>(() => Process.GetProcessById(pid), $"target {pid} is still in the process table");
    }

    [TestMethod]
    public async Task Launch_OfAProgramThatDoesNotExist_ReportsTheOperatingSystemsError()
    {
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        string missingProgram = Path.Combine(Path.GetTempPath(), $"drhook-missing-{Guid.NewGuid():N}", "program");

        using var session = new EngineSteppingSession();
        string launch = await session.LaunchAsync(missingProgram, Array.Empty<string>(), cwd: null,
            source, line: 1, hypothesis: "the spawn fails: the program does not exist", env: null, CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(launch);
        JsonElement root = doc.RootElement;
        Assert.AreEqual("error", root.GetProperty("status").GetString(), $"launch: {launch}");
        Assert.IsTrue(root.TryGetProperty("launchFailure", out JsonElement failure), $"no launchFailure in: {launch}");
        string message = root.GetProperty("message").GetString()!;
        Assert.IsFalse(message.Contains("0x80004005", StringComparison.Ordinal), $"the OS's error is hidden behind E_FAIL: {message}");
        if (OperatingSystem.IsLinux())
        {
            // The shell's exec failed: its exit code and its message, and the shell is reaped.
            Assert.AreEqual("runtime startup", failure.GetProperty("step").GetString(), $"launchFailure: {failure}");
            JsonElement exit = failure.GetProperty("targetExit");
            Assert.AreEqual("exited", exit.GetProperty("kind").GetString(), $"targetExit: {exit}");
            Assert.AreEqual(127, exit.GetProperty("code").GetInt32(), $"targetExit: {exit}");
            StringAssert.Contains(failure.GetProperty("stderr").GetString(), "not found", $"launchFailure: {failure}");
            int pid = failure.GetProperty("pid").GetInt32();
            Assert.ThrowsExactly<ArgumentException>(() => Process.GetProcessById(pid), $"target {pid} is still in the process table");
        }
        else
        {
            Assert.AreEqual(OperatingSystem.IsWindows() ? "CreateProcess" : "posix_spawn", failure.GetProperty("step").GetString(), $"launchFailure: {failure}");
            Assert.IsFalse(failure.TryGetProperty("pid", out _), $"nothing was spawned, so there is no pid: {failure}");
            if (OperatingSystem.IsMacOS())
                StringAssert.Contains(message, "No such file or directory", $"message: {message}");
        }
    }
}
