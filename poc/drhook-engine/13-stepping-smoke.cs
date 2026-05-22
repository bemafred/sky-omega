#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 13 — stepping (ADR-006 Phase 2)
// ===================================================
//
// Stepping rides the stopping model: at a stop, the engine creates an ICorDebugStepper on the
// stopped thread, arms a step, and Continues; completion arrives as a StepComplete callback the
// pump classifies as StopReason.Step. This probe stops at a breakpoint, then steps repeatedly
// and confirms each produces a Step stop.
//
// Flow: attach → setup Break → SetBreakpoint(Worker.Tick) → Resume → Breakpoint hit → StepOver
// N×, each WaitForStop returning StopReason.Step.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpoint failed;
//   7 breakpoint never hit; 8 a step produced no stop; 9 a step stop had the wrong reason; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 13-stepping-smoke.cs <path-to-11-bp-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Stepping13.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Stepping13
{
    const string ModuleSubstr = "11-bp-target";
    const string TypeName = "Worker";
    const string MethodName = "Tick";
    const int Steps = 3;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 13-stepping-smoke.cs <path-to-11-bp-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");

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

        if (!ready.Wait(TimeSpan.FromSeconds(90)))
        {
            Console.Error.WriteLine("FALSIFIED (target): no READY sentinel within 90s.");
            KillTree(proc);
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        DebugSession session;
        try { session = DebugSession.Attach(realPid, new NullSink()); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        int code = Drive(session);

        WriteFixture(realPid, code);

        // kill-first teardown (probe-08 pattern; avoids the detach/exit race, drhook-detach-exit-race).
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        session.Dispose();
        return code;
    }

    static int Drive(DebugSession session)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        if (!session.SetBreakpoint(ModuleSubstr, TypeName, MethodName))
        {
            Console.Error.WriteLine("FALSIFIED (SetBreakpoint): could not set the breakpoint.");
            return 6;
        }
        Console.WriteLine($"breakpoint : set on {TypeName}.{MethodName}; resuming to the hit");
        session.Resume();

        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (hit is null) { Console.Error.WriteLine("FALSIFIED: breakpoint never hit."); return 7; }
        if (hit.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"FALSIFIED: expected Breakpoint, got {hit.Reason}."); return 7; }
        Console.WriteLine($"hit        : {hit.Reason} at {TypeName}.{MethodName} — now stepping\n");

        for (int i = 1; i <= Steps; i++)
        {
            session.StepOver();
            StopInfo? stepped = session.WaitForStop(TimeSpan.FromSeconds(10));
            if (stepped is null) { Console.Error.WriteLine($"step {i}: no stop within 10s — StepComplete never fired."); return 8; }
            if (stepped.Reason != StopReason.Step) { Console.Error.WriteLine($"step {i}: stop reason {stepped.Reason}, expected Step."); return 9; }
            Console.WriteLine($"step {i}: STEP complete — debuggee synchronized at the next location");
        }

        Console.WriteLine($"\nPROBE 13 PASSED — stepped {Steps}× from a breakpoint; each StepComplete surfaced as StopReason.Step.");
        return 0;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"13-stepping-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 13 fixture — stepping (ADR-006 Phase 2)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"breakpoint       = {TypeName}.{MethodName}\n" +
            $"steps-required   = {Steps}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
