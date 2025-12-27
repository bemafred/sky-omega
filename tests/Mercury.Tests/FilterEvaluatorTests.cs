using SkyOmega.Mercury.Sparql;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for SPARQL FILTER expression evaluation.
///
/// The FilterEvaluator supports:
/// - Comparison operators: ==, !=, &lt;, &gt;, &lt;=, &gt;=
/// - Numeric literals (integer and double)
/// - String literals
/// - Built-in functions: bound(), isIRI(), str()
/// </summary>
public class FilterEvaluatorTests
{
    private static bool Evaluate(string filter)
    {
        var evaluator = new FilterEvaluator(filter.AsSpan());
        Span<Binding> bindingStorage = stackalloc Binding[16];
        var bindings = new BindingTable(bindingStorage);
        return evaluator.Evaluate(bindings);
    }

    #region Comparison Operators - Equality

    [Fact]
    public void Equal_IntegersMatch_ReturnsTrue()
    {
        Assert.True(Evaluate("5 == 5"));
    }

    [Fact]
    public void Equal_IntegersDiffer_ReturnsFalse()
    {
        Assert.False(Evaluate("5 == 3"));
    }

    [Fact]
    public void Equal_SingleEqualsSign_Works()
    {
        Assert.True(Evaluate("5 = 5"));
    }

    [Fact]
    public void NotEqual_IntegersDiffer_ReturnsTrue()
    {
        Assert.True(Evaluate("5 != 3"));
    }

    [Fact]
    public void NotEqual_IntegersMatch_ReturnsFalse()
    {
        Assert.False(Evaluate("5 != 5"));
    }

    #endregion

    #region Comparison Operators - Relational

    [Fact]
    public void GreaterThan_LeftGreater_ReturnsTrue()
    {
        Assert.True(Evaluate("5 > 3"));
    }

    [Fact]
    public void GreaterThan_LeftSmaller_ReturnsFalse()
    {
        Assert.False(Evaluate("3 > 5"));
    }

    [Fact]
    public void GreaterThan_Equal_ReturnsFalse()
    {
        Assert.False(Evaluate("5 > 5"));
    }

    [Fact]
    public void LessThan_LeftSmaller_ReturnsTrue()
    {
        Assert.True(Evaluate("3 < 5"));
    }

    [Fact]
    public void LessThan_LeftGreater_ReturnsFalse()
    {
        Assert.False(Evaluate("5 < 3"));
    }

    [Fact]
    public void LessThan_Equal_ReturnsFalse()
    {
        Assert.False(Evaluate("5 < 5"));
    }

    [Fact]
    public void GreaterOrEqual_LeftGreater_ReturnsTrue()
    {
        Assert.True(Evaluate("5 >= 3"));
    }

    [Fact]
    public void GreaterOrEqual_Equal_ReturnsTrue()
    {
        Assert.True(Evaluate("5 >= 5"));
    }

    [Fact]
    public void GreaterOrEqual_LeftSmaller_ReturnsFalse()
    {
        Assert.False(Evaluate("3 >= 5"));
    }

    [Fact]
    public void LessOrEqual_LeftSmaller_ReturnsTrue()
    {
        Assert.True(Evaluate("3 <= 5"));
    }

    [Fact]
    public void LessOrEqual_Equal_ReturnsTrue()
    {
        Assert.True(Evaluate("5 <= 5"));
    }

    [Fact]
    public void LessOrEqual_LeftGreater_ReturnsFalse()
    {
        Assert.False(Evaluate("5 <= 3"));
    }

    #endregion

    #region Numeric Types - Integers

    [Fact]
    public void Integer_Zero_Compares()
    {
        Assert.True(Evaluate("0 == 0"));
    }

    [Fact]
    public void Integer_Negative_Compares()
    {
        Assert.True(Evaluate("-5 < 0"));
    }

    [Fact]
    public void Integer_NegativeComparison_Works()
    {
        Assert.True(Evaluate("-10 < -5"));
    }

    [Fact]
    public void Integer_LargeValues_Compares()
    {
        Assert.True(Evaluate("1000000 > 999999"));
    }

