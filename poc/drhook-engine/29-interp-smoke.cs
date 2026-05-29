#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:package Microsoft.CodeAnalysis.CSharp@4.11.0
//
// DrHook.Engine probe 29 — Roslyn INTERPOLATION walker: {expr} fragments in logpoint messages
// ============================================================================================
//
// The "one front end, two consumers" convergence from finding 33. Probe 22/25's walker produced
// `Func<IEvalContext, bool>` from a Roslyn-parsed C# expression — used for conditions. This probe
// extends the SAME walker to produce `Func<IEvalContext, string>` from a Roslyn-parsed INTERPOLATED
// string: `$"v={v} doubled={2*v}"` becomes a logpoint renderer. The expression-eval core is shared
// (identifiers, literals, binary ops, member access via TryEvalMemberCall — all of which already
// existed); only the OUTER shape differs (Render vs Compile-bool).
//
// Two configs against ONE target/breakpoint, both built from the same evaluator:
//   A. INTERPOLATED LOGPOINT      — LogMessage parsed from $"v={v} doubled={2*v}",
//                                   Suspend.None, 2s -> sink collects logs like "v=4 doubled=8".
//   B. CONDITION + INTERPOLATION  — Condition parsed from "v == 3" AND LogMessage parsed from
//                                   $"matched v={v} doubled={2*v}", Suspend.All -> at v=3 the line
//                                   "matched v=3 doubled=6" is emitted AND the stop surfaces.
//                                   Proves ONE walker driving both consumers in ONE policy.
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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

// ONE walker, two consumers. Same Eval core used by Compile (-> Func<ctx,bool>) and
// CompileInterpolation (-> Func<ctx,string>). Member access composes via TryEvalMemberCall on a
// session (probe 25), but is not exercised here — pure local-and-arithmetic conditions/messages.
static class CSharp
{
    public static Func<IEvalContext, bool> Compile(string expression, DebugSession session)
    {
        ExpressionSyntax tree = SyntaxFactory.ParseExpression(expression);
        return ctx => (bool)Eval(tree, ctx, session)!;
    }

    public static Func<IEvalContext, string> CompileInterpolation(string template, DebugSession session)
    {
        // Wrap the template as a C# interpolated-string literal and let Roslyn parse it. This gets
        // us escape handling, format specifiers, and the {expr} structure for free.
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
            {
                sb.Append(text.TextToken.ValueText);
            }
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
        string thisLocal = target.Identifier.Text;
        string member = ma.Name.Identifier.Text;
        EvalStatus st = session.TryEvalMemberCall(thisLocal, member, TimeSpan.FromSeconds(10), out ArgumentValue v);
        if (st != EvalStatus.Completed)
            throw new InvalidOperationException($"member eval '{thisLocal}.{member}' did not complete: {st}");
        return v.RawValue ?? throw new InvalidOperationException($"member '{thisLocal}.{member}' has no primitive value");
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
            SyntaxKind.DivideExpression => l / r,
            SyntaxKind.ModuloExpression => l % r,
            _ => throw new NotSupportedException($"unsupported operator: {kind}")
        };
    }

    static long ToLong(object? o) => Convert.ToInt64(o, CultureInfo.InvariantCulture);
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
        Console.WriteLine($"breakpoint : {FileHint}:{markerLine}  (Roslyn-parsed interpolation: $\"{InterpTemplate}\")");

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

        // Migration to ADR-010 Increment 2c: policy attaches to the breakpoint at Set time. Each
        // config Remove+Re-SetBP with the next policy; between Suspend.None and the next config,
        // Pause+WaitForStop to synchronize before swapping (RemoveBreakpoint requires being stopped).

        // --- Config A: PURE INTERPOLATION LOGPOINT (Roslyn-parsed renderer) ---------------------
        int baseA = sink.Count;
        var logRenderer = CSharp.CompileInterpolation(InterpTemplate, session);
        var logpoint = new BreakpointPolicy(LogMessage: logRenderer, Suspend: SuspendPolicy.None);
        int bpA = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, logpoint);
        if (bpA == 0) { Console.Error.WriteLine($"FALSIFIED (A SetBreakpointAtLine): {FileHint}:{markerLine}."); return 7; }
        Console.WriteLine($"A. interp logpoint  : LogMessage parsed from $\"{InterpTemplate}\", Suspend.None, 2s …");
        session.Resume();
        StopInfo? stopA = session.WaitForStop(TimeSpan.FromSeconds(2));
        IReadOnlyList<LogRecord> logsA = sink.SnapshotSince(baseA);
        bool allMatchA = logsA.All(r => !r.IsFault && Regex.IsMatch(r.Message, @"^v=\d+ doubled=\d+$"));
        // Each line must satisfy doubled == 2*v (the interpolation actually evaluated 2*v).
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
        // Pause to swap the policy.
        session.Pause();
        StopInfo? pauseA = session.WaitForStop(TimeSpan.FromSeconds(5));
        if (pauseA is null || pauseA.Reason != StopReason.Pause) { Console.Error.WriteLine($"FALSIFIED (A→B Pause): {pauseA?.Reason.ToString() ?? "null"}."); return 7; }
        if (!session.RemoveBreakpoint(bpA)) { Console.Error.WriteLine("FALSIFIED (A→B swap): RemoveBreakpoint(bpA) failed."); return 7; }

        // --- Config B: CONDITION + INTERPOLATION FROM THE SAME WALKER ---------------------------
        int baseB = sink.Count;
        var matchedPolicy = new BreakpointPolicy(
            Condition: CSharp.Compile(Condition, session),
            LogMessage: CSharp.CompileInterpolation(MatchedTemplate, session),
            Suspend: SuspendPolicy.All);
        int bpB = session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, matchedPolicy);
        if (bpB == 0) { Console.Error.WriteLine("FALSIFIED (B SetBreakpointAtLine)."); return 8; }
        Console.WriteLine($"B. cond + interp    : Condition \"{Condition}\" AND LogMessage parsed from $\"{MatchedTemplate}\", Suspend.All …");
        session.Resume();
        StopInfo? stopB = session.WaitForStop(TimeSpan.FromSeconds(20));
        long? vAtB = session.GetLocals().FirstOrDefault(l => l.Name == "v").RawValue;
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

        Console.WriteLine("\nPROBE 29 PASSED — one Roslyn walker, two consumers: bool conditions AND interpolated string logpoint messages from the same Eval. Policy attaches at SetBreakpoint time (ADR-010 Increment 2c).");
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
            "# DrHook.Engine probe 29 fixture — Roslyn interpolation walker (one front end, two consumers)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"configs          = A interpolation-logpoint, B condition + interpolation from same walker\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
