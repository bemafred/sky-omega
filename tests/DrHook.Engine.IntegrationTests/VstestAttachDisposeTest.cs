// Layer 1 — INTEGRATION TEST for Legacy VSTest path (finding 62, refactored finding 64).
//
// Validates substrate's Attach against a testhost halted by VSTEST_HOST_DEBUG=1.
//
// Lifecycle (finding 64): substrate OWNS the testhost target via AttachAndOwn(pid).
// The dotnet test orchestration process is the bootstrap that gives us the testhost
// PID; once we have it, substrate takes over and kill-firsts testhost on Dispose.
// The dotnet test parent is then cleaned up separately (it's not the debug target).
//
// The dispose-then-kill race (MCH-RE-2, finding 63) is structurally impossible from
// this API surface — substrate enforces ordering inside Dispose.

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
    public void AttachAndOwn_VstestTestHost_BriefIdle_DisposeCleanly()
    {
        string targetProject = ResolveLegacyTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"Legacy VSTest integration target csproj not found at {targetProject}.");

        // Spawn dotnet test with VSTEST_HOST_DEBUG=1; capture testhost PID from stdout.
        // The dotnet-test process is a transient bootstrap — it will die naturally
        // when testhost (its grandchild) is killed by the substrate.
        Process dotnetTest = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"test \"{targetProject}\" -c Release --no-build --nologo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment = { ["VSTEST_HOST_DEBUG"] = "1" },
            }
        };
        dotnetTest.Start();

        try
        {
            int testHostPid = ExtractTestHostPid(dotnetTest);

            // Substrate owns testhost lifecycle from here. AttachAndOwn does
            // Process.GetProcessById(testHostPid) internally; on Dispose it
            // kill-firsts then tears down. The dotnet-test process and
            // vstest.console will exit when testhost dies.
            using DebugSession session = DebugSession.AttachAndOwn(testHostPid, new NullSink());

            // Brief observation window — substrate's CallbackPump initializes;
            // testhost's VSTEST_HOST_DEBUG=1 waiter detects Debugger.IsAttached
            // and continues; the [Fact] begins running (30s sleep).
            Thread.Sleep(TimeSpan.FromMilliseconds(500));

            // session.Dispose() at end of using — kill-first protocol internal
            // to substrate. testhost dies; dotnet-test + vstest.console cascade.
        }
        finally
        {
            // Defensive cleanup of the dotnet-test bootstrap. Substrate killed
            // testhost (Owned via AttachAndOwn); dotnet-test typically exits
            // shortly after, but ensure no orphan if anything went sideways.
            try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
            dotnetTest.Dispose();
        }
    }

    private static int ExtractTestHostPid(Process dotnetTest)
    {
        int pid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = dotnetTest.StandardOutput.ReadLine()) is not null)
            {
                // VSTest output (.NET 10.0.100, finding 62 D6):
                //   "Host debugging is enabled. Please attach debugger to testhost process to continue."
                //   "Process Id: NNNN, Name: dotnet"
                Match m = Regex.Match(line, @"Process Id:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid))
                {
                    Volatile.Write(ref pid, parsedPid);
                    ready.Set();
                }
            }
        }) { IsBackground = true, Name = "vstest-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (dotnetTest.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "vstest-stderr" };
        errDrain.Start();

        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(60)),
            "VSTest didn't print 'Process Id: NNNN' within 60s — VSTEST_HOST_DEBUG=1 stdout format may have shifted, or dotnet test failed to spawn testhost.");
        return Volatile.Read(ref pid);
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
