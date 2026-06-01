#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 60 — FILE-DRIVEN BREAKPOINT MODULE RESOLUTION (ADR-011 D3 smoke follow-up)
// ============================================================================================
//
// Substrate claim: DebugSession.SetBreakpointAtLine(fileHint, line, policy) — the FILE-ONLY
// overload — resolves the owning module by which loaded module's Portable PDB actually
// references the file, NOT by guessing the module from the file-name stem. This fixes the case
// the MCP D3 drain_log smoke hit: Program.cs is compiled into Launch.dll, so the assembly name
// "Launch" differs from the source stem "Program". The old MCP heuristic (ModuleSubstrForFile →
// "Program") could not find a module named "*Program*", so SetBreakpointAtLine returned 0 and the
// launch failed with "Could not set breakpoint". The fix iterates loaded modules and binds on the
// first whose PDB references Program.cs:line — "Launch".
//
// Construction: DebugSession.Launch spawns the BUILT Launch.dll (poc 33-launch-target: Program.cs
// → Launch). After the Debugger.Break setup stop, the probe arms a Suspend.None logpoint at the
// loop body (the PROBE_BREAK line) via the FILE-ONLY overload — passing ONLY the Program.cs path +
// line, no module hint. If the fix works the module is resolved from the PDB, the bp binds (id != 0),
// the 100-iteration loop fires it, and the sink accumulates "i=N v=M" with v == 2*N. Under the old
// stem-guess heuristic the id would be 0 → FALSIFIED-6 (this is the regression the fix removes).
//
// Falsification: 2 dll/source missing or marker not found; 5 no setup Break; 6 SetBreakpointAtLine
//   returned 0 (the fix did NOT resolve the module from the PDB); 7 logpoint suspended (Suspend.None
//   violated) / <5 logs / fault logs / malformed or wrong (v != 2*i) rendering; 0 PASS.
//
// Usage:  [DBGSHIM_PATH=<libdbgshim>] dotnet 60-file-driven-resolution-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return FileDrivenResolution60.Run();

sealed class RecordingSink : IDebugEventSink
{
    private readonly object _lock = new();
    private readonly List<LogRecord> _logs = new();
    public void OnEvent(string name) { /* informational stream — ignored */ }
    public void OnLog(LogRecord record) { lock (_lock) _logs.Add(record); }
    public IReadOnlyList<LogRecord> Snapshot() { lock (_lock) return _logs.ToArray(); }
}

static class FileDrivenResolution60
{
    // Resolved relative to CWD (probe convention: run from poc/drhook-engine). The SOURCE stem is
    // "Program"; the ASSEMBLY is "Launch" — the exact name-mismatch the fix addresses.
    const string SourceRel = "33-launch-target/Program.cs";
    const string DllRel     = "33-launch-target/bin/Debug/net10.0/Launch.dll";
    const string Marker     = "PROBE_BREAK";
    const string Template   = "i={i} v={v}";

