#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 10 — process→module navigation (ADR-006 Phase 2, breakpoint setting 4a)
// ============================================================================================
//
// Setting a breakpoint needs an ICorDebugFunction, reached via the live object graph:
// process → app domains → assemblies → modules → (metadata) → function. This probe validates
// the first leg — the raw-V-table walk down to modules (RuntimeNavigation) — by enumerating
// the target's loaded modules while it is stopped.
//
// It reuses the Debugger.Break() target (09-break-target.cs) to obtain a clean stop: ICorDebug
// inspection requires the process synchronized, and a Break stop gives that with the pump
// worker parked. At the stop we call DebugSession.EnumerateModules() and check the list.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no Break stop; 6 zero modules (navigation
//   slots wrong / walk failed); 7 modules listed but the always-loaded System.Private.CoreLib
//   is absent (partial or misaligned walk); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 10-module-navigation-smoke.cs <path-to-09-break-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Nav10.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Nav10
{
    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 10-module-navigation-smoke.cs <path-to-09-break-target.cs>");
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

        int code = 0;
        string[] modules = Array.Empty<string>();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (stop is null || stop.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no stop): got {(stop is null ? "timeout" : stop.Reason.ToString())}, expected Break.");
            code = 5;
        }
        else
        {
            Console.WriteLine($"stopped    : {stop.Reason} — enumerating modules (debuggee synchronized)");
            modules = session.EnumerateModules().ToArray();
            Console.WriteLine($"modules    : {modules.Length}");
            foreach (string m in modules) Console.WriteLine($"  - {m}");

            if (modules.Length == 0) { Console.Error.WriteLine("FALSIFIED: zero modules — navigation walk failed."); code = 6; }
            else if (!modules.Any(m => m.Contains("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)))
            { Console.Error.WriteLine("FALSIFIED: System.Private.CoreLib not found — walk is partial/misaligned."); code = 7; }

            session.Resume();
        }

        if (code == 0)
            Console.WriteLine($"\nPROBE 10 PASSED — walked process→app domains→assemblies→modules; {modules.Length} modules, CoreLib present.");

        WriteFixture(realPid, modules, code);
        session.Dispose();
        KillTree(proc);
        return code;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, string[] modules, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"10-module-navigation-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 10 fixture — process→module navigation (ADR-006 Phase 2, 4a)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"module-count     = {modules.Length}\n" +
            $"corelib-present  = {modules.Any(m => m.Contains("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n" +
            "modules:\n" + string.Concat(modules.Select(m => $"  {m}\n"));
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
