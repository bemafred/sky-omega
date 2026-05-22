#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 15 — variables (ADR-006 Phase 2, inspection 5b)
// ===================================================================
//
// Makes the stop VALUES observable: at a breakpoint on Worker.Compute(int n, long total) called
// with (7, 100), read the active frame's arguments and confirm n=7 (I4) and total=100 (I8) — and
// that `this` (arg 0) reports a Class element type with no primitive raw value.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpoint failed;
//   7 breakpoint never hit; 8 no arguments read (ILFrame QI failed — wrong IID?); 9 argument
//   values/types wrong; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 15-variables-smoke.cs <path-to-15-vars-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Variables15.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Variables15
{
    const string ModuleSubstr = "15-vars-target";
    const string TypeName = "Worker";
    const string MethodName = "Compute";

    // CorElementType
    const int ELEMENT_TYPE_I4 = 0x08;
    const int ELEMENT_TYPE_I8 = 0x0A;
    const int ELEMENT_TYPE_CLASS = 0x12;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 15-variables-smoke.cs <path-to-15-vars-target.cs>");
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

        ArgumentValue[] args = session.GetArguments().ToArray();
        Console.WriteLine($"stopped at {TypeName}.{MethodName} — {args.Length} argument(s):");
        for (int i = 0; i < args.Length; i++)
            Console.WriteLine($"  arg[{i}]  elementType=0x{args[i].ElementType:X2}  raw={(args[i].RawValue is { } v ? v.ToString(CultureInfo.InvariantCulture) : "(ref)")}");

        if (args.Length == 0) { Console.Error.WriteLine("FALSIFIED: no arguments read — ICorDebugILFrame QI likely failed (wrong IID?)."); return 8; }

        bool nOk = args.Any(a => a.ElementType == ELEMENT_TYPE_I4 && a.RawValue == 7);
        bool totalOk = args.Any(a => a.ElementType == ELEMENT_TYPE_I8 && a.RawValue == 100);
        bool thisOk = args.Any(a => a.ElementType == ELEMENT_TYPE_CLASS && a.RawValue is null);
        if (!nOk || !totalOk)
        {
            Console.Error.WriteLine($"FALSIFIED: expected I4=7 and I8=100; nOk={nOk} totalOk={totalOk} thisOk={thisOk}.");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 15 PASSED — read arguments: n=7 (I4), total=100 (I8){(thisOk ? ", this=(Class ref)" : "")}.");
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
        string path = Path.Combine(dir, $"15-variables-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 15 fixture — variables (ADR-006 Phase 2, 5b)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"breakpoint       = {TypeName}.{MethodName}(int n, long total) called with (7, 100)\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