    public static int Run()
    {
        string source = Path.GetFullPath(SourceRel);
        string dll    = Path.GetFullPath(DllRel);
        if (!File.Exists(source)) { Console.Error.WriteLine($"FALSIFIED (missing source): {source}. Run from poc/drhook-engine."); return 2; }
        if (!File.Exists(dll))    { Console.Error.WriteLine($"FALSIFIED (missing dll): {dll}. Build 33-launch-target first."); return 2; }

        int markerLine = FindMarkerLine(source);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (marker): '{Marker}' not found in {source}."); return 2; }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"target     : {dll}");
        Console.WriteLine($"source     : Program.cs  (assembly = Launch — name mismatch is the point)");
        Console.WriteLine($"logpoint   : template \"{Template}\" at Program.cs:{markerLine} via FILE-ONLY overload (no module hint)");

        var sink = new RecordingSink();
        DebugSession session;
        try { session = DebugSession.Launch("dotnet", new[] { "exec", dll }, Path.GetDirectoryName(dll), sink); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
        Console.WriteLine($"launched   : DebugSession established (pid {session.ProcessId})");

        int code = Drive(session, sink, source, markerLine);

        WriteFixture(session.ProcessId, code);
        try { session.Dispose(); } catch { /* best effort — Owned target torn down */ }
        return code;
    }

    static int Drive(DebugSession session, RecordingSink sink, string source, int markerLine)
    {
        // Setup stop: Debugger.Break() on line 11 of the target, after the runtime attaches.
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        Console.WriteLine($"setup stop : {setup.Reason} (Debugger.Break)");

        // THE FIX UNDER TEST: file-only overload — pass the Program.cs path + line, NO module hint.
        // Resolution must walk loaded modules and bind on "Launch" (whose PDB references Program.cs).
        BreakpointPolicy logpoint = session.Compile(new BreakpointPolicySpec(
            LogMessage: Template,
            Suspend:    SuspendPolicy.None));

        int bpId = session.SetBreakpointAtLine(source, markerLine, logpoint);
        if (bpId == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine returned 0): file-driven resolution did NOT bind Program.cs:{markerLine} — the module 'Launch' was not found from the PDB. This is the pre-fix regression.");
            return 6;
        }
        Console.WriteLine($"bound      : logpoint id={bpId} — module resolved from PDB by file (the fix works)");

        // Resume: the 100-iteration loop (×20ms ≈ 2s) fires the Suspend.None logpoint each pass and
        // auto-resumes; no Breakpoint stop should surface. The process then exits naturally.
        session.Resume();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(6));
        IReadOnlyList<LogRecord> logs = sink.Snapshot();
        string stopDesc = stop is null ? "null (timeout)" : stop.Reason.ToString();
        Console.WriteLine($"resumed    : stop={stopDesc}  logs={logs.Count}  first=\"{(logs.Count > 0 ? logs[0].Message : "")}\"  last=\"{(logs.Count > 0 ? logs[^1].Message : "")}\"");

        if (stop is not null && stop.Reason == StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED: logpoint suspended the target (got Break) — Suspend.None was violated.");
            return 7;
        }
        if (logs.Count < 5)
        {
            Console.Error.WriteLine($"FALSIFIED: expected >=5 LogRecord entries from the 100-iteration loop, got {logs.Count}.");
            return 7;
        }

        // Shape: "i=N v=M" with no fault, M == 2*N (proves both identifiers resolved against the
        // loop's locals through the resolved module), monotonic i.
        Regex shape = new(@"^i=(\d+) v=(\d+)$");
        bool allShapeOk = logs.All(r => !r.IsFault && shape.IsMatch(r.Message));
        bool arithmeticOk = logs.All(r =>
        {
            Match m = shape.Match(r.Message);
            if (!m.Success) return false;
            int i = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int v = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return v == i * 2;
        });
        var counters = logs.Select(r => shape.Match(r.Message)).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        bool monotonic = counters.Count > 1 && counters.SequenceEqual(counters.OrderBy(x => x));

        if (!allShapeOk || !arithmeticOk || !monotonic)
        {
            Console.Error.WriteLine($"FALSIFIED: shapeOK={allShapeOk}, arithOK(v==2*i)={arithmeticOk}, monotonic={monotonic}.");
            return 7;
        }

        Console.WriteLine($"\nPROBE 60 PASSED — file-only SetBreakpointAtLine resolved the 'Launch' module from Program.cs via its PDB (no module hint), bound logpoint id={bpId}, and rendered {logs.Count} well-formed \"i=N v=M\" entries (v==2*i, monotonic). The ADR-011 D3 drain_log blocker (source-stem ≠ assembly-name) is removed.");
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

    static void WriteFixture(int pid, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"60-file-driven-resolution-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 60 fixture — file-driven breakpoint module resolution (ADR-011 D3 follow-up)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"target           = Program.cs compiled into Launch.dll (source stem != assembly name)\n" +
            $"overload         = SetBreakpointAtLine(fileHint, line, policy)  [file-only, no module hint]\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
