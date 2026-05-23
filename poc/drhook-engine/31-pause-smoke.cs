#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 31 — AsyncBreak (DebugSession.Pause) =====================================
//
// The first Phase 3 substrate gap from ADR-006 Phase 3 / "what's left" assessment: interrupt a
// RUNNING debuggee on demand. Routed through the pump as a synthetic stopping event so the
// existing resume rendezvous handles it uniformly (the worker remains the sole caller of both
// controller.Stop and controller.Continue). Backs drhook_step_pause in the eventual MCP rewrite.
//
// The target runs a tight no-breakpoint loop. The probe attaches at the startup Break, resumes
// briefly, then calls Pause and expects a StopReason.Pause within budget. A second pause/resume
// cycle proves the rendezvous is repeatable, not one-shot.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 6 first Pause timed out;
//   7 first Pause surfaced with wrong reason; 8 resume didn't unblock; 9 second Pause failed; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 31-pause-smoke.cs <path-to-31-pause-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Pause31.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Pause31
{
    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 31-pause-smoke.cs <path-to-31-pause-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine("plan       : attach, resume, Pause -> expect StopReason.Pause; resume, Pause again; verify rendezvous repeats.");

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
        session.Resume();

        // Cycle 1 — run briefly, then async-break.
        Thread.Sleep(TimeSpan.FromMilliseconds(150));
        Console.WriteLine("cycle 1    : calling Pause on a running debuggee …");
        session.Pause();
        StopInfo? stop1 = session.WaitForStop(TimeSpan.FromSeconds(5));
        if (stop1 is null) { Console.Error.WriteLine("FALSIFIED: first Pause stop did not arrive within 5s."); return 6; }
        if (stop1.Reason != StopReason.Pause) { Console.Error.WriteLine($"FALSIFIED: first stop reason {stop1.Reason}, expected Pause."); return 7; }
        Console.WriteLine($"             -> stop={stop1.Reason}  (sole caller of controller.Stop is the pump worker, OK)");
        session.Resume();

        // Cycle 2 — prove the rendezvous repeats and the worker isn't stuck after the first pause.
        Thread.Sleep(TimeSpan.FromMilliseconds(150));
        Console.WriteLine("cycle 2    : second Pause to confirm the rendezvous repeats …");
        session.Pause();
        StopInfo? stop2 = session.WaitForStop(TimeSpan.FromSeconds(5));
        if (stop2 is null) { Console.Error.WriteLine("FALSIFIED: second Pause stop did not arrive — resume after first Pause did not unblock the worker."); return 8; }
        if (stop2.Reason != StopReason.Pause) { Console.Error.WriteLine($"FALSIFIED: second stop reason {stop2.Reason}, expected Pause."); return 9; }
        Console.WriteLine($"             -> stop={stop2.Reason}");
        session.Resume();

        Console.WriteLine("\nPROBE 31 PASSED — DebugSession.Pause interrupts the running debuggee; resume rendezvouses through the same _resume.Take used by callback-driven stops.");
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
        string path = Path.Combine(dir, $"31-pause-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 31 fixture — AsyncBreak / DebugSession.Pause\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"cycles           = two Pause/Resume cycles to prove the rendezvous repeats\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
