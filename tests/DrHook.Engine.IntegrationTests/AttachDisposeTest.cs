// Layer 1 — INTEGRATION TEST (finding 61 vocabulary, refactored finding 64).
//
// Substrate validation lifted from probe 42's shape (attach + Dispose → substrate
// teardown clean) using MTP's --debug as the attach-handshake mechanism.
//
// Lifecycle (finding 64): substrate OWNS the target Process via AttachAndOwn(pid).
// Caller's bootstrap Process is just for spawn + stdout-parse to extract the PID,
// then released. Substrate's Dispose handles kill-first internally — there is no
// way for the caller to misorder. The dispose-then-kill race that surfaced as
// MCH-RE-2 (finding 63) is structurally impossible from this API surface.

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
    public void AttachAndOwn_MtpTarget_BriefIdle_DisposeCleanly()
    {
        string targetExe = ResolveMtpIntegrationTargetExe();
        Assert.IsTrue(File.Exists(targetExe), $"MTP integration target exe not found at {targetExe}.");

        int pid = SpawnAndExtractPid(targetExe);

        // Substrate takes lifecycle ownership. From this point on, the substrate
        // is responsible for killing the target on Dispose (kill-first protocol,
        // finding 64). Caller does NOT touch Process.Kill anywhere — substrate
        // forbids the bad ordering by not exposing a kill API.
        using DebugSession session = DebugSession.AttachAndOwn(pid, new NullSink());

        // Brief observation window — substrate's CallbackPump initializes, may
        // drain setup callbacks. Target's [TestMethod] begins running (MTP
        // --debug-waiter saw Debugger.IsAttached become true).
        Thread.Sleep(TimeSpan.FromMilliseconds(200));

        // session.Dispose() runs at end of using block:
        //   1. pump.Dispose() — joins worker, drains queues
        //   2. TryKillTargetAndSettle() — kills target (Owned), 100ms settle
        //   3. Quiesce — drains mscordbi queued callbacks (against dying/dead target)
        //   4. Detach — releases debugger registration
        //   5. Terminate — tears down ICorDebug
        //   6. Release breakpoint refs / symbols / pProcess / pUnknown
        //   7. _callback.Dispose() — frees CCW memory
        //   8. _dbgShim.Dispose() — releases libdbgshim handle
        //   9. _targetProcess.Dispose() — releases substrate's Process handle
        //
        // The kill-first ordering (step 2 before step 4) is what closes
        // drhook-detach-exit-race for the Owned path — substrate's responsibility,
        // not caller's.
    }

    private static int SpawnAndExtractPid(string targetExe)
    {
        // The caller's Process handle is bootstrap-only — used to spawn, read stdout,
        // and extract the PID that MTP's --debug printed. After that, substrate
        // takes over via AttachAndOwn. We release the bootstrap handle immediately.
        using Process bootstrap = new()
        {
            StartInfo = new ProcessStartInfo(targetExe, "--debug")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        bootstrap.Start();

        int pid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = bootstrap.StandardOutput.ReadLine()) is not null)
            {
                // MTP --debug output: "Waiting for debugger to attach... Process Id: NNNN, Name: ..."
                Match m = Regex.Match(line, @"Process Id:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid))
                {
                    Volatile.Write(ref pid, parsedPid);
                    ready.Set();
                }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (bootstrap.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(30)),
            "MTP integration target did not print 'Process Id: NNNN' within 30s — MTP --debug handshake failed.");

        return Volatile.Read(ref pid);
        // bootstrap.Dispose() runs at end of using — releases caller's Process handle.
        // The OS process keeps running until substrate kills it via AttachAndOwn's lifecycle.
    }

    /// <summary>Resolve the MTP integration target's executable path. On Unix-like systems
    /// MTP test projects compile to an apphost binary with no .exe extension; Windows has
    /// .exe. Probe both shapes so the test is cross-platform.</summary>
    private static string ResolveMtpIntegrationTargetExe()
    {
        string testBin = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(testBin);
        for (int up = 0; up < 4 && dir is not null; up++) dir = dir.Parent;
        Assert.IsNotNull(dir, $"Couldn't walk up from {testBin} to find tests/ directory.");

        string targetBaseDir = Path.Combine(dir!.FullName, "DrHook.Engine.IntegrationTargets.Mtp", "bin");
        Assert.IsTrue(Directory.Exists(targetBaseDir), $"Integration target bin directory missing: {targetBaseDir}");

        string[] configDirs = Directory.GetDirectories(targetBaseDir);
        Assert.IsTrue(configDirs.Length > 0, $"No build configurations under {targetBaseDir}");

        string targetName = "DrHook.Engine.IntegrationTargets.Mtp";
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? targetName + ".exe" : targetName;

        string currentConfig = new DirectoryInfo(testBin).Parent?.Parent?.Name ?? "Release";
        string candidateConfigDir = configDirs.FirstOrDefault(d => Path.GetFileName(d) == currentConfig) ?? configDirs[0];

        string[] tfmDirs = Directory.GetDirectories(candidateConfigDir);
        Assert.IsTrue(tfmDirs.Length > 0, $"No TFM directories under {candidateConfigDir}");
        string tfmDir = tfmDirs[0];

        return Path.Combine(tfmDir, exeName);
    }
}
