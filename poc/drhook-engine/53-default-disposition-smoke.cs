#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 53 — CoreCLR DEFAULT SIGNAL DISPOSITION ================================
//
// ADR-008 Phase 0 (process lifecycle ground truth).
//
// HYPOTHESIS (open): "What is .NET CoreCLR's default disposition for a target with
// NO explicit signal handlers? Does it exit on SIGINT? On SIGTERM? With what exit
// code? Documents the baseline behavior assumed when targets don't register handlers."
//
// CONSTRUCTION: spawn 53-target.cs (parked in Task.Delay, no handlers). Run twice —
// once per signal — observing exit code + timing.
//
// Returns 0 with summary. No falsification arms — observational probe.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

return DefaultDispositionP53.Run(args);

static class Posix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
    public const int SIGINT = 2;
    public const int SIGTERM = 15;
}

static class DefaultDispositionP53
{
    const int ObserveMs = 2000;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 53-default-disposition-smoke.cs <path-to-53-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");

        // Round 1 — SIGINT default
        Console.WriteLine();
        Console.WriteLine("=== Round 1: SIGINT (default disposition, no handler) ===");
        var (sigintExited, sigintCode, sigintMs) = RunRound(args[0], Posix.SIGINT, "SIGINT");

        // Round 2 — SIGTERM default
        Console.WriteLine();
        Console.WriteLine("=== Round 2: SIGTERM (default disposition, no handler) ===");
        var (sigtermExited, sigtermCode, sigtermMs) = RunRound(args[0], Posix.SIGTERM, "SIGTERM");

        Console.WriteLine();
        Console.WriteLine($"PROBE 53 OBSERVATIONS — CoreCLR default disposition (no handler):");
        Console.WriteLine($"  SIGINT  : exited={sigintExited}  exit-code={sigintCode}  delay={sigintMs}ms");
        Console.WriteLine($"  SIGTERM : exited={sigtermExited}  exit-code={sigtermCode}  delay={sigtermMs}ms");
        Console.WriteLine();
        Console.WriteLine("Substrate implication: documents the baseline behavior the substrate can rely on when targets don't register explicit handlers. Standard Unix convention: exit code 128 + signal_number — 130 = SIGINT, 143 = SIGTERM, 137 = SIGKILL.");
        return 0;
    }

    static (bool exited, int code, long ms) RunRound(string targetPath, int sig, string sigName)
    {
        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{targetPath}\"")
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
        }) { IsBackground = true, Name = $"target-stdout-{sigName}" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = $"target-stderr-{sigName}" };
        errDrain.Start();

        ready.Wait(TimeSpan.FromSeconds(30));
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        Thread.Sleep(200);

        Stopwatch sw = Stopwatch.StartNew();
        Posix.kill(realPid, sig);
        bool exited = proc.WaitForExit(ObserveMs);
        sw.Stop();

        int code = exited ? proc.ExitCode : -1;
        Console.WriteLine($"{sigName,-7}    : sent → exited={exited} code={code} ({sw.ElapsedMilliseconds}ms)");
        if (!exited) try { proc.Kill(entireProcessTree: true); } catch { }
        return (exited, code, sw.ElapsedMilliseconds);
    }
}
