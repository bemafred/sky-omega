#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 42 — DISPOSE DURING THE WORKER'S _resumeHandler call ===================
//
// ADR-007 Phase 1, Probe 42 (post-renumber). The CallbackPump worker drains _events and, for
// each event, invokes _resumeHandler which does Stepping.Arm(thread, kind) + controller.Continue.
// The race window characterised in finding 53 (MCH-1) + finding 54 (T1/T2 walks): another thread
// calls DebugSession.Dispose while the worker is INSIDE _resumeHandler's COM call to
// controller.Continue. The previous substrate work resolved the queued-callback-flush race
// (drhook-clean-detach, probe 08 — Quiesce-before-Detach). Phase 1's engineering fixes added
// atomic idempotence gates (ENG-CP-1, ENG-DS-1) and the EngineAnomaly surface (EA-1..6).
//
// This probe characterises the race-under-load behavior of the post-fix substrate. The target is
// the continuous-event-flood program from probe 07: each iteration creates a Thread + throws-and-
// catches, generating mscordbi CreateThread/ExitThread/Exception callbacks. The worker is
// effectively ALWAYS inside _resumeHandler (or just about to enter it) when Dispose races.
//
// N attach/Dispose cycles against ONE long-lived flood target. Across cycles:
//   - 0 crashes (target process or test process) — substrate handles the race
//   - 0 WorkerException anomalies (would indicate Stepping.Arm or controller.Continue threw
//     through the pump boundary — a substrate bug)
//   - Optional LateCallback / WorkerSilentBreak anomalies are EXPECTED under flooding (they're
//     evidence of the substrate catching the race, not crashing it)
//   - Target process must survive every cycle (detach must leave it running)
//
// Falsification: 2 usage; 3 no READY; 4 first Attach failed; 5 no flood detected;
//   6 target process died mid-loop (Dispose killed target — substrate bug);
//   7 WorkerException anomaly observed (Stepping/Continue threw through pump);
//   8 Dispose threw outside the expected path; 9 attach failed mid-loop (target unstable);
//   0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 42-dispose-resumehandler-race-smoke.cs <path-to-07-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Observation;

return DisposeRaceP42.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 4096);
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public BoundedAnomalySink Anomalies => _anomalies;

    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class DisposeRaceP42
{
    const int Cycles = 20;
    const int FloodWindowMs = 200; // give the worker time to enter _resumeHandler under flood

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 42-dispose-resumehandler-race-smoke.cs <path-to-07-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : {Cycles} attach/Dispose cycles against continuous-flood target; expect 0 crashes, 0 WorkerException anomalies, target alive after each cycle");

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

        // Sentinel attach + drain to confirm flood is live before starting the cycle loop.
        var floodSink = new CountingAnomalySink();
        DebugSession floodSession;
        try { floodSession = DebugSession.Attach(realPid, floodSink); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (sentinel Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Thread.Sleep(TimeSpan.FromSeconds(1));
        int sentinelEvents = floodSink.EventCount;
        floodSession.Dispose();
        Console.WriteLine($"flood @1s  : {sentinelEvents} events (sentinel run before cycle loop)");
        if (sentinelEvents <= 1)
        {
            Console.Error.WriteLine("FALSIFIED (no flood): target isn't generating a callback stream; the race window can't be tested.");
            KillTree(proc);
            return 5;
        }

        // ── Cycle loop ───────────────────────────────────────────────────────────────────────
        var cycleSink = new CountingAnomalySink();
        int cleanCount = 0;
        int disposeExceptionCount = 0;
        int attachFailedCount = 0;
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < Cycles; i++)
        {
            if (proc.HasExited)
            {
                Console.Error.WriteLine($"FALSIFIED (target died): process exited before cycle {i + 1} began.");
                return 6;
            }

            DebugSession session;
            try { session = DebugSession.Attach(realPid, cycleSink); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Attach cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                attachFailedCount++;
                return 9;
            }

            Thread.Sleep(FloodWindowMs); // worker is now inside _resumeHandler

            try
            {
                session.Dispose();
                cleanCount++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warn (cycle {i + 1}): Dispose threw {ex.GetType().Name}: {ex.Message}");
                disposeExceptionCount++;
            }

            // Brief pause between cycles so mscordbi state settles.
            Thread.Sleep(50);
        }

        sw.Stop();
        bool alive = !proc.HasExited && ProcessInspector.IsDotnetProcess(realPid);
        Console.WriteLine($"cycles     : {cleanCount}/{Cycles} clean Dispose; {disposeExceptionCount} threw; elapsed {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"target     : {(alive ? "alive (resumed un-debugged)" : "GONE")}");

        if (!alive)
        {
            Console.Error.WriteLine("FALSIFIED (target died): Dispose path killed the target across the cycle loop.");
            return 6;
        }

        // ── Anomaly surface ──────────────────────────────────────────────────────────────────
        AnomalyDrainResult drain = cycleSink.Anomalies.Drain();
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced ({drain.Dropped} dropped to capacity)");

        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        int workerExceptions = byKind.GetValueOrDefault(AnomalyKind.WorkerException, 0);
        if (workerExceptions > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (WorkerException): pump worker hit {workerExceptions} unhandled exceptions across the loop — substrate bug in _resumeHandler (Stepping.Arm or controller.Continue threw across the boundary).");
            foreach (EngineAnomaly we in drain.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException))
                Console.Error.WriteLine($"  {we.Observed} (Context: {(we.Context is null ? "(none)" : string.Join(", ", we.Context.Select(kv => kv.Key + "=" + kv.Value)))})");
            return 7;
        }

        if (disposeExceptionCount > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (Dispose threw {disposeExceptionCount}x): Dispose path raised exceptions — investigate stack traces above.");
            return 8;
        }

        WriteFixture(realPid, sentinelEvents, Cycles, cleanCount, alive, sw.ElapsedMilliseconds, drain.Anomalies.Count, byKind);
        KillTree(proc);

        Console.WriteLine($"\nPROBE 42 PASSED — {cleanCount}/{Cycles} clean Dispose cycles under continuous flood; substrate's Quiesce + Interlocked gates (ENG-CP-1/DS-1) + EngineAnomaly path handle Dispose-during-_resumeHandler without crashes, without WorkerException, and without killing the target. Surfaced anomalies are evidence of the substrate catching late callbacks / silent breaks — not failures.");
        return 0;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int sentinelEvents, int cycles, int clean, bool alive, long elapsedMs, int anomalyCount, IReadOnlyDictionary<AnomalyKind, int> byKind)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"42-dispose-resumehandler-race-{rid}-{ts}.txt");
        string anomalies = byKind.Count == 0
            ? "  (none surfaced)"
            : string.Join("\n", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key,-32} : {kv.Value}"));
        string body =
            "# DrHook.Engine probe 42 fixture — Dispose during _resumeHandler race characterisation\n" +
            $"timestamp            = {DateTime.UtcNow:O}\n" +
            $"runtime              = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch              = {rid}\n" +
            $"target-pid           = {pid}\n" +
            $"sentinel-events-1s   = {sentinelEvents}\n" +
            $"cycles               = {cycles}\n" +
            $"clean-dispose-count  = {clean}\n" +
            $"target-alive-after   = {alive}\n" +
            $"elapsed-ms           = {elapsedMs}\n" +
            $"anomaly-count        = {anomalyCount}\n" +
            $"anomalies-by-kind    =\n{anomalies}\n" +
            $"verdict              = PASSED\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