    #endregion

    #region Numeric Types - Doubles

    [Fact]
    public void Double_Equal_ReturnsTrue()
    {
        Assert.True(Evaluate("3.14 == 3.14"));
    }

    [Fact]
    public void Double_GreaterThan_Works()
    {
        Assert.True(Evaluate("3.15 > 3.14"));
    }

    [Fact]
    public void Double_LessThan_Works()
    {
        Assert.True(Evaluate("2.5 < 3.5"));
    }

    [Fact]
    public void Double_NegativeValues_Compare()
    {
        Assert.True(Evaluate("-1.5 < -0.5"));
    }

    [Fact]
    public void Double_ZeroPointZero_Works()
    {
        Assert.True(Evaluate("0.0 == 0.0"));
    }

    #endregion

    #region String Comparisons

    [Fact]
    public void String_Equal_ReturnsTrue()
    {
        Assert.True(Evaluate("\"abc\" == \"abc\""));
    }

    [Fact]
    public void String_NotEqual_ReturnsFalse()
    {
        Assert.False(Evaluate("\"abc\" == \"xyz\""));
    }

    [Fact]
    public void String_NotEqualOperator_Works()
    {
        Assert.True(Evaluate("\"abc\" != \"xyz\""));
    }

    [Fact]
    public void String_LessThan_Lexicographic()
    {
        Assert.True(Evaluate("\"abc\" < \"abd\""));
    }

    [Fact]
    public void String_GreaterThan_Lexicographic()
    {
        Assert.True(Evaluate("\"xyz\" > \"abc\""));
    }

    [Fact]
    public void String_Empty_Compares()
    {
        Assert.True(Evaluate("\"\" == \"\""));
    }

    [Fact]
    public void String_EmptyLessThanNonEmpty()
    {
        Assert.True(Evaluate("\"\" < \"a\""));
    }

    [Fact]
    public void String_CaseSensitive()
    {
        // "A" (65) < "a" (97) in ASCII/Unicode
        Assert.True(Evaluate("\"A\" < \"a\""));
    }

    [Fact]
    public void String_WithSpaces_Works()
    {
        Assert.True(Evaluate("\"hello world\" == \"hello world\""));
    }

    #endregion

    #region Built-in Functions

    [Fact]
    public void Bound_UnboundVariable_ReturnsFalse()
    {
        Assert.False(Evaluate("bound(?x)"));
    }

    [Fact]
    public void Bound_FunctionAsExpression_CoercesToBool()
    {
        // bound(?x) returns a boolean Value, which gets coerced to bool
        Assert.False(Evaluate("bound(?unknown)"));
    }

    [Fact]
    public void IsIRI_UnboundVariable_ReturnsFalse()
    {
        // isIRI on unbound variable returns false (not a URI type)
        Assert.False(Evaluate("isIRI(?x)"));
    }

    [Fact]
    public void Str_FunctionExists()
    {
        // str() function should parse without error
        // Returns unbound, which coerces to false
        Assert.False(Evaluate("str(?x)"));
    }

    #endregion

    #region Type Mismatches

    [Fact]
    public void TypeMismatch_IntegerAndString_ReturnsFalse()
    {
        // Comparing different types returns false
        Assert.False(Evaluate("5 == \"5\""));
    }

    [Fact]
    public void TypeMismatch_IntegerLessThanString_ReturnsFalse()
    {
        Assert.False(Evaluate("5 < \"abc\""));
    }

    #endregion

    #region Whitespace Handling

    [Fact]
    public void Whitespace_LeadingSpaces_Ignored()
    {
        Assert.True(Evaluate("   5 > 3"));
    }

    [Fact]
    public void Whitespace_TrailingSpaces_Ignored()
    {
        Assert.True(Evaluate("5 > 3   "));
    }

    [Fact]
    public void Whitespace_NoSpaces_Works()
    {
        Assert.True(Evaluate("5>3"));
    }

    [Fact]
    public void Whitespace_ExtraSpaces_Works()
    {
        Assert.True(Evaluate("5    >    3"));
    }

