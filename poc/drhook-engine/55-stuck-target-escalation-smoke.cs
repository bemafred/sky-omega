#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 55 — SUBSTRATE TWO-STAGE ESCALATION (ADR-008 Increment 1) =============
//
// HYPOTHESIS: "The substrate's Dispose, against an Owned target that explicitly ignores SIGTERM
// (Cancel=true handler — Layer 1 discipline violator per finding 67/68), correctly performs
// Stage 1 → timeout → emit TargetStuckAtDispose anomaly → Stage 2 SIGKILL fallback → clean
// teardown. Total budget bounded by NaturalExitTimeout + KillSettleMs + ExitWorkSettleMs."
//
// CONSTRUCTION: spawn 51-target.cs (catches SIGINT + SIGTERM with Cancel=true, ignores both).
// AttachAndOwn with a SHORT naturalExitTimeout (500 ms) so probe runs fast. Dispose. Observe:
//   - Total elapsed time ≈ 500 ms (Stage 1) + 100 ms (KillSettle) + 200 ms (ExitWorkSettle) ≈ 800 ms.
//   - Exactly 1 TargetStuckAtDispose anomaly surfaced.
//   - Target dead after Dispose returns.
//   - No engine crash; no Dispose exception.
//
// Falsification:
//   2 usage; 3 no READY; 4 attach failed; 5 Dispose threw;
//   6 target survived Dispose (SIGKILL fallback failed);
//   7 wrong TargetStuckAtDispose count (expected exactly 1);
//   8 total elapsed outside expected window (< 400 ms — substrate skipped Stage 1; or > 5 s — substrate hung);
//   0 PASS.
//
// Usage:  dotnet 55-stuck-target-escalation-smoke.cs <path-to-51-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return StuckTargetEscalationP55.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 256);
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public BoundedAnomalySink Anomalies => _anomalies;
    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class StuckTargetEscalationP55
{
    static readonly TimeSpan NaturalExitTimeout = TimeSpan.FromMilliseconds(500);
    const int MinExpectedElapsedMs = 400;   // Stage 1 must wait at least most of NaturalExitTimeout
    const int MaxExpectedElapsedMs = 5000;  // Stage 2 + teardown should finish within 5s total

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 55-stuck-target-escalation-smoke.cs <path-to-51-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"plan       : spawn ignoring target (51-target.cs) → AttachAndOwn with {NaturalExitTimeout.TotalMilliseconds}ms naturalExitTimeout → Dispose → observe Stage 1 SIGTERM ignored → TargetStuckAtDispose anomaly → Stage 2 SIGKILL");

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

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("FALSIFIED (no READY).");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        Thread.Sleep(200);  // let target install both signal handlers before we Attach

        var sink = new CountingAnomalySink();
        DebugSession session;
        try { session = DebugSession.AttachAndOwn(realPid, sink, NaturalExitTimeout); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 4;
        }

        Thread.Sleep(200);  // brief observation window

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Dispose threw): {ex.GetType().Name}: {ex.Message}");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 5;
        }
        sw.Stop();
        long elapsedMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"dispose    : completed in {elapsedMs}ms");

        // Verify target is dead (SIGKILL fallback worked)
        if (!proc.HasExited)
        {
            Console.Error.WriteLine("FALSIFIED (target survived): Stage 2 SIGKILL fallback did not kill the target — kernel violated.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 6;
        }
        Console.WriteLine($"target     : dead (exit code {proc.ExitCode})");

        // Verify anomalies
        AnomalyDrainResult drain = sink.Anomalies.Drain();
        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced ({drain.Dropped} dropped)");
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        int stuckCount = byKind.GetValueOrDefault(AnomalyKind.TargetStuckAtDispose, 0);
        if (stuckCount != 1)
        {
            Console.Error.WriteLine($"FALSIFIED (TargetStuckAtDispose count = {stuckCount}, expected exactly 1): substrate did not surface the expected anomaly for the ignoring-target scenario.");
            return 7;
        }

        // Inspect the anomaly details
        EngineAnomaly stuck = drain.Anomalies.First(a => a.Kind == AnomalyKind.TargetStuckAtDispose);
        Console.WriteLine($"  → operation: {stuck.Operation}");
        Console.WriteLine($"  → observed : {stuck.Observed}");
        if (stuck.Context is not null)
            Console.WriteLine($"  → context  : {string.Join(", ", stuck.Context.Select(kv => kv.Key + "=" + kv.Value))}");

        if (elapsedMs < MinExpectedElapsedMs || elapsedMs > MaxExpectedElapsedMs)
        {
            Console.Error.WriteLine($"FALSIFIED (elapsed {elapsedMs}ms outside expected [{MinExpectedElapsedMs}, {MaxExpectedElapsedMs}]ms window): substrate timing diverged from two-stage discipline.");
            return 8;
        }

        Console.WriteLine();
        Console.WriteLine($"PROBE 55 PASSED — substrate two-stage SIGTERM-then-SIGKILL escalation:");
        Console.WriteLine($"  Stage 1: SIGTERM sent + waited {NaturalExitTimeout.TotalMilliseconds}ms (target ignored — Cancel=true)");
        Console.WriteLine($"  Anomaly: TargetStuckAtDispose surfaced (1) with target's PID + timeout context");
        Console.WriteLine($"  Stage 2: SIGKILL fallback killed target (exit code {proc.ExitCode} = 128 + SIGKILL)");
        Console.WriteLine($"  Total elapsed: {elapsedMs}ms (within [{MinExpectedElapsedMs}, {MaxExpectedElapsedMs}]ms budget)");
        Console.WriteLine($"  No engine crash; no Dispose exception. ADR-008 Increment 1 substrate semantics validated.");
        return 0;
    }
}
