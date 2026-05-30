#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 19 — DOES FUNC-EVAL WORK IN OUR ENGINE? (the decisive experiment)
// =====================================================================================
//
// netcoredbg's func-eval deadlocks on macOS/ARM64 — but that was characterized in netcoredbg, not
// proven a platform limit (vsdbg/Rider eval fine). This probe finds out whether OUR ICorDebug
// usage deadlocks. At a Debugger.Break stop it func-evals Probe.Answer() (a static method
// returning 42) and reports:
//   Completed + 42  -> func-eval WORKS  -> full C# expression eval (Roslyn + func-eval) is open
//   TimedOut        -> deadlock, like netcoredbg -> client-side eval is the path
//   ThrewException / SetupFailed -> other (investigate)
//
// Falsification (exit codes): 2 usage; 3 no READY; 4 attach; 5 no Break stop; 6 setup failed;
//   7 TIMED OUT (deadlock); 8 threw; 9 completed but wrong value; 0 PASS (Completed + 42).
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 19-funceval-smoke.cs <path-to-19-eval-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return FuncEval19.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class FuncEval19
{
    const string ModuleSubstr = "19-eval-target";
    const string TypeName = "Probe";
    const string MethodName = "Answer";
    const int ELEMENT_TYPE_I4 = 0x08;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 19-funceval-smoke.cs <path-to-19-eval-target.cs>");
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
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        Console.WriteLine($"stopped    : {setup.Reason} — func-evaluating {TypeName}.{MethodName}() …");

        EvalStatus status = session.TryEvalStaticCall(ModuleSubstr, TypeName, MethodName, TimeSpan.FromSeconds(10), out ArgumentValue result);
        Console.WriteLine($"eval status: {status}");
        if (status == EvalStatus.Completed)
            Console.WriteLine($"eval result: elementType=0x{result.ElementType:X2}  value={(result.RawValue is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : "(none)")}");

        switch (status)
        {
            case EvalStatus.SetupFailed:
                Console.Error.WriteLine("Result: eval setup failed (could not resolve / create eval).");
                return 6;
            case EvalStatus.TimedOut:
                Console.Error.WriteLine("Result: FUNC-EVAL DEADLOCKED (no EvalComplete in 10s) — same failure mode as netcoredbg.");
                return 7;
            case EvalStatus.ThrewException:
                Console.Error.WriteLine("Result: eval threw an exception.");
                return 8;
        }

        if (result.ElementType != ELEMENT_TYPE_I4 || !Equals(result.RawValue, 42))
        {
            Console.Error.WriteLine($"FALSIFIED: completed but result is not I4=42 (got 0x{result.ElementType:X2}={result.RawValue}).");
            return 9;
        }

        Console.WriteLine("\nPROBE 19 PASSED — func-eval WORKS in our engine: Probe.Answer() returned 42. The netcoredbg deadlock is NOT a platform limit for us.");
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
        string path = Path.Combine(dir, $"19-funceval-{rid}-{ts}.txt");
        string verdict = code == 0 ? "PASSED-funceval-works"
                       : code == 7 ? "FALSIFIED-deadlock"
                       : $"FALSIFIED-{code}";
        string body =
            "# DrHook.Engine probe 19 fixture — func-eval decisive experiment\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"eval             = {TypeName}.{MethodName}() expected 42\n" +
            $"verdict          = {verdict}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
