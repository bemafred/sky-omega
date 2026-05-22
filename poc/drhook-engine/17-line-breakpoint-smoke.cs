#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 17 — source-line breakpoints (ADR-006 symbols 6c)
// =====================================================================
//
// Set a breakpoint by file:line — the way a caller actually thinks — instead of method entry.
// The probe reads the target source for the "BREAK_HERE" marker line (a mid-method statement),
// sets a breakpoint there via DebugSession.SetBreakpointAtLine, runs, and confirms the hit lands
// at exactly that line (the stack frame shows "Worker.Step @ 17-line-target.cs:<markerLine>").
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine
//   failed; 7 breakpoint never hit; 8 hit at the wrong line; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 17-line-breakpoint-smoke.cs <path-to-17-line-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return LineBreakpoint17.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class LineBreakpoint17
{
    const string ModuleSubstr = "17-line-target";
    const string FileHint = "17-line-target.cs";
    const string Marker = "BREAK_HERE";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 17-line-breakpoint-smoke.cs <path-to-17-line-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' marker not found in target."); return 2; }
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

        WriteFixture(realPid, markerLine, code);
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
        if (!session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine))
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): could not bind {FileHint}:{markerLine}.");
            return 6;
        }
        Console.WriteLine($"breakpoint : set at {FileHint}:{markerLine}; resuming to the hit");
        session.Resume();

        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (hit is null || hit.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED: expected Breakpoint hit, got {(hit is null ? "timeout" : hit.Reason.ToString())}.");
            return 7;
        }

        string top = session.GetStackFrames().FirstOrDefault() ?? "(none)";
        Console.WriteLine($"hit        : {top}");

        // The hit must land at the marker line (mid-method, not entry) and name Worker.Step.
        bool ok = top.Contains("Worker.Step", StringComparison.Ordinal)
                  && top.EndsWith($":{markerLine}", StringComparison.Ordinal);
        if (!ok)
        {
            Console.Error.WriteLine($"FALSIFIED: hit '{top}' is not Worker.Step at line {markerLine}.");
            return 8;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 17 PASSED — line breakpoint at {FileHint}:{markerLine} bound mid-method and hit exactly there.");
        return 0;
    }

    static int FindMarkerLine(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(Marker, StringComparison.Ordinal))
                return i + 1; // 1-based
        return -1;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int markerLine, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"17-line-breakpoint-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 17 fixture — source-line breakpoint (ADR-006 symbols 6c)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"breakpoint       = {FileHint}:{markerLine} (mid-method)\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
