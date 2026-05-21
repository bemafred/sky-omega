#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 09 — stopping-event model (ADR-006 Phase 2 stepping keystone)
// =================================================================================
//
// Increment 1 proved the continue-loop auto-continues a callback STREAM (probe 07). But
// breakpoints and stepping need the opposite for SOME callbacks: a breakpoint hit / step
// complete / Debugger.Break must NOT auto-continue — the debuggee must stay synchronized
// (frozen) and the caller must control the resume. This probe validates that model end to end.
//
// The target (09-break-target.cs) calls Debugger.Break() in a loop — a stopping callback that
// needs no breakpoint-setting machinery (de-risked: it fires ICorDebugManagedCallback::Break).
// For each of N rounds the probe:
//   1. WaitForStop -> expects a Break stop (the engine surfaced it, did NOT auto-continue),
//   2. proves the debuggee is FROZEN: a second WaitForStop with a short timeout must return
//      nothing — if the target were auto-continued it would fire ~20 more Breaks in that window,
//   3. Resume -> the target advances to the next Break.
// N controlled stops, each proven frozen between resumes, is the stopping model working.
//
// Falsification (exit codes): 2 usage; 3 no READY; 4 attach; 5 first/Nth WaitForStop returned
//   no Break (model not surfacing stops); 6 a stop arrived while held without resume (not
//   actually frozen — auto-continue leaked); 7 unexpected ProcessExited; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 09-stopping-model-smoke.cs <path-to-09-break-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Stopping09.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { } // informational firehose ignored; this probe drives stops
}

static class Stopping09
{
    const int Rounds = 5;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 09-stopping-model-smoke.cs <path-to-09-break-target.cs>");
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
        try
        {
            session = DebugSession.Attach(realPid, new NullSink());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine($"attached   : DebugSession established; driving {Rounds} controlled stops\n");

        int code = 0;
        int completed = 0;
        for (int i = 1; i <= Rounds; i++)
        {
            StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
            if (stop is null) { Console.Error.WriteLine($"round {i}: no stop within 10s — engine did not surface a stop."); code = 5; break; }
            if (stop.Reason == StopReason.ProcessExited) { Console.Error.WriteLine($"round {i}: unexpected ProcessExited."); code = 7; break; }
            if (stop.Reason != StopReason.Break) { Console.Error.WriteLine($"round {i}: expected Break, got {stop.Reason}."); code = 5; break; }
            Console.WriteLine($"round {i}: STOP ({stop.Reason}) — auto-Continue suppressed, debuggee synchronized");

            // Prove FROZEN: while we hold the stop (no Resume), no further stop may arrive.
            StopInfo? leaked = session.WaitForStop(TimeSpan.FromMilliseconds(400));
            if (leaked is not null)
            {
                Console.Error.WriteLine($"round {i}: a second stop ({leaked.Reason}) arrived WITHOUT resume — debuggee not frozen (auto-continue leaked).");
                code = 6; break;
            }
            Console.WriteLine($"round {i}: confirmed frozen (0 stops in a 400 ms held window)");

            session.Resume(); // advance to the next Break
            completed = i;
        }

        if (code == 0)
            Console.WriteLine($"\nPROBE 09 PASSED — {completed} controlled stops; debuggee frozen between resumes, advancing only on Resume.");

        WriteFixture(realPid, completed, code);

        // Teardown: quiescent detach (increment 2), then kill the disposable target.
        session.Dispose();
        KillTree(proc);
        return code;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int completedRounds, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"09-stopping-model-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 09 fixture — stopping-event model (ADR-006 Phase 2 stepping keystone)\n" +
            $"timestamp          = {DateTime.UtcNow:O}\n" +
            $"runtime            = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch            = {rid}\n" +
            $"target-pid         = {pid}\n" +
            $"stops-controlled   = {completedRounds} / {Rounds}\n" +
            $"frozen-while-held  = {(code == 0 ? "true" : code == 6 ? "false" : "n/a")}\n" +
            $"verdict            = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
