#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 48 — MULTI-SESSION ACROSS DIFFERENT TARGETS IN SAME HOST (MCH-RE-3) ====
//
// ADR-007 Phase 1, Probe 48 (added 2026-05-25 by finding 67 — surfaced during Phase 8
// mass-promotion attempt).
//
// HYPOTHESIS: "The substrate must support N substrate sessions in the same host process,
// each against a different target. Per-session mscordbi state must release fully on Dispose
// so subsequent sessions don't accumulate state past mscordbi's tolerance."
//
// MCH-RE-3 is distinct from:
//   - MCH-RE-1 (finding 59): single-target re-attach saturates mscordbi at ~2 cycles.
//     Workaround: spawn fresh target per cycle. THIS PROBE TESTS the workaround.
//   - MCH-RE-2 (finding 63 → finding 64): dispose-then-kill ordering race in same-host
//     multi-session AttachAndOwn scenario. Closed structurally by substrate-owned
//     lifecycle. MCH-RE-3 is a related but distinct accumulation phenomenon that the
//     dispose-then-kill closure did NOT cover.
//
// CONSTRUCTION: cycle N times, each cycle spawns a fresh 48-target.cs, attaches, briefly
// observes, disposes (substrate kills via AttachAndOwn, exits cleanly per finding 66).
// Probe-host process accumulates per-cycle substrate state. If MCH-RE-3 exists, the probe
// crashes (SIGSEGV / SIGBUS) at some cycle N > some threshold.
//
// Observation evidence from 2026-05-25 Phase 8 attempt: integration-test MSTest exe with
// 6+ sequential substrate sessions (mix of AttachAndOwn + Borrowed Attach) SIGSEGVs around
// session 7. This probe characterises whether the same phenomenon reproduces in a
// file-based probe-host (substrate-level), or whether it's MSTest-specific.
//
// N=10 cycles. Each cycle:
//   1. Spawn fresh 48-target.cs
//   2. Wait for READY + extract PID
//   3. DebugSession.AttachAndOwn(pid, NullSink)
//   4. Sleep 200 ms (brief observation)
//   5. session.Dispose() — substrate kills + death-detects + tears down
//
// Falsification:
//   2 usage; 3 no READY in some cycle; 4 attach failed mid-loop;
//   5 SIGSEGV / SIGBUS at cycle N — MCH-RE-3 reproduced in probe;
//   6 Dispose threw mid-loop;
//   0 PASS (10/10 clean — MCH-RE-3 does NOT reproduce in file-based probe,
//   so the integration-test SIGSEGV is MSTest-specific, not substrate-level).
//
// Either outcome is informative:
//   PASS  → MCH-RE-3 is MSTest-host-specific; integration-test isolation strategy needed.
//   FAIL  → MCH-RE-3 is substrate-level; substrate fix needed before mass promotion.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 48-multi-target-session-smoke.cs <path-to-48-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return MultiSessionP48.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 4096);
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public BoundedAnomalySink Anomalies => _anomalies;

    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class MultiSessionP48
{
    const int Cycles = 10;
    const int ObservationMs = 200;
    const int InterCycleSettleMs = 100;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 48-multi-target-session-smoke.cs <path-to-48-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : {Cycles} cycles of spawn → AttachAndOwn → observe → Dispose against FRESH target per cycle;");
        Console.WriteLine($"             characterises MCH-RE-3 — multi-session-across-different-targets state accumulation in probe-host process;");
        Console.WriteLine($"             expect either: (PASS) substrate-level OK, MSTest issue is MSTest-specific;");
        Console.WriteLine($"                       OR:  (CRASH) substrate-level MCH-RE-3 reproduced");

        var cycleSink = new CountingAnomalySink();
        int cleanCount = 0;
        int disposeExceptionCount = 0;
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < Cycles; i++)
        {
            // Spawn FRESH target this cycle — that's the distinction from probe 42
            // (which uses one long-lived target across 50 cycles).
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

            // AttachAndOwn — substrate-managed lifecycle. Per finding 64 + finding 66,
            // Dispose kill-firsts + death-detects + cleanly tears down. If MCH-RE-3 is
            // real at substrate level, accumulated mscordbi state will SIGSEGV here.
            DebugSession session;
            try { session = DebugSession.AttachAndOwn(realPid, cycleSink); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Attach cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                KillTree(proc);
                return 4;
            }

            Thread.Sleep(ObservationMs);

            try
            {
                session.Dispose();
                cleanCount++;
                Console.WriteLine($"cycle {i + 1,2}/{Cycles}: fresh target pid {realPid}, AttachAndOwn + Dispose clean");
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

        AnomalyDrainResult drain = cycleSink.Anomalies.Drain();
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced ({drain.Dropped} dropped to capacity)");
        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        if (disposeExceptionCount > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (Dispose threw {disposeExceptionCount}x): substrate raised exceptions across multi-session cycles.");
            return 6;
        }

        WriteFixture(Cycles, cleanCount, sw.ElapsedMilliseconds, drain.Anomalies.Count, byKind);

        Console.WriteLine();
        Console.WriteLine($"PROBE 48 PASSED — {cleanCount}/{Cycles} clean Dispose cycles against fresh targets in same probe-host process. Substrate handles multi-session-across-different-targets WITHOUT MCH-RE-3 SIGSEGV at substrate level. The MSTest-exe SIGSEGV phenomenon observed during Phase 8 attempt (~6 session threshold) is therefore MSTest-host-specific, not substrate-level — likely related to MSTest's process model, AssemblyLoadContext interaction, or test parallelism rather than mscordbi state accumulation per se.");
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
        string path = Path.Combine(dir, $"48-multi-target-session-{rid}-{ts}.txt");
        string anomalies = byKind.Count == 0
            ? "  (none surfaced)"
            : string.Join("\n", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key,-32} : {kv.Value}"));
        string body =
            "# DrHook.Engine probe 48 fixture — multi-session-across-different-targets state accumulation (MCH-RE-3)\n" +
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
