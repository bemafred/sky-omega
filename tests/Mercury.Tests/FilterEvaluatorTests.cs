using SkyOmega.Mercury.Sparql;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for SPARQL FILTER expression evaluation
/// </summary>
public class FilterEvaluatorTests
{
    [Fact]
    public void NumericComparison_EvaluatesGreaterThan()
    {
        var filter = "5 > 3";
        var evaluator = new FilterEvaluator(filter.AsSpan());

        Span<Binding> bindingStorage = stackalloc Binding[16];
        var bindings = new BindingTable(bindingStorage);

        Assert.True(evaluator.Evaluate(bindings));
    }

    [Fact]
    public void StringComparison_EvaluatesEquality()
    {
        var filter = "\"abc\" == \"abc\"";
        var evaluator = new FilterEvaluator(filter.AsSpan());

        Span<Binding> bindingStorage = stackalloc Binding[16];
        var bindings = new BindingTable(bindingStorage);

        Assert.True(evaluator.Evaluate(bindings));
    }

    [Fact]
    public void BoundFunction_ReturnsFalseForUnboundVariable()
    {
        var filter = "bound(?x)";
        var evaluator = new FilterEvaluator(filter.AsSpan());

        Span<Binding> bindingStorage = stackalloc Binding[16];
        var bindings = new BindingTable(bindingStorage);

        // Should return false for unbound variable
        Assert.False(evaluator.Evaluate(bindings));
    }
}
