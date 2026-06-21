using System;
using System.Collections.Generic;
using System.Globalization;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using Xunit;
using Xunit.Abstractions;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-049 reconciliation (b): differential probe. Drives a battery of constant expressions through
/// BOTH the unified <see cref="FilterEvaluator.EvaluateToValue"/> and the conformant
/// <see cref="BindExpressionEvaluator"/> (the W3C-validated oracle), and reports every divergence in
/// rendered output form. The function library must converge so that BIND can route through
/// EvaluateToValue. Non-deterministic functions (NOW/UUID/RAND) are form-checked separately.
///
/// NOTE: this compares against BindExpressionEvaluator, which ADR-049 step 5 deletes. Before that
/// deletion the deterministic assertions here will be frozen to explicit expected literals.
/// </summary>
public class Adr049FunctionReconciliationProbeTests
{
    private readonly ITestOutputHelper _out;
    public Adr049FunctionReconciliationProbeTests(ITestOutputHelper output) => _out = output;

    private static string Render(scoped in Value v) => v.Type switch
    {
        ValueType.Unbound => "UNBOUND",
        ValueType.Integer => "INT:" + v.IntegerValue.ToString(CultureInfo.InvariantCulture),
        ValueType.Double => "DBL:" + v.DoubleValue.ToString("G17", CultureInfo.InvariantCulture),
        ValueType.Boolean => "BOOL:" + (v.BooleanValue ? "true" : "false"),
        ValueType.String => "STR:" + v.StringValue.ToString(),
        ValueType.Uri => "URI:" + v.StringValue.ToString(),
        _ => "??"
    };

    private static string ViaFilter(string expr)
    {
        var e = new FilterEvaluator(expr.AsSpan());
        return Render(e.EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null, ReadOnlySpan<char>.Empty));
    }

    private static string ViaBind(string expr)
    {
        var e = new BindExpressionEvaluator(
            expr.AsSpan(), ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty);
        return Render(e.Evaluate());
    }

    // Deterministic expressions: EvaluateToValue must match the conformant BindExpressionEvaluator.
    [Fact]
    public void Differential_DeterministicFunctions_Converge()
    {
        const string dt = "\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>";
        string[] battery =
        {
            // arithmetic / numeric (expected to already match)
            "3 + 4 * 2", "10 - 3", "7 / 2", "2 * 3 + 1", "ABS(0 - 5)",
            // numeric rounding — datatype preservation
            "CEIL(1.5)", "FLOOR(1.5)", "ROUND(2.5)", "ROUND(2.4)", "CEIL(4)", "FLOOR(7)",
            // strings (expected to already match)
            "STR(42)", "STRLEN(\"hello\")", "UCASE(\"abc\")", "LCASE(\"ABC\")",
            "CONCAT(\"a\", \"b\")", "SUBSTR(\"abcdef\", 2, 3)",
            "STRBEFORE(\"abcdef\", \"cd\")", "STRAFTER(\"abcdef\", \"cd\")",
            // REPLACE — quoting
            "REPLACE(\"abracadabra\", \"a\", \"X\")",
            // IRI / URI — bracketing
            "IRI(\"http://example.org/x\")", "URI(\"http://example.org/y\")",
            // date/time accessors over a constant typed literal
            "YEAR(" + dt + ")", "MONTH(" + dt + ")", "DAY(" + dt + ")",
            "HOURS(" + dt + ")", "MINUTES(" + dt + ")", "SECONDS(" + dt + ")",
            "TZ(" + dt + ")", "TIMEZONE(" + dt + ")",
            // conditionals
            "IF(1 > 0, 10, 20)", "IF(1 < 0, 10, 20)", "COALESCE(42, 0)",
            // completeness sweep over the rest of the shared library (oracle = bind)
            // NOTE: inline @lang-literal cases (DATATYPE/LANG/UCASE/LCASE/CONCAT over "x"@en)
            // are deliberately excluded — FilterEvaluator.ParseStringLiteral does not yet parse
            // the @lang suffix (it strips it), a separate ADR-049 finding tracked for the wiring
            // phase. LANG("plain") also diverges, but there FilterEvaluator is spec-correct ("")
            // and BindExpressionEvaluator is the buggy side (UNBOUND) — the spec is the oracle.
            "STR(3.14)", "STR(42)",
            "DATATYPE(\"5\"^^<http://www.w3.org/2001/XMLSchema#integer>)",
            "DATATYPE(\"plain\")",
            "STRDT(\"5\", <http://www.w3.org/2001/XMLSchema#integer>)",
            "STRLANG(\"hi\", \"en\")",
            "SUBSTR(\"abcdef\", 3)", "ENCODE_FOR_URI(\"a b\")",
            "MD5(\"abc\")", "SHA1(\"abc\")", "SHA256(\"abc\")", "SHA512(\"abc\")",
            "ABS(0 - 5)", "ABS(5)",
            "STRLEN(\"héllo\")",
        };

        var mismatches = new List<string>();
        foreach (var expr in battery)
        {
            string f, b;
            try { f = ViaFilter(expr); } catch (Exception ex) { f = "EX:" + ex.GetType().Name; }
            try { b = ViaBind(expr); } catch (Exception ex) { b = "EX:" + ex.GetType().Name; }
            if (f != b)
                mismatches.Add($"  {expr,-60}  filter={f,-40}  bind(oracle)={b}");
        }

        if (mismatches.Count > 0)
            _out.WriteLine("DIVERGENCES (filter vs bind-oracle):\n" + string.Join("\n", mismatches));

        Assert.Empty(mismatches);
    }

    // Non-deterministic functions: assert the conformant output FORM (can't equality-check time/uuid).
    [Fact]
    public void NonDeterministicFunctions_ProduceConformantForms()
    {
        var now = ViaFilter("NOW()");
        Assert.StartsWith("STR:\"", now);
        Assert.Contains("^^<http://www.w3.org/2001/XMLSchema#dateTime>", now);

        var uuid = ViaFilter("UUID()");
        Assert.StartsWith("URI:<urn:uuid:", uuid);
        Assert.EndsWith(">", uuid);

        var struuid = ViaFilter("STRUUID()");
        Assert.StartsWith("STR:\"", struuid);
        Assert.EndsWith("\"", struuid);

        var bnode = ViaFilter("BNODE(\"x\")");
        Assert.Matches(@"^URI:_:r\d+_x$", bnode);

        var rand = ViaFilter("RAND()");
        Assert.StartsWith("DBL:", rand);
        var r = double.Parse(rand.Substring(4), CultureInfo.InvariantCulture);
        Assert.InRange(r, 0.0, 1.0);
    }
}
