using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkyOmega.DrHook.Engine.Expressions;

/// <summary>C#-expression compiler over a live <see cref="IMemberResolver"/> + <see cref="IEvalContext"/>.
/// Roslyn parses the expression once; this walker translates the syntax tree into a typed
/// <see cref="System.Linq.Expressions.Expression"/> tree, which <see cref="LambdaExpression.Compile"/>
/// emits as IL into a real delegate. Conditions therefore evaluate at JIT'd-delegate speed,
/// preserving C# operator semantics (typed promotion, short-circuit logic, native arithmetic)
/// without per-hit interpretation. Validated end-to-end by probes 22-25 (findings 30/31/32/34)
/// for conditions, probes 29/30 for templates, and probe 58 for the logpoint surface.
///
/// <para>Scope of the translator (syntax kinds supported):</para>
/// <list type="bullet">
///   <item><see cref="LiteralExpressionSyntax"/> — numeric / boolean / string / char literals,
///         emitted as <c>Expression.Constant</c> with Roslyn's typed token value.</item>
///   <item><see cref="IdentifierNameSyntax"/> — resolves to a local via <see cref="IEvalContext.Locals"/>;
///         the local's CLR type is read from <see cref="LocalValue.RawValue"/>'s runtime type at
///         the schema-bind point, so the operand becomes a typed Expression node.</item>
///   <item><see cref="MemberAccessExpressionSyntax"/> — <c>operand.Member</c> where operand is an identifier;
///         resolves at evaluation time via <see cref="IMemberResolver.TryEvalMemberCall"/>. The
///         walker emits a typed unboxing convert based on the binary partner's static type.</item>
///   <item><see cref="ParenthesizedExpressionSyntax"/> — passthrough.</item>
///   <item><see cref="PrefixUnaryExpressionSyntax"/> — logical NOT (<c>!expr</c>) and unary minus.</item>
///   <item><see cref="BinaryExpressionSyntax"/> — full arithmetic <c>+ - * / %</c>, comparison
///         <c>== != &lt; &gt; &lt;= &gt;=</c>, and short-circuit logical <c>&amp;&amp; ||</c>;
///         operand types widened to a common numeric type via <see cref="Expression.Convert(Expression, Type)"/>.</item>
/// </list>
///
/// <para>Schema acquisition: the very first invocation of the returned delegate snapshots the CLR
/// types of each local from <paramref name="_"/>'s <see cref="LocalValue.RawValue"/> (typed via the
/// substrate's <see cref="Interop.Variables.ReifyPrimitive"/> per CorElementType). The walker is
/// then run against this schema, the typed <see cref="LambdaExpression"/> is compiled to IL once,
/// and subsequent hits invoke the cached delegate at native speed.</para>
///
/// <para>Failures: malformed templates throw <see cref="FormatException"/> at compile-call;
/// unsupported syntax throws <see cref="NotSupportedException"/> at first-hit compilation;
/// missing locals or failed member resolution throw <see cref="InvalidOperationException"/> at
/// evaluation. The <see cref="BreakpointPolicy"/> evaluator in
/// <see cref="DebugSession.WaitForStop(System.TimeSpan)"/>'s policy path catches these and surfaces
/// them via <see cref="StopReason.ConditionError"/> and a fault <see cref="LogRecord"/>
/// (finding 35) — a broken condition is never silently treated as false.</para></summary>
internal static class CSharpCondition
{
    /// <summary>Parse a C# expression into a typed predicate. Returns a delegate that, on first
    /// call, JIT-compiles the typed tree against the call-site's local schema and caches the
    /// result; subsequent calls hit the cached IL-emitted delegate.</summary>
    public static Func<IEvalContext, bool> Compile(string expression, IMemberResolver memberResolver)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(memberResolver);
        ExpressionSyntax tree = SyntaxFactory.ParseExpression(expression);
        ThrowOnDiagnostics(tree, $"condition expression: \"{expression}\"");
        Func<IEvalContext, bool>? cached = null;
        return ctx =>
        {
            cached ??= CompileToDelegate<bool>(tree, Schema.From(ctx), memberResolver);
            return cached(ctx);
        };
    }

    /// <summary>Parse a logpoint template into a typed renderer. The template is literal text
    /// interleaved with <c>{expr}</c> fragments; each fragment is a C# expression compiled by the
    /// same translator as <see cref="Compile"/>. Escape sequences <c>{{</c> and <c>}}</c> produce
    /// literal <c>{</c> and <c>}</c> respectively (matching VS Code's DAP logpoint convention).
    /// Roslyn parses the entire template as a verbatim interpolated string — there is no
    /// hand-rolled brace scanner, so any C# expression Roslyn accepts inside a fragment works.</summary>
    public static Func<IEvalContext, string> CompileTemplate(string template, IMemberResolver memberResolver)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(memberResolver);

        // Pre-validate the brace structure so we can surface the well-known error messages the
        // upstream tests pin (unmatched, empty fragment) without depending on Roslyn diagnostic text.
        ValidateBraceStructure(template);

        // Wrap as a verbatim interpolated string so Roslyn parses fragments natively via the C# grammar.
        // Verbatim form (`$@"..."`) escapes embedded `"` by doubling; brace literals already use {{/}}
        // in the template, which is Roslyn's own escape convention.
        string quoted = "$@\"" + template.Replace("\"", "\"\"") + "\"";
        ExpressionSyntax tree = SyntaxFactory.ParseExpression(quoted);
        ThrowOnDiagnostics(tree, $"template: \"{template}\"");
        if (tree is not InterpolatedStringExpressionSyntax interp)
            throw new FormatException($"template did not parse as an interpolated string: \"{template}\"");

        Func<IEvalContext, string>? cached = null;
        return ctx =>
        {
            cached ??= CompileTemplateToDelegate(interp, Schema.From(ctx), memberResolver);
            return cached(ctx);
        };
    }

    // ─── Schema (per-local CLR type, snapshot at first invocation) ────────────────────────────

    private sealed class Schema
    {
        public IReadOnlyDictionary<string, Type> Locals { get; }
        public IReadOnlyDictionary<string, Type> Arguments { get; }

        private Schema(IReadOnlyDictionary<string, Type> locals, IReadOnlyDictionary<string, Type> args)
        { Locals = locals; Arguments = args; }

        public static Schema From(IEvalContext ctx)
        {
            Dictionary<string, Type> locals = new(StringComparer.Ordinal);
            foreach (LocalValue l in ctx.Locals)
                locals[l.Name] = TypeOf(l.RawValue, l.ElementType);
            Dictionary<string, Type> args = new(StringComparer.Ordinal);
            int i = 0;
            foreach (ArgumentValue a in ctx.Arguments)
                args["arg" + i++] = TypeOf(a.RawValue, a.ElementType);
            return new Schema(locals, args);
        }

        private static Type TypeOf(object? rawValue, int elementType)
            => rawValue?.GetType() ?? ElementTypeToClrType(elementType) ?? typeof(object);
    }

    private static Type? ElementTypeToClrType(int elementType) => elementType switch
    {
        0x02 /* BOOLEAN */ => typeof(bool),
        0x03 /* CHAR    */ => typeof(char),
        0x04 /* I1      */ => typeof(sbyte),
        0x05 /* U1      */ => typeof(byte),
        0x06 /* I2      */ => typeof(short),
        0x07 /* U2      */ => typeof(ushort),
        0x08 /* I4      */ => typeof(int),
        0x09 /* U4      */ => typeof(uint),
        0x0A /* I8      */ => typeof(long),
        0x0B /* U8      */ => typeof(ulong),
        0x0C /* R4      */ => typeof(float),
        0x0D /* R8      */ => typeof(double),
        0x0E /* STRING  */ => typeof(string),
        0x12 /* CLASS   */ => typeof(object),
        0x1C /* OBJECT  */ => typeof(object),
        _                  => null
    };

    // ─── Compile (single expression → typed delegate) ─────────────────────────────────────────

    private static Func<IEvalContext, T> CompileToDelegate<T>(ExpressionSyntax tree, Schema schema, IMemberResolver resolver)
    {
        ParameterExpression ctxParam = Expression.Parameter(typeof(IEvalContext), "ctx");
        TranslationContext tc = new(ctxParam, schema, resolver);
        Expression body = Build(tree, tc);
        body = Coerce(body, typeof(T));
        return Expression.Lambda<Func<IEvalContext, T>>(body, ctxParam).Compile();
    }

    private static Func<IEvalContext, string> CompileTemplateToDelegate(InterpolatedStringExpressionSyntax interp, Schema schema, IMemberResolver resolver)
    {
        ParameterExpression ctxParam = Expression.Parameter(typeof(IEvalContext), "ctx");
        TranslationContext tc = new(ctxParam, schema, resolver);
        // Build each content node as a Func<IEvalContext, string>; render = concatenate.
        List<Func<IEvalContext, string>> segments = new();
        foreach (InterpolatedStringContentSyntax content in interp.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    // Roslyn's TextToken.ValueText for interpolated-string content keeps {{ and }}
                    // as-is (only the quote escape "" → " is performed); we unescape brace pairs
                    // here to match the user-facing template convention validated by tests.
                    string literal = text.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}");
                    segments.Add(_ => literal);
                    break;
                case InterpolationSyntax frag:
                    Expression fragmentBody = Build(frag.Expression, tc);
                    // Coerce the fragment value to object then stringify via Convert.ToString(InvariantCulture);
                    // null renders as the empty string (logpoint convention).
                    fragmentBody = Coerce(fragmentBody, typeof(object));
                    Func<IEvalContext, object?> fragmentDelegate =
                        Expression.Lambda<Func<IEvalContext, object?>>(fragmentBody, ctxParam).Compile();
                    segments.Add(c =>
                    {
                        object? value = fragmentDelegate(c);
                        return value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    });
                    break;
                default:
                    throw new NotSupportedException($"unsupported template content: {content.Kind()}");
            }
        }
        return ctx =>
        {
            StringBuilder sb = new();
            foreach (Func<IEvalContext, string> seg in segments) sb.Append(seg(ctx));
            return sb.ToString();
        };
    }

    // ─── Translator (Roslyn ExpressionSyntax → System.Linq.Expressions.Expression) ────────────

    private sealed record TranslationContext(ParameterExpression CtxParam, Schema Schema, IMemberResolver Resolver);

    private static Expression Build(ExpressionSyntax node, TranslationContext tc) => node switch
    {
        LiteralExpressionSyntax lit => BuildLiteral(lit),
        IdentifierNameSyntax id     => BuildIdentifier(id, tc),
        MemberAccessExpressionSyntax ma when ma.Kind() == SyntaxKind.SimpleMemberAccessExpression
            => BuildMemberAccess(ma, tc),
        ParenthesizedExpressionSyntax p => Build(p.Expression, tc),
        PrefixUnaryExpressionSyntax u when u.Kind() == SyntaxKind.LogicalNotExpression
            => Expression.Not(Coerce(Build(u.Operand, tc), typeof(bool))),
        PrefixUnaryExpressionSyntax u when u.Kind() == SyntaxKind.UnaryMinusExpression
            => Expression.Negate(Build(u.Operand, tc)),
        BinaryExpressionSyntax bin => BuildBinary(bin, tc),
        _ => throw new NotSupportedException($"unsupported expression: {node.Kind()}")
    };

    private static Expression BuildLiteral(LiteralExpressionSyntax lit)
    {
        // Roslyn's Token.Value is the typed boxed literal value (int 42, double 3.14, bool true,
        // string "x", char 'c', or null). Carry that type into the Expression tree.
        object? value = lit.Token.Value;
        Type type = value?.GetType() ?? typeof(object);
        return Expression.Constant(value, type);
    }

    private static Expression BuildIdentifier(IdentifierNameSyntax id, TranslationContext tc)
    {
        string name = id.Identifier.Text;
        // If the local is in the schema, emit a typed unbox so binary ops see the real CLR type.
        // Otherwise emit an untyped call: ReadLocalRaw throws at runtime, but only if reached
        // (short-circuit logical operators may guard the call). The translator must not pre-empt
        // the runtime — that breaks `value == 3 && missing == 1` when value != 3.
        Expression rawCall = Expression.Call(ReadLocalRawMethod, tc.CtxParam, Expression.Constant(name));
        if (tc.Schema.Locals.TryGetValue(name, out Type? clrType) && clrType != typeof(object))
            return Expression.Convert(rawCall, clrType);
        return rawCall;
    }

    private static Expression BuildMemberAccess(MemberAccessExpressionSyntax ma, TranslationContext tc)
    {
        if (ma.Expression is not IdentifierNameSyntax target)
            throw new NotSupportedException($"member-access operand must be an identifier, got {ma.Expression.Kind()}");
        // The return type is only knowable after the func-eval succeeds at hit time; emit as object
        // and let the binary-coercion path narrow to the partner operand's type. For standalone use
        // (e.g. as a bool predicate body), the outer Coerce-to-T at lambda compile widens via
        // Expression.Convert(object→bool), which unboxes correctly when the resolver returns a bool.
        return Expression.Call(
            ResolveMemberMethod,
            Expression.Constant(tc.Resolver),
            Expression.Constant(target.Identifier.Text),
            Expression.Constant(ma.Name.Identifier.Text));
    }

    private static Expression BuildBinary(BinaryExpressionSyntax bin, TranslationContext tc)
    {
        SyntaxKind kind = bin.Kind();
        // Short-circuit logical operators stay bool-typed end-to-end.
        if (kind == SyntaxKind.LogicalAndExpression || kind == SyntaxKind.LogicalOrExpression)
        {
            Expression bl = Coerce(Build(bin.Left, tc), typeof(bool));
            Expression br = Coerce(Build(bin.Right, tc), typeof(bool));
            return kind == SyntaxKind.LogicalAndExpression
                ? Expression.AndAlso(bl, br)
                : Expression.OrElse(bl, br);
        }

        Expression left  = Build(bin.Left, tc);
        Expression right = Build(bin.Right, tc);
        (left, right) = UnifyNumericOperandTypes(left, right);
        return Expression.MakeBinary(MapBinaryKind(kind), left, right);
    }

    private static (Expression, Expression) UnifyNumericOperandTypes(Expression left, Expression right)
    {
        if (left.Type == right.Type) return (left, right);
        // If one side is object (e.g. unresolved member access), narrow it to the other side's type.
        if (left.Type == typeof(object))  return (Expression.Convert(left,  right.Type), right);
        if (right.Type == typeof(object)) return (left,  Expression.Convert(right, left.Type));
        // Otherwise widen to a common numeric type via standard C# promotion (double > float > long > int).
        Type common = WidenedNumericType(left.Type, right.Type);
        if (left.Type  != common) left  = Expression.Convert(left,  common);
        if (right.Type != common) right = Expression.Convert(right, common);
        return (left, right);
    }

    private static Type WidenedNumericType(Type a, Type b)
    {
        if (a == typeof(double) || b == typeof(double)) return typeof(double);
        if (a == typeof(float)  || b == typeof(float))  return typeof(float);
        if (a == typeof(ulong)  || b == typeof(ulong))  return typeof(ulong);
        if (a == typeof(long)   || b == typeof(long))   return typeof(long);
        if (a == typeof(uint)   || b == typeof(uint))   return typeof(uint);
        return typeof(int);
    }

    private static ExpressionType MapBinaryKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddExpression                => ExpressionType.Add,
        SyntaxKind.SubtractExpression           => ExpressionType.Subtract,
        SyntaxKind.MultiplyExpression           => ExpressionType.Multiply,
        SyntaxKind.DivideExpression             => ExpressionType.Divide,
        SyntaxKind.ModuloExpression             => ExpressionType.Modulo,
        SyntaxKind.EqualsExpression             => ExpressionType.Equal,
        SyntaxKind.NotEqualsExpression          => ExpressionType.NotEqual,
        SyntaxKind.LessThanExpression           => ExpressionType.LessThan,
        SyntaxKind.LessThanOrEqualExpression    => ExpressionType.LessThanOrEqual,
        SyntaxKind.GreaterThanExpression        => ExpressionType.GreaterThan,
        SyntaxKind.GreaterThanOrEqualExpression => ExpressionType.GreaterThanOrEqual,
        _ => throw new NotSupportedException($"unsupported operator: {kind}")
    };

    private static Expression Coerce(Expression e, Type target)
    {
        if (e.Type == target) return e;
        // object → T unboxes; T → object boxes; numeric → numeric converts. Expression.Convert
        // handles all three uniformly via the runtime conversion logic the IL emitter produces.
        return Expression.Convert(e, target);
    }

    // ─── Runtime helpers (called via Expression.Call from the compiled delegate) ──────────────

    private static readonly MethodInfo ReadLocalRawMethod =
        typeof(CSharpCondition).GetMethod(nameof(ReadLocalRaw), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ResolveMemberMethod =
        typeof(CSharpCondition).GetMethod(nameof(ResolveMember), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static object? ReadLocalRaw(IEvalContext ctx, string name)
    {
        foreach (LocalValue l in ctx.Locals)
            if (l.Name == name)
                return l.RawValue ?? throw new InvalidOperationException($"local '{name}' has no primitive value");
        throw new InvalidOperationException($"local '{name}' not found at this stop");
    }

    private static object? ResolveMember(IMemberResolver resolver, string thisLocalName, string memberName)
    {
        EvalStatus st = resolver.TryEvalMemberCall(thisLocalName, memberName, TimeSpan.FromSeconds(10), out ArgumentValue v);
        if (st != EvalStatus.Completed)
            throw new InvalidOperationException($"member eval '{thisLocalName}.{memberName}' did not complete: {st}");
        return v.RawValue ?? throw new InvalidOperationException($"member '{thisLocalName}.{memberName}' has no primitive value");
    }

    // ─── Diagnostics passthrough + template brace pre-validation ──────────────────────────────

    private static void ThrowOnDiagnostics(ExpressionSyntax tree, string contextLabel)
    {
        foreach (var d in tree.GetDiagnostics())
            if (d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                throw new FormatException($"Roslyn parse error in {contextLabel}: {d.GetMessage()}");
    }

    private static void ValidateBraceStructure(string template)
    {
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{') { i += 2; continue; }
                int close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new FormatException($"unmatched '{{' at position {i} in template: \"{template}\"");
                if (close == i + 1)
                    throw new FormatException($"empty '{{}}' fragment at position {i} in template: \"{template}\"");
                i = close + 1;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}') { i += 2; continue; }
                throw new FormatException($"unmatched '}}' at position {i} in template: \"{template}\"");
            }
            i++;
        }
    }
}
