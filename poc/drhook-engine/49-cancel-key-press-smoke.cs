#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 49 — WELL-BEHAVED TARGET: Console.CancelKeyPress / SIGINT =============
//
// ADR-008 Phase 0 (process lifecycle ground truth).
//
// HYPOTHESIS: "A well-implemented CLI process installs a Console.CancelKeyPress
// handler that gracefully terminates on SIGINT (Unix Ctrl+C equivalent), performing
// cleanup work and exiting with code 0 within a brief window of signal arrival."
//
// CONSTRUCTION: spawn 49-target.cs (parked sleeper with CancelKeyPress handler),
// send SIGINT via P/Invoke to libc's kill(pid, SIGINT), observe:
//   - Target's stdout shows "GRACEFUL_CLEANUP_DONE" marker.
//   - Target exits with code 0 (clean) within bounded time (e.g., 2 seconds).
//
// macOS-arm64 reference run; Phase 9 expands to Linux + Windows. On Windows the
// equivalent is GenerateConsoleCtrlEvent(CTRL_C_EVENT, ...) — separate probe needed.
//
// Falsification:
//   2 usage; 3 no READY; 4 SIGINT send failed (errno); 5 no GRACEFUL marker in stdout;
//   6 target didn't exit within timeout (handler ignored signal or hung in cleanup);
//   7 wrong exit code (handler ran but Environment.Exit not called or code != 0);
//   0 PASS.
//
// Usage:  dotnet 49-cancel-key-press-smoke.cs <path-to-49-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

return CancelKeyPressP49.Run(args);

static class Posix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);

    public const int SIGINT = 2;
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;
}

static class CancelKeyPressP49
{
    const int SignalGracePeriodMs = 2000;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 49-cancel-key-press-smoke.cs <path-to-49-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"plan       : spawn well-behaved target (CancelKeyPress handler) → send SIGINT via libc.kill → assert GRACEFUL_CLEANUP_DONE within {SignalGracePeriodMs}ms + exit code 0");

        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{args[0]}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();

        int realPid = -1;
        bool gracefulCleanupSeen = false;
        ManualResetEventSlim ready = new(false);
        ManualResetEventSlim graceful = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                Match m = Regex.Match(line, @"READY (\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                {
                    Volatile.Write(ref realPid, pid);
                    ready.Set();
                }
                else if (line.Contains("GRACEFUL_CLEANUP_DONE"))
                {
                    gracefulCleanupSeen = true;
                    graceful.Set();
                }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("FALSIFIED (no READY): target didn't print READY sentinel within 30s.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        Thread.Sleep(200);  // let target reach Task.Delay parked state

        Stopwatch sw = Stopwatch.StartNew();
        int rc = Posix.kill(realPid, Posix.SIGINT);
        if (rc != 0)
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"FALSIFIED (kill failed): kill({realPid}, SIGINT) returned {rc}, errno {err}.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 4;
        }
        Console.WriteLine($"signal     : SIGINT sent to {realPid}");

        bool gracefulWithinWindow = graceful.Wait(TimeSpan.FromMilliseconds(SignalGracePeriodMs));
        bool exitedWithinWindow = proc.WaitForExit(SignalGracePeriodMs);
        sw.Stop();

        Console.WriteLine($"graceful   : {(gracefulCleanupSeen ? "GRACEFUL_CLEANUP_DONE observed" : "NOT observed")} ({sw.ElapsedMilliseconds}ms after signal)");
        Console.WriteLine($"exited     : {(exitedWithinWindow ? $"yes (code {proc.ExitCode})" : "no — target did not exit within window")}");

        if (!gracefulCleanupSeen)
        {
            Console.Error.WriteLine($"FALSIFIED (no GRACEFUL marker): target did not run its Console.CancelKeyPress handler — soft signal not honored.");
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return 5;
        }

        if (!exitedWithinWindow)
        {
            Console.Error.WriteLine($"FALSIFIED (did not exit): target ran handler but didn't complete exit within {SignalGracePeriodMs}ms — handler hung or Environment.Exit blocked.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 6;
        }

        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine($"FALSIFIED (exit code {proc.ExitCode}): well-behaved target should exit with code 0 after graceful cleanup.");
            return 7;
        }

        Console.WriteLine();
        Console.WriteLine($"PROBE 49 PASSED — well-behaved target (Console.CancelKeyPress handler) honors SIGINT: GRACEFUL_CLEANUP_DONE observed {sw.ElapsedMilliseconds}ms after signal, target exited cleanly with code 0. Soft-signal-honored-by-well-behaved-target Layer 1 discipline empirically validated on macOS-arm64.");
        return 0;
    }
}
