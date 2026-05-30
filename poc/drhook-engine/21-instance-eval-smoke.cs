#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 21 — instance-method func-eval (the realistic case: s.Length)
// =================================================================================
//
// The conditional-breakpoint workhorse: call an instance method/property on a value read from a
// local. At a breakpoint inside Worker.Inspect (where string s = "hello" is in scope), func-eval
// s.Length — i.e. String.get_Length with this = s — and expect 5. Exercises: passing a read
// debuggee value as `this`, and resolving a method on a DIFFERENT module (System.Private.CoreLib).
//
// Falsification (exit codes): 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break;
//   6 SetBreakpointAtLine failed; 7 no hit; 8 eval not Completed (setup/timeout/threw); 9 wrong
//   value; 0 PASS (5).
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 21-instance-eval-smoke.cs <path-to-21-instance-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Instance21.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Instance21
{
    const string ModuleSubstr = "21-instance-target";
    const string FileHint = "21-instance-target.cs";
    const string Marker = "INSTANCE_HERE";
    const string ThisLocal = "s";
    const string DeclModule = "System.Private.CoreLib";
    const string DeclType = "System.String";
    const string Method = "get_Length";
    const int ELEMENT_TYPE_I4 = 0x08;
    const int Expected = 5; // "hello".Length

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 21-instance-eval-smoke.cs <path-to-21-instance-target.cs>");
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
        Console.WriteLine($"stopped    : in Worker.Inspect — func-evaluating {ThisLocal}.Length (String.{Method} on `{ThisLocal}`) …");

        EvalStatus status = session.TryEvalInstanceCall(ThisLocal, DeclModule, DeclType, Method, TimeSpan.FromSeconds(10), out ArgumentValue result);
        Console.WriteLine($"eval status: {status}");
        if (status == EvalStatus.Completed)
            Console.WriteLine($"eval result: elementType=0x{result.ElementType:X2}  value={(result.RawValue is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : "(none)")}");

        if (status != EvalStatus.Completed)
        {
            Console.Error.WriteLine($"Result: instance func-eval did not complete ({status}).");
            return 8;
        }
        if (result.ElementType != ELEMENT_TYPE_I4 || !Equals(result.RawValue, Expected))
        {
            Console.Error.WriteLine($"FALSIFIED: expected I4={Expected}, got 0x{result.ElementType:X2}={result.RawValue}.");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 21 PASSED — instance func-eval works: \"hello\".Length = {Expected} via String.{Method}(this=s).");
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
        string path = Path.Combine(dir, $"21-instance-eval-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 21 fixture — instance-method func-eval (s.Length)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"eval             = String.{Method}(this=s) on s=\"hello\", expected {Expected}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
