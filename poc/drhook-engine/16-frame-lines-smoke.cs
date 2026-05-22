#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 16 — source lines in stack frames (ADR-006 symbols 6b)
// ==========================================================================
//
// Turns "where did we stop" from a method name into a SOURCE LOCATION. At the Worker.Tick
// breakpoint, GetStackFrames now reads each frame's IL offset (ICorDebugILFrame.GetIP@11) and
// maps it through the module's Portable PDB (SymbolReader) to "Type.Method @ file:line".
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpoint failed;
//   7 breakpoint never hit; 8 top frame has no "@ file.cs:line" (PDB/IP mapping failed); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 16-frame-lines-smoke.cs <path-to-11-bp-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return FrameLines16.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class FrameLines16
{
    const string ModuleSubstr = "11-bp-target";
    const string TypeName = "Worker";
    const string MethodName = "Tick";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 16-frame-lines-smoke.cs <path-to-11-bp-target.cs>");
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
        Console.WriteLine($"stopped at {TypeName}.{MethodName} — call stack with source lines:");
        for (int i = 0; i < frames.Length; i++) Console.WriteLine($"  #{i}  {frames[i]}");

        // Top frame must name the method AND carry a "@ <file>.cs:<line>" source location.
        bool ok = frames.Length > 0
                  && frames[0].Contains($"{TypeName}.{MethodName}", StringComparison.Ordinal)
                  && Regex.IsMatch(frames[0], @"@ .+\.cs:\d+");
        if (!ok)
        {
            Console.Error.WriteLine($"FALSIFIED: top frame '{(frames.Length > 0 ? frames[0] : "(none)")}' lacks a source line.");
            return 8;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 16 PASSED — top frame carries a source location: {frames[0]}");
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
        string path = Path.Combine(dir, $"16-frame-lines-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 16 fixture — source lines in stack frames (ADR-006 symbols 6b)\n" +
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
