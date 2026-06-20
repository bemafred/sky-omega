using System;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using Xunit;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-049 reconciliation probe: drives the unified <c>[110] Expression</c> evaluator
/// (<see cref="FilterEvaluator.EvaluateToValue"/>) directly with constant expressions.
///
/// The hang (ADR-049 step-3 finding) is structural: FilterEvaluator's function-argument
/// parsers consume each argument with the weak single-term <c>ParseTerm()</c>, not the full
/// grammar. A compound argument (e.g. <c>COALESCE(1 + 1, 0)</c>) leaves the parser parked on the
/// operator, and the COALESCE/CONCAT argument while-loops never advance → infinite loop.
/// Fixed-arity functions (STRSTARTS, etc.) don't hang but return wrong results for the same reason.
/// </summary>
public class Adr049EvaluateToValueProbeTests
{
    private static Value Eval(string expr)
    {
        var e = new FilterEvaluator(expr.AsSpan());
        return e.EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null, ReadOnlySpan<char>.Empty);
    }

    // The canonical hang: a compound (arithmetic) argument inside a variadic function.
    [Fact]
    public void Coalesce_WithArithmeticArgument_Terminates()
    {
        var v = Eval("COALESCE(1 + 1, 0)");
        Assert.Equal(ValueType.Integer, v.Type);
        Assert.Equal(2, v.IntegerValue);
    }

    [Fact]
    public void Concat_WithCompoundArgument_Terminates()
    {
        // CONCAT over two simple strings — sanity that variadic parsing still terminates.
        var v = Eval("CONCAT(\"a\", \"b\")");
        Assert.Equal(ValueType.String, v.Type);
    }

    // Fixed-arity function with a compound first argument: must evaluate the whole argument.
    [Fact]
    public void Abs_WithArithmeticArgument_EvaluatesArgument()
    {
        var v = Eval("ABS(0 - 5)");
        Assert.Equal(ValueType.Integer, v.Type);
        Assert.Equal(5, v.IntegerValue);
    }

    // Plain single-term arguments must be unchanged by the fix.
    [Fact]
    public void Coalesce_WithSimpleArguments_Unchanged()
    {
        var v = Eval("COALESCE(42, 0)");
        Assert.Equal(ValueType.Integer, v.Type);
        Assert.Equal(42, v.IntegerValue);
    }

    // Top-level arithmetic (the documented FILTER-arithmetic gap) — sanity that the grammar evaluates it.
    [Fact]
    public void TopLevelArithmetic_Evaluates()
    {
        var v = Eval("3 + 4 * 2");
        Assert.Equal(ValueType.Integer, v.Type);
        Assert.Equal(11, v.IntegerValue);
    }
}
