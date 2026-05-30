#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:package Microsoft.CodeAnalysis.CSharp@4.11.0
//
// DrHook.Engine probe 25 — Roslyn member-access WALKER: a fully-parsed "box.Size == 42" condition
// ================================================================================================
//
// Probe 22 parsed conditions over primitive locals ("value == 3"). Probe 23 proved func-eval works
// inside a conditional predicate — but with a HARDCODED predicate. Probe 24 generalized member
// resolution (TryEvalMemberCall: getter resolved on the value's runtime type, no hardcoding). This
// probe joins the three: the Roslyn walker now handles MemberAccessExpressionSyntax by calling
// session.TryEvalMemberCall, so "box.Size == 42" is parsed → walked → func-eval'd → compared, with
// nothing about Box hardcoded. The target's box.Size cycles 40..44; only the Size==42 hit surfaces.
//
// Engine boundary: the walker lives ABOVE the engine (Roslyn stays in the probe; the engine is
// BCL-only). For a member access the walker closes over DebugSession and calls TryEvalMemberCall —
// the same path probe 24 validated.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 conditional stop timed out / wrong reason (the func-eval-in-predicate path failed);
//   8 stopped but box.Size != 42 (walker logic wrong); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 25-member-walker-smoke.cs <path-to-25-member-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SkyOmega.DrHook.Engine;

return MemberWalker25.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

// C#-expression interpreter over a live DebugSession + IEvalContext. Extends probe 22's walker with
// MemberAccessExpressionSyntax: a member access (operand.Member) resolves via TryEvalMemberCall —
// func-eval of the getter on the operand's runtime type. Roslyn parses; we only walk the tree.
static class CSharpCondition
{
    public static Func<IEvalContext, bool> Compile(string expression, DebugSession session)
    {
        ExpressionSyntax tree = SyntaxFactory.ParseExpression(expression);
        return ctx => (bool)Eval(tree, ctx, session)!;
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

    // operand.Member — operand must be a simple identifier naming a local object; Member is a property.
    // The getter is func-eval'd on the operand's runtime type (probe 24's TryEvalMemberCall).
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
            _ => throw new NotSupportedException($"unsupported operator: {kind}")
        };
    }

    static long ToLong(object? o) => Convert.ToInt64(o, CultureInfo.InvariantCulture);
}

static class MemberWalker25
{
    const string ModuleSubstr = "25-member-target";
    const string FileHint = "25-member-target.cs";
    const string Marker = "MEMBER_HERE";
    const string Condition = "box.Size == 42";
    const string ThisLocal = "box";
    const string Member = "Size";
    const int ELEMENT_TYPE_I4 = 0x08;
    const int Expected = 42;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 25-member-walker-smoke.cs <path-to-25-member-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"condition  : \"{Condition}\" (Roslyn-parsed, member func-eval'd) at {FileHint}:{markerLine}");

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

        DebugSession session;
        try { session = DebugSession.Attach(realPid, new NullSink()); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        int code = Drive(session, markerLine);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session, int markerLine)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        // The whole point: the predicate comes from a Roslyn parse of "box.Size == 42". Each hit the
        // walker func-evals box.Size on its runtime type and compares — nothing about Box hardcoded.
        // Policy attaches at SetBreakpoint time (ADR-010 Increment 2c).
        Func<IEvalContext, bool> predicate = CSharpCondition.Compile(Condition, session);
        var policy = new BreakpointPolicy(Condition: predicate);
        if (session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, policy) == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): {FileHint}:{markerLine}.");
            return 6;
        }

        Console.WriteLine($"running    : breakpoint set with Condition policy; resuming until \"{Condition}\" (member func-eval'd each hit) …");
        session.Resume();

        // box.Size cycles 40..44, so the breakpoint hits every iteration — only Size==42 should surface.
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(30));
        if (stop is null) { Console.Error.WriteLine("FALSIFIED: conditional stop timed out (member func-eval in predicate failed)."); return 7; }
        if (stop.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"FALSIFIED: surfaced {stop.Reason}, expected Breakpoint."); return 7; }

        EvalStatus st = session.TryEvalMemberCall(ThisLocal, Member, TimeSpan.FromSeconds(10), out ArgumentValue v);
        Console.WriteLine($"stopped    : condition held — {ThisLocal}.{Member} eval {st}, value={(v.RawValue is { } x ? Convert.ToString(x, CultureInfo.InvariantCulture) : "(n/a)")}");
        if (st != EvalStatus.Completed || v.ElementType != ELEMENT_TYPE_I4 || !Equals(v.RawValue, Expected))
        {
            Console.Error.WriteLine($"FALSIFIED: stopped at {ThisLocal}.{Member}={v.RawValue} (0x{v.ElementType:X2}, {st}), but the condition was {ThisLocal}.{Member}=={Expected}.");
            return 8;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 25 PASSED — fully-parsed member-access conditional \"{Condition}\" stopped exactly when it held ({ThisLocal}.{Member}={Expected}).");
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
        string path = Path.Combine(dir, $"25-member-walker-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 25 fixture — Roslyn member-access conditional breakpoint\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"condition        = {Condition} (Roslyn-parsed; member func-eval'd on runtime type each hit)\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
