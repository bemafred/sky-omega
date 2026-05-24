#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 45 — WORKER-THREAD EXCEPTION PATH =====================================
//
// ADR-007 Phase 1, Probe 45 (post-renumber). Inject an exception into the pump worker's
// execution path (via a throwing user sink) and validate the substrate's outer Pump try/catch
// (EA-4) fires WorkerException AND surfaces it through the anomaly path WITHOUT process crash.
// Validates three distinct substrate guarantees:
//
//   1. WorkerException anomaly surfaces via BoundedAnomalySink (substrate catches + records).
//   2. Subsequent WaitForStop returns null cleanly within timeout (substrate doesn't lie about
//      state — dead worker means no future stops, BlockingCollection.TryTake honors timeout).
//   3. Dispose completes cleanly even with dead worker (pump.Dispose's CompleteAdding +
//      Join-on-already-exited-thread are no-ops, substrate teardown proceeds normally).
//
// Injection point: `_userSink.OnEvent(e.Name)` in CallbackPump.Pump's Informational branch.
// The flood target (probe 07) emits CreateThread/ExitThread (Informational) at ~50/sec
// → first OnEvent call hits ThrowingSink → InvalidOperationException → catch in Pump's outer
// try → OnAnomaly fires WorkerException → worker exits cleanly.
//
// Falsification: 2 usage; 3 no READY; 4 Attach failed; 5 WorkerException not observed within
//   10s (substrate's catch didn't fire); 6 process crashed; 7 multiple WorkerException
//   (worker resurrected — substrate bug); 8 WaitForStop hung (substrate lies about state);
//   9 Dispose threw (substrate teardown broken when worker is dead); 10 target died;
//   0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 45-worker-exception-smoke.cs <path-to-07-target.cs>

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

return WorkerExceptionP45.Run(args);

