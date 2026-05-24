#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 42 — DISPOSE DURING THE WORKER'S _resumeHandler call ===================
//
// ADR-007 Phase 1, Probe 42 (replacement design 2026-05-24).
//
// HYPOTHESIS: "Dispose during the worker's _resumeHandler(...) call."
//
// The CallbackPump worker (CallbackPump.cs:148–215) processes events. For each
// CallbackKind.Informational event it calls _resumeHandler!(Continue, 0), which performs
// a synchronous controller.Continue(0) COM call into mscordbi. The substrate race
// characterised in finding 53 MCH-1 + finding 54 T1/T2: the worker is mid-Continue
// when another thread calls DebugSession.Dispose. Phase 1's engineering work (Quiesce-
// before-Detach, ENG-CP-1/DS-1 Interlocked gates, EngineAnomaly substrate) is supposed
// to make this race teardown-safe.
//
// CONSTRUCTION: spawn 42-target.cs (Thread.Start/Join in a tight loop, no exceptions).
// Only Informational callbacks are generated, so the worker is ALWAYS either consuming
// the next event from _events or inside _resumeHandler's controller.Continue. It never
// parks at _resume.Take (no STOPPING events exist on this target). Randomized Dispose
// timing in [20, 500] ms samples the race window across the controller.Continue duration
// distribution.
//
// N=50 attach/Dispose cycles against ONE long-lived informational-flood target.
// Across cycles:
//   - 0 process crashes (substrate or target)
//   - 0 WorkerException anomalies (would mean controller.Continue threw through pump)
//   - 0 WorkerSilentBreak anomalies (would mean a stop fired against a no-stops target —
//     substrate bug)
//   - LateCallback anomalies under flood are EXPECTED (callbacks dispatched after
//     _events.CompleteAdding hit the LateCallback path — that IS the substrate catching
//     the race correctly)
//   - Target process must survive every cycle (detach must leave it running)
//
// Falsification:
//   2 usage; 3 no READY; 4 sentinel Attach failed; 5 flood not flowing (<24 events in 1s);
//   6 target died mid-loop; 7 WorkerException anomaly; 8 WorkerSilentBreak anomaly
//     (stop on a no-stops target — substrate semantics violated); 9 attach failed mid-loop;
//     10 Dispose threw; 0 PASS.
//
// Replaces the original probe 42 (which constructed a STOPPING-Exception-unconsumed
// scenario against 07-target.cs that parked the worker at _resume.Take — testing a
// different race than the hypothesis names). Original probe + finding 57 retired
// concurrently.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 42-dispose-resumehandler-race-smoke.cs <path-to-42-target.cs>

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
    const int Cycles = 50;
    const int MinDisposeDelayMs = 20;
    const int MaxDisposeDelayMs = 500;
    const int InterCycleSettleMs = 50;
    const int MinSentinelEvents = 10; // 1s of CreateThread+ExitThread @ ~2/iter should yield well above this

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 42-dispose-resumehandler-race-smoke.cs <path-to-42-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : {Cycles} attach/Dispose cycles against informational-only flood (Thread.Start/Join, no exceptions);");
        Console.WriteLine($"             randomized Dispose delay in [{MinDisposeDelayMs}, {MaxDisposeDelayMs}] ms;");
        Console.WriteLine($"             expect 0 crashes, 0 WorkerException, 0 WorkerSilentBreak, target alive each cycle");

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

        // Sentinel attach + drain to confirm informational flood is live before starting the cycle loop.
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
        if (sentinelEvents < MinSentinelEvents)
        {
            Console.Error.WriteLine($"FALSIFIED (no flood): target isn't generating ≥{MinSentinelEvents} callbacks/sec; the race window can't be tested.");
            KillTree(proc);
            return 5;
        }

        // ── Cycle loop ───────────────────────────────────────────────────────────────────────
        var cycleSink = new CountingAnomalySink();
        int cleanCount = 0;
        int disposeExceptionCount = 0;
        var rng = new Random(42); // deterministic seed for repeatable timing distribution
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
                return 9;
            }

            int delayMs = rng.Next(MinDisposeDelayMs, MaxDisposeDelayMs + 1);
            Thread.Sleep(delayMs);

            try
            {
                session.Dispose();
                cleanCount++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warn (cycle {i + 1}, delay {delayMs}ms): Dispose threw {ex.GetType().Name}: {ex.Message}");
                disposeExceptionCount++;
            }

            Thread.Sleep(InterCycleSettleMs);
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
            Console.Error.WriteLine($"FALSIFIED (WorkerException): pump worker hit {workerExceptions} unhandled exceptions across the loop — substrate bug in _resumeHandler (controller.Continue threw through the pump boundary).");
            foreach (EngineAnomaly we in drain.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException))
                Console.Error.WriteLine($"  {we.Observed} (Context: {(we.Context is null ? "(none)" : string.Join(", ", we.Context.Select(kv => kv.Key + "=" + kv.Value)))})");
            return 7;
        }

        int workerSilentBreaks = byKind.GetValueOrDefault(AnomalyKind.WorkerSilentBreak, 0);
        if (workerSilentBreaks > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (WorkerSilentBreak): {workerSilentBreaks} stops surfaced against a no-stops target — substrate produced a STOPPING event from CreateThread/ExitThread, which violates the pump's classification contract.");
            foreach (EngineAnomaly wsb in drain.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerSilentBreak))
                Console.Error.WriteLine($"  {wsb.Observed} (Context: {(wsb.Context is null ? "(none)" : string.Join(", ", wsb.Context.Select(kv => kv.Key + "=" + kv.Value)))})");
            return 8;
        }

        if (disposeExceptionCount > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (Dispose threw {disposeExceptionCount}x): Dispose path raised exceptions — investigate stack traces above.");
            return 10;
        }

        WriteFixture(realPid, sentinelEvents, Cycles, cleanCount, alive, sw.ElapsedMilliseconds, drain.Anomalies.Count, byKind);
        KillTree(proc);

        Console.WriteLine($"\nPROBE 42 PASSED — {cleanCount}/{Cycles} clean Dispose cycles under continuous informational flood; substrate's Quiesce + Interlocked gates (ENG-CP-1/DS-1) + EngineAnomaly path handle Dispose-during-_resumeHandler without crashes, without WorkerException, without WorkerSilentBreak, and without killing the target. LateCallback anomalies (if any) are evidence of the substrate catching post-CompleteAdding callbacks correctly.");
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
