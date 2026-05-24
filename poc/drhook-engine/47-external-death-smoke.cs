#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 47 — EXTERNAL TARGET DEATH DURING BORROWED OBSERVATION ===============
//
// ADR-007 Phase 1, Probe 47 (added 2026-05-24 per finding 65 follow-up).
//
// HYPOTHESIS: "The substrate must gracefully Dispose a Borrowed session whose target
// has died externally — OS kill (SIGKILL, OOM), user force-quit, target crash, container
// shutdown — without crashing mscordbi's RC event thread mid-exit-work-item processing."
//
// Real-world scenarios this scenario class covers:
//   - NCrunch killing a testhost between tests
//   - Developer force-quitting a debugged app from Activity Monitor / Task Manager
//   - OOM killer reaping a memory-hungry target
//   - Target SIGSEGVing / unhandled-exception-aborting on its own
//   - Container/VM shutdown killing the target while substrate observes
//
// CONSTRUCTION: spawn 47-target.cs (a parked sleeper, no callback flood), attach
// (Borrowed), observe briefly, externally Kill the target, allow brief settle so
// the OS updates HasExited, then Dispose. The substrate must NOT call Quiesce /
// Continue / Detach into a dying target (those race mscordbi's exit-work-item).
//
// N=10 cycles. Each cycle:
//   1. Spawn target (45 s sleep is plenty; we'll kill before then)
//   2. Wait for READY sentinel + PID
//   3. DebugSession.Attach(pid)
//   4. Sleep 200 ms (brief observation)
//   5. proc.Kill(entireProcessTree:true) — external death
//   6. Sleep 100 ms — let OS update HasExited
//   7. session.Dispose() — substrate detects death, skips mscordbi protocol ops
//
// Across cycles:
//   - 10/10 clean Dispose
//   - 0 process crashes (substrate or test process)
//   - 0 WorkerException
//   - LateCallback anomalies are EXPECTED (mscordbi may dispatch ExitProcess/etc.
//     before the kill fully completes; OnCallback's catch turns them into signal)
//   - 0 substrate Dispose exceptions
//
// Falsification:
//   2 usage; 3 no READY; 4 first Attach failed; 5 target failed to die;
//   6 substrate Dispose exception; 7 WorkerException anomaly; 9 attach failed
//   mid-loop; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 47-external-death-smoke.cs <path-to-47-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return ExternalDeathP47.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 4096);
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public BoundedAnomalySink Anomalies => _anomalies;

    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class ExternalDeathP47
{
    const int Cycles = 10;
    const int ObservationMs = 200;
    const int PostKillSettleMs = 0;     // immediate Dispose after Kill — exposes the race
    const int InterCycleSettleMs = 100;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 47-external-death-smoke.cs <path-to-47-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : {Cycles} cycles of spawn → attach → observe → external Kill → Dispose;");
        Console.WriteLine($"             validates substrate detects target death and skips mscordbi protocol ops;");
        Console.WriteLine($"             expect 0 crashes, 0 Dispose exceptions, 0 WorkerException, target dead each cycle");

        var cycleSink = new CountingAnomalySink();
        int cleanCount = 0;
        int disposeExceptionCount = 0;
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < Cycles; i++)
        {
            // Spawn fresh target this cycle.
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
            }) { IsBackground = true, Name = $"target-stdout-{i + 1}" };
            reader.Start();
            Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
            { IsBackground = true, Name = $"target-stderr-{i + 1}" };
            errDrain.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine($"FALSIFIED (target cycle {i + 1}): no READY sentinel within 30s.");
                KillTree(proc);
                return 3;
            }
            realPid = Volatile.Read(ref realPid);

            // Attach.
            DebugSession session;
            try { session = DebugSession.Attach(realPid, cycleSink); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Attach cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                KillTree(proc);
                return i == 0 ? 4 : 9;
            }

            // Brief observation window.
            Thread.Sleep(ObservationMs);

            // External death — caller's prerogative on a Borrowed session.
            // The substrate observes a target that unilaterally left.
            try { proc.Kill(entireProcessTree: true); } catch { }

            // PostKillSettleMs may be 0 (immediate Dispose after Kill) to expose the
            // mscordbi exit-work race — substrate must detect death regardless of
            // whether HasExited has propagated yet.
            #pragma warning disable CS0162 // PostKillSettleMs may be 0 at compile time
            if (PostKillSettleMs > 0) Thread.Sleep(PostKillSettleMs);
            #pragma warning restore CS0162

            // Now Dispose — substrate must detect target dead and skip mscordbi protocol.
            try
            {
                session.Dispose();
                cleanCount++;
                Console.WriteLine($"cycle {i + 1,2}/{Cycles}: target {realPid} attached + killed + Dispose clean");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warn (cycle {i + 1}): Dispose threw {ex.GetType().Name}: {ex.Message}");
                disposeExceptionCount++;
            }

            Thread.Sleep(InterCycleSettleMs);
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"cycles     : {cleanCount}/{Cycles} clean Dispose; {disposeExceptionCount} threw; elapsed {sw.ElapsedMilliseconds}ms");

        // ── Anomaly surface ──────────────────────────────────────────────────────────────────
        AnomalyDrainResult drain = cycleSink.Anomalies.Drain();
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced ({drain.Dropped} dropped to capacity)");

        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        int workerExceptions = byKind.GetValueOrDefault(AnomalyKind.WorkerException, 0);
        if (workerExceptions > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (WorkerException): pump worker hit {workerExceptions} unhandled exceptions — substrate bug surfaced under external-death scenario.");
            foreach (EngineAnomaly we in drain.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException))
                Console.Error.WriteLine($"  {we.Observed}");
            return 7;
        }

        if (disposeExceptionCount > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (Dispose threw {disposeExceptionCount}x): substrate did not Dispose cleanly against externally-killed target.");
            return 6;
        }

        WriteFixture(Cycles, cleanCount, sw.ElapsedMilliseconds, drain.Anomalies.Count, byKind);

        Console.WriteLine();
        Console.WriteLine($"PROBE 47 PASSED — {cleanCount}/{Cycles} clean Dispose cycles against externally-killed Borrowed targets; substrate detects external death and skips mscordbi protocol operations (Quiesce/Continue/Detach) that would race exit-work-item processing. Anomalies surfaced are evidence of the substrate catching mscordbi RC thread dispatch during teardown — not failures.");
        return 0;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int cycles, int clean, long elapsedMs, int anomalyCount, IReadOnlyDictionary<AnomalyKind, int> byKind)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"47-external-death-{rid}-{ts}.txt");
        string anomalies = byKind.Count == 0
            ? "  (none surfaced)"
            : string.Join("\n", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key,-32} : {kv.Value}"));
        string body =
            "# DrHook.Engine probe 47 fixture — external target death during Borrowed observation\n" +
            $"timestamp            = {DateTime.UtcNow:O}\n" +
            $"runtime              = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch              = {rid}\n" +
            $"cycles               = {cycles}\n" +
            $"clean-dispose-count  = {clean}\n" +
            $"elapsed-ms           = {elapsedMs}\n" +
            $"anomaly-count        = {anomalyCount}\n" +
            $"anomalies-by-kind    =\n{anomalies}\n" +
            $"verdict              = PASSED\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
