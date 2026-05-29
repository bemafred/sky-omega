#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 23 — func-eval INSIDE a conditional-breakpoint predicate (re-entrancy)
// ==========================================================================================
//
// Member-access conditions (s.Length > 3) need func-eval to evaluate the condition. But
// WaitForConditionalStop loops Resume/WaitForStop, and func-eval ALSO uses Resume/WaitForStop —
// so a func-evaluating predicate nests those calls. Does that work, or re-enter and break? This
// probe answers it: the predicate func-evals s.Length (via the proven TryEvalInstanceCall) and the
// conditional breakpoint must stop at the first iteration where s.Length > 3 (the target cycles
// lengths 1..4, so the first match is "abcd", length 4). Pure probe — no engine change; it composes
// WaitForConditionalStop + TryEvalInstanceCall.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 conditional stop timed out (the re-entrancy hazard would show here); 8 stopped but
//   s.Length != 4 (stopped at the wrong iteration); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 23-condeval-smoke.cs <path-to-23-condeval-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return CondEval23.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class CondEval23
{
    const string ModuleSubstr = "23-condeval-target";
    const string FileHint = "23-condeval-target.cs";
    const string Marker = "CONDEVAL_HERE";
    const string ThisLocal = "s";
    const string DeclModule = "System.Private.CoreLib";
    const string DeclType = "System.String";
    const string Method = "get_Length";
    const int Threshold = 3;       // condition: s.Length > 3
    const int FirstMatch = 4;      // first cycling length that exceeds 3

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 23-condeval-smoke.cs <path-to-23-condeval-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"condition  : s.Length > {Threshold} (func-eval'd) at {FileHint}:{markerLine}");

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
        // The condition func-evals s.Length and compares > 3 — exercising func-eval INSIDE the
        // policy's condition delegate (ADR-010 Increment 2c: policy attached at SetBreakpoint time).
        long EvalLength()
        {
            EvalStatus st = session.TryEvalInstanceCall(ThisLocal, DeclModule, DeclType, Method, TimeSpan.FromSeconds(10), out ArgumentValue v);
            return st == EvalStatus.Completed && v.RawValue is { } len ? len : -1;
        }
        Func<IEvalContext, bool> predicate = _ => EvalLength() > Threshold;
        var policy = new BreakpointPolicy(Condition: predicate);
        if (session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, policy) == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): {FileHint}:{markerLine}.");
            return 6;
        }

        Console.WriteLine($"running    : breakpoint set with Condition policy; resuming until s.Length > {Threshold} (each hit func-evals the condition) …");
        session.Resume();

        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(25));
        if (stop is null) { Console.Error.WriteLine("FALSIFIED: conditional stop timed out (func-eval-in-predicate may have re-entered)."); return 7; }
        if (stop.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"FALSIFIED: surfaced {stop.Reason}."); return 7; }

        long lengthAtStop = EvalLength();
        Console.WriteLine($"stopped    : condition held — s.Length = {lengthAtStop}");
        if (lengthAtStop != FirstMatch)
        {
            Console.Error.WriteLine($"FALSIFIED: stopped at s.Length={lengthAtStop}, expected first match {FirstMatch}.");
            return 8;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 23 PASSED — func-eval works inside a conditional predicate: stopped at the first s.Length > {Threshold} (= {FirstMatch}).");
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
        string path = Path.Combine(dir, $"23-condeval-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 23 fixture — func-eval inside a conditional predicate\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"condition        = s.Length > {Threshold} (func-eval'd each hit); first match length {FirstMatch}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
