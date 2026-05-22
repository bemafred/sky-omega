#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 24 — GENERAL member resolution (box.Size, no hardcoded type)
// ================================================================================
//
// Probes 21/23 hardcoded the declaring type (String.get_Length). This resolves a member on the
// value's RUNTIME type: at a breakpoint where `box` (a Box with Size = 42) is in scope, TryEvalMemberCall
// reads box, derives its runtime class (Box, in the target's own module), finds get_Size, and
// func-evals it — expecting 42. No type/module hardcoded; the walker only knows "box" and "Size".
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 no hit; 8 eval not Completed (SetupFailed = runtime-type resolution failed); 9 wrong value; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 24-member-resolution-smoke.cs <path-to-24-member-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Member24.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Member24
{
    const string ModuleSubstr = "24-member-target";
    const string FileHint = "24-member-target.cs";
    const string Marker = "MEMBER_HERE";
    const string ThisLocal = "box";
    const string Member = "Size";
    const int ELEMENT_TYPE_I4 = 0x08;
    const int Expected = 42;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 24-member-resolution-smoke.cs <path-to-24-member-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"resolve    : {ThisLocal}.{Member} on its runtime type at {FileHint}:{markerLine}");

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
        try { session.Dispose(); } catch { /* best effort */ }
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
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): {FileHint}:{markerLine}.");
            return 6;
        }
        session.Resume();

        StopInfo? hit = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (hit is null || hit.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED: expected Breakpoint hit, got {(hit is null ? "timeout" : hit.Reason.ToString())}.");
            return 7;
        }
        Console.WriteLine($"stopped    : in Worker.Inspect — resolving {ThisLocal}.{Member} on its runtime type …");

        EvalStatus status = session.TryEvalMemberCall(ThisLocal, Member, TimeSpan.FromSeconds(10), out ArgumentValue result);
        Console.WriteLine($"eval status: {status}");
        if (status == EvalStatus.Completed)
            Console.WriteLine($"eval result: elementType=0x{result.ElementType:X2}  value={(result.RawValue is { } v ? v.ToString(CultureInfo.InvariantCulture) : "(none)")}");

        if (status != EvalStatus.Completed) { Console.Error.WriteLine($"Result: member eval did not complete ({status})."); return 8; }
        if (result.ElementType != ELEMENT_TYPE_I4 || result.RawValue != Expected)
        {
            Console.Error.WriteLine($"FALSIFIED: expected I4={Expected}, got 0x{result.ElementType:X2}={result.RawValue}.");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 24 PASSED — general member resolution: {ThisLocal}.{Member} = {Expected} (getter resolved on the runtime type Box, no hardcoding).");
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
        string path = Path.Combine(dir, $"24-member-resolution-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 24 fixture — general member resolution (box.Size)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"resolved         = {ThisLocal}.{Member} on runtime type (no hardcoded type/module), expected {Expected}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
