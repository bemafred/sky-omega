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
            {
                var right = ValueAnd();
                left = Value.FromTruth(Value.Or(Value.EffectiveBoolean(left), Value.EffectiveBoolean(right)));
            }
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
            {
                var right = ValueRelational();
                left = Value.FromTruth(Value.And(Value.EffectiveBoolean(left), Value.EffectiveBoolean(right)));
            }
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
            return Value.FromTruth(EvaluateInExpression(left, negated));
        }
        _position = save; // a NOT not followed by IN is not ours — rewind

        var op = ParseComparisonOperator();
        if (op == ComparisonOperator.Unknown) return left;
        SkipWhitespace();
        var right = ValueAdditive();
        return Value.FromTruth(EvaluateComparison(left, op, right));
    }

    // [116] AdditiveExpression ::= MultiplicativeExpression ( ( '+' | '-' ) MultiplicativeExpression )*
    private Value ValueAdditive()
    {
        var left = ValueMultiplicative();
        while (true)
        {
            SkipWhitespace();
            var ch = Peek();
            if (ch == '+') { Advance(); var right = ValueMultiplicative(); left = Value.Arithmetic('+', left, right); }
            else if (ch == '-') { Advance(); var right = ValueMultiplicative(); left = Value.Arithmetic('-', left, right); }
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
            if (ch == '*') { Advance(); var right = ValueUnary(); left = Value.Arithmetic('*', left, right); }
            else if (ch == '/') { Advance(); var right = ValueUnary(); left = Value.Arithmetic('/', left, right); }
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
            var operand = ValueUnary(); // recurse so !!x / ! !x negate twice
            return Value.FromTruth(Value.Not(Value.EffectiveBoolean(operand)));
        }
        // NOT keyword — Mercury synonym for '!' (parallels the OR/AND keyword synonyms in ValueOr/ValueAnd). 'NOT IN'
        // is postfix and handled in ValueRelational, so it never reaches here; a leading NOT is always negation.
        if ((ch == 'N' || ch == 'n') && MatchKeyword("NOT"))
        {
            var operand = ValueUnary();
            return Value.FromTruth(Value.Not(Value.EffectiveBoolean(operand)));
        }
        if (ch is '-' or '+')
        {
            // A signed number is one literal token ([149]–[154]): -5, +1.0, -.5. A sign before anything else is the
            // unary operator.
            var next = PeekNext();
            if (IsDigit(next) || next == '.')
                return ValuePrimary();
            Advance();
            var operand = ValuePrimary();
            return ch == '-' ? Value.Negate(operand) : Value.Plus(operand);
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
}
