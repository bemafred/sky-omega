#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:package Microsoft.CodeAnalysis.CSharp@4.11.0
//
// DrHook.Engine probe 22 — conditional breakpoints with a STANDARD C# condition (Roslyn front end)
// ================================================================================================
//
// The payoff of the whole eval thread: a breakpoint with a condition the LLM writes in ordinary
// C#. Roslyn parses "value == 3"; a tiny tree-walk interpreter evaluates it against the engine's
// IEvalContext (the frame's named locals); DebugSession.WaitForConditionalStop stops only when it
// holds. This first slice is conditions over PRIMITIVE LOCALS (no member access / func-eval yet).
//
// The breakpoint is unconditional (marks WHERE); the predicate decides WHETHER. The target's
// `value` cycles 0..6, so an unconditional breakpoint hits every iteration — only `value == 3`
// should surface.
//
// Falsification: 2 usage/marker; 3 no READY; 4 attach; 5 no setup Break; 6 SetBreakpointAtLine;
//   7 conditional stop timed out; 8 stopped but value != 3 (condition logic wrong); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 22-conditional-bp-smoke.cs <path-to-22-cond-target.cs>

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

return Conditional22.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

// Minimal C#-expression interpreter over IEvalContext: literals, local identifiers, comparisons,
// boolean/relational operators. Roslyn does the parsing — we only walk the tree. (Member access /
// method calls — which need func-eval — are the next increment.)
static class CSharpCondition
{
    public static Func<IEvalContext, bool> Compile(string expression)
    {
        ExpressionSyntax tree = SyntaxFactory.ParseExpression(expression);
        return ctx => (bool)Eval(tree, ctx)!;
    }

    static object? Eval(ExpressionSyntax node, IEvalContext ctx) => node switch
    {
        LiteralExpressionSyntax lit => lit.Token.Value,
        IdentifierNameSyntax id => ResolveLocal(ctx, id.Identifier.Text),
        ParenthesizedExpressionSyntax p => Eval(p.Expression, ctx),
        PrefixUnaryExpressionSyntax u when u.Kind() == SyntaxKind.LogicalNotExpression => !(bool)Eval(u.Operand, ctx)!,
        BinaryExpressionSyntax bin => ApplyBinary(bin.Kind(), bin, ctx),
        _ => throw new NotSupportedException($"unsupported expression: {node.Kind()}")
    };

    static object ResolveLocal(IEvalContext ctx, string name)
    {
        foreach (LocalValue l in ctx.Locals)
            if (l.Name == name)
                return l.RawValue ?? throw new InvalidOperationException($"local '{name}' has no primitive value");
        throw new InvalidOperationException($"local '{name}' not found at this stop");
    }

    static object ApplyBinary(SyntaxKind kind, BinaryExpressionSyntax bin, IEvalContext ctx)
    {
        if (kind == SyntaxKind.LogicalAndExpression) return (bool)Eval(bin.Left, ctx)! && (bool)Eval(bin.Right, ctx)!;
        if (kind == SyntaxKind.LogicalOrExpression) return (bool)Eval(bin.Left, ctx)! || (bool)Eval(bin.Right, ctx)!;

        long l = ToLong(Eval(bin.Left, ctx));
        long r = ToLong(Eval(bin.Right, ctx));
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

static class Conditional22
{
    const string ModuleSubstr = "22-cond-target";
    const string FileHint = "22-cond-target.cs";
    const string Marker = "COND_HERE";
    const string Condition = "value == 3";
    const string LocalName = "value";
    const int Expected = 3;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 22-conditional-bp-smoke.cs <path-to-22-cond-target.cs>");
            return 2;
        }

        int markerLine = FindMarkerLine(args[0]);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"condition  : \"{Condition}\" at {FileHint}:{markerLine}");

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
        // ADR-010 Increment 2c: policy attaches to the breakpoint at Set time; plain WaitForStop
        // drives the wait, with the substrate auto-resuming non-matching hits internally.
        Func<IEvalContext, bool> predicate = CSharpCondition.Compile(Condition);
        var policy = new BreakpointPolicy(Condition: predicate);
        if (session.SetBreakpointAtLine(ModuleSubstr, FileHint, markerLine, policy) == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (SetBreakpointAtLine): {FileHint}:{markerLine}.");
            return 6;
        }

        Console.WriteLine($"running    : breakpoint set with Condition policy; resuming until \"{Condition}\" …");
        session.Resume();

        // The breakpoint hits every iteration (value cycles 0..6); only value==3 should surface.
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(20));
        if (stop is null) { Console.Error.WriteLine("FALSIFIED: conditional stop timed out."); return 7; }
        if (stop.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"FALSIFIED: surfaced {stop.Reason}, expected Breakpoint."); return 7; }

        long? value = session.GetLocals().FirstOrDefault(l => l.Name == LocalName).RawValue;
        Console.WriteLine($"stopped    : condition held — {LocalName} = {(value?.ToString(CultureInfo.InvariantCulture) ?? "(n/a)")}");
        if (value != Expected)
        {
            Console.Error.WriteLine($"FALSIFIED: stopped at {LocalName}={value}, but the condition was {LocalName}=={Expected}.");
            return 8;
        }

        session.Resume();
        Console.WriteLine($"\nPROBE 22 PASSED — standard-C# conditional breakpoint \"{Condition}\" stopped exactly when it held ({LocalName}={Expected}).");
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
        string path = Path.Combine(dir, $"22-conditional-bp-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 22 fixture — conditional breakpoint (Roslyn front end)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"condition        = {Condition}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
