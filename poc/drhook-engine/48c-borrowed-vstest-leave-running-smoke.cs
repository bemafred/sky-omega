#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 48c — BORROWED + VSTest + leave-running pattern in file-based host =====
//
// Sibling probe to 48 + 48b (Phase 8 follow-up; finding 67 distinguishing experiment).
//
// HYPOTHESIS: "The MSTest-exe SIGSEGV during Phase 8 attempt was tied to ONE specific test
// pattern — `Borrowed_VstestTestHost_PauseStop_DisposeWithoutResume_TargetSurvives`. If that
// pattern crashes in a file-based probe-host (no MSTest framework), the substrate's leave-
// running detach against VSTest testhost has a substrate-level race. If it passes, the issue
// is in the MSTest+VSTest interaction (test framework's adapter layer + IPC + substrate
// leave-running interleaving)."
//
// CONSTRUCTION: cycle N times. Each cycle EXACTLY mimics the failing MSTest test:
//   1. Spawn `dotnet test <vstest-integration-target> --no-build` + VSTEST_HOST_DEBUG=1.
//   2. Parse "Process Id: NNNN" → testhost pid.
//   3. DebugSession.Attach(testHostPid, NullSink)  -- BORROWED (not AttachAndOwn).
//   4. session.Pause().
//   5. WaitForStop(5s) expecting Pause stop.
//   6. session.Dispose() WITHOUT consuming the stop / resuming.
//   7. Sleep 200ms (let substrate's detach-leave-running settle).
//   8. Assert testhost is still alive (substrate must not kill Borrowed target).
//   9. Defensive kill of dotnet test bootstrap (to free resources for next cycle).
//
// PREREQUISITE: dotnet build tests/DrHook.Engine.IntegrationTargets.Vstest/...csproj -c Release
//
// Falsification:
//   2 usage; 3 no Process Id; 4 Attach failed; 5 no Pause stop arrived;
//   6 Dispose threw (substrate didn't handle leave-running pattern);
//   7 testhost died after Dispose (substrate violated Borrowed contract — killed our target);
//   8 SIGSEGV at cycle N (substrate-level race in leave-running against VSTest testhost);
//   0 PASS (10/10 — substrate handles the pattern; MSTest+VSTest interaction is the culprit).
//
// Usage:  dotnet 48c-borrowed-vstest-leave-running-smoke.cs <path-to-vstest-integration-target.csproj>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return BorrowedVstestLeaveRunningP48c.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 4096);
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public BoundedAnomalySink Anomalies => _anomalies;

    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class BorrowedVstestLeaveRunningP48c
{
    const int Cycles = 1;        // N=1 to isolate single-cycle crash from accumulation
    const int ObservationMs = 200;
    const int PostDisposeSettleMs = 200;
    const int InterCycleSettleMs = 100;
    const bool SkipExplicitKill = false;  // kill testhost after substrate Dispose → triggers the SIGBUS

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 48c-borrowed-vstest-leave-running-smoke.cs <path-to-vstest-integration-target.csproj>");
            return 2;
        }

        string targetProject = Path.GetFullPath(args[0]);
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"target     : {targetProject}");
        Console.WriteLine($"plan       : {Cycles} cycles of `dotnet test` → Attach(Borrowed) → Pause → Dispose-without-Resume → assert testhost alive;");
        Console.WriteLine($"             reproduces the FAILING MSTest test pattern in file-based host to distinguish substrate-level vs MSTest+VSTest interaction.");

        var cycleSink = new CountingAnomalySink();
        int cleanCount = 0;
        int disposeExceptionCount = 0;
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < Cycles; i++)
        {
            Console.WriteLine($"cycle {i + 1,2}/{Cycles}: spawning dotnet test...");

            using Process dotnetTest = new()
            {
                StartInfo = new ProcessStartInfo("dotnet", $"test \"{targetProject}\" -c Release --no-build --nologo")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    Environment = { ["VSTEST_HOST_DEBUG"] = "1" },
                }
            };
            dotnetTest.Start();

            int testHostPid = -1;
            ManualResetEventSlim ready = new(false);
            Thread reader = new(() =>
            {
                string? line;
                while ((line = dotnetTest.StandardOutput.ReadLine()) is not null)
                {
                    Match m = Regex.Match(line, @"Process Id:\s*(\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                    {
                        Volatile.Write(ref testHostPid, pid);
                        ready.Set();
                    }
                }
            }) { IsBackground = true, Name = $"vstest-stdout-{i + 1}" };
            reader.Start();
            Thread errDrain = new(() => { while (dotnetTest.StandardError.ReadLine() is not null) { } })
            { IsBackground = true, Name = $"vstest-stderr-{i + 1}" };
            errDrain.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(60)))
            {
                Console.Error.WriteLine($"FALSIFIED (target cycle {i + 1}): VSTest didn't print 'Process Id: NNNN' within 60s.");
                try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
                return 3;
            }
            testHostPid = Volatile.Read(ref testHostPid);

            // Acquire testhost Process handle for the alive-check after Dispose.
            Process testHost;
            try { testHost = Process.GetProcessById(testHostPid); }
            catch (ArgumentException)
            {
                Console.Error.WriteLine($"FALSIFIED (cycle {i + 1}): testhost {testHostPid} already gone before Attach.");
                try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
                return 4;
            }

            // BORROWED Attach — substrate must NOT kill the target on Dispose.
            DebugSession session;
            try { session = DebugSession.Attach(testHostPid, cycleSink); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Attach cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                testHost.Dispose();
                try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
                return 4;
            }

            Thread.Sleep(ObservationMs);

            // Pause-stop: worker parks at _resume.Take.
            session.Pause();
            StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
            if (stop is null || stop.Reason != StopReason.Pause)
            {
                Console.Error.WriteLine($"FALSIFIED (no Pause stop cycle {i + 1}): WaitForStop returned {(stop?.Reason.ToString() ?? "null")}.");
                try { session.Dispose(); } catch { }
                testHost.Dispose();
                try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
                return 5;
            }

            // Dispose WITHOUT consuming/resuming. Substrate's detach-leave-running fires.
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

            // Settle, then assert testhost survived the Borrowed Dispose.
            Thread.Sleep(PostDisposeSettleMs);
            if (testHost.HasExited)
            {
                Console.Error.WriteLine($"FALSIFIED (cycle {i + 1}): testhost exited after Borrowed Dispose — substrate killed our target (contract violation).");
                testHost.Dispose();
                try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
                return 7;
            }
            Console.WriteLine($"cycle {i + 1,2}/{Cycles}: testhost pid {testHostPid}, Borrowed Pause+Dispose clean, testhost alive");

            // Cleanup: kill testhost + dotnet test for next cycle.
            testHost.Dispose();
            #pragma warning disable CS0162 // SkipExplicitKill may be true at compile time
            if (!SkipExplicitKill)
            {
                try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
            }
            #pragma warning restore CS0162
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
            Console.Error.WriteLine($"FALSIFIED (Dispose threw {disposeExceptionCount}x): substrate-level race in Borrowed+VSTest+leave-running path.");
            return 6;
        }

        WriteFixture(Cycles, cleanCount, sw.ElapsedMilliseconds, drain.Anomalies.Count, byKind);

        Console.WriteLine();
        Console.WriteLine($"PROBE 48c PASSED — {cleanCount}/{Cycles} Borrowed+VSTest+Pause+Dispose-without-Resume cycles cleanly in file-based probe-host. The substrate handles this pattern correctly at substrate level. The MSTest-exe SIGSEGV at this exact test pattern is therefore an MSTest+VSTest interaction (test framework's adapter layer + IPC + substrate leave-running interleaving), NOT a substrate-level race.");
        return 0;
    }

    static void WriteFixture(int cycles, int clean, long elapsedMs, int anomalyCount, IReadOnlyDictionary<AnomalyKind, int> byKind)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"48c-borrowed-vstest-leave-running-{rid}-{ts}.txt");
        string anomalies = byKind.Count == 0
            ? "  (none surfaced)"
            : string.Join("\n", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key,-32} : {kv.Value}"));
        string body =
            "# DrHook.Engine probe 48c fixture — Borrowed+VSTest+leave-running pattern in file-based host\n" +
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
