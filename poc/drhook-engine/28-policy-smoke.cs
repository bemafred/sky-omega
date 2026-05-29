#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 28 — BreakpointPolicy: unified condition / hit-count / log / suspend
// ========================================================================================
//
// The engineering increment named in findings 33 + 35. ONE BreakpointPolicy record composing:
//   GATES  — Condition (tri-state with fault path), HitCount (Equals/AtLeast/Multiple);
//   ACTION — LogMessage (rendered + emitted as a structured LogRecord to IDebugEventSink.OnLog);
//   SUSPEND — All (surface a stop) or None (auto-resume, the logpoint corner).
// Conditional breakpoint and logpoint are TWO CONFIGS of this one type — composed capabilities,
// not a behavior flag. This probe drives ONE target/breakpoint with FOUR configs and asserts each:
//
//   A. CONDITIONAL BREAKPOINT  — Condition `v == 3`, Suspend.All  →  surfaces exactly at v == 3.
//   B. LOGPOINT                — LogMessage `v={v}`, Suspend.None →  never surfaces, sink collects.
//   C. HIT-COUNT GATED LOGPOINT — HitCount Equals(3) + LogMessage, Suspend.None
//                                 →  never surfaces, EXACTLY ONE log line in the window
//                                    (proves hit-count gating doubles as logpoint sampling).
//   D. FAULT                   — Condition that throws → ConditionError stop + a fault LogRecord
//                                 (proves a broken condition fails LOUD once, not silently false).
//
// Migration to ADR-010 Increment 2c (2026-05-28): policy now lives on the breakpoint, not at the
// wait site. SetBreakpointAtLine takes a BreakpointPolicy; the engine evaluates internally via
// CallbackPump → DebugSession.EvaluateBreakpointHit; the caller just calls plain WaitForStop.
// Between configs the probe Remove+Re-SetBP with the next policy (the substrate is stopped after
// a surfaced stop or after Pause+WaitForStop). Old session-scope WaitForPolicyStop is gone.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break;
//   7 config A failed; 8 config B failed; 9 config C failed; 10 config D failed; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 28-policy-smoke.cs <path-to-28-policy-target.cs>

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

return Policy28.Run(args);

sealed class RecordingSink : IDebugEventSink
{
    private readonly object _lock = new();
    private readonly List<LogRecord> _logs = new();
    public void OnEvent(string name) { /* informational stream — ignored */ }
    public void OnLog(LogRecord record) { lock (_lock) _logs.Add(record); }
    public int Count { get { lock (_lock) return _logs.Count; } }
    public IReadOnlyList<LogRecord> SnapshotSince(int fromIndex)
    {
        lock (_lock) { return _logs.GetRange(fromIndex, _logs.Count - fromIndex); }
    }
}

