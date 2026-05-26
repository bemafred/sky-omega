#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 54 — PROCESS TREE SIGNAL PROPAGATION ===================================
//
// ADR-008 Phase 0 (process lifecycle ground truth).
//
// HYPOTHESIS (open): "How do signals propagate across a parent-child process tree on
// macOS-arm64?
//   - Round A: signal-to-parent-only — do children inherit the signal? (Unix default: NO,
//     unless process group / job.)
//   - Round B: Process.Kill(entireProcessTree:true) — does .NET actually traverse the
//     tree and kill descendants?
//   - Round C: parent dies, what happens to orphaned children? (Unix: adopted by PID 1 /
//     launchd / systemd-equivalent.)
//
// CONSTRUCTION: spawn 54-target.cs which spawns 2 children (54-child-target.cs). Parse
// PARENT_READY + CHILD_READY × 2 from forwarded stdout.
//
// Returns 0 with full summary. Observational, no falsification arms.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

return ProcessTreeP54.Run(args);

static class Posix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;
}

static class ProcessTreeP54
{
    const int ObserveMs = 2000;

    public static int Run(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: dotnet 54-process-tree-smoke.cs <path-to-54-target.cs> <path-to-54-child-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");

        // ── Round A: signal-to-parent-only ──────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== Round A: SIGTERM to parent only ===");
        var roundA = RunRound(args[0], args[1], parentPid =>
        {
            Posix.kill(parentPid, Posix.SIGTERM);
        }, killOnExit: false);

        // ── Round B: Process.Kill(entireProcessTree:true) ───────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== Round B: Process.Kill(entireProcessTree:true) ===");
        var roundB = RunRound(args[0], args[1], (parentPid) =>
        {
            // Re-acquire Process handle from PID and use entireProcessTree kill.
            try
            {
                using Process p = Process.GetProcessById(parentPid);
                p.Kill(entireProcessTree: true);
            }
            catch { }
        }, killOnExit: false);

        // ── Summary ──────────────────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"PROBE 54 OBSERVATIONS — process tree signal propagation:");
        Console.WriteLine($"  Round A (SIGTERM to parent only):");
        Console.WriteLine($"    parent exited: {roundA.parentExited} (code {roundA.parentCode})");
        Console.WriteLine($"    child1 exited: {roundA.child1Exited}  ← Unix default: NO, parent's signal does NOT cascade to children");
        Console.WriteLine($"    child2 exited: {roundA.child2Exited}");
        Console.WriteLine();
        Console.WriteLine($"  Round B (Process.Kill(entireProcessTree:true)):");
        Console.WriteLine($"    parent exited: {roundB.parentExited} (code {roundB.parentCode})");
        Console.WriteLine($"    child1 exited: {roundB.child1Exited}  ← Expected: YES, .NET traverses tree");
        Console.WriteLine($"    child2 exited: {roundB.child2Exited}");
        Console.WriteLine();
        Console.WriteLine($"Substrate implication: for tree-kill semantics, Process.Kill(entireProcessTree:true) is the right .NET API surface; signal-to-leader alone is insufficient on macOS-arm64 (descendants must be killed individually OR via process-group machinery the substrate doesn't currently set up).");
        return 0;
    }

    sealed record RoundResult(
        bool parentExited, int parentCode,
        bool child1Exited, int child1Code,
        bool child2Exited, int child2Code);

    static RoundResult RunRound(string parentTargetPath, string childTargetPath, Action<int> signalParent, bool killOnExit)
    {
        using Process parent = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{parentTargetPath}\" \"{childTargetPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        parent.Start();

        int parentPid = -1;
        List<int> childPids = new();
        object pidLock = new();
        ManualResetEventSlim parentReady = new(false);
        ManualResetEventSlim bothChildrenReady = new(false);

        Thread reader = new(() =>
        {
            string? line;
            while ((line = parent.StandardOutput.ReadLine()) is not null)
            {
                Match pm = Regex.Match(line, @"PARENT_READY (\d+)");
                if (pm.Success && int.TryParse(pm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ppid))
                {
                    Volatile.Write(ref parentPid, ppid);
                    parentReady.Set();
                    continue;
                }
                Match cm = Regex.Match(line, @"CHILD_READY (\d+)");
                if (cm.Success && int.TryParse(cm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpid))
                {
                    lock (pidLock)
                    {
                        childPids.Add(cpid);
                        if (childPids.Count >= 2) bothChildrenReady.Set();
                    }
                }
            }
        }) { IsBackground = true, Name = "parent-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (parent.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "parent-stderr" };
        errDrain.Start();

        parentReady.Wait(TimeSpan.FromSeconds(30));
        bothChildrenReady.Wait(TimeSpan.FromSeconds(30));
        parentPid = Volatile.Read(ref parentPid);
        int[] kids;
        lock (pidLock) kids = childPids.ToArray();

        Console.WriteLine($"parent pid : {parentPid}");
        Console.WriteLine($"child pids : {string.Join(", ", kids)}");

        Thread.Sleep(300);  // settle

        signalParent(parentPid);

        bool parentExited = parent.WaitForExit(ObserveMs);
        int parentCode = parentExited ? parent.ExitCode : -1;

        // Probe each child individually for exit status by attempting GetProcessById.
        // Note: ExitCode is not available for GetProcessById-acquired Process; we just
        // observe whether the process is alive or gone (exited).
        (bool exited, int code) ProbeChild(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                bool exitedNow = p.WaitForExit(ObserveMs);
                return (exitedNow, -1);  // ExitCode unavailable via GetProcessById
            }
            catch (ArgumentException)
            {
                return (true, -2);  // already gone, code unobservable
            }
        }

        var (c1Ex, c1Code) = ProbeChild(kids[0]);
        var (c2Ex, c2Code) = ProbeChild(kids[1]);

        // Defensive cleanup (only if requested by caller — we want to observe natural state for Round A).
        if (killOnExit)
        {
            try { if (!parent.HasExited) parent.Kill(entireProcessTree: true); } catch { }
            foreach (int p in kids) { try { using Process pp = Process.GetProcessById(p); pp.Kill(); } catch { } }
        }
        else
        {
            // Always clean up anyway to avoid orphans across rounds.
            try { if (!parent.HasExited) parent.Kill(entireProcessTree: true); } catch { }
            foreach (int p in kids) { try { using Process pp = Process.GetProcessById(p); pp.Kill(); } catch { } }
        }

        return new RoundResult(parentExited, parentCode, c1Ex, c1Code, c2Ex, c2Code);
    }
}
