#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 43 — CONCURRENT PauseRequest + STOPPING callback =====================
//
// ADR-007 Phase 1, Probe 43 (post-renumber). The CallbackPump's _events queue is the
// rendezvous between mscordbi's event thread (writes STOPPING + Informational callbacks) and
// the MCP request thread (writes synthetic PauseRequest via RequestPause). The pump worker
// drains FIFO, single-consumer — so by construction, all events serialise. This probe
// validates that contract empirically under load: concurrent Adds from mscordbi (continuous
// Exception stream from 07-target) + periodic Pause requests must result in every requested
// Pause surfacing as exactly one Pause stop within bounded time.
//
// Per-iteration shape:
//   1. session.Pause() — enqueue PauseRequest on _events (one Add from MCP thread)
//   2. Drain via WaitForStop + Resume loop until we see the Pause stop. Each Exception stop
//      consumed along the way is evidence of the mscordbi flood serializing alongside Pause.
//   3. Assert: Pause stop arrived; the count of Exception stops between request and arrival
//      is the backlog observation (rate evidence, not pass/fail).
//
// Falsification: 2 usage; 3 no READY; 4 Attach failed; 5 no flood; 6 Pause request lost
//   (no Pause stop within timeout — substrate failed to enqueue or process); 7 WorkerException
//   anomaly (substrate bug); 8 process crashed mid-run; 9 target killed; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 43-pause-stopping-race-smoke.cs <path-to-07-target.cs>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Observation;

