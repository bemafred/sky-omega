#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 20 — func-eval WITH ARGUMENTS (breadth)
// ===========================================================
//
// Probe 19 proved func-eval of a static parameterless method. This proves CallFunction with an
// argument: at a stop, func-eval Probe.Doubled(21) — the argument 21 built via CreateValue/SetValue
// — and expect 42. Retires the "args work" unknown (instance methods then = arg0=this, a
// composition; Abort safety is wired into the timeout path here, validated separately).
//
// Falsification (exit codes): 2 usage; 3 no READY; 4 attach; 5 no stop; 6 setup; 7 timed out;
//   8 threw; 9 wrong value; 0 PASS (42).
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 20-eval-args-smoke.cs <path-to-20-eval-args-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return EvalArgs20.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class EvalArgs20
{
    const string ModuleSubstr = "20-eval-args-target";
    const string TypeName = "Probe";
    const string MethodName = "Doubled";
    const int Argument = 21;
    const int Expected = 42;
    const int ELEMENT_TYPE_I4 = 0x08;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 20-eval-args-smoke.cs <path-to-20-eval-args-target.cs>");
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
        Console.WriteLine($"stopped    : {setup.Reason} — func-evaluating {TypeName}.{MethodName}({Argument}) …");

        EvalStatus status = session.TryEvalStaticCallInt(ModuleSubstr, TypeName, MethodName, Argument, TimeSpan.FromSeconds(10), out ArgumentValue result);
        Console.WriteLine($"eval status: {status}");
        if (status == EvalStatus.Completed)
            Console.WriteLine($"eval result: elementType=0x{result.ElementType:X2}  value={(result.RawValue is { } v ? v.ToString(CultureInfo.InvariantCulture) : "(none)")}");

        switch (status)
        {
            case EvalStatus.SetupFailed: Console.Error.WriteLine("Result: eval setup failed."); return 6;
            case EvalStatus.TimedOut: Console.Error.WriteLine("Result: func-eval timed out (deadlock)."); return 7;
            case EvalStatus.ThrewException: Console.Error.WriteLine("Result: eval threw."); return 8;
        }

        if (result.ElementType != ELEMENT_TYPE_I4 || result.RawValue != Expected)
        {
            Console.Error.WriteLine($"FALSIFIED: expected I4={Expected}, got 0x{result.ElementType:X2}={result.RawValue}.");
            return 9;
        }

        Console.WriteLine($"\nPROBE 20 PASSED — func-eval with arguments works: {TypeName}.{MethodName}({Argument}) = {Expected}.");
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
        string path = Path.Combine(dir, $"20-eval-args-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 20 fixture — func-eval with arguments\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"eval             = {TypeName}.{MethodName}({Argument}) expected {Expected}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
