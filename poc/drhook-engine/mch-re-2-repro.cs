#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// MCH-RE-2 INVESTIGATION — minimal reproducer (no MSTest, no integration-test
// machinery). Two DebugSessions sequentially in the same .NET process. Tests
// four scenarios in sequence; reports SIGBUS / clean for each.
//
// Scenarios (run in sequence; if process crashes, output reflects partial run):
//
//   S1: Same target type both times (07-target.cs idle loop). Baseline.
//       Probe 42 already validated 20 cycles of this — should pass.
//
//   S2: Two DIFFERENT compiled exes — first 44-target's compiled idle loop,
//       then 07-target's compiled idle loop. If S1 passes + S2 SIGBUSes,
//       the issue is "different exe types in same hosting process."
//
//   S3: One simple target, then a dotnet-test-spawned testhost.
//       Mirrors the MSTest exe's actual scenario.
//
//   S4: S1 with explicit GC.Collect + Thread.Sleep + GC.WaitForPendingFinalizers
//       between sessions. Tests if finalizer / GC timing matters.
//
// Usage: dotnet run --no-cache mch-re-2-repro.cs

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Mch_RE_2_Repro.Run();

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Mch_RE_2_Repro
{
    public static int Run()
    {
        Console.WriteLine($"runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"pid        : {Environment.ProcessId}");
        Console.WriteLine();

        // S1: Two sequential sessions against the SAME target exe.
        Console.WriteLine("── S1: Two sessions against same target (07-target.cs) ──");
        bool s1 = RunScenarioSameTarget("/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/07-target.cs", "/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/07-target.cs", betweenSettleMs: 0);
        Console.WriteLine($"S1 result: {(s1 ? "PASSED" : "FAILED — substrate didn't reach completion")}");
        Console.WriteLine();

        // S2: Two sequential sessions against DIFFERENT target exes.
        Console.WriteLine("── S2: Two sessions, different targets (07-target then 44-target) ──");
        bool s2 = RunScenarioSameTarget("/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/07-target.cs", "/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/44-target.cs", betweenSettleMs: 0);
        Console.WriteLine($"S2 result: {(s2 ? "PASSED" : "FAILED — substrate didn't reach completion")}");
        Console.WriteLine();

        // S3: First simple target, then attach-via-PID where PID is a separately-launched
        // dotnet exec testhost.dll (simulating VSTest path's testhost attach).
        Console.WriteLine("── S3: Simple target, then dotnet-test-spawned testhost ──");
        bool s3 = RunScenarioMixed("/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/07-target.cs");
        Console.WriteLine($"S3 result: {(s3 ? "PASSED" : "FAILED")}");
        Console.WriteLine();

        // S4: S1 with GC + Sleep settle between sessions.
        Console.WriteLine("── S4: Two sessions same target, with GC.Collect+Sleep between ──");
        bool s4 = RunScenarioSameTarget("/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/07-target.cs", "/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/07-target.cs", betweenSettleMs: 1000);
        Console.WriteLine($"S4 result: {(s4 ? "PASSED" : "FAILED")}");
        Console.WriteLine();

        Console.WriteLine("──────────────────────────────────────");
        Console.WriteLine($"Final: S1={s1} S2={s2} S3={s3} S4={s4}");
        return 0;
    }

    // KILL-FIRST protocol: kill target BEFORE Dispose. drhook-detach-exit-race
    // limit doc states this is the validated mitigation (probe 12: 6/6 clean).
    // Testing if it works across multi-session in same hosting process.
    static bool RunScenarioSameTarget(string target1, string target2, int betweenSettleMs)
    {
        Console.WriteLine($"  Session 1: spawn {Path.GetFileName(target1)}");
        var (proc1, pid1) = SpawnAndReady(target1);
        if (proc1 is null) { Console.WriteLine("    spawn failed"); return false; }
        Console.WriteLine($"    pid={pid1}, attaching...");
        try
        {
            var s1 = DebugSession.Attach(pid1, new NullSink());
            Thread.Sleep(200);
            Console.WriteLine($"    KILL-FIRST: proc1...");
            KillTree(proc1);
            Thread.Sleep(100);  // let kernel reap + mscordbi notice
            Console.WriteLine($"    disposing session 1...");
            s1.Dispose();
            Console.WriteLine($"    session 1 disposed");
        }
        catch (Exception ex) { Console.WriteLine($"    session 1 EXCEPTION: {ex.GetType().Name}: {ex.Message}"); return false; }

        if (betweenSettleMs > 0)
        {
            Console.WriteLine($"  Settle: GC.Collect + Sleep({betweenSettleMs}ms)");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Thread.Sleep(betweenSettleMs);
        }

        Console.WriteLine($"  Session 2: spawn {Path.GetFileName(target2)}");
        var (proc2, pid2) = SpawnAndReady(target2);
        if (proc2 is null) { Console.WriteLine("    spawn failed"); return false; }
        Console.WriteLine($"    pid={pid2}, attaching...");
        try
        {
            var s2 = DebugSession.Attach(pid2, new NullSink());
            Thread.Sleep(200);
            Console.WriteLine($"    KILL-FIRST: proc2...");
            KillTree(proc2);
            Thread.Sleep(100);
            Console.WriteLine($"    disposing session 2...");
            s2.Dispose();
            Console.WriteLine($"    session 2 disposed");
        }
        catch (Exception ex) { Console.WriteLine($"    session 2 EXCEPTION: {ex.GetType().Name}: {ex.Message}"); return false; }

        return true;
    }

    static bool RunScenarioMixed(string firstTarget)
    {
        Console.WriteLine($"  Session 1: spawn {Path.GetFileName(firstTarget)}");
        var (proc1, pid1) = SpawnAndReady(firstTarget);
        if (proc1 is null) { Console.WriteLine("    spawn failed"); return false; }
        Console.WriteLine($"    pid={pid1}, attaching...");
        try
        {
            using var s1 = DebugSession.Attach(pid1, new NullSink());
            Thread.Sleep(200);
            s1.Dispose();
            Console.WriteLine($"    session 1 disposed");
        }
        catch (Exception ex) { Console.WriteLine($"    session 1 EXCEPTION: {ex.GetType().Name}: {ex.Message}"); KillTree(proc1); return false; }
        KillTree(proc1);

        Console.WriteLine($"  Session 2: spawn dotnet test with VSTEST_HOST_DEBUG=1, parse PID, attach to testhost");
        Process? procTest = SpawnVstest("/Users/bemafred/src/repos/sky-omega/tests/DrHook.Engine.IntegrationTargets.Vstest/DrHook.Engine.IntegrationTargets.Vstest.csproj", out int testhostPid);
        if (procTest is null || testhostPid <= 0) { Console.WriteLine("    vstest spawn failed"); return false; }
        Console.WriteLine($"    testhost pid={testhostPid}, attaching...");
        try
        {
            using var s2 = DebugSession.Attach(testhostPid, new NullSink());
            Thread.Sleep(500);
            s2.Dispose();
            Console.WriteLine($"    session 2 disposed");
        }
        catch (Exception ex) { Console.WriteLine($"    session 2 EXCEPTION: {ex.GetType().Name}: {ex.Message}"); KillTree(procTest); return false; }
        KillTree(procTest);

        return true;
    }

    static (Process? proc, int pid) SpawnAndReady(string targetPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{Path.GetFullPath(targetPath)}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        Process proc = new() { StartInfo = psi };
        proc.Start();

        int pid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                Match m = Regex.Match(line, @"READY (\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                {
                    Volatile.Write(ref pid, p);
                    ready.Set();
                }
            }
        }) { IsBackground = true };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } }) { IsBackground = true };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(60))) { KillTree(proc); return (null, -1); }
        return (proc, Volatile.Read(ref pid));
    }

    static Process? SpawnVstest(string csprojPath, out int testhostPid)
    {
        testhostPid = -1;
        var psi = new ProcessStartInfo("dotnet", $"test \"{Path.GetFullPath(csprojPath)}\" -c Release --no-build --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["VSTEST_HOST_DEBUG"] = "1" },
        };
        Process proc = new() { StartInfo = psi };
        proc.Start();

        int pid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                Match m = Regex.Match(line, @"Process Id:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                {
                    Volatile.Write(ref pid, p);
                    ready.Set();
                }
            }
        }) { IsBackground = true };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } }) { IsBackground = true };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(60))) { KillTree(proc); return null; }
        testhostPid = Volatile.Read(ref pid);
        return proc;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
    }
}
