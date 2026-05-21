#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 08 — quiescent detach UNDER FLOOD (ADR-006 Phase 2 increment 2)
// ====================================================================================
//
// Probe 07 validated the continue-loop but exposed a teardown segfault: Detach races
// mscordbi's RC event thread flushing queued callbacks (finding 14). Probe 07 worked around
// it by killing the target BEFORE detaching (draining the queue first). This probe removes
// that workaround: it attaches to the same flooding target and calls DebugSession.Dispose
// while the target is STILL flooding — the exact scenario that crashed (EXIT 139). It passes
// only if the engine's quiescent detach (Stop → Detach) makes that clean.
//
// Falsification:
//   segfault in Dispose (no "detached" line, EXIT 139)  -> quiescence insufficient   exit 139
//   attached but target not actually flooding (<=1 evt)  -> can't test the scenario   exit 5
//   target died on detach (detach must leave it running) -> detach killed the target  exit 6
//   clean Dispose under flood + target survived          -> quiescent detach works    exit 0
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 08-quiesce-detach-smoke.cs <path-to-07-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Observation;

return Quiesce08.Run(args);

sealed class Counter : IDebugEventSink
{
    int _total;
    public int Total => Volatile.Read(ref _total);
    public void OnEvent(string name) => Interlocked.Increment(ref _total);
}

static class Quiesce08
{
    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 08-quiesce-detach-smoke.cs <path-to-07-target.cs>");
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

        Counter sink = new();
        DebugSession session;
        try
        {
            session = DebugSession.Attach(realPid, sink);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        // Let the flood build a queued-callback backlog in mscordbi.
        Thread.Sleep(TimeSpan.FromSeconds(2));
        int flooded = sink.Total;
        Console.WriteLine($"events @2s : {flooded}");
        if (flooded <= 1)
        {
            Console.Error.WriteLine("FALSIFIED (no flood): target is not generating a callback stream; can't test detach-under-flood.");
            KillTree(proc);
            return 5;
        }

        // THE TEST: detach while the target is STILL flooding — no pre-kill. This is the exact
        // path that segfaulted in probe 07. A successful return past Dispose proves the
        // quiescent detach (Stop → Detach) does not race mscordbi's queued-callback flush.
        Console.WriteLine("disposing  : target still flooding, NO pre-kill (the probe-07 crash scenario)...");
        session.Dispose();
        Console.WriteLine("detached   : clean Dispose under flood (no segfault)");

        // Detach must leave the target RUNNING, not kill it.
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        bool alive = ProcessInspector.IsDotnetProcess(realPid);
        Console.WriteLine($"target post-detach : {(alive ? "alive (resumed un-debugged)" : "GONE")}");

        WriteFixture(realPid, flooded, alive);
        KillTree(proc);

        if (!alive)
        {
            Console.Error.WriteLine("Result: target died on detach — Detach must leave it running.");
            return 6;
        }
        Console.WriteLine("PROBE 08 PASSED — quiescent detach is clean under a continuous callback flood; target survived.");
        return 0;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int flooded, bool aliveAfterDetach)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"08-quiesce-detach-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 08 fixture — quiescent detach under flood (ADR-006 Phase 2 increment 2)\n" +
            $"timestamp            = {DateTime.UtcNow:O}\n" +
            $"runtime              = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch              = {rid}\n" +
            $"target-pid           = {pid}\n" +
            $"events-at-2s         = {flooded}\n" +
            $"clean-dispose        = true\n" +
            $"target-alive-detach  = {aliveAfterDetach}\n" +
            $"verdict              = {(aliveAfterDetach ? "PASSED" : "FALSIFIED-target-died")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
