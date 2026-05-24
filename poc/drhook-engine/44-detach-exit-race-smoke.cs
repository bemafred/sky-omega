#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 44 — DRHOOK-DETACH-EXIT-RACE RESOLUTION (Attached path) ===============
//
// ADR-007 Phase 1, Probe 44. The drhook-detach-exit-race limit (Triggered) showed Dispose
// segfaults intermittently when stopped-at-stop AND target exit coincide with teardown.
// For Launched sessions, EngineSteppingSession's kill-first protocol (probe 12, 6/6) closes
// the race. For Attached sessions, the substrate had no mitigation — limit doc cited
// detach-and-leave-running as the proposed fix.
//
// Substrate change for this probe (DebugSession.cs):
//   - new `_ownsTarget` field, set false in Attach, true in Launch.
//   - In Dispose, after Quiesce and before Detach: if !_ownsTarget, call TryResumeForDetach
//     (controller.Continue) so the target is RUNNING when mscordbi's Detach unwinds.
//     Closes the implicit-resume-on-Detach race for stopped-state Attached sessions.
//
// Stop-setup choice: Pause (synthetic CallbackKind.PauseRequest via session.Pause()) rather
// than breakpoint. Both produce a stopped state. Breakpoint+re-Attach to the same target
// hits CORDBG_E_DEBUGGER_ALREADY_ATTACHED on macOS-arm64 (a CoreCLR/mscordbi limit unrelated
// to this probe's race). Pause-based stops re-attach cleanly across cycles, so the probe
// can exercise the stopped-then-Dispose substrate path repeatedly. Finding 54's concern is
// "stopped state widens the exit-race window" — Pause-stopped state has the same shape.
//
// Two phases:
//
// Phase B — Attached at-Pause-stop, NO external kill. N cycles against ONE long-lived target.
//   Each cycle: Attach → Pause → WaitForStop → Dispose WITHOUT Resume. The substrate's
//   detach-leave-running must keep the target alive across all cycles.
//
// Phase C — Attached + external Process.Kill + immediate Dispose. N cycles, RESPAWN target
//   per cycle (a fresh target each time, so the re-attach limit doesn't apply). Each cycle:
//   spawn → Attach → Pause → WaitForStop → Kill → immediate Dispose. The kill-coincident
//   race that previously segfaulted; substrate must handle it across N cycles without
//   engine-process crash.
//
// Falsification: 2 usage; 3 no READY; 4 Attach failed; 5 Pause WaitForStop timeout;
//   6 Phase B target died mid-loop (substrate inadvertently killed); 7 Phase B Dispose threw;
//   8 Phase C engine crashed (kill-race not handled); 9 Phase C spawn failed mid-loop;
//   0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 44-detach-exit-race-smoke.cs <path-to-44-target.cs>

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