    [Fact]
    public void Whitespace_Tabs_Works()
    {
        Assert.True(Evaluate("5\t>\t3"));
    }

    #endregion

    #region Boolean Coercion

    [Fact]
    public void Coercion_NonZeroInteger_IsTrue()
    {
        // A bare integer that's non-zero coerces to true
        Assert.True(Evaluate("1"));
    }

    [Fact]
    public void Coercion_ZeroInteger_IsFalse()
    {
        Assert.False(Evaluate("0"));
    }

    [Fact]
    public void Coercion_NonEmptyString_IsTrue()
    {
        Assert.True(Evaluate("\"hello\""));
    }

    [Fact]
    public void Coercion_EmptyString_IsFalse()
    {
        Assert.False(Evaluate("\"\""));
    }

    [Fact]
    public void Coercion_NonZeroDouble_IsTrue()
    {
        Assert.True(Evaluate("0.001"));
    }

    [Fact]
    public void Coercion_ZeroDouble_IsFalse()
    {
        Assert.False(Evaluate("0.0"));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EdgeCase_UnboundInComparison_ReturnsFalse()
    {
        // Comparing with unbound variable returns false
        Assert.False(Evaluate("?x == 5"));
    }

    [Fact]
    public void EdgeCase_BothUnbound_ReturnsFalse()
    {
        Assert.False(Evaluate("?x == ?y"));
    }

    [Fact]
    public void EdgeCase_NegativeZero_EqualsZero()
    {
        Assert.True(Evaluate("-0 == 0"));
    }

    #endregion

    #region Logical AND Operator

    [Fact]
    public void And_BothTrue_ReturnsTrue()
    {
        Assert.True(Evaluate("1 == 1 && 2 == 2"));
    }

    [Fact]
    public void And_LeftFalse_ReturnsFalse()
    {
        Assert.False(Evaluate("1 == 2 && 2 == 2"));
    }

    [Fact]
    public void And_RightFalse_ReturnsFalse()
    {
        Assert.False(Evaluate("1 == 1 && 2 == 3"));
    }

    [Fact]
    public void And_BothFalse_ReturnsFalse()
    {
        Assert.False(Evaluate("1 == 2 && 2 == 3"));
    }

    [Fact]
    public void And_Keyword_Works()
    {
        Assert.True(Evaluate("1 == 1 AND 2 == 2"));
    }

    [Fact]
    public void And_KeywordLowercase_Works()
    {
        Assert.True(Evaluate("1 == 1 and 2 == 2"));
    }

    [Fact]
    public void And_MultipleChained_Works()
    {
        Assert.True(Evaluate("1 == 1 && 2 == 2 && 3 == 3"));
    }

    [Fact]
    public void And_MultipleChained_OneFails_ReturnsFalse()
    {
        Assert.False(Evaluate("1 == 1 && 2 == 2 && 3 == 4"));
    }

    #endregion

    #region Logical OR Operator

    [Fact]
    public void Or_BothTrue_ReturnsTrue()
    {
        Assert.True(Evaluate("1 == 1 || 2 == 2"));
    }

    [Fact]
    public void Or_LeftTrue_ReturnsTrue()
    {
        Assert.True(Evaluate("1 == 1 || 2 == 3"));
    }

    [Fact]
    public void Or_RightTrue_ReturnsTrue()
    {
        Assert.True(Evaluate("1 == 2 || 2 == 2"));
    }

    [Fact]
    public void Or_BothFalse_ReturnsFalse()
    {
        Assert.False(Evaluate("1 == 2 || 2 == 3"));
    }

    [Fact]
    public void Or_Keyword_Works()
    {
        Assert.True(Evaluate("1 == 1 OR 2 == 3"));
    }

    [Fact]
    public void Or_KeywordLowercase_Works()
    {
        Assert.True(Evaluate("1 == 2 or 2 == 2"));
    }

    [Fact]
    public void Or_MultipleChained_Works()
    {
        Assert.True(Evaluate("1 == 2 || 2 == 3 || 3 == 3"));
    }

    [Fact]
    public void Or_MultipleChained_AllFail_ReturnsFalse()
    {
        Assert.False(Evaluate("1 == 2 || 2 == 3 || 3 == 4"));
    }

    #endregion

    #region Logical NOT Operator

    [Fact]
    public void Not_True_ReturnsFalse()
    {
        Assert.False(Evaluate("!1"));
    }

    [Fact]
    public void Not_False_ReturnsTrue()
    {
        Assert.True(Evaluate("!0"));
    }

    [Fact]
    public void Not_Comparison_Works()
    {
        Assert.True(Evaluate("!(1 == 2)"));
    }

    [Fact]
    public void Not_TrueComparison_ReturnsFalse()
    {
        Assert.False(Evaluate("!(1 == 1)"));
    }

    [Fact]
    public void Not_Keyword_Works()
    {
        Assert.True(Evaluate("NOT 0"));
    }

    [Fact]
    public void Not_KeywordLowercase_Works()
    {
        Assert.True(Evaluate("not 0"));
    }

    [Fact]
    public void Not_DoubleNegation_ReturnsOriginal()
    {
        Assert.True(Evaluate("!!1"));
    }

    [Fact]
    public void Not_TripleNegation_ReturnsNegated()
    {
        Assert.False(Evaluate("!!!1"));
    }

    #endregion

    #region Operator Precedence

    [Fact]
    public void Precedence_AndBeforeOr_LeftGrouping()
    {
        // 1 == 1 || 1 == 2 && 2 == 2 => 1 == 1 || (1 == 2 && 2 == 2) => true || false => true
        Assert.True(Evaluate("1 == 1 || 1 == 2 && 2 == 2"));
    }

    [Fact]
    public void Precedence_AndBeforeOr_RightGrouping()
    {
        // 1 == 2 && 2 == 2 || 3 == 3 => (1 == 2 && 2 == 2) || 3 == 3 => false || true => true
        Assert.True(Evaluate("1 == 2 && 2 == 2 || 3 == 3"));
    }

    [Fact]
    public void Precedence_NotBeforeAnd()
    {
        // !0 && 1 => true && true => true
        Assert.True(Evaluate("!0 && 1"));
    }

    [Fact]
    public void Precedence_NotBeforeOr()
    {
        // !1 || 1 => false || true => true
        Assert.True(Evaluate("!1 || 1"));
    }

    [Fact]
    public void Precedence_ParenthesesOverride()
    {
        // (1 == 1 || 1 == 2) && 2 == 3 => true && false => false
        Assert.False(Evaluate("(1 == 1 || 1 == 2) && 2 == 3"));
    }

    [Fact]
    public void Precedence_NestedParentheses()
    {
        // ((1 == 1 && 2 == 2) || 3 == 4) && 5 == 5 => (true || false) && true => true && true => true
        Assert.True(Evaluate("((1 == 1 && 2 == 2) || 3 == 4) && 5 == 5"));
    }

    #endregion

    #region Complex Expressions

    [Fact]
    public void Complex_MixedOperators()
    {
        // 5 > 3 && 10 < 20 || 1 == 2 => (true && true) || false => true
        Assert.True(Evaluate("5 > 3 && 10 < 20 || 1 == 2"));
    }

    [Fact]
    public void Complex_NotWithAnd()
    {
        // !(1 == 2) && 3 == 3 => true && true => true
        Assert.True(Evaluate("!(1 == 2) && 3 == 3"));
    }

    [Fact]
    public void Complex_NotWithOr()
    {
        // 1 == 2 || !(3 == 4) => false || true => true
        Assert.True(Evaluate("1 == 2 || !(3 == 4)"));
    }

    [Fact]
    public void Complex_StringsAndLogical()
    {
        Assert.True(Evaluate("\"abc\" == \"abc\" && \"xyz\" != \"abc\""));
    }

    [Fact]
    public void Complex_KeywordsAndSymbols()
    {
        // Mix AND keyword with || symbol
        Assert.True(Evaluate("1 == 1 AND 2 == 2 || 3 == 4"));
    }

    [Fact]
    public void Complex_AllKeywords()
    {
        Assert.True(Evaluate("NOT 0 AND 1 OR 0"));
    }

    #endregion
}
