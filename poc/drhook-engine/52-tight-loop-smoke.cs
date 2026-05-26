#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 52 — TIGHT CPU-BOUND LOOP: signal delivery under no-yield code =========
//
// ADR-008 Phase 0 (process lifecycle ground truth).
//
// HYPOTHESIS (open): "What does .NET CoreCLR's default signal disposition do for a
// target that is in a pure CPU-bound tight loop with no async safepoints? Does
// SIGTERM cause graceful exit, abrupt termination, or no response at all?"
//
// CONSTRUCTION: spawn 52-target.cs (counter loop, no handler), send SIGTERM, observe.
// Either outcome is informative — this probe documents CoreCLR's actual behavior
// rather than asserting one specific outcome.
//
//   Path A: target exits within window → CoreCLR's default SIGTERM disposition
//           terminates the process even in tight CPU code. Document exit code +
//           timing.
//   Path B: target doesn't exit within window → tight loops can resist SIGTERM.
//           Substrate must escalate to SIGKILL for stuck-in-compute targets.
//
// No falsification arms — this is observational. Returns 0 always, with summary.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

return TightLoopP52.Run(args);

static class Posix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;
}

static class TightLoopP52
{
    const int SigTermObserveMs = 3000;
    const int SigKillObserveMs = 1000;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 52-tight-loop-smoke.cs <path-to-52-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"plan       : spawn tight-loop target (no handler), observe SIGTERM behavior (waiting {SigTermObserveMs}ms), escalate to SIGKILL if needed");

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
        ManualResetEventSlim ready = new(false);
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
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("FALSIFIED (no READY).");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        Thread.Sleep(200);  // give the loop time to start spinning

        // SIGTERM observation
        Console.WriteLine($"phase A    : sending SIGTERM, observing up to {SigTermObserveMs}ms...");
        Stopwatch sw = Stopwatch.StartNew();
        Posix.kill(realPid, Posix.SIGTERM);
        bool exitedFromTerm = proc.WaitForExit(SigTermObserveMs);
        sw.Stop();

        if (exitedFromTerm)
        {
            Console.WriteLine($"phase A    : target exited from SIGTERM in {sw.ElapsedMilliseconds}ms with code {proc.ExitCode}");
            Console.WriteLine();
            Console.WriteLine($"PROBE 52 OBSERVATION — CoreCLR's default SIGTERM disposition terminates a tight-loop target (no explicit handler) in {sw.ElapsedMilliseconds}ms with exit code {proc.ExitCode}. Signals ARE delivered to pure CPU-bound user code on macOS-arm64 (CoreCLR's runtime intercept layer is sufficient).");
            return 0;
        }

        Console.WriteLine($"phase A    : target survived SIGTERM beyond {SigTermObserveMs}ms — tight loop resisted soft signal");

        // Escalate to SIGKILL
        Console.WriteLine($"phase B    : sending SIGKILL, observing up to {SigKillObserveMs}ms...");
        sw.Restart();
        Posix.kill(realPid, Posix.SIGKILL);
        bool exitedFromKill = proc.WaitForExit(SigKillObserveMs);
        sw.Stop();

        if (!exitedFromKill)
        {
            Console.Error.WriteLine("FALSIFIED: target survived SIGKILL — kernel violated (impossible).");
            return 5;
        }

        Console.WriteLine($"phase B    : SIGKILL effective in {sw.ElapsedMilliseconds}ms (exit code {proc.ExitCode})");
        Console.WriteLine();
        Console.WriteLine($"PROBE 52 OBSERVATION — tight CPU-bound loop with NO explicit handler can RESIST SIGTERM (target alive after {SigTermObserveMs}ms). SIGKILL terminated the target in {sw.ElapsedMilliseconds}ms. Substrate implication: for stuck-in-compute targets, the soft-then-hard escalation pattern is necessary — soft signal alone is insufficient.");
        return 0;
    }
}
