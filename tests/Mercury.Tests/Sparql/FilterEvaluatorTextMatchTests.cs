using System;
using Xunit;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for the text:match FILTER function.
/// </summary>
public class FilterEvaluatorTextMatchTests
{
    private static bool Evaluate(string expression)
    {
        var evaluator = new FilterEvaluator(expression.AsSpan());
        return evaluator.Evaluate();
    }

    private static bool EvaluateWithStringBinding(string filter, string varName, string value)
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.Bind(varName, value.AsSpan());
        var evaluator = new FilterEvaluator(filter.AsSpan());
        return evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer());
    }

    private static bool EvaluateWithTwoStringBindings(string filter, string var1, string val1, string var2, string val2)
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[512];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.Bind(var1, val1.AsSpan());
        bindings.Bind(var2, val2.AsSpan());
        var evaluator = new FilterEvaluator(filter.AsSpan());
        return evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer());
    }

    #region Basic Matching Tests

    [Fact]
    public void TextMatch_SimpleMatch_ReturnsTrue()
    {
        Assert.True(Evaluate("text:match(\"hello world\", \"hello\")"));
    }

    [Fact]
    public void TextMatch_NoMatch_ReturnsFalse()
    {
        Assert.False(Evaluate("text:match(\"hello world\", \"goodbye\")"));
    }

    [Fact]
    public void TextMatch_ExactMatch_ReturnsTrue()
    {
        Assert.True(Evaluate("text:match(\"hello\", \"hello\")"));
    }

    [Fact]
    public void TextMatch_EmptyQuery_ReturnsTrue()
    {
        // Empty string is contained in any string
        Assert.True(Evaluate("text:match(\"hello world\", \"\")"));
    }

    [Fact]
    public void TextMatch_EmptyText_ReturnsFalse()
    {
        Assert.False(Evaluate("text:match(\"\", \"hello\")"));
    }

    [Fact]
    public void TextMatch_BothEmpty_ReturnsTrue()
    {
        Assert.True(Evaluate("text:match(\"\", \"\")"));
    }

    #endregion

    #region Case Insensitivity Tests

    [Fact]
    public void TextMatch_CaseInsensitive_LowercaseInUppercase()
    {
        Assert.True(Evaluate("text:match(\"HELLO WORLD\", \"hello\")"));
    }

    [Fact]
    public void TextMatch_CaseInsensitive_UppercaseInLowercase()
    {
        Assert.True(Evaluate("text:match(\"hello world\", \"HELLO\")"));
    }

    [Fact]
    public void TextMatch_CaseInsensitive_MixedCase()
    {
        Assert.True(Evaluate("text:match(\"HeLLo WoRLd\", \"hello\")"));
    }

    #endregion

    #region Swedish Characters Tests

    [Fact]
    public void TextMatch_Swedish_LowercaseAO()
    {
        // Swedish: å, ä, ö
        Assert.True(Evaluate("text:match(\"Göteborg\", \"göteborg\")"));
    }

    [Fact]
    public void TextMatch_Swedish_UppercaseAO()
    {
        Assert.True(Evaluate("text:match(\"göteborg\", \"GÖTEBORG\")"));
    }

    [Fact]
    public void TextMatch_Swedish_PartialMatch()
    {
        Assert.True(Evaluate("text:match(\"Räksmörgås\", \"smörg\")"));
    }

    [Fact]
    public void TextMatch_Swedish_NoMatch()
    {
        Assert.False(Evaluate("text:match(\"Göteborg\", \"Stockholm\")"));
    }

    [Fact]
    public void TextMatch_Swedish_CaseFolding()
    {
        // Verify Swedish characters case-fold correctly
        Assert.True(Evaluate("text:match(\"ÅÄÖSTRAND\", \"åäöstrand\")"));
    }

    #endregion

    #region Variable Binding Tests

    [Fact]
    public void TextMatch_WithVariable_Matches()
    {
        Assert.True(EvaluateWithStringBinding(
            "text:match(?name, \"Stock\")",
            "?name", "Stockholm"));
    }

    [Fact]
    public void TextMatch_WithVariable_NoMatch()
    {
        Assert.False(EvaluateWithStringBinding(
            "text:match(?name, \"Göte\")",
            "?name", "Stockholm"));
    }

    [Fact]
    public void TextMatch_WithBothVariables_Matches()
    {
        Assert.True(EvaluateWithTwoStringBindings(
            "text:match(?text, ?query)",
            "?text", "Hello World",
            "?query", "world"));
    }

    [Fact]
    public void TextMatch_UnboundVariable_ReturnsFalse()
    {
        // Unbound variable should return false
        var evaluator = new FilterEvaluator("text:match(?unbound, \"hello\")".AsSpan());
        Assert.False(evaluator.Evaluate());
    }

    #endregion

    #region Alternative Syntax Tests

    [Fact]
    public void TextMatch_MatchSyntax_Works()
    {
        // "match" is an alias for "text:match"
        Assert.True(Evaluate("match(\"hello world\", \"hello\")"));
    }

    [Fact]
    public void TextMatch_MatchSyntax_CaseInsensitive()
    {
        Assert.True(Evaluate("MATCH(\"hello world\", \"HELLO\")"));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TextMatch_SpecialCharacters_Matches()
    {
        Assert.True(Evaluate("text:match(\"hello (world)\", \"(world)\")"));
    }

    [Fact]
    public void TextMatch_Numbers_Matches()
    {
        Assert.True(Evaluate("text:match(\"Stockholm 2024\", \"2024\")"));
    }

    [Fact]
    public void TextMatch_Whitespace_Matches()
    {
        Assert.True(Evaluate("text:match(\"hello   world\", \"   \")"));
    }

    [Fact]
    public void TextMatch_QueryLongerThanText_ReturnsFalse()
    {
        Assert.False(Evaluate("text:match(\"hi\", \"hello world\")"));
    }

    #endregion

    #region Combined Filter Tests

    [Fact]
    public void TextMatch_CombinedWithAnd_Works()
    {
        Assert.True(Evaluate("text:match(\"hello\", \"ell\") && 1 = 1"));
    }

    [Fact]
    public void TextMatch_CombinedWithOr_Works()
    {
        Assert.True(Evaluate("text:match(\"hello\", \"xyz\") || text:match(\"world\", \"orl\")"));
    }

    [Fact]
    public void TextMatch_Negated_Works()
    {
        Assert.True(Evaluate("!text:match(\"hello\", \"xyz\")"));
    }

    #endregion
}
