#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 56 — SUBSTRATE DISPOSE AGAINST BREAK-HALTED TARGET ====================
//
// SCENARIO (surfaced during ADR-008 Increment 4b / Phase 8 promotion attempt):
// AnomalyInjectionTest spawned target with --debug-style handshake, target hit
// Debugger.Break(), substrate received Break stop, test failed an assertion, using
// block disposed substrate, and the target REMAINED ALIVE for 30+ minutes despite
// substrate's SIGTERM-then-SIGKILL escalation (Increment 1 / finding 69) supposedly
// killing it.
//
// HYPOTHESIS (open): "When substrate's Dispose runs against a target halted at a
// Break stop (Debugger.Break() suspended target), the SIGTERM-then-SIGKILL escalation
// in Owned-path Dispose may not actually terminate the target within reasonable time
// — either Stage 1 SIGTERM is queued behind mscordbi's halt and never delivered, AND
// Stage 2 SIGKILL is somehow also delayed/blocked by mscordbi state."
//
// CONSTRUCTION:
//   1. Spawn 56-target.cs (bare Debugger.Break()).
//   2. Parse READY PID.
//   3. AttachAndOwn(pid, sink, naturalExitTimeout=2000ms) — discipline-aligned default.
//   4. WaitForStop expecting Break stop within reasonable time.
//   5. Call session.Dispose() — this triggers the Stage 1 → Stage 2 escalation.
//   6. Measure total Dispose time + observe whether target dies.
//
// Falsification:
//   2 usage; 3 no READY; 4 attach failed; 5 no Break stop arrived;
//   6 substrate Dispose threw;
//   7 target survived Dispose beyond 30 s — substrate-correctness gap confirmed;
//   0 PASS (target dies cleanly within reasonable time after Dispose).
//
// Either outcome is informative: PASS confirms the gap is elsewhere (specific to MTP/VSTest
// framework interaction), FAIL confirms substrate-level issue with Break-halted target.
//
// Usage:  dotnet 56-break-stopped-dispose-smoke.cs <path-to-56-target.cs>

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

return BreakStoppedDisposeP56.Run(args);

sealed class CollectingSink : IDebugEventSink
{
    private readonly object _lock = new();
    private readonly List<EngineAnomaly> _anomalies = new();
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public IReadOnlyList<EngineAnomaly> Anomalies { get { lock (_lock) { return _anomalies.ToArray(); } } }
    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly a) { lock (_lock) { _anomalies.Add(a); } }
}

static class BreakStoppedDisposeP56
{
    const int TargetDeathObserveSeconds = 30;  // generous; substrate should terminate target far sooner

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 56-break-stopped-dispose-smoke.cs <path-to-56-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"plan       : spawn 56-target.cs → AttachAndOwn → WaitForStop(Break) → Dispose → measure target-death time");

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
                Console.WriteLine($"target>>>  {line}");
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

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("FALSIFIED (no READY): target didn't print READY within 30 s.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        var sink = new CollectingSink();
        DebugSession session;
        try { session = DebugSession.AttachAndOwn(realPid, sink); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 4;
        }
        Console.WriteLine("attached   : AttachAndOwn established");

        Console.WriteLine("wait-stop  : awaiting Break stop (up to 10 s)...");
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (stop is null)
        {
            Console.Error.WriteLine("FALSIFIED (no stop): Break stop did not arrive within 10 s.");
            try { session.Dispose(); } catch { }
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 5;
        }
        Console.WriteLine($"stop       : {stop.Reason} arrived");

        Console.WriteLine("dispose    : calling session.Dispose() — measuring time to target death...");
        Stopwatch sw = Stopwatch.StartNew();
        try { session.Dispose(); }
        catch (Exception ex)
        {
            sw.Stop();
            Console.Error.WriteLine($"FALSIFIED (Dispose threw after {sw.ElapsedMilliseconds} ms): {ex.GetType().Name}: {ex.Message}");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 6;
        }
        sw.Stop();
        Console.WriteLine($"dispose    : completed in {sw.ElapsedMilliseconds} ms");

        // Surface anomalies seen so far.
        Console.WriteLine($"anomalies  : {sink.Anomalies.Count} surfaced");
        foreach (var byKind in sink.Anomalies.GroupBy(a => a.Kind))
            Console.WriteLine($"  {byKind.Key,-32} : {byKind.Count()}");

        // Did Dispose actually kill the target? Poll HasExited up to 30 s.
        Console.WriteLine($"observe    : polling target.HasExited up to {TargetDeathObserveSeconds} s...");
        Stopwatch deathSw = Stopwatch.StartNew();
        bool died = false;
        while (deathSw.Elapsed < TimeSpan.FromSeconds(TargetDeathObserveSeconds))
        {
            if (proc.HasExited) { died = true; break; }
            Thread.Sleep(200);
        }
        deathSw.Stop();

        if (!died)
        {
            Console.Error.WriteLine($"FALSIFIED (target survived): target {realPid} alive {TargetDeathObserveSeconds}+ s after Dispose — substrate-correctness gap CONFIRMED. Substrate's SIGTERM-then-SIGKILL escalation against Break-halted target does NOT terminate the target.");
            Console.Error.WriteLine($"  Cleaning up via direct probe-host Kill (separate from substrate path).");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 7;
        }

        Console.WriteLine();
        Console.WriteLine($"PROBE 56 PASSED — substrate Dispose against Break-halted target terminated target in {deathSw.ElapsedMilliseconds} ms (exit code {proc.ExitCode}). Substrate's SIGTERM-then-SIGKILL escalation handles the Break-halted scenario correctly. The Phase 8b AnomalyInjectionTest hang must therefore be specific to MTP/VSTest framework interaction, not substrate-level.");
        return 0;
    }
}
