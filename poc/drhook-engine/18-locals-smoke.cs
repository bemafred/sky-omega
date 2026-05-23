#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 18 — locals by name (ADR-006 symbols 6d)
// ============================================================
//
// Completes "stopped here, with THESE named values". At a line breakpoint inside
// Worker.Step(5) (where a=6, b=60 are assigned), GetLocals pairs PDB local NAMES with the values
// read from the frame: a (I4) = 6, b (I8) = 60.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine
//   failed; 7 breakpoint never hit; 8 no named locals read; 9 a/b name/type/value wrong; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 18-locals-smoke.cs <path-to-18-locals-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Locals18.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Locals18
{
    const string ModuleSubstr = "18-locals-target";
    const string FileHint = "18-locals-target.cs";
    const string Marker = "LOCALS_READY";
    const int ELEMENT_TYPE_I4 = 0x08;
    const int ELEMENT_TYPE_I8 = 0x0A;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 18-locals-smoke.cs <path-to-18-locals-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' marker not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"break line : {FileHint}:{markerLine}");

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

        int code = Drive(session, markerLine);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        session.Dispose();
        return code;
    }

    static int Drive(DebugSession session, int markerLine)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        if (session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine) == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): could not bind {FileHint}:{markerLine}.");
            return 6;
        }
        session.Resume();

        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (hit is null || hit.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED: expected Breakpoint hit, got {(hit is null ? "timeout" : hit.Reason.ToString())}.");
            return 7;
        }

        LocalValue[] locals = session.GetLocals().ToArray();
        Console.WriteLine($"stopped at {FileHint}:{markerLine} — named locals:");
        foreach (LocalValue l in locals)
            Console.WriteLine($"  {l.Name} : elementType=0x{l.ElementType:X2}  value={(l.RawValue is { } v ? v.ToString(CultureInfo.InvariantCulture) : "(n/a)")}");

        if (locals.Length == 0) { Console.Error.WriteLine("FALSIFIED: no named locals read."); return 8; }

        bool aOk = locals.Any(l => l.Name == "a" && l.ElementType == ELEMENT_TYPE_I4 && l.RawValue == 6);
        bool bOk = locals.Any(l => l.Name == "b" && l.ElementType == ELEMENT_TYPE_I8 && l.RawValue == 60);
        if (!aOk || !bOk)
        {
            Console.Error.WriteLine($"FALSIFIED: expected a=6 (I4) and b=60 (I8); aOk={aOk} bOk={bOk}.");
            return 9;
        }

        session.Resume();
        Console.WriteLine("\nPROBE 18 PASSED — read named locals a=6 (I4), b=60 (I8) at the breakpoint.");
        return 0;
    }

    static int FindMarkerLine(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(Marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
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
        string path = Path.Combine(dir, $"18-locals-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 18 fixture — locals by name (ADR-006 symbols 6d)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"locals           = a=6 (I4), b=60 (I8) at Worker.Step\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
