#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 48b — HEAVYWEIGHT (dotnet test) SPAWN PATTERN IN FILE-BASED HOST ========
//
// Sibling probe to 48 (Phase 8 follow-up; finding 67 distinguishing experiment).
//
// HYPOTHESIS: "If MCH-RE-3 (the ~6-session SIGSEGV in MSTest exe during Phase 8 mass-promotion
// attempt) is caused by the heavyweight per-test child-process tree (dotnet test → vstest.console
// → testhost = 3 processes per cycle, plus pipes/handles), then reproducing the SAME spawn
// pattern in a FILE-BASED probe-host should also crash at the same threshold. If probe 48b
// passes 10/10, the spawn pattern is innocent and the MSTest-specific environment is the
// real differentiator (MTP test-orchestration, MSTest TestClass lifecycle, AssemblyLoadContext,
// parallel test execution, etc.)."
//
// CONSTRUCTION: cycle N times. Each cycle:
//   1. Spawn `dotnet test <vstest-integration-target> --no-build` with VSTEST_HOST_DEBUG=1.
//      This produces the SAME 3-process tree as the VSTest integration tests:
//        dotnet test (orchestrator)
//          └── vstest.console
//                └── testhost (the actual debug target)
//   2. Parse "Process Id: NNNN" from stdout (VSTest's undocumented handshake).
//   3. AttachAndOwn(testHostPid, NullSink).
//   4. Sleep 200 ms.
//   5. session.Dispose() — substrate kills testhost; dotnet test orchestrator cleans up.
//   6. Defensive kill of the dotnet test bootstrap (in case substrate didn't cascade through).
//   7. Inter-cycle settle.
//
// Probe-host process accumulates per-cycle: 3 child processes (dotnet test, vstest.console,
// testhost) + their stdin/stdout/stderr pipes. If the OS resource ceiling is what bit the
// MSTest exe (per `feedback_resource_limit_class_audit` trust gap), the same accumulation
// pattern here should hit it.
//
// PREREQUISITE: the VSTest integration target must be built. Run from repo root:
//   dotnet build tests/DrHook.Engine.IntegrationTargets.Vstest/DrHook.Engine.IntegrationTargets.Vstest.csproj -c Release
//
// Falsification:
//   2 usage; 3 no Process Id parsed; 4 attach failed mid-loop;
//   5 SIGSEGV / SIGBUS at cycle N — heavyweight-spawn pattern reproduces in probe-host;
//   6 Dispose threw mid-loop;
//   0 PASS (10/10 — heavyweight spawn pattern is INNOCENT, MSTest-environment is the cause).
//
// Usage:  dotnet 48b-heavyweight-spawn-smoke.cs <path-to-vstest-integration-target.csproj>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return HeavyweightSpawnP48b.Run(args);

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 4096);
    private int _eventCount;
    public int EventCount => Volatile.Read(ref _eventCount);
    public BoundedAnomalySink Anomalies => _anomalies;

    public void OnEvent(string name) => Interlocked.Increment(ref _eventCount);
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class HeavyweightSpawnP48b
{
    const int Cycles = 10;
    const int ObservationMs = 200;
    const int InterCycleSettleMs = 100;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 48b-heavyweight-spawn-smoke.cs <path-to-vstest-integration-target.csproj>");
            return 2;
        }

        string targetProject = Path.GetFullPath(args[0]);
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"target     : {targetProject}");
        Console.WriteLine($"plan       : {Cycles} cycles of `dotnet test` (3-process tree) → AttachAndOwn(testhost) → observe → Dispose;");
        Console.WriteLine($"             characterises heavyweight-spawn-pattern as MCH-RE-3 candidate cause vs MSTest-environment.");

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

            DebugSession session;
            try { session = DebugSession.AttachAndOwn(testHostPid, cycleSink); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FALSIFIED (Attach cycle {i + 1}): {ex.GetType().Name}: {ex.Message}");
                try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }
                return 4;
            }

            Thread.Sleep(ObservationMs);

            try
            {
                session.Dispose();
                cleanCount++;
                Console.WriteLine($"cycle {i + 1,2}/{Cycles}: testhost pid {testHostPid}, AttachAndOwn + Dispose clean");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warn (cycle {i + 1}): Dispose threw {ex.GetType().Name}: {ex.Message}");
                disposeExceptionCount++;
            }

            // Defensive kill of dotnet test bootstrap (substrate killed testhost; orchestrator
            // usually exits naturally shortly after, but ensure no orphan tree).
            try { if (!dotnetTest.HasExited) dotnetTest.Kill(entireProcessTree: true); } catch { }

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
            Console.Error.WriteLine($"FALSIFIED (Dispose threw {disposeExceptionCount}x): substrate raised exceptions across multi-cycle heavyweight spawn.");
            return 6;
        }

        WriteFixture(Cycles, cleanCount, sw.ElapsedMilliseconds, drain.Anomalies.Count, byKind);

        Console.WriteLine();
        Console.WriteLine($"PROBE 48b PASSED — {cleanCount}/{Cycles} heavyweight-spawn cycles (dotnet test 3-process tree per cycle) completed cleanly in file-based probe-host. The heavyweight-spawn pattern is INNOCENT — MCH-RE-3 SIGSEGV during Phase 8 attempt is MSTest-environment-specific, NOT a function of per-cycle child-process accumulation at probe-host level. Next diagnosis step: lldb attach to MSTest exe at SIGSEGV to identify the actual MSTest-side mechanism.");
        return 0;
    }

    static void WriteFixture(int cycles, int clean, long elapsedMs, int anomalyCount, IReadOnlyDictionary<AnomalyKind, int> byKind)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"48b-heavyweight-spawn-{rid}-{ts}.txt");
        string anomalies = byKind.Count == 0
            ? "  (none surfaced)"
            : string.Join("\n", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key,-32} : {kv.Value}"));
        string body =
            "# DrHook.Engine probe 48b fixture — heavyweight (dotnet test) spawn pattern in file-based host\n" +
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
