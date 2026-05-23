#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 37 — CROSS-MODULE member resolution via ICorDebugType.GetBase ============
//
// Object inspection slice 3. MemberResolver.ResolveGetter now walks inheritance via
// ICorDebugValue2.GetExactType@3 → ICorDebugType.GetBase@7. The runtime handles mdTypeRef
// resolution across module boundaries — no manual GetTypeRefProps + module-enumeration dance
// required. At each Type level, MetadataResolver.FindMethodInType (within-module walker from
// probe 36) still handles same-module extends; GetBase crosses to the parent's actual module.
//
// Target: ProbeException : System.Exception directly (NO same-module intermediate). ex.Message
// can ONLY be resolved by crossing into CoreLib. The probe arms a filter, waits for the
// ProbeException stop, func-evals get_Message, and expects the rendered string content to come
// back via probe 35's reference-string path.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 ArmExceptionFilter;
//   7 no exception stop; 8 eval status != Completed (cross-module walk failed);
//   9 StringValue null or mismatch; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 37-crossmod-smoke.cs <path-to-37-crossmod-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return CrossMod37.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class CrossMod37
{
    const string ExpectedType   = "ProbeException";
    const string Member         = "Message";     // declared on System.Exception in CoreLib — cross-module
    const string ExpectedString = "hello message";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 37-crossmod-smoke.cs <path-to-37-crossmod-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : at a ProbeException stop, func-eval ex.{Member} via cross-module ICorDebugType.GetBase walk; expect StringValue == \"{ExpectedString}\".");

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
        Console.WriteLine($"stopped    : Exception {type} @ {stop.ExceptionKind}");

        EvalStatus status = session.TryEvalCurrentExceptionMember(Member, TimeSpan.FromSeconds(10), out ArgumentValue result);
        Console.WriteLine($"eval status: {status}  StringValue=\"{result.StringValue ?? "(null)"}\"");
        if (status != EvalStatus.Completed)
        {
            Console.Error.WriteLine($"FALSIFIED: cross-module eval did not complete ({status}).");
            return 8;
        }
        if (result.StringValue != ExpectedString)
        {
            Console.Error.WriteLine($"FALSIFIED: StringValue mismatch — expected \"{ExpectedString}\", got \"{result.StringValue ?? "(null)"}\".");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 37 PASSED — cross-module member resolution via ICorDebugType.GetBase: ex.{Member} (declared on System.Exception in CoreLib) func-eval'd from a ProbeException in user code, returning the rendered string content.");
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
        string path = Path.Combine(dir, $"37-crossmod-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 37 fixture — cross-module member resolution (ICorDebugType.GetBase)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"plan             = func-eval ex.{Member} on a ProbeException : Exception (direct cross-module); expect StringValue == \"{ExpectedString}\"\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