sealed class ThrowingSink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 256);
    private int _onEventCalls;
    private int _throwsArmed = 1;  // 1 = will throw on next OnEvent; 0 = no longer throwing

    public BoundedAnomalySink Anomalies => _anomalies;
    public int OnEventCalls => Volatile.Read(ref _onEventCalls);

    public void OnEvent(string name)
    {
        Interlocked.Increment(ref _onEventCalls);
        // Throw on the FIRST OnEvent call only. The substrate's catch should record
        // WorkerException; the worker exits; no further OnEvent calls reach us.
        if (Interlocked.Exchange(ref _throwsArmed, 0) == 1)
            throw new InvalidOperationException($"probe-45 injected throw on first OnEvent ('{name}')");
    }

    // OnAnomaly MUST NOT throw — otherwise the catch in Pump propagates uncaught + process
    // crashes (the worker thread is IsBackground=true, an unhandled exception is fatal under
    // .NET runtime's policy for managed background threads). Substrate evidence preserved.
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class WorkerExceptionP45
{
    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 45-worker-exception-smoke.cs <path-to-07-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : Attach with ThrowingSink → first OnEvent throws → substrate Pump catch (EA-4) fires WorkerException → assert anomaly surfaces, WaitForStop returns null cleanly, Dispose completes, target alive.");

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

        var sink = new ThrowingSink();
        DebugSession session;
        try { session = DebugSession.Attach(realPid, sink); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        int code = Drive(session, sink, proc);

        KillTree(proc);
        return code;
    }

    static int Drive(DebugSession session, ThrowingSink sink, Process proc)
    {
        // Wait up to 10s for WorkerException to surface in the anomaly sink.
        Stopwatch sw = Stopwatch.StartNew();
        int workerExceptionCount = 0;
        bool sinkThrew = false;

        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(100);
            if (sink.OnEventCalls > 0 && !sinkThrew)
            {
                sinkThrew = true;
                Console.WriteLine($"injection  : OnEvent invoked at {sw.ElapsedMilliseconds}ms (throwing-sink fired)");
            }

            // Peek anomaly count without draining.
            workerExceptionCount = sink.Anomalies.Count;
            if (workerExceptionCount > 0) break;
        }

        if (!sinkThrew)
        {
            Console.Error.WriteLine($"FALSIFIED: no OnEvent calls observed within 10s — target isn't generating Informational callbacks for the substrate to deliver. Probe can't validate without an injection point.");
            try { session.Dispose(); } catch { }
            return 5;
        }

        // Drain anomalies — should contain at least one WorkerException.
        AnomalyDrainResult drain = sink.Anomalies.Drain();
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced after {sw.ElapsedMilliseconds}ms ({drain.Dropped} dropped)");
        var byKind = drain.Anomalies.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach ((AnomalyKind kind, int count) in byKind.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-32} : {count}");

        int weCount = byKind.GetValueOrDefault(AnomalyKind.WorkerException, 0);
        if (weCount == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (no WorkerException): substrate's outer Pump catch (EA-4) did not fire — injection happened ({sink.OnEventCalls} OnEvent calls) but no WorkerException surfaced.");
            try { session.Dispose(); } catch { }
            return 5;
        }
        if (weCount > 1)
        {
            Console.Error.WriteLine($"FALSIFIED (multiple WorkerException): worker resurrected itself? Expected exactly 1, got {weCount}.");
            try { session.Dispose(); } catch { }
            return 7;
        }

        Console.WriteLine($"  WorkerException details:");
        foreach (EngineAnomaly we in drain.Anomalies.Where(a => a.Kind == AnomalyKind.WorkerException))
            Console.WriteLine($"    thread={we.Thread}  operation={we.Operation}  observed={we.Observed}");

        // WaitForStop after worker death must return null cleanly within timeout, not hang.
        Stopwatch waitSw = Stopwatch.StartNew();
        StopInfo? noStop = session.WaitForStop(TimeSpan.FromSeconds(2));
        waitSw.Stop();
        Console.WriteLine($"post-death : WaitForStop returned {(noStop is null ? "null" : noStop.Reason.ToString())} after {waitSw.ElapsedMilliseconds}ms (timeout was 2000ms)");
        if (noStop is not null)
        {
            Console.Error.WriteLine($"FALSIFIED (unexpected stop): substrate published a stop after worker died?");
            try { session.Dispose(); } catch { }
            return 8;
        }
        if (waitSw.ElapsedMilliseconds < 1900)
        {
            Console.Error.WriteLine($"FALSIFIED (early return): WaitForStop returned null too quickly ({waitSw.ElapsedMilliseconds}ms vs ~2000ms expected) — substrate is lying about state.");
            try { session.Dispose(); } catch { }
            return 8;
        }

        // Dispose must complete cleanly even with dead worker.
        Stopwatch disposeSw = Stopwatch.StartNew();
        try { session.Dispose(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Dispose threw): substrate teardown broken with dead worker: {ex.GetType().Name}: {ex.Message}");
            return 9;
        }
        disposeSw.Stop();
        Console.WriteLine($"dispose    : completed in {disposeSw.ElapsedMilliseconds}ms (dead worker, no Join wait)");

        // Detach must leave target alive (substrate's detach-leave-running for Attached).
        Thread.Sleep(200);
        if (proc.HasExited)
        {
            Console.Error.WriteLine($"FALSIFIED (target died): Dispose killed the target.");
            return 10;
        }
        Console.WriteLine($"target     : alive (resumed un-debugged)");

        WriteFixture(proc.Id, sink.OnEventCalls, weCount, waitSw.ElapsedMilliseconds, disposeSw.ElapsedMilliseconds);

        Console.WriteLine($"\nPROBE 45 PASSED — substrate's outer Pump try/catch (EA-4) caught the injected exception, fired exactly 1 WorkerException anomaly via OnAnomaly, the dead worker doesn't lie (WaitForStop returns null cleanly after timeout), Dispose completes cleanly, target survives. The Worker-thread exception path closes the Phase 1 substrate-correctness arc.");
        return 0;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int onEventCalls, int workerExceptionCount, long waitElapsedMs, long disposeElapsedMs)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"45-worker-exception-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 45 fixture — Worker-thread exception path validation\n" +
            $"timestamp            = {DateTime.UtcNow:O}\n" +
            $"runtime              = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch              = {rid}\n" +
            $"target-pid           = {pid}\n" +
            $"on-event-calls       = {onEventCalls}\n" +
            $"worker-exception-cnt = {workerExceptionCount}\n" +
            $"wait-elapsed-ms      = {waitElapsedMs}\n" +
            $"dispose-elapsed-ms   = {disposeElapsedMs}\n" +
            $"verdict              = PASSED\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
