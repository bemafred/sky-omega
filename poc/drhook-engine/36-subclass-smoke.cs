#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 36 — SUBCLASS-WALK in member resolution (within-module extends chain) ====
//
// Object inspection slice 2. MetadataResolver.FindMethodInType now walks the extends chain of the
// runtime type so a member declared on a base class is findable. Within-module only this slice
// (extends == mdTypeDef, high byte 0x02); cross-module (mdTypeRef → CoreLib) is the next slice.
//
// Target: ProbeException : TitledException : Exception, with Title declared on TitledException.
// Phase A: func-eval ex.Title — must succeed (walks ProbeException → TitledException → finds
//   get_Title) and return the string "hello title" via the probe-35 reference-string path.
// Phase B: func-eval ex.Message — must STILL FAIL (Message is declared on System.Exception in
//   CoreLib, cross-module not yet supported). Confirms the scope boundary so the walk doesn't
//   accidentally reach into CoreLib via some other path.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 ArmExceptionFilter;
//   7 no exception stop; 8 phase A eval not Completed or wrong StringValue;
//   9 phase B unexpectedly Completed (cross-module shouldn't resolve in this slice); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 36-subclass-smoke.cs <path-to-36-subclass-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Subclass36.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Subclass36
{
    const string ExpectedType   = "ProbeException";
    const string InheritedMember = "Title";    // declared on TitledException (target module)
    const string CrossModuleMember = "Message"; // declared on System.Exception (CoreLib)
    const string ExpectedString = "hello title";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 36-subclass-smoke.cs <path-to-36-subclass-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : at a ProbeException stop, eval ex.{InheritedMember} (within-module subclass-walk, expect \"{ExpectedString}\") and ex.{CrossModuleMember} (cross-module, expect SetupFailed).");

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

        // --- Phase A: inherited Title (declared on TitledException — same module) -----------------
        EvalStatus statusA = session.TryEvalCurrentExceptionMember(InheritedMember, TimeSpan.FromSeconds(10), out ArgumentValue resultA);
        Console.WriteLine($"phase A    : eval ex.{InheritedMember} -> {statusA}  StringValue=\"{resultA.StringValue ?? "(null)"}\"");
        if (statusA != EvalStatus.Completed || resultA.StringValue != ExpectedString)
        {
            Console.Error.WriteLine($"FALSIFIED (phase A): expected Completed with StringValue=\"{ExpectedString}\", got {statusA} / \"{resultA.StringValue ?? "(null)"}\".");
            return 8;
        }

        // --- Phase B: cross-module Message (declared on System.Exception in CoreLib) --------------
        EvalStatus statusB = session.TryEvalCurrentExceptionMember(CrossModuleMember, TimeSpan.FromSeconds(10), out ArgumentValue _);
        Console.WriteLine($"phase B    : eval ex.{CrossModuleMember} -> {statusB} (expect SetupFailed; cross-module is next slice)");
        if (statusB == EvalStatus.Completed)
        {
            Console.Error.WriteLine($"FALSIFIED (phase B): cross-module ex.{CrossModuleMember} unexpectedly resolved — the within-module walk should not reach CoreLib.");
            return 9;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 36 PASSED — subclass-walk in member resolution: ex.{InheritedMember} resolves through ProbeException → TitledException (within-module extends chain); ex.{CrossModuleMember} correctly stays unresolved (cross-module is the next slice).");
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
        string path = Path.Combine(dir, $"36-subclass-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 36 fixture — subclass-walk in member resolution (within-module)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"phases           = A within-module inherited member resolves; B cross-module member correctly does not\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
