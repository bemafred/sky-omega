#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 07 — continue-loop LIVE smoke (ADR-006 Phase 2 increment 1)
// ===============================================================================
//
// What probe 05 left open: it attached cleanly (DebugActiveProcess/Detach/Terminate all
// S_OK) but observed ZERO callbacks, because its target was parked and modern CoreCLR replays
// nothing on attach. So Phase 1 never proved a callback STREAM flows — only that one could be
// received in-process (probe 06) and that attach/detach works (probe 05).
//
// This smoke closes that gap with the assembled engine (DebugSession + ManagedCallbackHost +
// CallbackPump), not a probe reimplementation. It launches 07-target.cs (a live event
// generator), attaches the real engine, and watches the recorded event count over a window.
// The continue-loop's whole job is to Continue after each synchronized stop so the next
// callback can fire; the discriminator is unambiguous:
//
//   0 events  -> no delivery at all (attach issue or target not generating)        exit 5
//   1 event   -> delivered once then wedged: Continue is NOT resuming the target   exit 6
//   >>1 events-> the loop drains a stream: Phase-1's single-event ceiling is gone  exit 0
//
// It then Disposes the session (clean detach — the worker is joined before Detach) and kills
// the target. Teardown also exercises the late-callback swallow path hardened this increment.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 07-continue-loop-smoke.cs <path-to-07-target.cs>
//   DBGSHIM_PATH is read by the engine's DbgShim resolver (it also checks the NuGet cache).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Observation;

return Smoke07.Run(args);

// Records the callback stream. OnEvent is invoked on the pump's WORKER thread, so the
// accumulators are concurrent; the harness thread reads them after the observation window.
sealed class Histogram : IDebugEventSink
{
    readonly ConcurrentDictionary<string, int> _counts = new();
    int _total;

    public int Total => Volatile.Read(ref _total);

    public void OnEvent(string name)
    {
        _counts.AddOrUpdate(name, 1, static (_, n) => n + 1);
        Interlocked.Increment(ref _total);
    }

    public string Render()
    {
        List<string> parts = new();
        foreach (KeyValuePair<string, int> kv in _counts) parts.Add($"{kv.Key}={kv.Value}");
        parts.Sort(StringComparer.Ordinal);
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }
}

static class Smoke07
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: dotnet 07-continue-loop-smoke.cs <path-to-07-target.cs>");
            return 2;
        }
        string targetCs = args[0];
        if (!File.Exists(targetCs))
        {
            Console.Error.WriteLine($"FALSIFIED (usage): target not found: {targetCs}");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"target     : {targetCs}");

        // ---- spawn the live event generator ----
        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{targetCs}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();

        // The managed pid is whatever the target prints, NOT proc.Id (dotnet may exec a child).
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

        // Drain stderr so a chatty build can't fill the pipe and block the child before READY.
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(90)))
        {
            Console.Error.WriteLine("FALSIFIED (target): no READY sentinel within 90s (CLR never came up).");
            KillTree(proc);
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");
        Console.WriteLine($"is-dotnet  : {ProcessInspector.IsDotnetProcess(realPid)}");

        // ---- attach the real engine ----
        Histogram sink = new();
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

        // ---- observe the stream: sample early then after the window ----
        Thread.Sleep(TimeSpan.FromSeconds(1));
        int afterFirst = sink.Total;
        Thread.Sleep(TimeSpan.FromSeconds(3));
        int afterWindow = sink.Total;

        // ---- verdict + evidence FIRST: the continue-loop result is what this probe tests, and
        // it is independent of teardown. Record it before touching detach (which is a separate,
        // known-fragile path against a flooding target — see teardown note below). ----
        string histogram = sink.Render();
        Console.WriteLine($"events @1s : {afterFirst}");
        Console.WriteLine($"events @4s : {afterWindow}");
        Console.WriteLine($"histogram  : {histogram}");

        int code = afterWindow == 0 ? 5 : afterWindow == 1 ? 6 : 0;
        WriteFixture(realPid, afterFirst, afterWindow, histogram);
        if (code == 5) Console.Error.WriteLine("Result: attached but ZERO callbacks — no delivery (attach issue or parked target).");
        else if (code == 6) Console.Error.WriteLine("Result: ONE callback then stuck — continue-loop is NOT resuming the target (Phase-1 wedge).");
        else Console.WriteLine($"PROBE 07 PASSED — continue-loop drained {afterWindow} callbacks from a live stream.");

        // ---- teardown: stop the flood, THEN detach. Clean detach against a flooding target is
        // a known limit (finding 14 / docs/limits/drhook-clean-detach.md): mscordbi's RC event
        // thread can segfault flushing queued events as Detach tears down its shim. Killing the
        // target first quiets the queue so the quiet-detach path (probe 05) applies; if a late
        // dispatch still faults, the verdict and fixture above already stand. ----
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300)); // let mscordbi observe debuggee death
        try
        {
            session.Dispose();
            Console.WriteLine("detached   : DebugSession disposed");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"teardown   : {ex.GetType().Name} during dispose (finding 14)");
        }
        return code;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int afterFirst, int afterWindow, string histogram)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"07-continue-loop-smoke-{rid}-{ts}.txt");
        string verdict = afterWindow == 0 ? "FALSIFIED-no-delivery"
                       : afterWindow == 1 ? "FALSIFIED-wedge"
                       : "PASSED";
        string body =
            "# DrHook.Engine probe 07 fixture — continue-loop live smoke (ADR-006 Phase 2)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"events-at-1s     = {afterFirst}\n" +
            $"events-at-4s     = {afterWindow}\n" +
            $"histogram        = {histogram}\n" +
            $"verdict          = {verdict}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
