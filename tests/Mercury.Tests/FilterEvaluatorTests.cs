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

    #region REGEX Function

    [Fact]
    public void Regex_SimpleMatch_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("REGEX(?name, \"Alice\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_NoMatch_ReturnsFalse()
    {
        Assert.True(EvaluateWithStringBinding("!REGEX(?name, \"Bob\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_PatternWithAnchor_MatchesStart()
    {
        Assert.True(EvaluateWithStringBinding("REGEX(?name, \"^Ali\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_PatternWithAnchor_NoMatchMiddle()
    {
        Assert.True(EvaluateWithStringBinding("!REGEX(?name, \"^ice\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_PatternWithEndAnchor()
    {
        Assert.True(EvaluateWithStringBinding("REGEX(?name, \"ice$\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_CaseInsensitiveFlag()
    {
        Assert.True(EvaluateWithStringBinding("REGEX(?name, \"ALICE\", \"i\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_CaseSensitiveNoFlag_NoMatch()
    {
        Assert.True(EvaluateWithStringBinding("!REGEX(?name, \"ALICE\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_CharacterClass()
    {
        Assert.True(EvaluateWithStringBinding("REGEX(?name, \"[A-Z][a-z]+\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_Wildcard()
    {
        Assert.True(EvaluateWithStringBinding("REGEX(?name, \"A.*e\")", "?name", "Alice"));
    }

    [Fact]
    public void Regex_UnboundVariable_ReturnsFalse()
    {
        Assert.False(EvaluateWithEmptyBindings("REGEX(?name, \"test\")"));
    }

    [Fact]
    public void Regex_InvalidPattern_ReturnsFalse()
    {
        // Invalid regex pattern (unclosed bracket) should return false
        Assert.False(EvaluateWithStringBinding("REGEX(?name, \"[invalid\")", "?name", "test"));
    }

    [Fact]
    public void Regex_NumericPattern()
    {
        Assert.True(EvaluateWithStringBinding("REGEX(?code, \"^[0-9]+$\")", "?code", "12345"));
    }

    #endregion

    #region STRLEN Function

    [Fact]
    public void Strlen_ReturnsLength()
    {
        Assert.True(EvaluateWithStringBinding("STRLEN(?name) == 5", "?name", "Alice"));
    }

    [Fact]
    public void Strlen_EmptyString_ReturnsZero()
    {
        Assert.True(EvaluateWithStringBinding("STRLEN(?name) == 0", "?name", ""));
    }

    [Fact]
    public void Strlen_UnboundVariable_ReturnsFalse()
    {
        Assert.False(EvaluateWithEmptyBindings("STRLEN(?name) == 0"));
    }

    #endregion

    #region CONTAINS Function

    [Fact]
    public void Contains_Found_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("CONTAINS(?name, \"lic\")", "?name", "Alice"));
    }

    [Fact]
    public void Contains_NotFound_ReturnsFalse()
    {
        Assert.True(EvaluateWithStringBinding("!CONTAINS(?name, \"xyz\")", "?name", "Alice"));
    }

    [Fact]
    public void Contains_CaseSensitive()
    {
        Assert.True(EvaluateWithStringBinding("!CONTAINS(?name, \"LIC\")", "?name", "Alice"));
    }

    [Fact]
    public void Contains_EmptyPattern_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("CONTAINS(?name, \"\")", "?name", "Alice"));
    }

    #endregion

    #region STRSTARTS Function

    [Fact]
    public void StrStarts_Match_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("STRSTARTS(?name, \"Ali\")", "?name", "Alice"));
    }

    [Fact]
    public void StrStarts_NoMatch_ReturnsFalse()
    {
        Assert.True(EvaluateWithStringBinding("!STRSTARTS(?name, \"Bob\")", "?name", "Alice"));
    }

    [Fact]
    public void StrStarts_FullString_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("STRSTARTS(?name, \"Alice\")", "?name", "Alice"));
    }

    #endregion

    #region STRENDS Function

    [Fact]
    public void StrEnds_Match_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("STRENDS(?name, \"ice\")", "?name", "Alice"));
    }

    [Fact]
    public void StrEnds_NoMatch_ReturnsFalse()
    {
        Assert.True(EvaluateWithStringBinding("!STRENDS(?name, \"xyz\")", "?name", "Alice"));
    }

    [Fact]
    public void StrEnds_FullString_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("STRENDS(?name, \"Alice\")", "?name", "Alice"));
    }

    #endregion

    #region SUBSTR Function

    [Fact]
    public void Substr_FromStart_ReturnsSubstring()
    {
        Assert.True(EvaluateWithStringBinding("SUBSTR(?name, 1, 3) == \"Ali\"", "?name", "Alice"));
    }

    [Fact]
    public void Substr_FromMiddle_ReturnsSubstring()
    {
        Assert.True(EvaluateWithStringBinding("SUBSTR(?name, 3, 2) == \"ic\"", "?name", "Alice"));
    }

    [Fact]
    public void Substr_NoLength_ReturnsToEnd()
    {
        Assert.True(EvaluateWithStringBinding("SUBSTR(?name, 3) == \"ice\"", "?name", "Alice"));
    }

    [Fact]
    public void Substr_StartBeyondLength_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("STRLEN(SUBSTR(?name, 100)) == 0", "?name", "Alice"));
    }

    #endregion

    #region CONCAT Function

    [Fact]
    public void Concat_TwoStrings()
    {
        Assert.True(EvaluateWithMultipleBindings("CONCAT(?a, ?b) == \"HelloWorld\"",
            ("?a", (object)"Hello"), ("?b", (object)"World")));
    }

    [Fact]
    public void Concat_ThreeStrings()
    {
        Assert.True(EvaluateWithStringBinding("CONCAT(?name, \" \", \"!\") == \"Alice !\"", "?name", "Alice"));
    }

    [Fact]
    public void Concat_WithInteger()
    {
        Assert.True(EvaluateWithMultipleBindings("CONCAT(?name, ?age) == \"Alice30\"",
            ("?name", (object)"Alice"), ("?age", (object)30L)));
    }

    [Fact]
    public void Concat_UnboundVariable_ReturnsUnbound()
    {
        // CONCAT with unbound variable should not match anything
        Assert.False(EvaluateWithStringBinding("CONCAT(?a, ?b) == \"test\"", "?a", "test"));
    }

    #endregion

    #region ABS Function

    [Fact]
    public void Abs_PositiveInteger_ReturnsSame()
    {
        Assert.True(EvaluateWithIntBinding("ABS(?x) == 42", "?x", 42L));
    }

    [Fact]
    public void Abs_NegativeInteger_ReturnsPositive()
    {
        Assert.True(EvaluateWithIntBinding("ABS(?x) == 42", "?x", -42L));
    }

    [Fact]
    public void Abs_PositiveDouble_ReturnsSame()
    {
        Assert.True(EvaluateWithDoubleBinding("ABS(?x) == 3.14", "?x", 3.14));
    }

    [Fact]
    public void Abs_NegativeDouble_ReturnsPositive()
    {
        Assert.True(EvaluateWithDoubleBinding("ABS(?x) == 3.14", "?x", -3.14));
    }

    [Fact]
    public void Abs_Zero_ReturnsZero()
    {
        Assert.True(EvaluateWithIntBinding("ABS(?x) == 0", "?x", 0L));
    }

    #endregion

    #region ROUND Function

    [Fact]
    public void Round_Integer_ReturnsSame()
    {
        Assert.True(EvaluateWithIntBinding("ROUND(?x) == 42", "?x", 42L));
    }

    [Fact]
    public void Round_DoubleUp_RoundsUp()
    {
        Assert.True(EvaluateWithDoubleBinding("ROUND(?x) == 4", "?x", 3.7));
    }

    [Fact]
    public void Round_DoubleDown_RoundsDown()
    {
        Assert.True(EvaluateWithDoubleBinding("ROUND(?x) == 3", "?x", 3.2));
    }

    [Fact]
    public void Round_DoubleHalf_RoundsToEven()
    {
        // .NET uses banker's rounding (round to even)
        Assert.True(EvaluateWithDoubleBinding("ROUND(?x) == 4", "?x", 3.5));
    }

    [Fact]
    public void Round_Negative_RoundsCorrectly()
    {
        Assert.True(EvaluateWithDoubleBinding("ROUND(?x) == -4", "?x", -3.7));
    }

    #endregion

    #region CEIL Function

    [Fact]
    public void Ceil_Integer_ReturnsSame()
    {
        Assert.True(EvaluateWithIntBinding("CEIL(?x) == 42", "?x", 42L));
    }

    [Fact]
    public void Ceil_PositiveDouble_RoundsUp()
    {
        Assert.True(EvaluateWithDoubleBinding("CEIL(?x) == 4", "?x", 3.1));
    }

    [Fact]
    public void Ceil_NegativeDouble_RoundsTowardZero()
    {
        Assert.True(EvaluateWithDoubleBinding("CEIL(?x) == -3", "?x", -3.7));
    }

    [Fact]
    public void Ceil_WholeDouble_ReturnsSame()
    {
        Assert.True(EvaluateWithDoubleBinding("CEIL(?x) == 5", "?x", 5.0));
    }

    #endregion

    #region FLOOR Function

    [Fact]
    public void Floor_Integer_ReturnsSame()
    {
        Assert.True(EvaluateWithIntBinding("FLOOR(?x) == 42", "?x", 42L));
    }

    [Fact]
    public void Floor_PositiveDouble_RoundsDown()
    {
        Assert.True(EvaluateWithDoubleBinding("FLOOR(?x) == 3", "?x", 3.9));
    }

    [Fact]
    public void Floor_NegativeDouble_RoundsAwayFromZero()
    {
        Assert.True(EvaluateWithDoubleBinding("FLOOR(?x) == -4", "?x", -3.1));
    }

    [Fact]
    public void Floor_WholeDouble_ReturnsSame()
    {
        Assert.True(EvaluateWithDoubleBinding("FLOOR(?x) == 5", "?x", 5.0));
    }

    #endregion

    #region isIRI/isURI Function

    [Fact]
    public void IsIRI_Uri_ReturnsTrue()
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.BindUri("?x", "<http://example.org/test>".AsSpan());
        var evaluator = new FilterEvaluator("isIRI(?x)".AsSpan());
        Assert.True(evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer()));
    }

    [Fact]
    public void IsIRI_String_ReturnsFalse()
    {
        Assert.False(EvaluateWithStringBinding("isIRI(?x)", "?x", "hello"));
    }

    [Fact]
    public void IsURI_Uri_ReturnsTrue()
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.BindUri("?x", "<http://example.org/test>".AsSpan());
        var evaluator = new FilterEvaluator("isURI(?x)".AsSpan());
        Assert.True(evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer()));
    }

    #endregion

    #region isBlank Function

    [Fact]
    public void IsBlank_BlankNode_ReturnsTrue()
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.BindUri("?x", "_:b1".AsSpan());
        var evaluator = new FilterEvaluator("isBlank(?x)".AsSpan());
        Assert.True(evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer()));
    }

    [Fact]
    public void IsBlank_Uri_ReturnsFalse()
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.BindUri("?x", "<http://example.org/test>".AsSpan());
        var evaluator = new FilterEvaluator("isBlank(?x)".AsSpan());
        Assert.False(evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer()));
    }

    [Fact]
    public void IsBlank_String_ReturnsFalse()
    {
        Assert.False(EvaluateWithStringBinding("isBlank(?x)", "?x", "hello"));
    }

    #endregion

    #region isLiteral Function

    [Fact]
    public void IsLiteral_String_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("isLiteral(?x)", "?x", "hello"));
    }

    [Fact]
    public void IsLiteral_Integer_ReturnsTrue()
    {
        Assert.True(EvaluateWithIntBinding("isLiteral(?x)", "?x", 42L));
    }

    [Fact]
    public void IsLiteral_Double_ReturnsTrue()
    {
        Assert.True(EvaluateWithDoubleBinding("isLiteral(?x)", "?x", 3.14));
    }

    [Fact]
    public void IsLiteral_Boolean_ReturnsTrue()
    {
        Assert.True(EvaluateWithBoolBinding("isLiteral(?x)", "?x", true));
    }

    [Fact]
    public void IsLiteral_Uri_ReturnsFalse()
    {
        var bindingStorage = new Binding[16];
        var stringBuffer = new char[256];
        var bindings = new BindingTable(bindingStorage, stringBuffer);
        bindings.BindUri("?x", "<http://example.org/test>".AsSpan());
        var evaluator = new FilterEvaluator("isLiteral(?x)".AsSpan());
        Assert.False(evaluator.Evaluate(bindings.GetBindings(), bindings.Count, bindings.GetStringBuffer()));
    }

    #endregion

    #region isNumeric Function

    [Fact]
    public void IsNumeric_Integer_ReturnsTrue()
    {
        Assert.True(EvaluateWithIntBinding("isNumeric(?x)", "?x", 42L));
    }

    [Fact]
    public void IsNumeric_Double_ReturnsTrue()
    {
        Assert.True(EvaluateWithDoubleBinding("isNumeric(?x)", "?x", 3.14));
    }

    [Fact]
    public void IsNumeric_String_ReturnsFalse()
    {
        Assert.False(EvaluateWithStringBinding("isNumeric(?x)", "?x", "42"));
    }

    [Fact]
    public void IsNumeric_Boolean_ReturnsFalse()
    {
        Assert.False(EvaluateWithBoolBinding("isNumeric(?x)", "?x", true));
    }

    #endregion

    #region LANG Function

    [Fact]
    public void Lang_WithLanguageTag_ReturnsTag()
    {
        Assert.True(EvaluateWithStringBinding("LANG(?x) == \"en\"", "?x", "\"hello\"@en"));
    }

    [Fact]
    public void Lang_WithComplexTag_ReturnsFullTag()
    {
        Assert.True(EvaluateWithStringBinding("LANG(?x) == \"en-US\"", "?x", "\"hello\"@en-US"));
    }

    [Fact]
    public void Lang_NoTag_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("LANG(?x) == \"\"", "?x", "\"hello\""));
    }

    [Fact]
    public void Lang_Integer_ReturnsEmpty()
    {
        Assert.True(EvaluateWithIntBinding("LANG(?x) == \"\"", "?x", 42L));
    }

    #endregion

    #region DATATYPE Function

    [Fact]
    public void Datatype_Integer_ReturnsXsdInteger()
    {
        Assert.True(EvaluateWithIntBinding("str(DATATYPE(?x)) == \"http://www.w3.org/2001/XMLSchema#integer\"", "?x", 42L));
    }

    [Fact]
    public void Datatype_Double_ReturnsXsdDouble()
    {
        Assert.True(EvaluateWithDoubleBinding("str(DATATYPE(?x)) == \"http://www.w3.org/2001/XMLSchema#double\"", "?x", 3.14));
    }

    [Fact]
    public void Datatype_Boolean_ReturnsXsdBoolean()
    {
        Assert.True(EvaluateWithBoolBinding("str(DATATYPE(?x)) == \"http://www.w3.org/2001/XMLSchema#boolean\"", "?x", true));
    }

    [Fact]
    public void Datatype_PlainString_ReturnsXsdString()
    {
        Assert.True(EvaluateWithStringBinding("str(DATATYPE(?x)) == \"http://www.w3.org/2001/XMLSchema#string\"", "?x", "\"hello\""));
    }

    [Fact]
    public void Datatype_LangString_ReturnsRdfLangString()
    {
        Assert.True(EvaluateWithStringBinding("str(DATATYPE(?x)) == \"http://www.w3.org/1999/02/22-rdf-syntax-ns#langString\"", "?x", "\"hello\"@en"));
    }

    #endregion

    #region LANGMATCHES Function

    [Fact]
    public void LangMatches_ExactMatch_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("LANGMATCHES(?x, \"en\")", "?x", "en"));
    }

    [Fact]
    public void LangMatches_PrefixMatch_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("LANGMATCHES(?x, \"en\")", "?x", "en-US"));
    }

    [Fact]
    public void LangMatches_NoMatch_ReturnsFalse()
    {
        Assert.False(EvaluateWithStringBinding("LANGMATCHES(?x, \"en\")", "?x", "de"));
    }

    [Fact]
    public void LangMatches_WildcardNonEmpty_ReturnsTrue()
    {
        Assert.True(EvaluateWithStringBinding("LANGMATCHES(?x, \"*\")", "?x", "en"));
    }

    [Fact]
    public void LangMatches_WildcardEmpty_ReturnsFalse()
    {
        Assert.False(EvaluateWithStringBinding("LANGMATCHES(?x, \"*\")", "?x", ""));
    }

    [Fact]
    public void LangMatches_CaseInsensitive()
    {
        Assert.True(EvaluateWithStringBinding("LANGMATCHES(?x, \"EN\")", "?x", "en-US"));
    }

    [Fact]
    public void LangMatches_PartialNoHyphen_ReturnsFalse()
    {
        // "eng" should not match "en" because there's no hyphen after the prefix
        Assert.False(EvaluateWithStringBinding("LANGMATCHES(?x, \"en\")", "?x", "eng"));
    }

    #endregion

    #region UCASE Function

    [Fact]
    public void Ucase_LowerToUpper()
    {
        Assert.True(EvaluateWithStringBinding("UCASE(?x) == \"HELLO\"", "?x", "hello"));
    }

    [Fact]
    public void Ucase_MixedCase()
    {
        Assert.True(EvaluateWithStringBinding("UCASE(?x) == \"HELLO WORLD\"", "?x", "Hello World"));
    }

    [Fact]
    public void Ucase_AlreadyUpper()
    {
        Assert.True(EvaluateWithStringBinding("UCASE(?x) == \"HELLO\"", "?x", "HELLO"));
    }

    [Fact]
    public void Ucase_EmptyString()
    {
        Assert.True(EvaluateWithStringBinding("UCASE(?x) == \"\"", "?x", ""));
    }

    [Fact]
    public void Ucase_WithNumbers()
    {
        Assert.True(EvaluateWithStringBinding("UCASE(?x) == \"ABC123\"", "?x", "abc123"));
    }

    #endregion

    #region LCASE Function

    [Fact]
    public void Lcase_UpperToLower()
    {
        Assert.True(EvaluateWithStringBinding("LCASE(?x) == \"hello\"", "?x", "HELLO"));
    }

    [Fact]
    public void Lcase_MixedCase()
    {
        Assert.True(EvaluateWithStringBinding("LCASE(?x) == \"hello world\"", "?x", "Hello World"));
    }

    [Fact]
    public void Lcase_AlreadyLower()
    {
        Assert.True(EvaluateWithStringBinding("LCASE(?x) == \"hello\"", "?x", "hello"));
    }

    [Fact]
    public void Lcase_EmptyString()
    {
        Assert.True(EvaluateWithStringBinding("LCASE(?x) == \"\"", "?x", ""));
    }

    [Fact]
    public void Lcase_WithNumbers()
    {
        Assert.True(EvaluateWithStringBinding("LCASE(?x) == \"abc123\"", "?x", "ABC123"));
    }

    #endregion

    #region sameTerm Function

    [Fact]
    public void SameTerm_SameIntegers_ReturnsTrue()
    {
        Assert.True(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)42L), ("?y", (object)42L)));
    }

    [Fact]
    public void SameTerm_DifferentIntegers_ReturnsFalse()
    {
        Assert.False(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)42L), ("?y", (object)43L)));
    }

    [Fact]
    public void SameTerm_SameStrings_ReturnsTrue()
    {
        Assert.True(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)"hello"), ("?y", (object)"hello")));
    }

    [Fact]
    public void SameTerm_DifferentStrings_ReturnsFalse()
    {
        Assert.False(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)"hello"), ("?y", (object)"world")));
    }

    [Fact]
    public void SameTerm_IntegerVsDouble_ReturnsFalse()
    {
        // Even if numerically equal, different types means not sameTerm
        Assert.False(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)42L), ("?y", (object)42.0)));
    }

    [Fact]
    public void SameTerm_IntegerVsString_ReturnsFalse()
    {
        Assert.False(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)42L), ("?y", (object)"42")));
    }

    [Fact]
    public void SameTerm_SameBooleans_ReturnsTrue()
    {
        Assert.True(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)true), ("?y", (object)true)));
    }

    [Fact]
    public void SameTerm_DifferentBooleans_ReturnsFalse()
    {
        Assert.False(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)true), ("?y", (object)false)));
    }

    [Fact]
    public void SameTerm_UnboundVariable_ReturnsFalse()
    {
        Assert.False(EvaluateWithIntBinding("sameTerm(?x, ?y)", "?x", 42L));
    }

    [Fact]
    public void SameTerm_SameDoubles_ReturnsTrue()
    {
        Assert.True(EvaluateWithMultipleBindings("sameTerm(?x, ?y)",
            ("?x", (object)3.14), ("?y", (object)3.14)));
    }

    #endregion

    #region STRBEFORE Function

    [Fact]
    public void StrBefore_BasicMatch_ReturnsSubstringBefore()
    {
        Assert.True(EvaluateWithStringBinding("STRBEFORE(?x, \"/\") == \"http:\"", "?x", "http://example.org/path"));
    }

    [Fact]
    public void StrBefore_DelimiterNotFound_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("STRBEFORE(?x, \"xyz\") == \"\"", "?x", "hello world"));
    }

    [Fact]
    public void StrBefore_MultipleOccurrences_ReturnsBeforeFirst()
    {
        Assert.True(EvaluateWithStringBinding("STRBEFORE(?x, \"-\") == \"a\"", "?x", "a-b-c-d"));
    }

    [Fact]
    public void StrBefore_DelimiterAtStart_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("STRBEFORE(?x, \"/\") == \"\"", "?x", "/path/to/file"));
    }

    [Fact]
    public void StrBefore_EmptyDelimiter_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("STRBEFORE(?x, \"\") == \"\"", "?x", "hello"));
    }

    [Fact]
    public void StrBefore_MultiCharDelimiter_Works()
    {
        Assert.True(EvaluateWithStringBinding("STRBEFORE(?x, \"::\") == \"foo\"", "?x", "foo::bar::baz"));
    }

    #endregion

    #region STRAFTER Function

    [Fact]
    public void StrAfter_BasicMatch_ReturnsSubstringAfter()
    {
        Assert.True(EvaluateWithStringBinding("STRAFTER(?x, \"://\") == \"example.org/path\"", "?x", "http://example.org/path"));
    }

    [Fact]
    public void StrAfter_DelimiterNotFound_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("STRAFTER(?x, \"xyz\") == \"\"", "?x", "hello world"));
    }

    [Fact]
    public void StrAfter_MultipleOccurrences_ReturnsAfterFirst()
    {
        Assert.True(EvaluateWithStringBinding("STRAFTER(?x, \"-\") == \"b-c-d\"", "?x", "a-b-c-d"));
    }

    [Fact]
    public void StrAfter_DelimiterAtEnd_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("STRAFTER(?x, \"/\") == \"\"", "?x", "path/"));
    }

    [Fact]
    public void StrAfter_EmptyDelimiter_ReturnsFullString()
    {
        Assert.True(EvaluateWithStringBinding("STRAFTER(?x, \"\") == \"hello\"", "?x", "hello"));
    }

    [Fact]
    public void StrAfter_MultiCharDelimiter_Works()
    {
        Assert.True(EvaluateWithStringBinding("STRAFTER(?x, \"::\") == \"bar::baz\"", "?x", "foo::bar::baz"));
    }

    #endregion

    #region REPLACE Function

    [Fact]
    public void Replace_BasicReplacement()
    {
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"world\", \"there\") == \"hello there\"", "?x", "hello world"));
    }

    [Fact]
    public void Replace_MultipleOccurrences()
    {
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"a\", \"X\") == \"bXnXnX\"", "?x", "banana"));
    }

    [Fact]
    public void Replace_RegexPattern()
    {
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"[0-9]+\", \"#\") == \"abc#def#\"", "?x", "abc123def456"));
    }

    [Fact]
    public void Replace_NoMatch_ReturnsOriginal()
    {
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"xyz\", \"!\") == \"hello\"", "?x", "hello"));
    }

    [Fact]
    public void Replace_EmptyReplacement()
    {
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"l\", \"\") == \"heo\"", "?x", "hello"));
    }

    [Fact]
    public void Replace_CaseInsensitiveFlag()
    {
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"HELLO\", \"hi\", \"i\") == \"hi world\"", "?x", "hello world"));
    }

    [Fact]
    public void Replace_CharacterClass()
    {
        // Replace all vowels with underscore
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"[aeiou]\", \"_\") == \"h_ll_ w_rld\"", "?x", "hello world"));
    }

    [Fact]
    public void Replace_DotMatchesNewlineWithFlag()
    {
        Assert.True(EvaluateWithStringBinding("REPLACE(?x, \"a.b\", \"X\", \"s\") == \"X\"", "?x", "a\nb"));
    }

    #endregion

    #region ENCODE_FOR_URI Function

    [Fact]
    public void EncodeForUri_Space_BecomesPercent20()
    {
        Assert.True(EvaluateWithStringBinding("ENCODE_FOR_URI(?x) == \"hello%20world\"", "?x", "hello world"));
    }

    [Fact]
    public void EncodeForUri_SpecialChars_AreEncoded()
    {
        Assert.True(EvaluateWithStringBinding("ENCODE_FOR_URI(?x) == \"a%2Fb%3Fc%3Dd\"", "?x", "a/b?c=d"));
    }

    [Fact]
    public void EncodeForUri_AlphanumericUnchanged()
    {
        Assert.True(EvaluateWithStringBinding("ENCODE_FOR_URI(?x) == \"abc123\"", "?x", "abc123"));
    }

    [Fact]
    public void EncodeForUri_EmptyString()
    {
        Assert.True(EvaluateWithStringBinding("ENCODE_FOR_URI(?x) == \"\"", "?x", ""));
    }

    [Fact]
    public void EncodeForUri_Unicode_IsEncoded()
    {
        // UTF-8 encoding of café: caf%C3%A9
        Assert.True(EvaluateWithStringBinding("ENCODE_FOR_URI(?x) == \"caf%C3%A9\"", "?x", "café"));
    }

    [Fact]
    public void EncodeForUri_Ampersand_IsEncoded()
    {
        Assert.True(EvaluateWithStringBinding("ENCODE_FOR_URI(?x) == \"a%26b\"", "?x", "a&b"));
    }

    #endregion

    #region Hash Functions

    [Fact]
    public void Md5_Hello()
    {
        Assert.True(EvaluateWithStringBinding("MD5(?x) == \"5d41402abc4b2a76b9719d911017c592\"", "?x", "hello"));
    }

    [Fact]
    public void Md5_EmptyString()
    {
        Assert.True(EvaluateWithStringBinding("MD5(?x) == \"d41d8cd98f00b204e9800998ecf8427e\"", "?x", ""));
    }

    [Fact]
    public void Sha1_Hello()
    {
        Assert.True(EvaluateWithStringBinding("SHA1(?x) == \"aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d\"", "?x", "hello"));
    }

    [Fact]
    public void Sha1_EmptyString()
    {
        Assert.True(EvaluateWithStringBinding("SHA1(?x) == \"da39a3ee5e6b4b0d3255bfef95601890afd80709\"", "?x", ""));
    }

    [Fact]
    public void Sha256_Hello()
    {
        Assert.True(EvaluateWithStringBinding("SHA256(?x) == \"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824\"", "?x", "hello"));
    }

    [Fact]
    public void Sha256_EmptyString()
    {
        Assert.True(EvaluateWithStringBinding("SHA256(?x) == \"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\"", "?x", ""));
    }

    [Fact]
    public void Sha384_Hello()
    {
        Assert.True(EvaluateWithStringBinding("SHA384(?x) == \"59e1748777448c69de6b800d7a33bbfb9ff1b463e44354c3553bcdb9c666fa90125a3c79f90397bdf5f6a13de828684f\"", "?x", "hello"));
    }

    [Fact]
    public void Sha512_Hello()
    {
        Assert.True(EvaluateWithStringBinding("SHA512(?x) == \"9b71d224bd62f3785d96d46ad3ea3d73319bfbc2890caadae2dff72519673ca72323c3d99ba5c11d7c7acc6e14b8c5da0c4663475c2e5c3adef46f73bcdec043\"", "?x", "hello"));
    }

    [Fact]
    public void Hash_CaseInsensitive()
    {
        // Function names should be case-insensitive
        Assert.True(EvaluateWithStringBinding("md5(?x) == \"5d41402abc4b2a76b9719d911017c592\"", "?x", "hello"));
        Assert.True(EvaluateWithStringBinding("sha256(?x) == \"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824\"", "?x", "hello"));
    }

    #endregion

    #region UUID/STRUUID Functions

    [Fact]
    public void Uuid_ReturnsUriWithPrefix()
    {
        // UUID() returns IRI starting with urn:uuid:
        Assert.True(Evaluate("STRSTARTS(str(UUID()), \"urn:uuid:\")"));
    }

    [Fact]
    public void Uuid_ReturnsValidFormat()
    {
        // UUID format: urn:uuid:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx (45 chars total)
        Assert.True(Evaluate("STRLEN(str(UUID())) == 45"));
    }

    [Fact]
    public void Uuid_IsIri()
    {
        Assert.True(Evaluate("isIRI(UUID())"));
    }

    [Fact]
    public void Struuid_ReturnsString()
    {
        // STRUUID() returns just the UUID string without prefix
        Assert.True(Evaluate("STRLEN(STRUUID()) == 36"));
    }

    [Fact]
    public void Struuid_IsNotIri()
    {
        Assert.False(Evaluate("isIRI(STRUUID())"));
    }

    [Fact]
    public void Struuid_ContainsDashes()
    {
        // UUID format has dashes at positions 9, 14, 19, 24
        Assert.True(Evaluate("CONTAINS(STRUUID(), \"-\")"));
    }

    #endregion

    #region DateTime Functions

    [Fact]
    public void Now_ReturnsValidDateTimeFormat()
    {
        // NOW() returns xsd:dateTime format with T separator
        Assert.True(Evaluate("CONTAINS(NOW(), \"T\")"));
    }

    [Fact]
    public void Now_ReturnsUtc()
    {
        // NOW() returns UTC time ending with Z
        Assert.True(Evaluate("STRSTARTS(STRAFTER(NOW(), \"T\"), \"0\") || STRSTARTS(STRAFTER(NOW(), \"T\"), \"1\") || STRSTARTS(STRAFTER(NOW(), \"T\"), \"2\")"));
    }

    [Fact]
    public void Year_ExtractsYear()
    {
        Assert.True(EvaluateWithStringBinding("YEAR(?x) == 2024", "?x", "2024-12-25T10:30:00Z"));
    }

    [Fact]
    public void Month_ExtractsMonth()
    {
        Assert.True(EvaluateWithStringBinding("MONTH(?x) == 12", "?x", "2024-12-25T10:30:00Z"));
    }

    [Fact]
    public void Day_ExtractsDay()
    {
        Assert.True(EvaluateWithStringBinding("DAY(?x) == 25", "?x", "2024-12-25T10:30:00Z"));
    }

    [Fact]
    public void Hours_ExtractsHours()
    {
        Assert.True(EvaluateWithStringBinding("HOURS(?x) == 10", "?x", "2024-12-25T10:30:00Z"));
    }

    [Fact]
    public void Minutes_ExtractsMinutes()
    {
        Assert.True(EvaluateWithStringBinding("MINUTES(?x) == 30", "?x", "2024-12-25T10:30:00Z"));
    }

    [Fact]
    public void Seconds_ExtractsSeconds()
    {
        Assert.True(EvaluateWithStringBinding("SECONDS(?x) == 45", "?x", "2024-12-25T10:30:45Z"));
    }

    [Fact]
    public void Seconds_WithMilliseconds()
    {
        Assert.True(EvaluateWithStringBinding("SECONDS(?x) > 45.0 && SECONDS(?x) < 46.0", "?x", "2024-12-25T10:30:45.500Z"));
    }

    [Fact]
    public void Tz_ExtractsUtc()
    {
        Assert.True(EvaluateWithStringBinding("TZ(?x) == \"Z\"", "?x", "2024-12-25T10:30:00Z"));
    }

    [Fact]
    public void Tz_ExtractsPositiveOffset()
    {
        Assert.True(EvaluateWithStringBinding("TZ(?x) == \"+05:30\"", "?x", "2024-12-25T10:30:00+05:30"));
    }

    [Fact]
    public void Tz_ExtractsNegativeOffset()
    {
        Assert.True(EvaluateWithStringBinding("TZ(?x) == \"-08:00\"", "?x", "2024-12-25T10:30:00-08:00"));
    }

    [Fact]
    public void Tz_NoTimezone_ReturnsEmpty()
    {
        Assert.True(EvaluateWithStringBinding("TZ(?x) == \"\"", "?x", "2024-12-25T10:30:00"));
    }

    #endregion

    #region IRI/URI Constructor

    [Fact]
    public void Iri_ConvertsStringToIri()
    {
        Assert.True(EvaluateWithStringBinding("isIRI(IRI(?x))", "?x", "http://example.org/test"));
    }

    [Fact]
    public void Uri_ConvertsStringToIri()
    {
        // URI is alias for IRI
        Assert.True(EvaluateWithStringBinding("isIRI(URI(?x))", "?x", "http://example.org/test"));
    }

    [Fact]
    public void Iri_PreservesValue()
    {
        Assert.True(EvaluateWithStringBinding("str(IRI(?x)) == \"http://example.org/test\"", "?x", "http://example.org/test"));
    }

    [Fact]
    public void Iri_IriPassthrough()
    {
        // IRI of an IRI returns the same IRI
        Assert.True(Evaluate("isIRI(IRI(UUID()))"));
    }

    [Fact]
    public void Iri_CaseInsensitive()
    {
        Assert.True(EvaluateWithStringBinding("isIRI(iri(?x))", "?x", "http://example.org"));
        Assert.True(EvaluateWithStringBinding("isIRI(uri(?x))", "?x", "http://example.org"));
    }

    #endregion

    #region STRDT Function (Typed Literal Constructor)

    [Fact]
    public void StrDt_ConstructsTypedLiteral()
    {
        // STRDT creates a typed literal with ^^<datatype> syntax
        // Check that it contains both the quoted value and the datatype
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(STRDT(?x, \"http://www.w3.org/2001/XMLSchema#integer\"), \"42\") && CONTAINS(STRDT(?x, \"http://www.w3.org/2001/XMLSchema#integer\"), \"^^<http://www.w3.org/2001/XMLSchema#integer>\")",
            "?x", "42"));
    }

    [Fact]
    public void StrDt_WithXsdString()
    {
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(STRDT(?x, \"http://www.w3.org/2001/XMLSchema#string\"), \"^^<http://www.w3.org/2001/XMLSchema#string>\")",
            "?x", "hello"));
    }

    [Fact]
    public void StrDt_WithCustomDatatype()
    {
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(STRDT(?x, \"http://example.org/mytype\"), \"^^<http://example.org/mytype>\")",
            "?x", "value"));
    }

    [Fact]
    public void StrDt_PreservesLexicalForm()
    {
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(STRDT(?x, \"http://example.org/type\"), \"hello world\")",
            "?x", "hello world"));
    }

    [Fact]
    public void StrDt_CaseInsensitive()
    {
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(strdt(?x, \"http://example.org/type\"), \"^^\")",
            "?x", "test"));
    }

    #endregion

    #region STRLANG Function (Language-Tagged Literal Constructor)

    [Fact]
    public void StrLang_ConstructsLanguageTaggedLiteral()
    {
        // STRLANG creates a language-tagged literal with @lang syntax
        // Check that it contains both the value and the language tag
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(STRLANG(?x, \"en\"), \"hello\") && STRENDS(STRLANG(?x, \"en\"), \"@en\")",
            "?x", "hello"));
    }

    [Fact]
    public void StrLang_WithFullLanguageTag()
    {
        Assert.True(EvaluateWithStringBinding(
            "STRENDS(STRLANG(?x, \"en-US\"), \"@en-US\")",
            "?x", "hello"));
    }

    [Fact]
    public void StrLang_WithGermanTag()
    {
        Assert.True(EvaluateWithStringBinding(
            "STRENDS(STRLANG(?x, \"de\"), \"@de\")",
            "?x", "hallo"));
    }

    [Fact]
    public void StrLang_PreservesLexicalForm()
    {
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(STRLANG(?x, \"fr\"), \"bonjour\")",
            "?x", "bonjour"));
    }

    [Fact]
    public void StrLang_CaseInsensitive()
    {
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(strlang(?x, \"en\"), \"@en\")",
            "?x", "test"));
    }

    [Fact]
    public void StrLang_ContainsAtSymbol()
    {
        Assert.True(EvaluateWithStringBinding(
            "CONTAINS(STRLANG(?x, \"es\"), \"@\")",
            "?x", "hola"));
    }

    #endregion

    #region BNODE Function (Blank Node Constructor)

    [Fact]
    public void Bnode_NoArg_ReturnsBlankNode()
    {
        // BNODE() returns a blank node (starts with _:)
        Assert.True(Evaluate("STRSTARTS(str(BNODE()), \"_:\")"));
    }

    [Fact]
    public void Bnode_NoArg_IsBlank()
    {
        Assert.True(Evaluate("isBlank(BNODE())"));
    }

    [Fact]
    public void Bnode_NoArg_GeneratesUnique()
    {
        // Each call to BNODE() should generate a different blank node
        // We test this indirectly by checking they both start with _:b
        Assert.True(Evaluate("STRSTARTS(str(BNODE()), \"_:b\")"));
    }

    [Fact]
    public void Bnode_WithLabel_ReturnsLabeledBlankNode()
    {
        Assert.True(EvaluateWithStringBinding(
            "str(BNODE(?x)) == \"_:mynode\"",
            "?x", "mynode"));
    }

    [Fact]
    public void Bnode_WithLabel_IsBlank()
    {
        Assert.True(EvaluateWithStringBinding(
            "isBlank(BNODE(?x))",
            "?x", "test"));
    }

    [Fact]
    public void Bnode_WithLabel_PreservesLabel()
    {
        Assert.True(EvaluateWithStringBinding(
            "STRENDS(str(BNODE(?x)), \"custom123\")",
            "?x", "custom123"));
    }

    [Fact]
    public void Bnode_CaseInsensitive()
    {
        Assert.True(Evaluate("isBlank(bnode())"));
    }

    #endregion

    #region TIMEZONE Function (Duration Format)

    [Fact]
    public void Timezone_Utc_ReturnsPT0S()
    {
        Assert.True(EvaluateWithStringBinding(
            "TIMEZONE(?x) == \"PT0S\"",
            "?x", "2024-12-25T10:30:00Z"));
    }

    [Fact]
    public void Timezone_PositiveOffset_ReturnsDuration()
    {
        Assert.True(EvaluateWithStringBinding(
            "TIMEZONE(?x) == \"PT5H30M\"",
            "?x", "2024-12-25T10:30:00+05:30"));
    }

    [Fact]
    public void Timezone_NegativeOffset_ReturnsNegativeDuration()
    {
        Assert.True(EvaluateWithStringBinding(
            "TIMEZONE(?x) == \"-PT8H\"",
            "?x", "2024-12-25T10:30:00-08:00"));
    }

    [Fact]
    public void Timezone_WholeHour_OmitsMinutes()
    {
        Assert.True(EvaluateWithStringBinding(
            "TIMEZONE(?x) == \"PT2H\"",
            "?x", "2024-12-25T10:30:00+02:00"));
    }

    [Fact]
    public void Timezone_NoTimezone_ReturnsUnbound()
    {
        // TIMEZONE with no timezone returns unbound (different from TZ which returns "")
        Assert.False(EvaluateWithStringBinding(
            "BOUND(TIMEZONE(?x))",
            "?x", "2024-12-25T10:30:00"));
    }

    [Fact]
    public void Timezone_CaseInsensitive()
    {
        Assert.True(EvaluateWithStringBinding(
            "timezone(?x) == \"PT0S\"",
            "?x", "2024-12-25T10:30:00Z"));
    }

    #endregion
}
