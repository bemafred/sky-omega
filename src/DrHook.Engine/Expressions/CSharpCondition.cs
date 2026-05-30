using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkyOmega.DrHook.Engine.Expressions;

/// <summary>C#-expression interpreter over a live <see cref="IMemberResolver"/> + <see cref="IEvalContext"/>.
/// The substrate front end that turns a condition string ("box.Size == 42", "value &gt; 3") into a
/// <see cref="Func{IEvalContext, Boolean}"/> predicate. Validated end-to-end by probes 22-25
/// (findings 30/31/32/34). Roslyn parses the expression; this walker resolves identifiers against
/// the stop's locals + arguments, and member access via <see cref="IMemberResolver.TryEvalMemberCall"/>
/// (func-eval of the getter on the operand's runtime type).
///
/// <para>Supported syntax (the subset validated by probes 22-25):</para>
/// <list type="bullet">
///   <item><see cref="LiteralExpressionSyntax"/> — numeric / boolean / string literals.</item>
///   <item><see cref="IdentifierNameSyntax"/> — names a local variable from <see cref="IEvalContext.Locals"/>.</item>
///   <item><see cref="MemberAccessExpressionSyntax"/> — <c>operand.Member</c> where operand is an identifier;
///         resolves via <see cref="IMemberResolver.TryEvalMemberCall"/>.</item>
///   <item><see cref="ParenthesizedExpressionSyntax"/> — passthrough.</item>
///   <item><see cref="PrefixUnaryExpressionSyntax"/> — logical NOT (<c>!expr</c>).</item>
///   <item><see cref="BinaryExpressionSyntax"/> — <c>&amp;&amp; || == != &lt; &gt; &lt;= &gt;=</c>.</item>
/// </list>
///
/// <para>Unsupported syntax throws <see cref="NotSupportedException"/>; identifier or member resolution
/// failures throw <see cref="InvalidOperationException"/>. The <see cref="BreakpointPolicy"/> evaluator
/// (in <see cref="DebugSession.EvaluatePolicy"/>) catches these and surfaces them as
/// <see cref="StopReason.ConditionError"/> with a fault <see cref="LogRecord"/> (finding 35) — a broken
/// condition is never silently treated as false.</para></summary>
internal static class CSharpCondition
{
    /// <summary>Parse a C# expression into a predicate over <see cref="IEvalContext"/>. The
    /// <paramref name="memberResolver"/> is closed over for member-access resolution
    /// (<see cref="IMemberResolver.TryEvalMemberCall"/>); identifier-only conditions don't use it but
    /// the parameter is mandatory for consistency.</summary>
    public static Func<IEvalContext, bool> Compile(string expression, IMemberResolver memberResolver)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(memberResolver);
        ExpressionSyntax tree = SyntaxFactory.ParseExpression(expression);
        return ctx => (bool)Eval(tree, ctx, memberResolver)!;
    }

    /// <summary>Parse a logpoint template into a renderer over <see cref="IEvalContext"/>. The template
    /// is literal text interleaved with <c>{expr}</c> fragments; each fragment is a C# expression
    /// parsed and walked via the same <see cref="Eval"/> as <see cref="Compile"/>. Escape sequences
    /// <c>{{</c> and <c>}}</c> produce literal <c>{</c> and <c>}</c> respectively (matches VS Code DAP
    /// convention). At render time, fragment results are stringified via
    /// <see cref="Convert.ToString(object?, IFormatProvider?)"/> with <see cref="CultureInfo.InvariantCulture"/>;
    /// null results render as the empty string.
    ///
    /// <para>Supported syntax inside fragments is the same subset <see cref="Compile"/> supports:
    /// literals, identifiers (local lookup), member access (via <see cref="IMemberResolver.TryEvalMemberCall"/>),
    /// parenthesized expressions, logical NOT, binary <c>&amp;&amp; || == != &lt; &gt; &lt;= &gt;=</c>.
    /// Arithmetic and format specifiers are NOT supported — out of scope per ADR-010 Increment 7.</para>
    ///
    /// <para>Throws <see cref="ArgumentNullException"/> on null arguments, <see cref="FormatException"/>
    /// on mismatched braces or empty fragments, and propagates any <see cref="NotSupportedException"/>
    /// from <see cref="Eval"/> for unsupported syntax inside a fragment (the substrate's
    /// <see cref="BreakpointPolicy"/> evaluator catches and surfaces these via the existing
    /// <see cref="LogRecord.IsFault"/> path at render time).</para></summary>
    public static Func<IEvalContext, string> CompileTemplate(string template, IMemberResolver memberResolver)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(memberResolver);

        // Parse the template into alternating literal + expression segments. Each "segment" is
        // either a verbatim string (literal) or a compiled expression delegate.
        List<Func<IEvalContext, string>> segments = new();
        StringBuilder literal = new();
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    // Escape sequence: '{{' produces a literal '{'.
                    literal.Append('{');
                    i += 2;
                    continue;
                }
                // Flush accumulated literal text before starting an expression fragment.
                if (literal.Length > 0)
                {
                    string text = literal.ToString();
                    segments.Add(_ => text);
                    literal.Clear();
                }
                // Scan to the matching '}'. We don't support nested '{...}' inside expressions.
                int close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new FormatException($"unmatched '{{' at position {i} in template: \"{template}\"");
                string exprText = template.Substring(i + 1, close - i - 1);
                if (exprText.Length == 0)
                    throw new FormatException($"empty '{{}}' fragment at position {i} in template: \"{template}\"");
                ExpressionSyntax exprTree = SyntaxFactory.ParseExpression(exprText);
                segments.Add(ctx =>
                {
                    object? value = Eval(exprTree, ctx, memberResolver);
                    return value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                });
                i = close + 1;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    // Escape sequence: '}}' produces a literal '}'.
                    literal.Append('}');
                    i += 2;
                    continue;
                }
                throw new FormatException($"unmatched '}}' at position {i} in template: \"{template}\"");
            }
            literal.Append(c);
            i++;
        }
        if (literal.Length > 0)
        {
            string trailing = literal.ToString();
            segments.Add(_ => trailing);
        }

        // Render = concatenate every segment's contribution.
        return ctx =>
        {
            StringBuilder sb = new();
            foreach (Func<IEvalContext, string> seg in segments) sb.Append(seg(ctx));
            return sb.ToString();
        };
    }

    static object? Eval(ExpressionSyntax node, IEvalContext ctx, IMemberResolver memberResolver) => node switch
    {
        LiteralExpressionSyntax lit => lit.Token.Value,
        IdentifierNameSyntax id => ResolveLocal(ctx, id.Identifier.Text),
        MemberAccessExpressionSyntax ma when ma.Kind() == SyntaxKind.SimpleMemberAccessExpression
            => ResolveMember(memberResolver, ma),
        ParenthesizedExpressionSyntax p => Eval(p.Expression, ctx, memberResolver),
        PrefixUnaryExpressionSyntax u when u.Kind() == SyntaxKind.LogicalNotExpression
            => !(bool)Eval(u.Operand, ctx, memberResolver)!,
        BinaryExpressionSyntax bin => ApplyBinary(bin.Kind(), bin, ctx, memberResolver),
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
    static object ResolveMember(IMemberResolver memberResolver, MemberAccessExpressionSyntax ma)
    {
        if (ma.Expression is not IdentifierNameSyntax target)
            throw new NotSupportedException($"member-access operand must be an identifier, got {ma.Expression.Kind()}");
        string thisLocal = target.Identifier.Text;
        string member = ma.Name.Identifier.Text;
        EvalStatus st = memberResolver.TryEvalMemberCall(thisLocal, member, TimeSpan.FromSeconds(10), out ArgumentValue v);
        if (st != EvalStatus.Completed)
            throw new InvalidOperationException($"member eval '{thisLocal}.{member}' did not complete: {st}");
        return v.RawValue ?? throw new InvalidOperationException($"member '{thisLocal}.{member}' has no primitive value");
    }

    static object ApplyBinary(SyntaxKind kind, BinaryExpressionSyntax bin, IEvalContext ctx, IMemberResolver memberResolver)
    {
        if (kind == SyntaxKind.LogicalAndExpression) return (bool)Eval(bin.Left, ctx, memberResolver)! && (bool)Eval(bin.Right, ctx, memberResolver)!;
        if (kind == SyntaxKind.LogicalOrExpression) return (bool)Eval(bin.Left, ctx, memberResolver)! || (bool)Eval(bin.Right, ctx, memberResolver)!;

        long l = ToLong(Eval(bin.Left, ctx, memberResolver));
        long r = ToLong(Eval(bin.Right, ctx, memberResolver));
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