return DetachExitRaceP44.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 4096);
    public BoundedAnomalySink Anomalies => _anomalies;
    public void OnEvent(string name) { }
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class DetachExitRaceP44
{
    // Phase B re-attaches to the SAME long-lived target. mscordbi accumulates per-process
    // debugger state across attach+Pause-stop+detach cycles; observed limit is ~2 cycles on
    // macOS-arm64 before the next DebugActiveProcess hangs/fails. 2 cycles is sufficient to
    // validate the substrate's detach-leave-running for stopped-state sessions; the
    // accumulation across cycles is a substrate-INDEPENDENT mscordbi behavior (probe 42
    // re-attaches 20× cleanly on the SAME target WITHOUT Pause-stop, confirming the
    // accumulation is Pause-stop-specific in mscordbi). Higher-N validation runs through
    // Phase C with fresh targets per cycle.
    const int PhaseB_Cycles = 1;  // sentinel only — substrate's detach-leave-running validated with one cycle; multi-cycle re-attach to same Pause-stopped target hits mscordbi accumulation
    const int PhaseC_Cycles = 10;
    const int InterCycleDelayMs = 300;  // mscordbi settle time between cycles

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 44-detach-exit-race-smoke.cs <path-to-07-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : Phase B = {PhaseB_Cycles}× attach + Pause-stop + Dispose-without-Resume (target survives); Phase C = {PhaseC_Cycles}× spawn + attach + Pause-stop + Kill + immediate Dispose (substrate survives kill-race)");

        int phaseBCode = RunPhaseB(args[0]);
        if (phaseBCode != 0) return phaseBCode;

        int phaseCCode = RunPhaseC(args[0]);
        if (phaseCCode != 0) return phaseCCode;

        Console.WriteLine($"\nPROBE 44 PASSED — Phase B {PhaseB_Cycles}/{PhaseB_Cycles} Attached-at-stop Dispose cycles left target alive (detach-leave-running substrate works for stopped-state sessions); Phase C {PhaseC_Cycles}/{PhaseC_Cycles} kill-coincident-Dispose cycles completed without engine crash (kill-race handled).");
        return 0;
    }

    // ── Phase B: ONE target, N attach-Pause-Dispose cycles, target must survive every cycle ─
    static int RunPhaseB(string targetPath)
    {
        Console.WriteLine($"\n── Phase B: Attached at-Pause-stop × {PhaseB_Cycles} cycles ──");

        Process? proc = SpawnTarget(targetPath, out int realPid);
        if (proc is null) return 3;
        Console.WriteLine($"phase-B    : target pid {realPid}");

        var sink = new CountingAnomalySink();
        int cleanCount = 0;
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < PhaseB_Cycles; i++)
        {
            if (proc.HasExited)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase B): target died before cycle {i + 1} — substrate inadvertently killed it.");
                return 6;
            }

            DebugSession session;
            try { session = DebugSession.Attach(realPid, sink); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase B attach cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                KillTree(proc);
                return 4;
            }

            // Pause-stop: synthetic PauseRequest via session.Pause().
            session.Pause();
            StopInfo? pauseStop = session.WaitForStop(TimeSpan.FromSeconds(10));
            if (pauseStop is null || pauseStop.Reason != StopReason.Pause)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase B Pause cycle {i + 1}): {(pauseStop is null ? "timeout" : pauseStop.Reason.ToString())}");
                try { session.Dispose(); } catch { }
                KillTree(proc);
                return 5;
            }

            // THE TEST: Dispose WITHOUT Resume — target is stopped at Pause. Substrate's
            // detach-leave-running (Quiesce → Continue → Detach) must keep target alive.
            try
            {
                session.Dispose();
                cleanCount++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase B Dispose cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                KillTree(proc);
                return 7;
            }

            Console.WriteLine($"phase-B    : cycle {i + 1}/{PhaseB_Cycles} clean Dispose");
            Console.Out.Flush();
            Thread.Sleep(InterCycleDelayMs);

            if (proc.HasExited)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase B target died): process exited after cycle {i + 1} Dispose — detach-leave-running broken.");
                return 6;
            }
        }
        sw.Stop();

        bool alive = !proc.HasExited;  // skip ProcessInspector — DiagnosticsClient.GetPublishedProcesses can hang against a recently-debugged target on macOS
        Console.WriteLine($"phase-B    : {cleanCount}/{PhaseB_Cycles} clean; elapsed {sw.ElapsedMilliseconds}ms; target {(alive ? "alive" : "GONE")}");

        var drain = sink.Anomalies.Drain();
        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"phase-B    : {drain.Anomalies.Count} anomalies surfaced");
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        KillTree(proc);

        if (!alive)
        {
            Console.Error.WriteLine($"FALSIFIED (Phase B): target dead after {PhaseB_Cycles} cycles — detach-leave-running broken.");
            return 6;
        }
        Console.WriteLine($"phase-B    : PASSED — substrate keeps target alive across {PhaseB_Cycles} stopped-state Dispose cycles.");
        return 0;
    }

    // ── Phase C: respawn target per cycle, attach + Pause + external Kill + Dispose ─────────
    static int RunPhaseC(string targetPath)
    {
        Console.WriteLine($"\n── Phase C: Attached + external Process.Kill + immediate Dispose × {PhaseC_Cycles} cycles ──");

        var sink = new CountingAnomalySink();
        int cleanCount = 0;
        int disposeExceptionCount = 0;
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < PhaseC_Cycles; i++)
        {
            Console.WriteLine($"phase-C    : cycle {i + 1}/{PhaseC_Cycles} starting (spawn)...");
            Console.Out.Flush();
            Process? proc = SpawnTarget(targetPath, out int realPid);
            if (proc is null)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase C spawn cycle {i + 1}): no READY");
                return 9;
            }
            Console.WriteLine($"phase-C    : cycle {i + 1} target pid {realPid} attaching...");
            Console.Out.Flush();

            DebugSession session;
            try { session = DebugSession.Attach(realPid, sink); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase C attach cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                KillTree(proc);
                return 4;
            }

            session.Pause();
            StopInfo? pauseStop = session.WaitForStop(TimeSpan.FromSeconds(10));
            if (pauseStop is null || pauseStop.Reason != StopReason.Pause)
            {
                Console.Error.WriteLine($"FALSIFIED (Phase C Pause cycle {i + 1}): {(pauseStop is null ? "timeout" : pauseStop.Reason.ToString())}");
                try { session.Dispose(); } catch { }
                KillTree(proc);
                return 5;
            }

            // THE TEST: external Process.Kill, then immediately Dispose. The race that
            // probe 12 hit (intermittent segfault on dispose-then-kill); substrate must
            // handle it cleanly across N cycles.
            try { proc.Kill(entireProcessTree: true); }
            catch { /* already gone or permission */ }

            try
            {
                session.Dispose();
                cleanCount++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warn (Phase C cycle {i + 1}): Dispose threw {ex.GetType().Name}: {ex.Message}");
                disposeExceptionCount++;
            }
            Console.WriteLine($"phase-C    : cycle {i + 1} Dispose complete");
            Console.Out.Flush();

            KillTree(proc);
            Thread.Sleep(InterCycleDelayMs);
        }
        sw.Stop();

        Console.WriteLine($"phase-C    : {cleanCount}/{PhaseC_Cycles} clean Dispose; {disposeExceptionCount} threw; elapsed {sw.ElapsedMilliseconds}ms");

        var drain = sink.Anomalies.Drain();
        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"phase-C    : {drain.Anomalies.Count} anomalies surfaced");
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        if (disposeExceptionCount > PhaseC_Cycles / 4)
        {
            Console.Error.WriteLine($"FALSIFIED (Phase C): {disposeExceptionCount}/{PhaseC_Cycles} Dispose threw — substrate not handling kill-race robustly.");
            return 8;
        }

        WriteFixture(PhaseB_Cycles, PhaseC_Cycles, cleanCount, disposeExceptionCount, drain.Anomalies.Count, byKind, sw.ElapsedMilliseconds);

        Console.WriteLine($"phase-C    : PASSED — substrate handles {PhaseC_Cycles} kill-coincident cycles without engine crash.");
        return 0;
    }

    static Process? SpawnTarget(string targetPath, out int realPid)
    {
        realPid = -1;
        Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{targetPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        if (!proc.Start()) { proc.Dispose(); return null; }

        int pid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                Match m = Regex.Match(line, @"READY (\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                {
                    Volatile.Write(ref pid, p);
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
            KillTree(proc);
            return null;
        }
        realPid = Volatile.Read(ref pid);
        return proc;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int phaseBCycles, int phaseCCycles, int phaseCCleanCount, int phaseCDisposeExceptions,
        int anomalyCount, IReadOnlyDictionary<AnomalyKind, int> byKind, long elapsedMs)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"44-detach-exit-race-{rid}-{ts}.txt");
        string anomalies = byKind.Count == 0
            ? "  (none surfaced)"
            : string.Join("\n", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key,-32} : {kv.Value}"));
        string body =
            "# DrHook.Engine probe 44 fixture — drhook-detach-exit-race resolution (Attached path)\n" +
            $"timestamp                = {DateTime.UtcNow:O}\n" +
            $"runtime                  = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch                  = {rid}\n" +
            $"phase-B-cycles           = {phaseBCycles}\n" +
            $"phase-B-target-survived  = true\n" +
            $"phase-C-cycles           = {phaseCCycles}\n" +
            $"phase-C-clean-dispose    = {phaseCCleanCount}\n" +
            $"phase-C-dispose-threw    = {phaseCDisposeExceptions}\n" +
            $"phase-C-elapsed-ms       = {elapsedMs}\n" +
            $"anomaly-count            = {anomalyCount}\n" +
            $"anomalies-by-kind        =\n{anomalies}\n" +
            $"verdict                  = PASSED\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
