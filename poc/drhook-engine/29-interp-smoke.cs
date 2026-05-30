#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 29 — Roslyn INTERPOLATION walker: {expr} fragments in logpoint messages
// ============================================================================================
//
// The "one front end, two consumers" convergence from finding 33. The substrate's
// CSharpCondition (see SkyOmega.DrHook.Engine.Expressions) compiles a single Roslyn-parsed
// expression-syntax surface into BOTH a Func<IEvalContext,bool> (conditions) AND a
// Func<IEvalContext,string> (logpoint templates) — the expression-eval core is shared, only the
// outer shape differs. ADR-010 Increment 7 promoted that template path from probe-local code
// into the substrate; this probe migrated off its private CSharp walker (deleted) onto
// session.Compile(spec).
//
// Two configs against ONE target/breakpoint, both built from BreakpointPolicySpec:
//   A. INTERPOLATED LOGPOINT      — LogMessage = "v={v} doubled={2*v}", Suspend.None, 2s ->
//                                   sink collects logs like "v=4 doubled=8".
//   B. CONDITION + INTERPOLATION  — Condition = "v == 3" AND LogMessage = "matched v={v} doubled={2*v}",
//                                   Suspend.All -> at v=3 the line "matched v=3 doubled=6" emits
//                                   AND the stop surfaces. Proves the substrate compiler drives
//                                   both consumers from one spec.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 config A failed (no logs / malformed); 8 config B failed (no stop / wrong v / no/wrong log);
//   0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 29-interp-smoke.cs <path-to-29-interp-target.cs>

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

return Interp29.Run(args);

sealed class RecordingSink : IDebugEventSink
{
    private readonly object _lock = new();
    private readonly List<LogRecord> _logs = new();
    public void OnEvent(string name) { }
    public void OnLog(LogRecord record) { lock (_lock) _logs.Add(record); }
    public int Count { get { lock (_lock) return _logs.Count; } }
    public IReadOnlyList<LogRecord> SnapshotSince(int fromIndex)
    {
        lock (_lock) { return _logs.GetRange(fromIndex, _logs.Count - fromIndex); }
    }
}

static class Interp29
{
    const string ModuleSubstr = "29-interp-target";
    const string FileHint = "29-interp-target.cs";
    const string Marker = "INTERP_HERE";
    const string InterpTemplate = "v={v} doubled={2*v}";
    const string Condition = "v == 3";
    const string MatchedTemplate = "matched v={v} doubled={2*v}";
    const int ConditionalExpected = 3;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 29-interp-smoke.cs <path-to-29-interp-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"breakpoint : {FileHint}:{markerLine}  (substrate-compiled template \"{InterpTemplate}\")");

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

        // --- Config A: PURE INTERPOLATION LOGPOINT (substrate compiler) -------------------------
        int baseA = sink.Count;
        BreakpointPolicy logpoint = session.Compile(new BreakpointPolicySpec(
            LogMessage: InterpTemplate,
            Suspend:    SuspendPolicy.None));
        int bpA = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, logpoint);
        if (bpA == 0) { Console.Error.WriteLine($"FALSIFIED (A SetBreakpointAtLine): {FileHint}:{markerLine}."); return 7; }
        Console.WriteLine($"A. interp logpoint  : LogMessage \"{InterpTemplate}\", Suspend.None, 2s …");
        session.Resume();
        StopInfo? stopA = session.WaitForStop(TimeSpan.FromSeconds(2));
        IReadOnlyList<LogRecord> logsA = sink.SnapshotSince(baseA);
        bool allMatchA = logsA.All(r => !r.IsFault && Regex.IsMatch(r.Message, @"^v=\d+ doubled=\d+$"));
        bool arithmeticCheckA = logsA.All(r =>
        {
            Match m = Regex.Match(r.Message, @"^v=(\d+) doubled=(\d+)$");
            return m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 2
                              == int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        });
        Console.WriteLine($"                 -> stop={(stopA is null ? "null (timeout, expected)" : stopA.Reason.ToString())}  logs={logsA.Count}  first=\"{(logsA.Count > 0 ? logsA[0].Message : "")}\"  shapeOK={allMatchA}  arithOK={arithmeticCheckA}");
        if (stopA is not null || logsA.Count < 5 || !allMatchA || !arithmeticCheckA)
        {
            Console.Error.WriteLine($"FALSIFIED (A): expected null stop + >=5 well-formed interpolated logs; stop={stopA?.Reason.ToString() ?? "null"}, logs={logsA.Count}, shape={allMatchA}, arith={arithmeticCheckA}.");
            return 7;
        }
        session.Pause();
        StopInfo? pauseA = session.WaitForStop(TimeSpan.FromSeconds(5));
        if (pauseA is null || pauseA.Reason != StopReason.Pause) { Console.Error.WriteLine($"FALSIFIED (A→B Pause): {pauseA?.Reason.ToString() ?? "null"}."); return 7; }
        if (!session.RemoveBreakpoint(bpA)) { Console.Error.WriteLine("FALSIFIED (A→B swap): RemoveBreakpoint(bpA) failed."); return 7; }

        // --- Config B: CONDITION + INTERPOLATION FROM ONE SPEC -----------------------------------
        int baseB = sink.Count;
        BreakpointPolicy matchedPolicy = session.Compile(new BreakpointPolicySpec(
            Condition:  Condition,
            LogMessage: MatchedTemplate,
            Suspend:    SuspendPolicy.All));
        int bpB = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, matchedPolicy);
        if (bpB == 0) { Console.Error.WriteLine("FALSIFIED (B SetBreakpointAtLine)."); return 8; }
        Console.WriteLine($"B. cond + interp    : Condition \"{Condition}\" AND LogMessage \"{MatchedTemplate}\", Suspend.All …");
        session.Resume();
        StopInfo? stopB = session.WaitForStop(TimeSpan.FromSeconds(20));
        int? vAtB = session.GetLocals().FirstOrDefault(l => l.Name == "v").RawValue as int?;
        IReadOnlyList<LogRecord> logsB = sink.SnapshotSince(baseB);
        string expected = $"matched v={ConditionalExpected} doubled={2 * ConditionalExpected}";
        bool oneMatch = logsB.Count == 1 && !logsB[0].IsFault && logsB[0].Message == expected;
        Console.WriteLine($"                 -> stop={(stopB is null ? "null" : stopB.Reason.ToString())}  v={vAtB?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}  logs={logsB.Count}  first=\"{(logsB.Count > 0 ? logsB[0].Message : "")}\"");
        if (stopB is null || stopB.Reason != StopReason.Breakpoint || vAtB != ConditionalExpected || !oneMatch)
        {
            Console.Error.WriteLine($"FALSIFIED (B): expected Breakpoint with v={ConditionalExpected} + exactly 1 log \"{expected}\"; stop={stopB?.Reason.ToString() ?? "null"}, v={vAtB}, logs={logsB.Count}.");
            return 8;
        }
        session.Resume();

        Console.WriteLine("\nPROBE 29 PASSED — substrate Roslyn walker drives bool conditions AND interpolated string logpoint messages from one BreakpointPolicySpec; the probe's local walker is retired (ADR-010 Increment 7).");
        return 0;
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
        string path = Path.Combine(dir, $"29-interp-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 29 fixture — substrate-driven interpolation (ADR-010 Increment 7)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"configs          = A interpolation-logpoint, B condition + interpolation from one spec\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
