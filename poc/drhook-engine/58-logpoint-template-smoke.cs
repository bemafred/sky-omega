#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 58 — LOGPOINT TEMPLATE COMPILER (ADR-010 Increment 7 live validation)
// ==========================================================================================
//
// Substrate claim being validated: CSharpCondition.CompileTemplate turns a template like
// "counter={counter} times2={counter*2} plus10={counter+10} even={counter%2==0}" into a typed
// Func<IEvalContext, string> renderer — fragments compile via Roslyn → System.Linq.Expressions
// → Lambda.Compile() so each operator runs at native int-typed speed (no long-flatten); the
// result of each fragment stringifies via Convert.ToString(InvariantCulture). BreakpointPolicySpec
// with LogMessage = template + Suspend.None produces a non-stopping logpoint that emits one
// LogRecord per qualifying hit via IDebugEventSink.OnLog.
//
// Construction: target loops Probe(counter) with counter incrementing 0..N. Probe arms a
// breakpoint at the LOGPOINT_HERE line with the template above (Suspend.None). Each hit fires
// the policy's LogMessage renderer, which evaluates `counter` (typed int identifier),
// `counter*2` and `counter+10` (typed int arithmetic), and `counter%2==0` (typed int modulo +
// equality → bool, stringified "True"/"False") via the substrate's Expression.Compile()-backed
// walker. The auto-resume bypass in DebugSession.WaitForStop's policy-evaluation path keeps the
// target running; no Breakpoint stop surfaces; the sink accumulates LogRecord entries.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 expected null timeout-stop, got a real stop / fewer than 5 logs / malformed template
//   rendering / fault logs; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 58-logpoint-template-smoke.cs <path-to-58-logpoint-template-target.cs>

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

return LogpointTemplate58.Run(args);

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

static class LogpointTemplate58
{
    const string ModuleSubstr = "58-logpoint-template-target";
    const string FileHint     = "58-logpoint-template-target.cs";
    const string Marker       = "LOGPOINT_HERE";
    const string Template     = "counter={counter} times2={counter*2} plus10={counter+10} even={counter%2==0}";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 58-logpoint-template-smoke.cs <path-to-58-logpoint-template-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"logpoint   : template \"{Template}\" at {FileHint}:{markerLine} (Suspend.None)");

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

        // Build the logpoint policy via the substrate's canonical compilation path: a spec with
        // LogMessage + Suspend.None → DebugSession.Compile → BreakpointPolicy with the renderer.
        // Same path the MCP layer uses (Increment 7 deliverable: drhook_step_breakpoint(logMessage)).
        BreakpointPolicy logpoint = session.Compile(new BreakpointPolicySpec(
            LogMessage: Template,
            Suspend:    SuspendPolicy.None));

        int bpId = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, logpoint);
        if (bpId == 0) { Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): {FileHint}:{markerLine}."); return 6; }

        int baseLogs = sink.Count;
        Console.WriteLine($"running    : logpoint armed (id={bpId}); resuming for 3s to accumulate emissions …");
        session.Resume();

        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(3));
        IReadOnlyList<LogRecord> logs = sink.SnapshotSince(baseLogs);
        Console.WriteLine($"               -> stop={(stop is null ? "null (timeout, expected)" : stop.Reason.ToString())}  logs={logs.Count}  first=\"{(logs.Count > 0 ? logs[0].Message : "")}\"  last=\"{(logs.Count > 0 ? logs[^1].Message : "")}\"");

        if (stop is not null)
        {
            Console.Error.WriteLine($"FALSIFIED: expected null timeout-stop (Suspend.None auto-resumes each hit), got {stop.Reason}.");
            return 7;
        }
        if (logs.Count < 5)
        {
            Console.Error.WriteLine($"FALSIFIED: expected >=5 LogRecord entries in the 3s window, got {logs.Count}.");
            return 7;
        }

        // Every log line must match the template's shape: identifier interpolation + int arithmetic
        // (counter*2 and counter+10) + bool fragment (counter%2==0 returns True/False). The
        // substrate's typed Expression.Compile() path is validated by the arithmetic equalities
        // below — int * int → int (no long-flatten), int % int == int → bool, all rendered via
        // Convert.ToString(InvariantCulture).
        Regex shape = new(@"^counter=(\d+) times2=(\d+) plus10=(\d+) even=(True|False)$");
        bool allShapeOk = logs.All(r => !r.IsFault && shape.IsMatch(r.Message));
        // Each line must internally satisfy times2 == 2*counter, plus10 == counter+10, and
        // even == (counter%2 == 0) — proving the typed-arithmetic + bool walker, not just shape.
        bool arithmeticOk = logs.All(r =>
        {
            Match m = shape.Match(r.Message);
            if (!m.Success) return false;
            int counter = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int times2  = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int plus10  = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            bool even   = bool.Parse(m.Groups[4].Value);
            return times2 == counter * 2 && plus10 == counter + 10 && even == (counter % 2 == 0);
        });
        bool anyEvenTrue  = logs.Any(r => r.Message.EndsWith("even=True", StringComparison.Ordinal));
        bool anyEvenFalse = logs.Any(r => r.Message.EndsWith("even=False", StringComparison.Ordinal));
        // Counter values must monotonically increase across emissions (each hit increments).
        var counters = logs
            .Select(r => shape.Match(r.Message))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        bool monotonic = counters.Count > 1 && counters.SequenceEqual(counters.OrderBy(x => x));

        if (!allShapeOk || !arithmeticOk || !anyEvenTrue || !anyEvenFalse || !monotonic)
        {
            Console.Error.WriteLine($"FALSIFIED: shapeOK={allShapeOk}, arithOK={arithmeticOk}, evenTrue={anyEvenTrue}, evenFalse={anyEvenFalse}, monotonic={monotonic}. " +
                $"Template typed-arithmetic walker did not produce correct int*int, int+int, int%int==int outputs.");
            return 7;
        }

        Console.WriteLine($"\nPROBE 58 PASSED — logpoint template \"{Template}\" rendered {logs.Count} well-formed entries; typed int arithmetic (counter*2, counter+10), modulo + equality (counter%2==0 → bool), and Convert.ToString(InvariantCulture) rendering all hold across hits. Substrate Expression.Compile() walker drives logpoints end-to-end (ADR-010 Increment 7).");
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
        string path = Path.Combine(dir, $"58-logpoint-template-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 58 fixture — logpoint template compiler (ADR-010 Increment 7)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"template         = {Template}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
