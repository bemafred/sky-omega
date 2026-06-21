using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// The single SPARQL <c>[110] Expression</c> evaluator (divergence S2 / ADR-049). Both FILTER and BIND consume the
/// same grammar production; the only difference is post-evaluation — FILTER takes the effective boolean value
/// (§17.2.2), BIND binds the term and leaves the variable unbound on error (§18). This partial adds the
/// <see cref="EvaluateToValue"/> entry that produces a <see cref="Value"/> (an RDF term, or <c>ValueType.Unbound</c>
/// for an error) for the full grammar — logical connectives, comparisons, <c>IN</c>, arithmetic, and every built-in —
/// at spec precedence: <c>|| → &amp;&amp; → relational → additive → multiplicative → unary(!,+,-) → primary</c>. The
/// function library, comparison ops, and EBV are reused from the FILTER side; only arithmetic is added here.
/// </summary>
internal ref partial struct FilterEvaluator
{
    /// <summary>
    /// Evaluate the full <c>[110] Expression</c> and return its value (RDF term or <c>Unbound</c> on error).
    /// Used by BIND; FILTER derives its boolean via <c>CoerceToBool(EvaluateToValue(...))</c>.
    /// </summary>
    internal Value EvaluateToValue(ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer,
        PrefixMapping[]? prefixes, ReadOnlySpan<char> source, ReadOnlySpan<char> baseIri = default)
    {
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
        _prefixes = prefixes;
        _source = source;
        _baseIri = baseIri;
        _filterScopeDepth = -1;
        return ValueOr();
    }

    // [111] ConditionalOrExpression ::= ConditionalAndExpression ( '||' ConditionalAndExpression )*
    private Value ValueOr()
    {
        var left = ValueAnd();
        while (true)
        {
            SkipWhitespace();
            if (MatchOperator("||") || MatchKeyword("OR"))
                left = LogicalOr(left, ValueAnd());
            else
                break;
        }
        return left;
    }

    // [112] ConditionalAndExpression ::= ValueLogical ( '&&' ValueLogical )*
    private Value ValueAnd()
    {
        var left = ValueRelational();
        while (true)
        {
            SkipWhitespace();
            if (MatchOperator("&&") || MatchKeyword("AND"))
                left = LogicalAnd(left, ValueRelational());
            else
                break;
        }
        return left;
    }

    // [113]/[114] RelationalExpression ::= NumericExpression ( ( '=' | '!=' | '<' | '>' | '<=' | '>=' | 'IN' | 'NOT' 'IN' ) NumericExpression )?
    private Value ValueRelational()
    {
        var left = ValueAdditive();
        SkipWhitespace();
        if (IsAtEnd()) return left;

        // NOT IN / IN
        int save = _position;
        bool negated = false;
        if (MatchKeyword("NOT")) { SkipWhitespace(); negated = true; }
        if (MatchKeyword("IN"))
        {
            SkipWhitespace();
            return BoolValue(EvaluateInExpression(left, negated));
        }
        _position = save; // a NOT not followed by IN is not ours — rewind

        var op = ParseComparisonOperator();
        if (op == ComparisonOperator.Unknown) return left;
        SkipWhitespace();
        var right = ValueAdditive();
        if (left.Type == ValueType.Unbound || right.Type == ValueType.Unbound)
            return new Value { Type = ValueType.Unbound };
        return BoolValue(EvaluateComparisonOp(left, op, right));
    }

    // [116] AdditiveExpression ::= MultiplicativeExpression ( ( '+' | '-' ) MultiplicativeExpression )*
    private Value ValueAdditive()
    {
        var left = ValueMultiplicative();
        while (true)
        {
            SkipWhitespace();
            var ch = Peek();
            if (ch == '+') { Advance(); left = Add(left, ValueMultiplicative()); }
            else if (ch == '-') { Advance(); left = Subtract(left, ValueMultiplicative()); }
            else break;
        }
        return left;
    }

    // [117] MultiplicativeExpression ::= UnaryExpression ( ( '*' | '/' ) UnaryExpression )*
    private Value ValueMultiplicative()
    {
        var left = ValueUnary();
        while (true)
        {
            SkipWhitespace();
            var ch = Peek();
            if (ch == '*') { Advance(); left = Multiply(left, ValueUnary()); }
            else if (ch == '/') { Advance(); left = Divide(left, ValueUnary()); }
            else break;
        }
        return left;
    }

    // [118] UnaryExpression ::= ( '!' | '+' | '-' )? PrimaryExpression
    private Value ValueUnary()
    {
        SkipWhitespace();
        var ch = Peek();
        if (ch == '!')
        {
            Advance();
            return BoolValue(!CoerceToBool(ValueUnary())); // recurse so !!x / ! !x negate twice
        }
        // NOT keyword — Mercury synonym for '!' (parallels the OR/AND keyword synonyms in
        // ValueOr/ValueAnd). 'NOT IN' is postfix and handled in ValueRelational, so it never
        // reaches here; a leading NOT is always logical negation.
        if ((ch == 'N' || ch == 'n') && MatchKeyword("NOT"))
            return BoolValue(!CoerceToBool(ValueUnary()));
        if (ch == '-')
        {
            // A negative numeric literal (-5) is parsed by ParseTerm; unary minus on anything else applies Negate.
            if (!IsDigit(PeekNext())) { Advance(); return Negate(ValuePrimary()); }
            return ValuePrimary();
        }
        if (ch == '+')
        {
            Advance(); // unary plus is identity (ParseTerm's numeric does not accept a leading '+')
            return ValuePrimary();
        }
        return ValuePrimary();
    }

    // [119]/[120] PrimaryExpression ::= BrackettedExpression '(' Expression ')' | (BuiltInCall | iriOrFunction | RDFLiteral | NumericLiteral | BooleanLiteral | Var via ParseTerm)
    private Value ValuePrimary()
    {
        SkipWhitespace();
        if (Peek() == '(')
        {
            Advance();
            var v = ValueOr();
            SkipWhitespace();
            if (Peek() == ')') Advance();
            return v;
        }
        return ParseTerm();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char PeekNext() => _position + 1 < _expression.Length ? _expression[_position + 1] : '\0';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value BoolValue(bool b) => new() { Type = ValueType.Boolean, BooleanValue = b };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsError(Value v) => v.Type == ValueType.Unbound;

    // SPARQL §17.4.1.5 logical-or: true if either operand's EBV is true; else error if either is an error; else false.
    private static Value LogicalOr(Value l, Value r)
    {
        if (CoerceToBool(l) || CoerceToBool(r)) return BoolValue(true);
        if (IsError(l) || IsError(r)) return new Value { Type = ValueType.Unbound };
        return BoolValue(false);
    }

    // SPARQL §17.4.1.6 logical-and: false if either operand is a genuine false; else error if either is an error; else true.
    private static Value LogicalAnd(Value l, Value r)
    {
        bool lFalse = !CoerceToBool(l) && !IsError(l);
        bool rFalse = !CoerceToBool(r) && !IsError(r);
        if (lFalse || rFalse) return BoolValue(false);
        if (IsError(l) || IsError(r)) return new Value { Type = ValueType.Unbound };
        return BoolValue(true);
    }

    // ── Arithmetic over Values (SPARQL §17.4.2 operator mapping). Ported from the deleted BindExpressionEvaluator. ──

    private static Value Add(Value left, Value right)
    {
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue + right.IntegerValue };
        return NumericResult(CoerceToNumber(left), CoerceToNumber(right), static (a, b) => a + b);
    }

    private static Value Subtract(Value left, Value right)
    {
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue - right.IntegerValue };
        return NumericResult(CoerceToNumber(left), CoerceToNumber(right), static (a, b) => a - b);
    }

    private static Value Multiply(Value left, Value right)
    {
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue * right.IntegerValue };
        return NumericResult(CoerceToNumber(left), CoerceToNumber(right), static (a, b) => a * b);
    }

    private static Value Divide(Value left, Value right)
    {
        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r) || r == 0)
            return new Value { Type = ValueType.Unbound };
        return new Value { Type = ValueType.Double, DoubleValue = l / r };
    }

    private static Value Negate(Value val)
    {
        if (val.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = -val.IntegerValue };
        var n = CoerceToNumber(val);
        if (double.IsNaN(n)) return new Value { Type = ValueType.Unbound };
        if (n == Math.Floor(n) && n >= long.MinValue && n <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = -(long)n };
        return new Value { Type = ValueType.Double, DoubleValue = -n };
    }

    // Promote to integer when the result is a whole number representable as long, else double (matches xsd:integer
    // vs xsd:decimal/double result typing as the prior BIND arithmetic did).
    private static Value NumericResult(double l, double r, Func<double, double, double> op)
    {
        if (double.IsNaN(l) || double.IsNaN(r)) return new Value { Type = ValueType.Unbound };
        var result = op(l, r);
        if (result == Math.Floor(result) && result >= long.MinValue && result <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = (long)result };
        return new Value { Type = ValueType.Double, DoubleValue = result };
    }

    private static double CoerceToNumber(Value val) => val.Type switch
    {
        ValueType.Integer => val.IntegerValue,
        ValueType.Double => val.DoubleValue,
        ValueType.String => ParseNumberFromLexical(val.StringValue),
        _ => double.NaN
    };

    private static double ParseNumberFromLexical(ReadOnlySpan<char> s)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        // Typed literal: "value"^^<datatype> — extract the lexical value between the quotes.
        if (s.Length > 2 && s[0] == '"')
        {
            var endQuote = s.Slice(1).IndexOf('"');
            if (endQuote > 0 && double.TryParse(s.Slice(1, endQuote), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return result;
        }
        return double.NaN;
    }
}