return PauseStoppingP43.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 4096);
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public BoundedAnomalySink Anomalies => _anomalies;

    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class PauseStoppingP43
{
    const int PauseCycles = 10;
    const int InterPauseDelayMs = 150;       // give the pump room to publish Pause + drain pre-existing backlog
    const int PauseTimeoutSeconds = 5;       // max wall-clock per Pause cycle
    const int DrainPerStopTimeoutMs = 1000;  // per-WaitForStop budget

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 43-pause-stopping-race-smoke.cs <path-to-07-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : {PauseCycles} Pause cycles against continuous-flood target; each must surface as exactly one Pause stop within {PauseTimeoutSeconds}s");

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

        var sink = new CountingAnomalySink();
        DebugSession session;
        try { session = DebugSession.Attach(realPid, sink); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        int code;
        try { code = Drive(session, sink, proc); }
        finally
        {
            try { session.Dispose(); } catch { /* best effort */ }
            KillTree(proc);
        }

        return code;
    }

    static int Drive(DebugSession session, CountingAnomalySink sink, Process proc)
    {
        // Sentinel: wait briefly for the flood to be live, then start the Pause loop.
        Thread.Sleep(TimeSpan.FromMilliseconds(500));
        int sentinelEvents = sink.EventCount;
        Console.WriteLine($"flood @0.5s: {sentinelEvents} events (sentinel before Pause loop)");
        if (sentinelEvents <= 1)
        {
            Console.Error.WriteLine("FALSIFIED (no flood): target isn't generating a callback stream; can't test concurrent Pause+STOPPING race.");
            return 5;
        }

        var perCycle = new List<(int request, int exceptionBacklog, int pauseStopAt, long elapsedMs)>(PauseCycles);
        int totalExceptionStops = 0;
        int totalPauseStops = 0;
        int totalOtherStops = 0;
        Stopwatch overall = Stopwatch.StartNew();

        for (int i = 0; i < PauseCycles; i++)
        {
            if (proc.HasExited)
            {
                Console.Error.WriteLine($"FALSIFIED (target died): process exited before Pause cycle {i + 1}.");
                return 9;
            }

            Stopwatch cycle = Stopwatch.StartNew();
            session.Pause();

            int exceptionBacklog = 0;
            int pauseStopAtIndex = -1;
            int stopIndex = 0;
            bool sawPause = false;

            while (cycle.Elapsed < TimeSpan.FromSeconds(PauseTimeoutSeconds))
            {
                StopInfo? stop = session.WaitForStop(TimeSpan.FromMilliseconds(DrainPerStopTimeoutMs));
                if (stop is null) continue;

                stopIndex++;
                switch (stop.Reason)
                {
                    case StopReason.Pause:
                        totalPauseStops++;
                        pauseStopAtIndex = stopIndex;
                        sawPause = true;
                        session.Resume();
                        break;
                    case StopReason.Exception:
                        exceptionBacklog++;
                        totalExceptionStops++;
                        session.Resume();
                        break;
                    default:
                        totalOtherStops++;
                        session.Resume();
                        break;
                }

                if (sawPause) break;
            }

            cycle.Stop();
            perCycle.Add((i + 1, exceptionBacklog, pauseStopAtIndex, cycle.ElapsedMilliseconds));
            Console.WriteLine($"cycle {i + 1,2}/{PauseCycles} : pause-stop-at-stop-#{pauseStopAtIndex,2}  exception-backlog={exceptionBacklog,3}  elapsed={cycle.ElapsedMilliseconds,5}ms");

            if (!sawPause)
            {
                Console.Error.WriteLine($"FALSIFIED (Pause lost cycle {i + 1}): no Pause stop within {PauseTimeoutSeconds}s.");
                return 6;
            }

            Thread.Sleep(InterPauseDelayMs);
        }
        overall.Stop();

        // ── Anomaly surface ──────────────────────────────────────────────────────────────────
        AnomalyDrainResult drain = sink.Anomalies.Drain();
        Console.WriteLine($"\noverall    : {totalPauseStops} Pause stops + {totalExceptionStops} Exception stops + {totalOtherStops} other; elapsed {overall.ElapsedMilliseconds}ms");
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced ({drain.Dropped} dropped to capacity)");
        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        int workerExceptions = byKind.GetValueOrDefault(AnomalyKind.WorkerException, 0);
        if (workerExceptions > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (WorkerException × {workerExceptions}): substrate bug — _resumeHandler or _pauseHandler threw through the pump boundary.");
            foreach (EngineAnomaly we in drain.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException))
                Console.Error.WriteLine($"  {we.Observed}");
            return 7;
        }

        if (totalPauseStops != PauseCycles)
        {
            Console.Error.WriteLine($"FALSIFIED (Pause count): expected exactly {PauseCycles} Pause stops, got {totalPauseStops}.");
            return 6;
        }

        if (proc.HasExited)
        {
            Console.Error.WriteLine("FALSIFIED (target died): target exited during the loop.");
            return 9;
        }

        int pid = 0;
        try { pid = proc.Id; } catch { /* exited */ }
        bool alive = pid != 0 && ProcessInspector.IsDotnetProcess(pid);
        Console.WriteLine($"target     : {(alive ? "alive (resumed un-debugged)" : "GONE")}");
        WriteFixture(pid, sentinelEvents, perCycle, totalExceptionStops, drain.Anomalies.Count, byKind, overall.ElapsedMilliseconds);

        Console.WriteLine($"\nPROBE 43 PASSED — {PauseCycles}/{PauseCycles} Pause requests surfaced as Pause stops under continuous mscordbi STOPPING flood; pump's single-consumer FIFO queue serialises concurrent _events.Add calls correctly. {totalExceptionStops} Exception stops consumed in the drain — evidence of the concurrent stream.");
        return 0;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int sentinelEvents,
        List<(int request, int exceptionBacklog, int pauseStopAt, long elapsedMs)> perCycle,
        int totalExceptionStops, int anomalyCount, IReadOnlyDictionary<AnomalyKind, int> byKind, long elapsedMs)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"43-pause-stopping-race-{rid}-{ts}.txt");
        string anomalies = byKind.Count == 0
            ? "  (none surfaced)"
            : string.Join("\n", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key,-32} : {kv.Value}"));
        string cycles = string.Join("\n", perCycle.Select(c =>
            $"  cycle={c.request,2}  exception-backlog={c.exceptionBacklog,3}  pause-stop-at-stop-#{c.pauseStopAt,2}  elapsed={c.elapsedMs,5}ms"));
        string body =
            "# DrHook.Engine probe 43 fixture — Concurrent PauseRequest + STOPPING callback serialisation\n" +
            $"timestamp            = {DateTime.UtcNow:O}\n" +
            $"runtime              = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch              = {rid}\n" +
            $"target-pid           = {pid}\n" +
            $"sentinel-events-0.5s = {sentinelEvents}\n" +
            $"pause-cycles         = {perCycle.Count}\n" +
            $"total-exception-stops = {totalExceptionStops}\n" +
            $"elapsed-ms           = {elapsedMs}\n" +
            $"anomaly-count        = {anomalyCount}\n" +
            $"anomalies-by-kind    =\n{anomalies}\n" +
            $"per-cycle            =\n{cycles}\n" +
            $"verdict              = PASSED\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
