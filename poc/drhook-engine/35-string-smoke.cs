#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 35 — REFERENCE-STRING RENDERING (ICorDebugStringValue via Dereference) ==
//
// First slice of object inspection (the longest pole of Phase 3 substrate). String is the most
// common reference result (ex.Message-style debugging, return values of get_String, etc.). The
// chain: QI ICorDebugReferenceValue -> Dereference -> QI ICorDebugStringValue -> GetLength@9 +
// GetString@10. Hooked into Variables.ReadValue so every value read (locals, args, func-eval
// results) gets free string rendering when applicable. ArgumentValue / LocalValue gained an
// optional StringValue field (default null; non-strings unaffected).
//
// Target throws ProbeException(Description="hello string") in a caught loop. Probe arms a filter
// on ProbeException, WaitForStop returns the matching Exception stop, TryEvalCurrentExceptionMember
// func-evals get_Description, and the returned ArgumentValue carries the rendered text.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 ArmExceptionFilter;
//   7 no exception stop; 8 eval status != Completed; 9 StringValue null or mismatch; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 35-string-smoke.cs <path-to-35-string-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return String35.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class String35
{
    const string ExpectedType   = "ProbeException";
    const string Member         = "Description";
    const string ExpectedString = "hello string";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 35-string-smoke.cs <path-to-35-string-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : arm filter on {ExpectedType}; at the exception stop, func-eval {Member}; expect StringValue == \"{ExpectedString}\".");

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
        try { session.Dispose(); } catch { /* best effort */ }
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

        int id = session.ArmExceptionFilter(ExpectedType, ExceptionStopKind.FirstChance);
        if (id <= 0) { Console.Error.WriteLine("FALSIFIED: ArmExceptionFilter returned non-positive."); return 6; }
        session.Resume();

        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
        string? type = session.GetCurrentExceptionTypeName();
        if (stop is null || stop.Reason != StopReason.Exception || type != ExpectedType)
        {
            Console.Error.WriteLine($"FALSIFIED (exception stop): {(stop is null ? "timeout" : stop.Reason.ToString())}, type={type ?? "(none)"}.");
            return 7;
        }
        Console.WriteLine($"stopped    : Exception {type} @ {stop.ExceptionKind} — func-eval {Member} …");

        EvalStatus status = session.TryEvalCurrentExceptionMember(Member, TimeSpan.FromSeconds(10), out ArgumentValue result);
        Console.WriteLine($"eval status: {status}");
        if (status != EvalStatus.Completed)
        {
            Console.Error.WriteLine($"FALSIFIED: eval did not complete ({status}).");
            return 8;
        }
        Console.WriteLine($"eval result: elementType=0x{result.ElementType:X2}  raw={(result.RawValue?.ToString(CultureInfo.InvariantCulture) ?? "(none)")}  StringValue=\"{result.StringValue ?? "(null)"}\"");

        if (result.StringValue != ExpectedString)
        {
            Console.Error.WriteLine($"FALSIFIED: StringValue mismatch — expected \"{ExpectedString}\", got \"{result.StringValue ?? "(null)"}\".");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 35 PASSED — reference-string rendering works: func-eval of {ExpectedType}.{Member} returned a string ICorDebugValue whose contents were resolved through Dereference -> ICorDebugStringValue -> GetString.");
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
        string path = Path.Combine(dir, $"35-string-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 35 fixture — reference-string rendering (ICorDebugStringValue via Dereference)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"plan             = func-eval {ExpectedType}.{Member}; expect StringValue == \"{ExpectedString}\"\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
