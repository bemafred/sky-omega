#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:package Microsoft.CodeAnalysis.CSharp@4.11.0
//
// DrHook.Engine probe 30 — EXCEPTION-THROUGH-POLICY: BreakpointPolicy at exception stops
// =======================================================================================
//
// The exception-location axis of finding 33 wired to the BreakpointPolicy substrate (finding 38).
// Engine: DebugSession.WaitForExceptionPolicyStop(typeName, policy, timeout) filters Exception stops
// by type then applies the SAME EvaluatePolicy core as WaitForPolicyStop — same gates/log/suspend
// semantics, same fault path. The walker special-cases the `ex` operand: `ex.X` resolves via
// TryEvalCurrentExceptionMember (probe 27) instead of TryEvalMemberCall on a local.
//
// Three configs against one target throwing ProbeException(Code=42) caught in a loop:
//   A. CONDITIONAL EX BP   — Condition parsed from "ex.Code == 42", Suspend.All
//                            → surfaces at the first matching first-chance ProbeException.
//   B. EX LOGPOINT         — LogMessage parsed from $"caught ex.Code={ex.Code}", Suspend.None, 2s
//                            → never surfaces, sink collects "caught ex.Code=42" repeatedly.
//   C. EX FAULT            — Condition parsed from "ex.Nope == 0" (member doesn't exist on the
//                            runtime type) → the walker throws on resolution failure →
//                            ConditionError + IsFault LogRecord (finding 35 tri-state, at an
//                            exception stop). Proves the fault path works for exception location too.
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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

// Same Eval core as probe 29, with one new special case: the identifier `ex` as the operand of a
// member access routes to TryEvalCurrentExceptionMember (probe 27) instead of TryEvalMemberCall.
// `ex` stands for the in-flight exception object; the engine sources it via GetCurrentException at
// the stop, so the walker just names the convention.
static class CSharp
{
    public const string ExceptionOperand = "ex";

    public static Func<IEvalContext, bool> Compile(string expression, DebugSession session)
    {
        ExpressionSyntax tree = SyntaxFactory.ParseExpression(expression);
        return ctx => (bool)Eval(tree, ctx, session)!;
    }

    public static Func<IEvalContext, string> CompileInterpolation(string template, DebugSession session)
    {
        ExpressionSyntax tree = SyntaxFactory.ParseExpression("$\"" + template + "\"");
        if (tree is not InterpolatedStringExpressionSyntax interp)
            throw new ArgumentException($"not an interpolated string: {template}", nameof(template));
        return ctx => Render(interp, ctx, session);
    }

    static string Render(InterpolatedStringExpressionSyntax tree, IEvalContext ctx, DebugSession session)
    {
        var sb = new StringBuilder();
        foreach (InterpolatedStringContentSyntax part in tree.Contents)
        {
            if (part is InterpolatedStringTextSyntax text)
                sb.Append(text.TextToken.ValueText);
            else if (part is InterpolationSyntax interpolation)
            {
                object? value = Eval(interpolation.Expression, ctx, session);
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            }
        }
        return sb.ToString();
    }

    static object? Eval(ExpressionSyntax node, IEvalContext ctx, DebugSession session) => node switch
    {
        LiteralExpressionSyntax lit => lit.Token.Value,
        IdentifierNameSyntax id => ResolveLocal(ctx, id.Identifier.Text),
        MemberAccessExpressionSyntax ma when ma.Kind() == SyntaxKind.SimpleMemberAccessExpression
            => ResolveMember(session, ma),
        ParenthesizedExpressionSyntax p => Eval(p.Expression, ctx, session),
        PrefixUnaryExpressionSyntax u when u.Kind() == SyntaxKind.LogicalNotExpression => !(bool)Eval(u.Operand, ctx, session)!,
        BinaryExpressionSyntax bin => ApplyBinary(bin.Kind(), bin, ctx, session),
        _ => throw new NotSupportedException($"unsupported expression: {node.Kind()}")
    };

    static object ResolveLocal(IEvalContext ctx, string name)
    {
        foreach (LocalValue l in ctx.Locals)
            if (l.Name == name)
                return l.RawValue ?? throw new InvalidOperationException($"local '{name}' has no primitive value");
        throw new InvalidOperationException($"local '{name}' not found at this stop");
    }

    static object ResolveMember(DebugSession session, MemberAccessExpressionSyntax ma)
    {
        if (ma.Expression is not IdentifierNameSyntax target)
            throw new NotSupportedException($"member-access operand must be an identifier, got {ma.Expression.Kind()}");
        string operand = target.Identifier.Text;
        string member = ma.Name.Identifier.Text;

        // The convention: `ex` is the in-flight exception object at an exception stop.
        EvalStatus st;
        ArgumentValue v;
        if (operand == ExceptionOperand)
            st = session.TryEvalCurrentExceptionMember(member, TimeSpan.FromSeconds(10), out v);
        else
            st = session.TryEvalMemberCall(operand, member, TimeSpan.FromSeconds(10), out v);

