// Layer 1 — INTEGRATION TEST for Legacy VSTest path (finding 62; refactored finding 64; redesigned ADR-008 Increment 3).
//
// Validates substrate's AttachAndOwn against a testhost halted by VSTEST_HOST_DEBUG=1.
//
// Lifecycle (ADR-008 / finding 67):
//   - Substrate's AttachAndOwn takes lifecycle ownership of the testhost target for
//     finding-64 race protection (substrate-owned kill ordering).
//   - Substrate's Dispose now (Increment 1, finding 69) performs Stage 1 SIGTERM-then-
//     wait-for-natural-exit, with Stage 2 SIGKILL fallback only against violators.
//     testhost's [Fact] does brief observable work (Increment 2 finding-70-style); xUnit
//     reports test result; testhost exits cleanly; dotnet-test parent + vstest.console
//     cascade.
//   - Test asserts natural exit via Process.WaitForExit(timeout) on the dotnet-test
//     bootstrap AFTER Dispose. The bootstrap's entire process tree (vstest.console +
//     testhost) exits when testhost (its grandchild) exits — that's natural lifecycle,
//     not substrate kill.

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
    public void AttachAndOwn_VstestTestHost_BriefWork_NaturalExit()
    {
        string targetProject = ResolveLegacyTargetProjectPath();
        Assert.IsTrue(File.Exists(targetProject), $"Legacy VSTest integration target csproj not found at {targetProject}.");

        // Spawn dotnet test with VSTEST_HOST_DEBUG=1; capture testhost PID from stdout.
        // dotnet-test process is a transient bootstrap that hosts vstest.console + testhost
        // as descendants. When testhost exits naturally, the parents wind down too.
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

            using (DebugSession session = DebugSession.AttachAndOwn(testHostPid, new NullSink()))
            {
                // Brief observation window — substrate's CallbackPump initializes;
                // testhost's VSTEST_HOST_DEBUG=1 waiter detects Debugger.IsAttached
                // and continues; the [Fact] runs brief observable work (~500 ms).
                Thread.Sleep(TimeSpan.FromMilliseconds(500));

                // session.Dispose() at end of using:
                //   Stage 1: SIGTERM → testhost's [Fact] is already wrapping up its
                //            brief work OR was already done. testhost reports to
                //            vstest.console, exits. Substrate observes natural exit.
                //   Stage 2: would only fire if testhost ignored SIGTERM — doesn't
                //            happen for well-behaved xUnit + dotnet test combo.
                //   No TargetStuckAtDispose anomaly expected.
            }

            // Increment 3 discipline assertion: dotnet-test bootstrap should exit
            // naturally after testhost dies, vstest.console reports, and dotnet test
            // completes. Total post-Dispose budget: ~5-10 seconds for the full VSTest
            // tree to wind down (slower than MTP because of the deeper process tree).
            bool exitedNaturally = dotnetTest.WaitForExit(10000);
            Assert.IsTrue(exitedNaturally,
                "VSTest dotnet-test bootstrap did not exit naturally within 10s after Dispose — Layer 1 " +
                "discipline violation (testhost may be stuck, or vstest reporting may be hanging).");
        }
        finally
        {
            // Defensive fallback only — if natural-exit assertion succeeded, no-op.
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
