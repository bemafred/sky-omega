#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 30 — EXCEPTION-THROUGH-POLICY: BreakpointPolicy at exception stops
// =======================================================================================
//
// The exception-location axis of finding 33 wired to the BreakpointPolicy substrate (finding 38).
// Engine: ArmExceptionFilter(typeName, kind, policy) installs a filter; at the exception stop the
// SAME EvaluatePolicy core as a breakpoint applies — same gates/log/suspend semantics, same
// fault path. The walker is the substrate's CSharpCondition (ADR-010 Increment 7); for this probe
// the identifier `ex` names the in-flight exception object — its member access (e.g. `ex.Code`)
// resolves via TryEvalCurrentExceptionMember rather than TryEvalMemberCall, accomplished by a
// custom IMemberResolver wrapping the session.
//
// Three configs against one target throwing ProbeException(Code=42) caught in a loop:
//   A. CONDITIONAL EX BP   — Condition = "ex.Code == 42", Suspend.All
//                            → surfaces at the first matching first-chance ProbeException.
//   B. EX LOGPOINT         — LogMessage = "caught ex.Code={ex.Code}", Suspend.None, 2s
//                            → never surfaces, sink collects "caught ex.Code=42" repeatedly.
//   C. EX FAULT            — Condition = "ex.Nope == 0" (member doesn't exist on the runtime type)
//                            → resolver returns SetupFailed → walker throws → ConditionError +
//                            IsFault LogRecord (finding 35 tri-state at an exception stop).
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 7 config A; 8 config B; 9 config C; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 30-expolicy-smoke.cs <path-to-30-expolicy-target.cs>

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

return ExPolicy30.Run(args);

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

// Custom IMemberResolver: the substrate compiler resolves `ex.Member` via this wrapper, which
// routes the `ex` identifier to TryEvalCurrentExceptionMember (the in-flight exception). All
// other operands fall through to the session's standard local-member resolution.
sealed class ExceptionAwareResolver : IMemberResolver
{
    const string ExceptionOperand = "ex";
    private readonly DebugSession _session;
    public ExceptionAwareResolver(DebugSession session) => _session = session;

    public EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result)
        => thisLocalName == ExceptionOperand
            ? _session.TryEvalCurrentExceptionMember(memberName, timeout, out result)
            : _session.TryEvalMemberCall(thisLocalName, memberName, timeout, out result);
}

static class ExPolicy30
{
    const string ExpectedType = "ProbeException";
    const string CondMatch    = "ex.Code == 42";
    const string LogTemplate  = "caught ex.Code={ex.Code}";
    const string CondFault    = "ex.Nope == 0";   // member doesn't exist on ProbeException
    const int ExpectedCode = 42;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 30-expolicy-smoke.cs <path-to-30-expolicy-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : exception location = {ExpectedType}; three policy configs (cond / log / fault) via substrate compiler + ExceptionAwareResolver");

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

