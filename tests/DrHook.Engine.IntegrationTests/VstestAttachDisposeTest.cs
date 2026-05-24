// Layer 1 — INTEGRATION TEST for the Legacy VSTest path (Probe 46b, finding 62).
//
// Validates substrate's Attach against a testhost halted by VSTEST_HOST_DEBUG=1.
// The orchestration is more layered than MTP (which only needs --debug):
//
//   AttachDisposeTest (Layer 1)                       (this integration test)
//        │
//        └── dotnet test  (intermediate orchestrator; we spawn this)
//                 │
//                 └── vstest.console  (intermediate runner)
//                          │
//                          └── testhost (Layer 3 — DrHook attaches HERE)
//                                  │
//                                  └── xUnit adapter executes [Fact]
//
// VSTEST_HOST_DEBUG=1 env var on `dotnet test` halts testhost at startup BEFORE
// any [Fact] runs and prints "Process Id: NNNN, Name: dotnet" on stdout (testhost
// runs as `dotnet exec testhost.dll`). Integration test parses PID, attaches via
// DrHook.Engine, brief observe, Dispose. After Detach, testhost continues, runs
// the [Fact] (which sleeps 30s), then exits cleanly. We Kill the dotnet test
// process tree on test exit to avoid lingering processes.
//
// Phase 2 / Probe 46b exemplar. If this passes, the Legacy VSTest community-utility
// path is substrate-validated and Phase 8 mass promotion can begin.
//
// Discoveries to capture in finding 62: actual stdout format (already captured —
// identical to MTP's --debug regex), process tree depth (3 intermediates), kill
// propagation behavior, any unknown unknowns from the multi-process attach.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class VstestAttachDisposeTest
{
    private sealed class NullSink : IDebugEventSink
    {
        public void OnEvent(string name) { }
    }

    [TestMethod]
    public void AttachToVstestTestHost_BriefIdle_Dispose_TestHostSurvives()
    {
        string targetProject = ResolveLegacyTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"Legacy VSTest integration target csproj not found at {targetProject}.");

        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"test \"{targetProject}\" -c Release --no-build --nologo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment = { ["VSTEST_HOST_DEBUG"] = "1" },
            }
        };
        proc.Start();

        int testHostPid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                // VSTest output (captured 2026-05-24 on .NET 10.0.100 SDK):
                //   "Host debugging is enabled. Please attach debugger to testhost process to continue."
                //   "Process Id: NNNN, Name: dotnet"
                Match m = Regex.Match(line, @"Process Id:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                {
                    Volatile.Write(ref testHostPid, pid);
                    ready.Set();
                }
            }
        }) { IsBackground = true, Name = "vstest-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "vstest-stderr" };
        errDrain.Start();

        try
        {
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(60)),
                "VSTest didn't print 'Process Id: NNNN' within 60s — VSTEST_HOST_DEBUG=1 stdout format may have shifted, or dotnet test failed to spawn testhost.");
            testHostPid = Volatile.Read(ref testHostPid);

            DebugSession session;
            try
            {
                session = DebugSession.Attach(testHostPid, new NullSink());
            }
            catch (Exception ex)
            {
                Assert.Fail($"DebugSession.Attach failed against VSTest testhost (pid {testHostPid}): {ex.GetType().Name}: {ex.Message}");
                return; // unreachable, satisfies nullable flow
            }

            // Brief observation window — pump initializes, drains setup callbacks;
            // testhost's VSTEST_HOST_DEBUG wait detects debugger and continues, [Fact]
            // begins running (30s sleep).
            Thread.Sleep(TimeSpan.FromMilliseconds(500));

            session.Dispose();

            // Brief settle for mscordbi state after Detach (finding 59).
            Thread.Sleep(TimeSpan.FromMilliseconds(200));

            // Check testhost is still alive by PID lookup. Process.GetProcessById will
            // throw ArgumentException if the process has exited.
            bool testHostAlive = false;
            try { using var th = Process.GetProcessById(testHostPid); testHostAlive = !th.HasExited; }
            catch (ArgumentException) { testHostAlive = false; }
            Assert.IsTrue(testHostAlive,
                "Testhost died after Dispose — substrate's detach-leave-running (finding 59) should keep testhost alive (it's mid-[Fact]-sleep).");
        }
        finally
        {
            // Kill the entire dotnet-test process tree — cascades to vstest.console + testhost.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>Resolve the Legacy VSTest integration target's csproj path. Walks
    /// up from the integration-test bin dir to tests/, then into the Legacy target
    /// project directory.</summary>
    private static string ResolveLegacyTargetProjectPath()
    {
        string testBin = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(testBin);
        for (int up = 0; up < 4 && dir is not null; up++) dir = dir.Parent;
        Assert.IsNotNull(dir, $"Couldn't walk up from {testBin} to find tests/ directory.");

        return Path.Combine(dir!.FullName, "DrHook.Engine.IntegrationTargets.Vstest", "DrHook.Engine.IntegrationTargets.Vstest.csproj");
    }
}
