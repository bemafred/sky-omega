// Layer 1 — INTEGRATION TEST (finding 61 vocabulary).
//
// First integration test. Substrate validation lifted from probe 42's shape
// (attach + Dispose → target survives) using MTP's --debug as the attach-handshake
// mechanism (cleaner than the file-based-probe READY-PID handshake; MTP-native).
//
// Sequence:
//   1. Launch the MTP integration target exe with `--debug` arg.
//   2. MTP prints "Process Id: NNNN, Name: ..." and waits for Debugger.IsAttached.
//   3. Integration test parses PID, calls DebugSession.Attach.
//   4. mscordbi attaches; MTP's --debug-waiter sees Debugger.IsAttached=true;
//      MTP proceeds to run the [TestMethod]s in the target.
//   5. Brief observation window (200ms — pump initializes, drains setup callbacks).
//   6. DebugSession.Dispose (substrate's detach-leave-running per finding 59).
//   7. Assert target still alive (target's [TestMethod] is mid-sleep).
//
// Falsification → MSTest Assert.* throws; the test fails with structured message.
// Phase 2 / Probe 46 exemplar: if this passes, the integration-test promotion
// mechanism works and Phase 8 has a rail for promoting probes 41-45.

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
    public void AttachToMtpTarget_BriefIdle_Dispose_TargetSurvives()
    {
        string targetExe = ResolveMtpIntegrationTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP integration target exe not found at {targetExe}. Run 'dotnet build' against DrHook.Engine.IntegrationTargets.Mtp first.");

        using Process proc = new()
        {
            // --debug = MTP-native attach handshake: target prints PID + waits for Debugger.IsAttached.
            StartInfo = new ProcessStartInfo(targetExe, "--debug")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();

        int realPid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                // MTP --debug output: "Waiting for debugger to attach... Process Id: NNNN, Name: ..."
                Match m = Regex.Match(line, @"Process Id:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                {
                    Volatile.Write(ref realPid, pid);
                    ready.Set();
                }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        try
        {
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(30)), "MTP integration target did not print 'Process Id: NNNN' within 30s — MTP --debug handshake failed.");
            realPid = Volatile.Read(ref realPid);

            DebugSession session;
            try
            {
                session = DebugSession.Attach(realPid, new NullSink());
            }
            catch (Exception ex)
            {
                Assert.Fail($"DebugSession.Attach failed against MTP integration target (pid {realPid}): {ex.GetType().Name}: {ex.Message}");
                return; // unreachable, satisfies nullable flow
            }

            // Brief observation window — substrate's CallbackPump initializes, drains setup
            // callbacks (CreateProcess, CreateAppDomain, LoadModule, etc.). Target's [TestMethod]
            // begins running (MTP --debug-waiter saw Debugger.IsAttached become true).
            Thread.Sleep(TimeSpan.FromMilliseconds(200));

            session.Dispose();

            // Brief settle for mscordbi state after Detach (finding 59: mscordbi takes time).
            Thread.Sleep(TimeSpan.FromMilliseconds(200));

            Assert.IsFalse(proc.HasExited, "Target died after Dispose — substrate's detach-leave-running (finding 59) should keep the MTP integration target alive while its [TestMethod] is still sleeping.");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>Resolve the MTP integration target's executable path.
    ///
    /// On Unix-like systems (macOS, Linux), MTP test projects compile to an apphost
    /// binary with no .exe extension. On Windows it has .exe. Probe both shapes.</summary>
    private static string ResolveMtpIntegrationTargetExe()
    {
        string testBin = AppContext.BaseDirectory;
        // Walk up from tests/DrHook.Engine.IntegrationTests/bin/{config}/{tfm}/ to tests/.
        DirectoryInfo? dir = new(testBin);
        for (int up = 0; up < 4 && dir is not null; up++) dir = dir.Parent;
        Assert.IsNotNull(dir, $"Couldn't walk up from {testBin} to find tests/ directory.");

        string targetBaseDir = Path.Combine(dir!.FullName, "DrHook.Engine.IntegrationTargets.Mtp", "bin");
        Assert.IsTrue(Directory.Exists(targetBaseDir), $"Integration target bin directory missing: {targetBaseDir}");

        string[] configDirs = Directory.GetDirectories(targetBaseDir);
        Assert.IsTrue(configDirs.Length > 0, $"No build configurations under {targetBaseDir}");

        string targetName = "DrHook.Engine.IntegrationTargets.Mtp";
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? targetName + ".exe" : targetName;

        // Prefer matching configuration with the integration-test exe's, fall back to first.
        string currentConfig = new DirectoryInfo(testBin).Parent?.Parent?.Name ?? "Release";
        string candidateConfigDir = configDirs.FirstOrDefault(d => Path.GetFileName(d) == currentConfig) ?? configDirs[0];

        string[] tfmDirs = Directory.GetDirectories(candidateConfigDir);
        Assert.IsTrue(tfmDirs.Length > 0, $"No TFM directories under {candidateConfigDir}");
        string tfmDir = tfmDirs[0];

        return Path.Combine(tfmDir, exeName);
    }
}