        int code = Drive(session, sink);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session, RecordingSink sink)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        ExceptionAwareResolver exResolver = new(session);

        // --- Config A: conditional exception breakpoint --------------------------------------------
        BreakpointPolicy condPolicy = session.Compile(
            new BreakpointPolicySpec(Condition: CondMatch, Suspend: SuspendPolicy.All),
            exResolver);
        int filterA = session.ArmExceptionFilter(ExpectedType, ExceptionStopKind.None, condPolicy);
        Console.WriteLine($"A. cond ex bp    : Condition \"{CondMatch}\", Suspend.All …");
        session.Resume();
        StopInfo? stopA = session.WaitForStop(TimeSpan.FromSeconds(20));
        string? typeA = session.GetCurrentExceptionTypeName();
        Console.WriteLine($"               -> stop={(stopA is null ? "null" : stopA.Reason.ToString())}  type={typeA ?? "n/a"}  phase={stopA?.ExceptionKind}");
        if (stopA is null || stopA.Reason != StopReason.Exception || stopA.ExceptionKind != ExceptionStopKind.FirstChance || typeA != ExpectedType)
        {
            Console.Error.WriteLine($"FALSIFIED (A): expected FirstChance {ExpectedType}; got stop={stopA?.Reason.ToString() ?? "null"}, kind={stopA?.ExceptionKind}, type={typeA}.");
            return 7;
        }
        if (!session.RemoveExceptionFilter(filterA)) { Console.Error.WriteLine("FALSIFIED (A→B swap): RemoveExceptionFilter(filterA) failed."); return 7; }

        // --- Config B: exception logpoint -----------------------------------------------------------
        int baseB = sink.Count;
        BreakpointPolicy logPolicy = session.Compile(
            new BreakpointPolicySpec(LogMessage: LogTemplate, Suspend: SuspendPolicy.None),
            exResolver);
        int filterB = session.ArmExceptionFilter(ExpectedType, ExceptionStopKind.None, logPolicy);
        Console.WriteLine($"B. ex logpoint   : LogMessage \"{LogTemplate}\", Suspend.None, 2s …");
        session.Resume();
        StopInfo? stopB = session.WaitForStop(TimeSpan.FromSeconds(2));
        IReadOnlyList<LogRecord> logsB = sink.SnapshotSince(baseB);
        string expectedLine = $"caught ex.Code={ExpectedCode}";
        bool allOk = logsB.Count >= 3 && logsB.All(r => !r.IsFault && r.Message == expectedLine);
        Console.WriteLine($"               -> stop={(stopB is null ? "null (timeout, expected)" : stopB.Reason.ToString())}  logs={logsB.Count}  first=\"{(logsB.Count > 0 ? logsB[0].Message : "")}\"");
        if (stopB is not null || !allOk)
        {
            Console.Error.WriteLine($"FALSIFIED (B): expected null stop + >=3 logs of \"{expectedLine}\"; stop={stopB?.Reason.ToString() ?? "null"}, logs={logsB.Count}.");
            return 8;
        }
        session.Pause();
        StopInfo? pauseB = session.WaitForStop(TimeSpan.FromSeconds(5));
        if (pauseB is null || pauseB.Reason != StopReason.Pause) { Console.Error.WriteLine($"FALSIFIED (B→C Pause): {pauseB?.Reason.ToString() ?? "null"}."); return 8; }
        if (!session.RemoveExceptionFilter(filterB)) { Console.Error.WriteLine("FALSIFIED (B→C swap): RemoveExceptionFilter(filterB) failed."); return 8; }

        // --- Config C: fault path (member doesn't exist on the runtime type) ----------------------
        int baseC = sink.Count;
        BreakpointPolicy faultPolicy = session.Compile(
            new BreakpointPolicySpec(Condition: CondFault),
            exResolver);
        int filterC = session.ArmExceptionFilter(ExpectedType, ExceptionStopKind.None, faultPolicy);
        Console.WriteLine($"C. ex fault      : Condition \"{CondFault}\" (member does not exist), Suspend.All …");
        session.Resume();
        StopInfo? stopC = session.WaitForStop(TimeSpan.FromSeconds(10));
        IReadOnlyList<LogRecord> logsC = sink.SnapshotSince(baseC);
        int faultLogs = logsC.Count(r => r.IsFault);
        Console.WriteLine($"               -> stop={(stopC is null ? "null" : stopC.Reason.ToString())}  faultLogs={faultLogs}");
        if (stopC is null || stopC.Reason != StopReason.ConditionError || faultLogs < 1
            || !logsC.First(r => r.IsFault).Message.Contains("ex.Nope", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"FALSIFIED (C): expected ConditionError + a fault log naming ex.Nope; stop={stopC?.Reason.ToString() ?? "null"}, faultLogs={faultLogs}.");
            return 9;
        }
        session.Resume();

        Console.WriteLine("\nPROBE 30 PASSED — substrate BreakpointPolicySpec drives exception-location condition / logpoint / fault via session.Compile(spec, ExceptionAwareResolver); the probe's local walker is retired (ADR-010 Increment 7).");
        return 0;
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
        string path = Path.Combine(dir, $"30-expolicy-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 30 fixture — exception-through-policy (substrate compiler + custom resolver)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"configs          = A cond ex bp (ex.Code==42), B ex logpoint, C ex fault (ex.Nope)\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
