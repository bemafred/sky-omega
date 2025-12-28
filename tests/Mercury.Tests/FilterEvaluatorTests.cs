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
        return evaluator.Evaluate();
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
    public void TypeMismatch_IntegerAndNonNumericString_ReturnsFalse()
    {
        // Comparing integer to non-numeric string returns false (can't coerce)
        Assert.False(Evaluate("5 == \"abc\""));
    }

    [Fact]
    public void TypeCoercion_IntegerAndNumericString_ReturnsTrue()
    {
        // Comparing integer to numeric string coerces and compares
        Assert.True(Evaluate("5 == \"5\""));
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

    #region Variable Bindings

    // Test helper that sets up bindings and evaluates a filter expression
    // Uses heap allocation for test simplicity - production code uses stackalloc
    private static bool EvaluateWithIntBinding(string filter, string varName, long value)
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.Bind(varName, value);
        var evaluator = new FilterEvaluator(filter.AsSpan());
        return evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer());
    }

    private static bool EvaluateWithDoubleBinding(string filter, string varName, double value)
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.Bind(varName, value);
        var evaluator = new FilterEvaluator(filter.AsSpan());
        return evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer());
    }

    private static bool EvaluateWithBoolBinding(string filter, string varName, bool value)
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.Bind(varName, value);
        var evaluator = new FilterEvaluator(filter.AsSpan());
        return evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer());
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

    private static bool EvaluateWithMultipleBindings(string filter, params (string name, object value)[] vars)
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        foreach (var (name, value) in vars)
        {
            switch (value)
            {
                case long l: bindings.Bind(name, l); break;
                case int i: bindings.Bind(name, (long)i); break;
                case double d: bindings.Bind(name, d); break;
                case bool b: bindings.Bind(name, b); break;
                case string s: bindings.Bind(name, s.AsSpan()); break;
            }
        }
        var evaluator = new FilterEvaluator(filter.AsSpan());
        return evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer());
    }

    private static bool EvaluateWithEmptyBindings(string filter)
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        var evaluator = new FilterEvaluator(filter.AsSpan());
        return evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer());
    }

    [Fact]
    public void Variable_IntegerComparison_True()
    {
        Assert.True(EvaluateWithIntBinding("?age > 18", "?age", 25L));
    }

    [Fact]
    public void Variable_IntegerComparison_False()
    {
        Assert.False(EvaluateWithIntBinding("?age > 18", "?age", 15L));
    }

    [Fact]
    public void Variable_IntegerEquality()
    {
        Assert.True(EvaluateWithIntBinding("?x == 42", "?x", 42L));
    }

    [Fact]
    public void Variable_DoubleComparison()
    {
        Assert.True(EvaluateWithDoubleBinding("?price < 100.0", "?price", 49.99));
    }

    [Fact]
    public void Variable_BooleanTrue()
    {
        Assert.True(EvaluateWithBoolBinding("?active", "?active", true));
    }

    [Fact]
    public void Variable_BooleanFalse()
    {
        Assert.False(EvaluateWithBoolBinding("?active", "?active", false));
    }

    [Fact]
    public void Variable_StringEquality()
    {
        Assert.True(EvaluateWithStringBinding("?name == \"Alice\"", "?name", "Alice"));
    }

    [Fact]
    public void Variable_StringInequality()
    {
        Assert.True(EvaluateWithStringBinding("?name != \"Bob\"", "?name", "Alice"));
    }

    [Fact]
    public void Variable_TwoVariables()
    {
        Assert.True(EvaluateWithMultipleBindings("?x < ?y", ("?x", 10L), ("?y", 20L)));
    }

    [Fact]
    public void Variable_UnboundVariable_ReturnsFalse()
    {
        Assert.False(EvaluateWithEmptyBindings("?unknown > 0"));
    }

    [Fact]
    public void Variable_WithLogicalOperators()
    {
        Assert.True(EvaluateWithMultipleBindings("?age >= 18 && ?active", ("?age", 21L), ("?active", true)));
    }

    [Fact]
    public void Variable_MixedWithLiterals()
    {
        Assert.True(EvaluateWithIntBinding("?count > 5 && 10 < 20", "?count", 8L));
    }

    [Fact]
    public void Variable_BoundFunction_BoundVariable()
    {
        Assert.True(EvaluateWithIntBinding("bound(?x)", "?x", 42L));
    }

    [Fact]
    public void Variable_BoundFunction_UnboundVariable()
    {
        Assert.False(EvaluateWithEmptyBindings("bound(?x)"));
    }

    [Fact]
    public void Variable_NotBound()
    {
        Assert.True(EvaluateWithIntBinding("!bound(?missing)", "?other", 1L));
    }

    [Fact]
    public void Variable_ComplexExpression()
    {
        // under 18 but is VIP, so first clause is true; also active
        Assert.True(EvaluateWithMultipleBindings("(?age > 18 || ?vip) && ?active",
            ("?age", 16L), ("?vip", true), ("?active", true)));
    }

    #endregion

    #region IN/NOT IN Operators

    [Fact]
    public void In_IntegerInList_ReturnsTrue()
    {
        Assert.True(EvaluateWithIntBinding("?x IN (1, 2, 3)", "?x", 2L));
    }

    [Fact]
    public void In_IntegerNotInList_ReturnsFalse()
    {
        Assert.False(EvaluateWithIntBinding("?x IN (1, 2, 3)", "?x", 5L));
    }

    [Fact]
    public void NotIn_IntegerNotInList_ReturnsTrue()
    {
        Assert.True(EvaluateWithIntBinding("?x NOT IN (1, 2, 3)", "?x", 5L));
    }

    [Fact]
    public void NotIn_IntegerInList_ReturnsFalse()
    {
        Assert.False(EvaluateWithIntBinding("?x NOT IN (1, 2, 3)", "?x", 2L));
    }

    [Fact]
    public void In_StringInList_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("?name IN (\"Alice\", \"Bob\", \"Charlie\")", "?name", "Alice"));
    }

    [Fact]
    public void In_StringNotInList_ReturnsFalse()
    {
        Assert.False(EvaluateWithStringBinding("?name IN (\"Alice\", \"Bob\")", "?name", "Charlie"));
    }

    [Fact]
    public void NotIn_StringNotInList_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("?name NOT IN (\"Alice\", \"Bob\")", "?name", "Charlie"));
    }

    [Fact]
    public void In_EmptyList_ReturnsFalse()
    {
        Assert.False(EvaluateWithIntBinding("?x IN ()", "?x", 1L));
    }

    [Fact]
    public void NotIn_EmptyList_ReturnsTrue()
    {
        Assert.True(EvaluateWithIntBinding("?x NOT IN ()", "?x", 1L));
    }

    [Fact]
    public void In_SingleValue_ReturnsTrue()
    {
        Assert.True(EvaluateWithIntBinding("?x IN (42)", "?x", 42L));
    }

    [Fact]
    public void In_MixedTypes_WithCoercion()
    {
        // String "25" should match integer 25 with type coercion
        Assert.True(EvaluateWithStringBinding("?age IN (20, 25, 30)", "?age", "25"));
    }

    [Fact]
    public void In_CombinedWithAnd()
    {
        Assert.True(EvaluateWithMultipleBindings("?x IN (1, 2, 3) && ?y > 10",
            ("?x", 2L), ("?y", 15L)));
    }

    [Fact]
    public void In_CombinedWithOr()
    {
        Assert.True(EvaluateWithMultipleBindings("?x IN (1, 2, 3) || ?y > 100",
            ("?x", 5L), ("?y", 150L)));
    }

    #endregion

    #region BOUND Function

    [Fact]
    public void Bound_BoundVariable_ReturnsTrue()
    {
        Assert.True(EvaluateWithIntBinding("BOUND(?x)", "?x", 42L));
    }

    [Fact]
    public void Bound_InFilter_FiltersCorrectly()
    {
        Assert.True(EvaluateWithMultipleBindings("BOUND(?x) && ?x > 10",
            ("?x", 20L)));
    }

    #endregion

    #region IF Function

    [Fact]
    public void If_ConditionTrue_ReturnsThenValue()
    {
        Assert.True(EvaluateWithIntBinding("IF(?x > 5, 1, 0) == 1", "?x", 10L));
    }

    [Fact]
    public void If_ConditionFalse_ReturnsElseValue()
    {
        Assert.True(EvaluateWithIntBinding("IF(?x > 5, 1, 0) == 0", "?x", 3L));
    }

    [Fact]
    public void If_WithStrings()
    {
        // Can't easily test string result with current helper, but verify no crash
        Assert.True(EvaluateWithIntBinding("IF(?x > 18, 1, 0) == 1", "?x", 25L));
    }

    [Fact]
    public void If_NestedCondition()
    {
        Assert.True(EvaluateWithMultipleBindings("IF(?x > 5 && ?y < 10, 1, 0) == 1",
            ("?x", 10L), ("?y", 5L)));
    }

    #endregion

    #region COALESCE Function

    [Fact]
    public void Coalesce_FirstBound_ReturnsFirst()
    {
        Assert.True(EvaluateWithMultipleBindings("COALESCE(?x, ?y, 0) == 10",
            ("?x", 10L), ("?y", 20L)));
    }

    [Fact]
    public void Coalesce_FirstUnbound_ReturnsSecond()
    {
        Assert.True(EvaluateWithIntBinding("COALESCE(?x, ?y, 0) == 20", "?y", 20L));
    }

    [Fact]
    public void Coalesce_AllUnbound_ReturnsDefault()
    {
        Assert.True(EvaluateWithEmptyBindings("COALESCE(?x, ?y, 99) == 99"));
    }

    [Fact]
    public void Coalesce_SingleValue()
    {
        Assert.True(EvaluateWithIntBinding("COALESCE(?x) == 42", "?x", 42L));
    }

    #endregion
}
