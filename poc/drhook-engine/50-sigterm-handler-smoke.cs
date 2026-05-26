#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 50 — WELL-BEHAVED TARGET: PosixSignalRegistration / SIGTERM ===========
//
// ADR-008 Phase 0 (process lifecycle ground truth).
//
// HYPOTHESIS: "A well-implemented .NET process installs PosixSignalRegistration for
// SIGTERM and exits gracefully on receipt — the canonical container/orchestration-
// platform-friendly shape (Kubernetes SIGTERM, docker stop, systemd stop)."
//
// CONSTRUCTION: spawn 50-target.cs (PosixSignalRegistration SIGTERM handler), send
// SIGTERM via libc.kill(pid, 15), observe GRACEFUL_CLEANUP_DONE + clean exit.
//
// Falsification:
//   2 usage; 3 no READY; 4 kill failed; 5 no GRACEFUL marker; 6 no exit; 7 wrong code;
//   0 PASS.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

return SigtermHandlerP50.Run(args);

static class Posix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
    public const int SIGTERM = 15;
}

static class SigtermHandlerP50
{
    const int SignalGracePeriodMs = 2000;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 50-sigterm-handler-smoke.cs <path-to-50-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"plan       : spawn well-behaved target (PosixSignalRegistration SIGTERM) → send SIGTERM via libc.kill → assert GRACEFUL_CLEANUP_DONE within {SignalGracePeriodMs}ms + exit code 0");

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
            Console.Error.WriteLine("FALSIFIED (no READY): target didn't print READY sentinel.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        Thread.Sleep(200);

        Stopwatch sw = Stopwatch.StartNew();
        int rc = Posix.kill(realPid, Posix.SIGTERM);
        if (rc != 0)
        {
            Console.Error.WriteLine($"FALSIFIED (kill failed): kill({realPid}, SIGTERM) returned {rc}, errno {Marshal.GetLastWin32Error()}.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 4;
        }
        Console.WriteLine($"signal     : SIGTERM sent to {realPid}");

        bool gracefulWithin = graceful.Wait(TimeSpan.FromMilliseconds(SignalGracePeriodMs));
        bool exitedWithin = proc.WaitForExit(SignalGracePeriodMs);
        sw.Stop();

        Console.WriteLine($"graceful   : {(gracefulCleanupSeen ? $"GRACEFUL_CLEANUP_DONE observed ({sw.ElapsedMilliseconds}ms)" : "NOT observed")}");
        Console.WriteLine($"exited     : {(exitedWithin ? $"yes (code {proc.ExitCode})" : "no")}");

        if (!gracefulCleanupSeen) { Console.Error.WriteLine("FALSIFIED: no GRACEFUL marker."); try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } return 5; }
        if (!exitedWithin) { Console.Error.WriteLine("FALSIFIED: target did not exit."); try { proc.Kill(entireProcessTree: true); } catch { } return 6; }
        if (proc.ExitCode != 0) { Console.Error.WriteLine($"FALSIFIED: exit code {proc.ExitCode} ≠ 0."); return 7; }

        Console.WriteLine();
        Console.WriteLine($"PROBE 50 PASSED — well-behaved target (PosixSignalRegistration for SIGTERM) honors SIGTERM with graceful cleanup + exit 0 within {sw.ElapsedMilliseconds}ms.");
        return 0;
    }
}
