#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 12 — breakpoint hit (ADR-006 Phase 2, breakpoint setting 4c — the payoff)
// ==============================================================================================
//
// The whole arc lands here: a debugger-SET breakpoint, HIT by the running target, surfacing as
// a StopReason.Breakpoint the caller controls. Flow:
//   1. attach; WaitForStop -> the target's setup Debugger.Break (synchronized window),
//   2. SetBreakpoint("11-bp-target", "Worker", "Tick")  — navigate(4a)+metadata(4b)+create(4c),
//   3. Resume; WaitForStop -> a Breakpoint stop (Worker.Tick entry was hit),
//   4. prove frozen while held, Resume, and confirm the breakpoint re-arms across N hits.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpoint failed; 7 the
//   breakpoint never hit (timeout after resume); 8 a stop arrived but not StopReason.Breakpoint;
//   9 not frozen while held (auto-continue leaked); 0 PASS (N breakpoint hits).
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 12-breakpoint-hit-smoke.cs <path-to-11-bp-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Breakpoint12.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Breakpoint12
{
    const string ModuleSubstr = "11-bp-target";
    const string TypeName = "Worker";
    const string MethodName = "Tick";
    const int Hits = 5;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 12-breakpoint-hit-smoke.cs <path-to-11-bp-target.cs>");
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

        // Verdict + fixture are captured before teardown (the breakpoint result is independent of it).
        WriteFixture(realPid, code);

        // Teardown: kill the target FIRST, then dispose — the probe-08 pattern. Disposing while
        // stopped at a breakpoint and then killing races mscordbi's exit handler (ExitProcessWorkItem,
        // finding 14 class); killing first lets the exit be processed while cleanly attached.
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
            Console.Error.WriteLine($"FALSIFIED (no setup stop): got {(setup is null ? "timeout" : setup.Reason.ToString())}, expected Break.");
            return 5;
        }
        Console.WriteLine($"stopped    : {setup.Reason} (setup) — setting breakpoint on {TypeName}.{MethodName}");

        if (session.SetBreakpoint(ModuleSubstr, TypeName, MethodName) == 0)
        {
            Console.Error.WriteLine("FALSIFIED (SetBreakpoint): could not set the breakpoint.");
            return 6;
        }
        Console.WriteLine($"breakpoint : set on {TypeName}.{MethodName}; resuming\n");
        session.Resume();

        for (int i = 1; i <= Hits; i++)
        {
            StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(10));
            if (hit is null) { Console.Error.WriteLine($"hit {i}: breakpoint never fired within 10s."); return 7; }
            if (hit.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"hit {i}: stop reason {hit.Reason}, expected Breakpoint."); return 8; }
            Console.WriteLine($"hit {i}: BREAKPOINT — Worker.Tick entry; debuggee synchronized");

            StopInfo? leaked = session.WaitForStop(TimeSpan.FromMilliseconds(400));
            if (leaked is not null) { Console.Error.WriteLine($"hit {i}: another stop ({leaked.Reason}) WITHOUT resume — not frozen."); return 9; }

            session.Resume(); // run to the next Tick → next hit
        }

        Console.WriteLine($"\nPROBE 12 PASSED — debugger-set breakpoint on {TypeName}.{MethodName} hit {Hits}× via the stopping model; frozen between hits.");
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
        string path = Path.Combine(dir, $"12-breakpoint-hit-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 12 fixture — breakpoint hit (ADR-006 Phase 2, 4c)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"breakpoint       = {TypeName}.{MethodName}\n" +
            $"hits-required    = {Hits}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
