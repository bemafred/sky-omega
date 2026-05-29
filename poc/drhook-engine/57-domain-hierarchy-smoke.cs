#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 57 — TARGET-DEFINED EXCEPTION HIERARCHY (subclass walk across the target's own module)
// ===========================================================================================================
//
// Substrate claim being validated: ExceptionInspector.CurrentExceptionTypeChain walks via
// ICorDebugType.GetBase, so the chain spans modules — including the target's own module where
// the application defines its own exception types. Probe 38 / finding 47 validated subclass-aware
// filtering for BCL types. This probe validates the same mechanism against TARGET-DEFINED types.
//
// Construction: the target throws MyApp.OrderValidationException, derived from MyApp.DomainException,
// which is itself derived from System.Exception — three frames of inheritance, two of them in the
// target's own module. The probe arms an exception filter on the BASE (MyApp.DomainException) and
// asserts the substrate's filter loop in WaitForStop admits the throw, surfaces an Exception stop,
// and reports the runtime type as the DERIVED MyApp.OrderValidationException. The match could only
// have happened via a subclass-chain walk that crossed into the target's own module to read the
// base-type relationship — there's no BCL path that could match the derived type against the base
// filter without that walk.
//
// Out of MCP scope: this probe runs against the substrate directly (DebugSession.ArmExceptionFilter
// + plain WaitForStop). The MCP-layer surface that exposes target-defined-hierarchy filtering to
// agents is ADR-010 Increment 6 §1; the probe is the substrate-correctness guard for it.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 ArmExceptionFilter returned 0;
//   7 stop was wrong reason / never came / wrong runtime type; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 57-domain-hierarchy-smoke.cs <path-to-57-domain-hierarchy-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return DomainHierarchy57.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class DomainHierarchy57
{
    const string BaseType    = "MyApp.DomainException";
    const string DerivedType = "MyApp.OrderValidationException";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 57-domain-hierarchy-smoke.cs <path-to-57-domain-hierarchy-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : filter on BASE \"{BaseType}\" (target-defined); target throws DERIVED \"{DerivedType}\"; substrate's subclass walk must match across target's own module");

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

        // Filter on the BASE type — MyApp.DomainException — defined in the target's own module.
        // Substrate's MatchesChain must walk ICorDebugType.GetBase from the runtime type
        // (MyApp.OrderValidationException) and admit the base match without any BCL participation.
        int filterId = session.ArmExceptionFilter(BaseType, ExceptionStopKind.None);
        if (filterId == 0) { Console.Error.WriteLine($"FALSIFIED (ArmExceptionFilter): {BaseType}."); return 6; }
        Console.WriteLine($"filter     : armed on BASE \"{BaseType}\" (id={filterId}); resuming until a chain-match surfaces …");

        session.Resume();

        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(20));
        if (stop is null || stop.Reason != StopReason.Exception)
        {
            Console.Error.WriteLine($"FALSIFIED: expected Exception stop within 20s; got {(stop is null ? "null" : stop.Reason.ToString())}.");
            return 7;
        }

        string? runtimeType = session.GetCurrentExceptionTypeName();
        Console.WriteLine($"stopped    : Exception ({stop.ExceptionKind})  runtime-type=\"{runtimeType ?? "(n/a)"}\"");
        if (runtimeType != DerivedType)
        {
            Console.Error.WriteLine($"FALSIFIED: expected runtime type \"{DerivedType}\" (target throws the derived); got \"{runtimeType ?? "(n/a)"}\". " +
                "The filter matching the BASE while the runtime type is the DERIVED is the whole point — without that gap the probe proves nothing.");
            return 7;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 57 PASSED — subclass-chain walk admitted a target-defined hierarchy: filter on \"{BaseType}\" matched a runtime throw of \"{DerivedType}\", both defined in the target's own module (no BCL participation in the match).");
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
        string path = Path.Combine(dir, $"57-domain-hierarchy-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 57 fixture — target-defined exception hierarchy (subclass walk across target's own module)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"filter-base-type = {BaseType}\n" +
            $"target-derived   = {DerivedType}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
