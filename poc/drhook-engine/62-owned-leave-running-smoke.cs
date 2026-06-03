#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 62 — F-010-2 OWNED DETACH-LEAVE-RUNNING (held/stopped × clean detach) ====
//
// The cell the lost-night "survival probe" never tested. That probe launched a FREE-RUNNING target
// and rude-EXITED the debugger (Environment.Exit, no Detach), then INFERRED clean-detach works.
// F-010-2 actually requires the opposite axes: an Owned target that is currently SYNCHRONIZED
// (the normal debugging state), then a CLEAN leave-running detach. Probe 33's own cleanup comment
// records the risk this probe must refute: "Dispose's Detach path leaves a currently-stopped
// process synchronized indefinitely (no implicit Continue)" — i.e. detaching a stopped launched
// target without an explicit resume HANGS it. DetachLeaveRunning's Quiesce → TryResumeForDetach →
// Detach is the explicit-resume path; this probe proves it leaves the target genuinely RUNNING.
//
// Mechanism (proven pieces only): in-repo compiled target (62-leave-running-target/LeaveRunning.dll,
// the reproducible replacement for the survival probe's ephemeral /tmp Runner). Launch as Owned with
// the entry-module hold-gate engaged (entryModule != null — the MCP launch path). Resume past the
// hold; let the loop heartbeat to a file; Pause-stop it (probe 44 established Pause-stop and
// breakpoint-stop are the same synchronized shape, and Pause avoids a breakpoint re-hit during the
// resume-to-detach). Then DetachLeaveRunning and assert the file heartbeat keeps advancing.
//
// Isolation: the target writes ONLY to a file, never Console — the console-pipe (D2/D4) survival of a
// leave-running target is a SEPARATE unknown, not compounded here.
//
// Falsification: 2 target not built; 3 Launch failed; 4 first stop != EntryModuleLoaded;
//   6 Pause stop wrong / no heartbeat before Pause; 7 DetachLeaveRunning threw;
//   8 heartbeat did NOT advance after detach (target left synchronized/hung); 9 target gone;
//   10 target survived but the detach raised UnexpectedHResult anomalies (not a clean Detach); 0 PASS.
//
// Usage:  dotnet run --no-cache 62-owned-leave-running-smoke.cs
//         (build the target first: dotnet build 62-leave-running-target)

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SkyOmega.DrHook.Engine;

