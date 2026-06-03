#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 62b — F-010-2 OWNED DETACH-LEAVE-RUNNING from a BREAKPOINT stop ===========
//
// Probe 62 validated the clean leave-running detach for a Pause-stopped Owned target. 62b is the
// MCP-realistic axis: the agent detaches while the target is stopped at an actual BREAKPOINT (the
// normal debugging state), not a synthetic Pause. The risk 62b closes: a breakpoint in the loop
// would be re-hit if the detach RESUMED with the breakpoint still armed — but DetachLeaveRunning's
// v2 recipe (Quiesce -> Detach, no pre-resume) lets ICorDebug Detach REMOVE the breakpoint and
// resume atomically, so there is no re-hit. 62b proves Detach cleanly removes the breakpoint and
// leaves the target running. NO engine change vs probe 62 — same DetachLeaveRunning.
//
// Reuses 62-leave-running-target (file-only heartbeat — console-pipe survival isolated out), arming
// a line breakpoint at the BEAT_HERE marker via the entry-module hold-gate (the flow the hold-gate
// was designed for: hold at entry-module load -> arm bp -> continue to it).
//
// Falsification: 2 target not built / no marker; 3 Launch; 4 first stop != EntryModuleLoaded;
//   5 SetBreakpointAtLine; 6 second stop != Breakpoint / no heartbeat before stop;
//   7 DetachLeaveRunning threw; 8 heartbeat did NOT advance after detach (hung); 9 target gone;
//   10 survived but the detach raised UnexpectedHResult (not a clean Detach); 0 PASS.
//
// Usage:  dotnet run --no-cache 62b-owned-leave-running-breakpoint-smoke.cs
//         (build the target first: dotnet build 62-leave-running-target)

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SkyOmega.DrHook.Engine;

return LeaveRunning62b.Run();

sealed class CountingAnomalySink : IDebugEventSink
{
    private readonly BoundedAnomalySink _anomalies = new(capacity: 1024);
    public BoundedAnomalySink Anomalies => _anomalies;
    public void OnEvent(string name) { }
    public void OnAnomaly(EngineAnomaly anomaly) => _anomalies.OnAnomaly(anomaly);
}

static class LeaveRunning62b
{
    const string TargetDir = "62-leave-running-target";
    const string TargetDll = "LeaveRunning.dll";
    const string EntryModule = "LeaveRunning";
    const string MarkerToken = "BEAT_HERE";

    public static int Run()
    {
        string scriptDir = FindPocDir() ?? Environment.CurrentDirectory;
        string targetProj = Path.Combine(scriptDir, TargetDir);
        string targetDll = Path.Combine(targetProj, "bin", "Debug", "net10.0", TargetDll);
        string targetSource = Path.Combine(targetProj, "Program.cs");

        if (!File.Exists(targetDll))
        {
            Console.Error.WriteLine($"FALSIFIED (target not built): expected {targetDll} — run `dotnet build {targetProj}` first.");
            return 2;
        }
        int markerLine = FindMarker(targetSource, MarkerToken);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED: '{MarkerToken}' not found in {targetSource}."); return 2; }

        string beatFile = Path.Combine(Path.GetTempPath(), $"drhook-p62b-beat-{Environment.ProcessId}.txt");
        try { if (File.Exists(beatFile)) File.Delete(beatFile); } catch { /* fresh */ }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"target     : dotnet {targetDll} {beatFile}");
        Console.WriteLine($"plan       : Launch(Owned, hold-gate) -> EntryModuleLoaded -> arm bp at Program.cs:{markerLine} -> Resume -> Breakpoint stop -> DetachLeaveRunning -> assert file heartbeat advances (bp removed, target left running).");

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

        // Stop 1 — the hold-gate EntryModuleLoaded synchronized hold; arm the breakpoint here.
        StopInfo? stop1 = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (stop1 is null || stop1.Reason != StopReason.EntryModuleLoaded)
        {
            Console.Error.WriteLine($"FALSIFIED (first stop): {(stop1 is null ? "timeout" : stop1.Reason.ToString())}, expected EntryModuleLoaded (hold-gate).");
            TryKill(pid); try { session.Dispose(); } catch { }
            return 4;
        }
        Console.WriteLine($"stop 1     : {stop1.Reason} (hold-gate)");

