#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 27 — func-eval at an EXCEPTION stop ====================================
//
// The second probe-gated unknown from finding 35. Probe 23 proved func-eval composes inside a
// BREAKPOINT stop; an exception stop is a DIFFERENT ICorDebug controller state, and cordebug.idl
// notes "FuncEval will clear out the exception object on setup and restore it on completion" — so
// does func-eval work there? This probe stops on a first-chance ProbeException (probe 26), then
// func-evals get_Code on the IN-FLIGHT exception object (its value from GetCurrentException, NOT a
// local) via DebugSession.TryEvalCurrentExceptionMember, expecting Code == 42. PASS proves
// conditional exception breakpoints (ex.Code == 42, ex.Message …) are viable: it composes the
// exception stop with general member resolution (probe 24) across the eval's internal resume.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 7 no exception stop within budget;
//   8 exception stop but func-eval did NOT complete (the controller-state unknown — eval at an
//   exception stop fails/hangs); 9 completed but Code != 42; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 27-eval-exception-smoke.cs <path-to-27-eval-exception-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return EvalException27.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class EvalException27
{
    const string ExpectedType = "ProbeException";
    const string Member = "Code";
    const int ELEMENT_TYPE_I4 = 0x08;
    const int Expected = 42;
    const int Budget = 40;   // bounded resumes past stray first-chance exceptions

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 27-eval-exception-smoke.cs <path-to-27-eval-exception-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : at a FirstChance {ExpectedType}, func-eval {ExpectedType}.{Member} (expect {Expected})");

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
        Console.WriteLine("running    : resuming; waiting for a first-chance exception …");
        session.Resume();

        bool sawAnyException = false;
        for (int i = 0; i < Budget; i++)
        {
            StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
            if (stop is null)
            {
                Console.Error.WriteLine(sawAnyException
                    ? "FALSIFIED: no further stop before matching our exception."
                    : "FALSIFIED: no exception stop within budget.");
                return 7;
            }
            if (stop.Reason != StopReason.Exception) { session.Resume(); continue; }

            sawAnyException = true;
            string? type = session.GetCurrentExceptionTypeName();
            if (!(stop.ExceptionKind == ExceptionStopKind.FirstChance && type == ExpectedType))
            {
                session.Resume();
                continue;
            }

            Console.WriteLine($"stopped    : FirstChance {type} — func-eval {type}.{Member} on the in-flight exception …");
            EvalStatus status = session.TryEvalCurrentExceptionMember(Member, TimeSpan.FromSeconds(10), out ArgumentValue result);
            Console.WriteLine($"eval status: {status}");
            if (status == EvalStatus.Completed)
                Console.WriteLine($"eval result: elementType=0x{result.ElementType:X2}  value={(result.RawValue is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : "(none)")}");

            if (status != EvalStatus.Completed)
            {
                Console.Error.WriteLine($"FALSIFIED: func-eval at the exception stop did not complete ({status}).");
                return 8;
            }
            if (result.ElementType != ELEMENT_TYPE_I4 || !Equals(result.RawValue, Expected))
            {
                Console.Error.WriteLine($"FALSIFIED: expected I4={Expected}, got 0x{result.ElementType:X2}={result.RawValue}.");
                return 9;
            }

            session.Resume();
            Console.WriteLine($"\nPROBE 27 PASSED — func-eval works at an exception stop: {type}.{Member} = {Expected} (getter resolved on the runtime type, eval'd on the in-flight exception).");
            return 0;
        }

        Console.Error.WriteLine($"FALSIFIED: {Budget} stops seen, never matched {ExpectedType}+FirstChance.");
        return 7;
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
        string path = Path.Combine(dir, $"27-eval-exception-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 27 fixture — func-eval at an exception stop\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"plan             = func-eval {ExpectedType}.{Member} on the in-flight exception, expect {Expected}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