        if (st != EvalStatus.Completed)
            throw new InvalidOperationException($"member eval '{operand}.{member}' did not complete: {st}");
        return v.RawValue ?? throw new InvalidOperationException($"member '{operand}.{member}' has no primitive value");
    }

    static object ApplyBinary(SyntaxKind kind, BinaryExpressionSyntax bin, IEvalContext ctx, DebugSession session)
    {
        if (kind == SyntaxKind.LogicalAndExpression) return (bool)Eval(bin.Left, ctx, session)! && (bool)Eval(bin.Right, ctx, session)!;
        if (kind == SyntaxKind.LogicalOrExpression) return (bool)Eval(bin.Left, ctx, session)! || (bool)Eval(bin.Right, ctx, session)!;

        long l = ToLong(Eval(bin.Left, ctx, session));
        long r = ToLong(Eval(bin.Right, ctx, session));
        return kind switch
        {
            SyntaxKind.EqualsExpression => l == r,
            SyntaxKind.NotEqualsExpression => l != r,
            SyntaxKind.GreaterThanExpression => l > r,
            SyntaxKind.LessThanExpression => l < r,
            SyntaxKind.GreaterThanOrEqualExpression => l >= r,
            SyntaxKind.LessThanOrEqualExpression => l <= r,
            SyntaxKind.AddExpression => l + r,
            SyntaxKind.SubtractExpression => l - r,
            SyntaxKind.MultiplyExpression => l * r,
            _ => throw new NotSupportedException($"unsupported operator: {kind}")
        };
    }

    static long ToLong(object? o) => Convert.ToInt64(o, CultureInfo.InvariantCulture);
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
        Console.WriteLine($"plan       : exception location = {ExpectedType}; three policy configs (cond / log / fault)");

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
        session.Resume();

        // --- Config A: conditional exception breakpoint --------------------------------------------
        var condPolicy = new BreakpointPolicy(Condition: CSharp.Compile(CondMatch, session));
        Console.WriteLine($"A. cond ex bp    : Condition \"{CondMatch}\", Suspend.All …");
        StopInfo? stopA = session.WaitForExceptionPolicyStop(ExpectedType, condPolicy, TimeSpan.FromSeconds(20));
        string? typeA = session.GetCurrentExceptionTypeName();
        Console.WriteLine($"               -> stop={(stopA is null ? "null" : stopA.Reason.ToString())}  type={typeA ?? "n/a"}  phase={stopA?.ExceptionKind}");
        if (stopA is null || stopA.Reason != StopReason.Exception || stopA.ExceptionKind != ExceptionStopKind.FirstChance || typeA != ExpectedType)
        {
            Console.Error.WriteLine($"FALSIFIED (A): expected FirstChance {ExpectedType}; got stop={stopA?.Reason.ToString() ?? "null"}, kind={stopA?.ExceptionKind}, type={typeA}.");
            return 7;
        }
        session.Resume();

        // --- Config B: exception logpoint -----------------------------------------------------------
        int baseB = sink.Count;
        var logPolicy = new BreakpointPolicy(
            LogMessage: CSharp.CompileInterpolation(LogTemplate, session),
            Suspend: SuspendPolicy.None);
        Console.WriteLine($"B. ex logpoint   : LogMessage parsed from $\"{LogTemplate}\", Suspend.None, 2s …");
        StopInfo? stopB = session.WaitForExceptionPolicyStop(ExpectedType, logPolicy, TimeSpan.FromSeconds(2));
        IReadOnlyList<LogRecord> logsB = sink.SnapshotSince(baseB);
        string expectedLine = $"caught ex.Code={ExpectedCode}";
        bool allOk = logsB.Count >= 3 && logsB.All(r => !r.IsFault && r.Message == expectedLine);
        Console.WriteLine($"               -> stop={(stopB is null ? "null (timeout, expected)" : stopB.Reason.ToString())}  logs={logsB.Count}  first=\"{(logsB.Count > 0 ? logsB[0].Message : "")}\"");
        if (stopB is not null || !allOk)
        {
            Console.Error.WriteLine($"FALSIFIED (B): expected null stop + >=3 logs of \"{expectedLine}\"; stop={stopB?.Reason.ToString() ?? "null"}, logs={logsB.Count}.");
            return 8;
        }

        // --- Config C: fault path (member doesn't exist on the runtime type) ----------------------
        int baseC = sink.Count;
        var faultPolicy = new BreakpointPolicy(Condition: CSharp.Compile(CondFault, session));
        Console.WriteLine($"C. ex fault      : Condition \"{CondFault}\" (member does not exist), Suspend.All …");
        StopInfo? stopC = session.WaitForExceptionPolicyStop(ExpectedType, faultPolicy, TimeSpan.FromSeconds(10));
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

        Console.WriteLine("\nPROBE 30 PASSED — BreakpointPolicy drives the EXCEPTION location: type filter + condition / logpoint / fault all compose with the same policy substrate.");
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
            "# DrHook.Engine probe 30 fixture — exception-through-policy (BreakpointPolicy at exception stops)\n" +
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