static class Policy28
{
    const string ModuleSubstr = "28-policy-target";
    const string FileHint = "28-policy-target.cs";
    const string Marker = "POLICY_HERE";
    const string LocalName = "v";
    const int ConditionalExpected = 3;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 28-policy-smoke.cs <path-to-28-policy-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"breakpoint : {FileHint}:{markerLine}  (one breakpoint, four policy configs)");

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

        var sink = new RecordingSink();
        DebugSession session;
        try { session = DebugSession.Attach(realPid, sink); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        int code = Drive(session, sink, markerLine);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session, RecordingSink sink, int markerLine)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }

        // --- Config A: CONDITIONAL BREAKPOINT (Condition, Suspend.All) ---------------------------
        var condPolicy = new BreakpointPolicy(Condition: ctx => ReadLocal(ctx, LocalName) == ConditionalExpected);
        int bpA = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, condPolicy);
        if (bpA == 0) { Console.Error.WriteLine($"FALSIFIED (A SetBreakpointAtLine): {FileHint}:{markerLine}."); return 7; }
        Console.WriteLine($"A. conditional   : Condition v == {ConditionalExpected}, Suspend.All …");
        session.Resume();
        StopInfo? stopA = session.WaitForStop(TimeSpan.FromSeconds(20));
        long? vAtA = session.GetLocals().FirstOrDefault(l => l.Name == LocalName).RawValue;
        Console.WriteLine($"               -> stop={(stopA is null ? "null" : stopA.Reason.ToString())}  v={vAtA?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
        if (stopA is null || stopA.Reason != StopReason.Breakpoint || vAtA != ConditionalExpected)
        {
            Console.Error.WriteLine($"FALSIFIED (A): expected Breakpoint with v == {ConditionalExpected}, got stop={stopA?.Reason.ToString() ?? "null"}, v={vAtA}.");
            return 7;
        }
        if (!session.RemoveBreakpoint(bpA)) { Console.Error.WriteLine("FALSIFIED (A→B swap): RemoveBreakpoint(bpA) failed."); return 7; }

        // --- Config B: LOGPOINT (LogMessage, Suspend.None) ---------------------------------------
        int baseB = sink.Count;
        var logPolicy = new BreakpointPolicy(
            LogMessage: ctx => $"v={ReadLocal(ctx, LocalName)?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            Suspend: SuspendPolicy.None);
        int bpB = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, logPolicy);
        if (bpB == 0) { Console.Error.WriteLine("FALSIFIED (B SetBreakpointAtLine)."); return 8; }
        Console.WriteLine("B. logpoint      : LogMessage \"v={v}\", Suspend.None, 2s …");
        session.Resume();
        StopInfo? stopB = session.WaitForStop(TimeSpan.FromSeconds(2));
        IReadOnlyList<LogRecord> logsB = sink.SnapshotSince(baseB);
        Console.WriteLine($"               -> stop={(stopB is null ? "null (timeout, expected)" : stopB.Reason.ToString())}  logs={logsB.Count}  first=\"{(logsB.Count > 0 ? logsB[0].Message : "")}\"");
        bool everyLogMatches = logsB.All(r => !r.IsFault && Regex.IsMatch(r.Message, @"^v=\d+$"));
        if (stopB is not null || logsB.Count < 5 || !everyLogMatches)
        {
            Console.Error.WriteLine($"FALSIFIED (B): expected null stop + >=5 well-formed logpoint lines; stop={stopB?.Reason.ToString() ?? "null"}, logs={logsB.Count}, allMatch={everyLogMatches}.");
            return 8;
        }
        // Stop the running target so we can swap the policy.
        session.Pause();
        StopInfo? pauseB = session.WaitForStop(TimeSpan.FromSeconds(5));
        if (pauseB is null || pauseB.Reason != StopReason.Pause) { Console.Error.WriteLine($"FALSIFIED (B→C Pause): {pauseB?.Reason.ToString() ?? "null"}."); return 8; }
        if (!session.RemoveBreakpoint(bpB)) { Console.Error.WriteLine("FALSIFIED (B→C swap): RemoveBreakpoint(bpB) failed."); return 8; }

        // --- Config C: HIT-COUNT GATED LOGPOINT (HitCount + LogMessage, Suspend.None) ------------
        int baseC = sink.Count;
        var hitLogPolicy = new BreakpointPolicy(
            HitCount: new HitCountGate(HitCountMode.Equals, 3),
            LogMessage: ctx => $"hit-3 v={ReadLocal(ctx, LocalName)?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            Suspend: SuspendPolicy.None);
        int bpC = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, hitLogPolicy);
        if (bpC == 0) { Console.Error.WriteLine("FALSIFIED (C SetBreakpointAtLine)."); return 9; }
        Console.WriteLine("C. hit-count gate: HitCount Equals(3) + LogMessage, Suspend.None, 2s …");
        session.Resume();
        StopInfo? stopC = session.WaitForStop(TimeSpan.FromSeconds(2));
        IReadOnlyList<LogRecord> logsC = sink.SnapshotSince(baseC);
        Console.WriteLine($"               -> stop={(stopC is null ? "null (timeout, expected)" : stopC.Reason.ToString())}  logs={logsC.Count}  (must be exactly 1)  first=\"{(logsC.Count > 0 ? logsC[0].Message : "")}\"");
        if (stopC is not null || logsC.Count != 1 || logsC[0].IsFault || !logsC[0].Message.StartsWith("hit-3 v=", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"FALSIFIED (C): expected null stop + exactly 1 well-formed log; stop={stopC?.Reason.ToString() ?? "null"}, logs={logsC.Count}.");
            return 9;
        }
        session.Pause();
        StopInfo? pauseC = session.WaitForStop(TimeSpan.FromSeconds(5));
        if (pauseC is null || pauseC.Reason != StopReason.Pause) { Console.Error.WriteLine($"FALSIFIED (C→D Pause): {pauseC?.Reason.ToString() ?? "null"}."); return 9; }
        if (!session.RemoveBreakpoint(bpC)) { Console.Error.WriteLine("FALSIFIED (C→D swap): RemoveBreakpoint(bpC) failed."); return 9; }

        // --- Config D: FAULT (Condition throws → ConditionError + fault LogRecord) ---------------
        int baseD = sink.Count;
        var faultPolicy = new BreakpointPolicy(
            Condition: _ => throw new InvalidOperationException("simulated bad condition"));
        int bpD = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, faultPolicy);
        if (bpD == 0) { Console.Error.WriteLine("FALSIFIED (D SetBreakpointAtLine)."); return 10; }
        Console.WriteLine("D. fault         : Condition throws -> ConditionError + IsFault log …");
        session.Resume();
        StopInfo? stopD = session.WaitForStop(TimeSpan.FromSeconds(10));
        IReadOnlyList<LogRecord> logsD = sink.SnapshotSince(baseD);
        Console.WriteLine($"               -> stop={(stopD is null ? "null" : stopD.Reason.ToString())}  faultLogs={logsD.Count(r => r.IsFault)}");
        if (stopD is null || stopD.Reason != StopReason.ConditionError
            || logsD.Count(r => r.IsFault) != 1
            || !logsD.First(r => r.IsFault).Message.Contains("simulated bad condition", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"FALSIFIED (D): expected ConditionError + exactly 1 fault log with the diagnostic; stop={stopD?.Reason.ToString() ?? "null"}, faultLogs={logsD.Count(r => r.IsFault)}.");
            return 10;
        }
        session.Resume();

        Console.WriteLine("\nPROBE 28 PASSED — BreakpointPolicy unifies conditional / logpoint / hit-count / fault as four configs of one type, with policy attached at SetBreakpoint time (ADR-010 Increment 2c).");
        return 0;
    }

    static long? ReadLocal(IEvalContext ctx, string name)
    {
        foreach (LocalValue local in ctx.Locals)
            if (local.Name == name) return local.RawValue;
        return null;
    }

    static int FindMarkerLine(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(Marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"28-policy-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 28 fixture — BreakpointPolicy unification\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"configs          = A conditional, B logpoint, C hit-count-gated logpoint, D fault\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