        int bpId = session.SetBreakpointAtLine(EntryModule, "Program.cs", markerLine);
        if (bpId == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): Program.cs:{markerLine}.");
            TryKill(pid); try { session.Dispose(); } catch { }
            return 5;
        }
        Console.WriteLine($"breakpoint : Program.cs:{markerLine} id={bpId}; resuming to it");
        session.Resume();

        // Stop 2 — stopped AT the breakpoint, deep in the heartbeat loop: the MCP-realistic induced stop.
        StopInfo? stop2 = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (stop2 is null || stop2.Reason != StopReason.Breakpoint)
        {
            Console.Error.WriteLine($"FALSIFIED (second stop): {(stop2 is null ? "timeout" : stop2.Reason.ToString())}, expected Breakpoint.");
            TryKill(pid); try { session.Dispose(); } catch { }
            return 6;
        }
        int beatAtStop = ReadBeat(beatFile);
        Console.WriteLine($"stop 2     : {stop2.Reason}  beat-at-stop={beatAtStop}  (Owned target STOPPED at a breakpoint — the induced stop)");
        if (beatAtStop < 1)
        {
            Console.Error.WriteLine("FALSIFIED (setup): no heartbeat written before the breakpoint stop.");
            TryKill(pid); try { session.Dispose(); } catch { }
            return 6;
        }

        // ── THE TEST: clean leave-running detach of an OWNED target stopped at a BREAKPOINT ──
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
        Console.WriteLine($"detached   : DetachLeaveRunning returned (debugger released; breakpoint should be removed; no kill issued)");

        // Survival: the heartbeat must keep advancing. If Detach left the breakpoint armed and the
        // target re-hit it with no debugger, or left the target synchronized, beat would not advance.
        Thread.Sleep(TimeSpan.FromSeconds(2));
        int beatAfter = ReadBeat(beatFile);
        bool aliveAfter = IsAlive(pid);
        Console.WriteLine($"survival   : beat-after-detach={beatAfter} (was {beatAtStop})  target-alive={aliveAfter}");

        var drain = sink.Anomalies.Drain();
        Console.WriteLine($"anomalies  : {drain.Anomalies.Count} surfaced during the session");
        foreach (EngineAnomaly a in drain.Anomalies)
            Console.WriteLine($"  {a.Kind} @ {a.Operation}: {a.Observed}");
        int unexpectedHr = drain.Anomalies.Count(a => a.Kind == AnomalyKind.UnexpectedHResult);

        int code;
        if (!aliveAfter)
        {
            Console.Error.WriteLine($"FALSIFIED (F-010-2 bp): target pid {pid} is GONE after DetachLeaveRunning.");
            code = 9;
        }
        else if (beatAfter <= beatAtStop)
        {
            Console.Error.WriteLine($"FALSIFIED (F-010-2 bp): heartbeat did NOT advance after detach ({beatAtStop} -> {beatAfter}) — target hung (breakpoint not removed, or left synchronized).");
            code = 8;
        }
        else if (unexpectedHr > 0)
        {
            Console.Error.WriteLine($"FALSIFIED (F-010-2 bp not clean): target survived and advanced {beatAtStop} -> {beatAfter}, but the detach raised {unexpectedHr} UnexpectedHResult anomalies.");
            code = 10;
        }
        else
        {
            Console.WriteLine($"\nPROBE 62b PASSED — DetachLeaveRunning left an OWNED, BREAKPOINT-stopped target genuinely RUNNING via a CLEAN detach: Detach removed the breakpoint and resumed; heartbeat advanced {beatAtStop} -> {beatAfter}, target alive, 0 UnexpectedHResult. F-010-2 validated for the breakpoint-stop × clean-detach cell (the MCP-realistic case) on macOS-arm64.");
            code = 0;
        }

        WriteFixture(scriptDir, pid, markerLine, beatAtStop, beatAfter, aliveAfter, drain.Anomalies.Count, code);

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

    static int FindMarker(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    static void WriteFixture(string scriptDir, int pid, int markerLine, int beatAtStop, int beatAfter, bool alive, int anomalyCount, int code)
    {
        string dir = Path.Combine(scriptDir, "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"62b-owned-leave-running-bp-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 62b fixture — F-010-2 Owned detach-leave-running (breakpoint-stop x clean detach)\n" +
            $"timestamp            = {DateTime.UtcNow:O}\n" +
            $"runtime              = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch              = {rid}\n" +
            $"launched-pid         = {pid}\n" +
            $"breakpoint-line      = {markerLine}\n" +
            $"beat-at-stop         = {beatAtStop}\n" +
            $"beat-after-detach    = {beatAfter}\n" +
            $"target-alive-after   = {alive}\n" +
            $"anomaly-count        = {anomalyCount}\n" +
            $"verdict              = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
