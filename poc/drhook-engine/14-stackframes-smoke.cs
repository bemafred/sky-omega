#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 14 — stack frames (ADR-006 Phase 2, inspection 5a)
// ======================================================================
//
// Makes the stop LOCATION observable: at a stop, walk the managed call stack of the stopped
// thread → method names. The probe stops at the Worker.Tick breakpoint and checks the stack:
// the top frame is Worker.Tick, and there is at least one caller above it (the target's loop).
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpoint failed;
//   7 breakpoint never hit; 8 zero frames (walk failed); 9 top frame is not Worker.Tick; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 14-stackframes-smoke.cs <path-to-11-bp-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return StackFrames14.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class StackFrames14
{
    const string ModuleSubstr = "11-bp-target";
    const string TypeName = "Worker";
    const string MethodName = "Tick";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 14-stackframes-smoke.cs <path-to-11-bp-target.cs>");
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
            Console.Error.WriteLine("FALSIFIED (SetBreakpoint).");
            return 6;
        }
        session.Resume();

        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (hit is null || hit.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED: expected Breakpoint hit, got {(hit is null ? "timeout" : hit.Reason.ToString())}.");
            return 7;
        }

        string[] frames = session.GetStackFrames().ToArray();
        Console.WriteLine($"stopped at {TypeName}.{MethodName} — call stack ({frames.Length} frames):");
        for (int i = 0; i < frames.Length; i++) Console.WriteLine($"  #{i}  {frames[i]}");

        if (frames.Length == 0) { Console.Error.WriteLine("FALSIFIED: zero frames — the walk failed."); return 8; }
        if (!frames[0].Contains(MethodName, StringComparison.Ordinal) || !frames[0].Contains(TypeName, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"FALSIFIED: top frame '{frames[0]}' is not {TypeName}.{MethodName}.");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 14 PASSED — top frame is {frames[0]} with {frames.Length - 1} caller frame(s) above it.");
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
        string path = Path.Combine(dir, $"14-stackframes-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 14 fixture — stack frames (ADR-006 Phase 2, 5a)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"breakpoint       = {TypeName}.{MethodName}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