return LeaveRunning62.Run();

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 1024);
    public BoundedAnomalySink Anomalies => _anomalies;
    public void OnEvent(string name) { }
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class LeaveRunning62
{
    const string TargetDir = "62-leave-running-target";
    const string TargetDll = "LeaveRunning.dll";
    const string EntryModule = "LeaveRunning";

    public static int Run()
    {
        string scriptDir = FindPocDir() ?? Environment.CurrentDirectory;
        string targetProj = Path.Combine(scriptDir, TargetDir);
        string targetDll = Path.Combine(targetProj, "bin", "Debug", "net10.0", TargetDll);

        if (!File.Exists(targetDll))
        {
            Console.Error.WriteLine($"FALSIFIED (target not built): expected {targetDll} — run `dotnet build {targetProj}` first.");
            return 2;
        }

        string beatFile = Path.Combine(Path.GetTempPath(), $"drhook-p62-beat-{Environment.ProcessId}.txt");
        try { if (File.Exists(beatFile)) File.Delete(beatFile); } catch { /* fresh */ }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"target     : dotnet {targetDll} {beatFile}");
        Console.WriteLine($"plan       : Launch(Owned, hold-gate) -> EntryModuleLoaded -> Resume -> Pause-stop -> DetachLeaveRunning -> assert file heartbeat advances (target left running).");

        string dotnet = ResolveDotnet();
        var sink = new CountingAnomalySink();

        DebugSession session;
        try
        {
            session = DebugSession.Launch(dotnet, new[] { targetDll, beatFile }, workingDirectory: null, sink: sink, entryModule: EntryModule);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
        int pid = session.ProcessId;
        Console.WriteLine($"launched   : pid={pid} (debugger attached before main; held at entry module)");

        // Stop 1 — the hold-gate EntryModuleLoaded synchronized hold (the MCP-faithful launch path).
        StopInfo? stop1 = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (stop1 is null || stop1.Reason != StopReason.EntryModuleLoaded)
        {
            Console.Error.WriteLine($"FALSIFIED (first stop): {(stop1 is null ? "timeout" : stop1.Reason.ToString())}, expected EntryModuleLoaded (hold-gate).");
            TryKill(pid); try { session.Dispose(); } catch { }
            return 4;
        }
        Console.WriteLine($"stop 1     : {stop1.Reason} (hold-gate; resuming past it to run main)");
        session.Resume();

        // Let the loop run a few heartbeats, then Pause-stop it: an Owned target synchronized
        // mid-execution — the cell the dogfood hit and the survival probe skipped.
        Thread.Sleep(TimeSpan.FromMilliseconds(700));
        session.Pause();
        StopInfo? stop2 = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (stop2 is null || stop2.Reason != StopReason.Pause)
        {
            Console.Error.WriteLine($"FALSIFIED (Pause stop): {(stop2 is null ? "timeout" : stop2.Reason.ToString())}, expected Pause.");
            TryKill(pid); try { session.Dispose(); } catch { }
            return 6;
        }
        int beatAtStop = ReadBeat(beatFile);
        Console.WriteLine($"stop 2     : {stop2.Reason}  beat-at-stop={beatAtStop}  (Owned target STOPPED/synchronized — the induced stop)");
        if (beatAtStop < 1)
        {
            Console.Error.WriteLine("FALSIFIED (setup): no heartbeat written before Pause — target did not run past the hold.");
            TryKill(pid); try { session.Dispose(); } catch { }
            return 6;
        }

        // ── THE TEST: clean leave-running detach of an OWNED, currently-STOPPED target ──
        try
        {
            session.DetachLeaveRunning();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (DetachLeaveRunning threw): {ex.GetType().Name}: {ex.Message}");
            TryKill(pid);
            return 7;
        }
        Console.WriteLine($"detached   : DetachLeaveRunning returned (debugger released; no kill issued)");

        // Survival: the heartbeat must keep advancing now that the target runs un-debugged. If the
        // detach left it synchronized (probe 33's hung state) or killed it, beat will not advance.
        Thread.Sleep(TimeSpan.FromSeconds(2));
        int beatAfter = ReadBeat(beatFile);
        bool aliveAfter = IsAlive(pid);
        Console.WriteLine($"survival   : beat-after-detach={beatAfter} (was {beatAtStop})  target-alive={aliveAfter}");

        var drain = sink.Anomalies.Drain();
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced during the session");
        foreach (EngineAnomaly a in drain.Anomalies)
            Console.WriteLine($"  {a.Kind} @ {a.Operation}: {a.Observed}");
        // WorkerSilentBreak is the benign teardown-while-paused unpark; UnexpectedHResult is NOT
        // benign — it means an ICorDebug call in the detach sequence failed, so the "survival" may be
        // reparent-on-exit rather than a clean Detach (the v1 trap). A clean pass requires zero.
        int unexpectedHr = drain.Anomalies.Count(a => a.Kind == AnomalyKind.UnexpectedHResult);

        int code;
        if (!aliveAfter)
        {
            Console.Error.WriteLine($"FALSIFIED (F-010-2): target pid {pid} is GONE after DetachLeaveRunning — detach did not leave it running.");
            code = 9;
        }
        else if (beatAfter <= beatAtStop)
        {
            Console.Error.WriteLine($"FALSIFIED (F-010-2): heartbeat did NOT advance after detach ({beatAtStop} -> {beatAfter}) — target left synchronized/hung, not running.");
            code = 8;
        }
        else if (unexpectedHr > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (F-010-2 not clean): target survived and advanced {beatAtStop} -> {beatAfter}, but the detach sequence raised {unexpectedHr} UnexpectedHResult anomalies — Detach did not complete cleanly (survival may be reparent-on-exit, not a clean ICorDebug Detach).");
            code = 10;
        }
        else
        {
            Console.WriteLine($"\nPROBE 62 PASSED — DetachLeaveRunning left an OWNED, synchronized target genuinely RUNNING via a CLEAN detach: heartbeat advanced {beatAtStop} -> {beatAfter}, target alive, 0 UnexpectedHResult anomalies. F-010-2 substrate path validated for the held/stopped × clean-detach cell on macOS-arm64.");
            code = 0;
        }

        WriteFixture(scriptDir, pid, beatAtStop, beatAfter, aliveAfter, drain.Anomalies.Count, code);

        // Cleanup: survival-past-detach is proven; do not leak the ~60 s heartbeat process.
        TryKill(pid);
        try { if (File.Exists(beatFile)) File.Delete(beatFile); } catch { }
        return code;
    }

    static int ReadBeat(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                string s = File.ReadAllText(path).Trim();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            }
            catch { /* mid-write; retry */ }
            Thread.Sleep(20);
        }
        return -1;
    }

    static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    static void TryKill(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    static string ResolveDotnet()
    {
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string? dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (dotnetRoot is not null)
        {
            string candidate = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    static string? FindPocDir()
    {
        string cwd = Environment.CurrentDirectory;
        return Directory.Exists(Path.Combine(cwd, TargetDir)) ? cwd : null;
    }

    static void WriteFixture(string scriptDir, int pid, int beatAtStop, int beatAfter, bool alive, int anomalyCount, int code)
    {
        string dir = Path.Combine(scriptDir, "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"62-owned-leave-running-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 62 fixture — F-010-2 Owned detach-leave-running (held/stopped x clean detach)\n" +
            $"timestamp            = {DateTime.UtcNow:O}\n" +
            $"runtime              = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch              = {rid}\n" +
            $"launched-pid         = {pid}\n" +
            $"beat-at-stop         = {beatAtStop}\n" +
            $"beat-after-detach    = {beatAfter}\n" +
            $"target-alive-after   = {alive}\n" +
            $"anomaly-count        = {anomalyCount}\n" +
            $"verdict              = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
