using System;
using System.Collections.Generic;
using System.Globalization;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using Xunit;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-049 reconciliation lock for the unified <c>[110] Expression</c> function library
/// (<see cref="FilterEvaluator.EvaluateToValue"/>). Originally a differential probe against the
/// (now-deleted) BindExpressionEvaluator; the converged conformant output forms are frozen here as
/// explicit literals. The W3C suite is the primary conformance gate — this pins the exact lexical
/// output of every shared built-in so a future change to a function's form is caught immediately.
/// Where the deleted evaluator was buggy (LANG; STRDT with an inline IRI datatype) the spec is the
/// oracle and the case is asserted explicitly below.
/// </summary>
public class Adr049FunctionReconciliationProbeTests
{
    // A value as the term it binds (ADR-056 D6); an error as UNBOUND
    private static string Render(scoped in Value v) => v.Type == ValueType.Unbound ? "UNBOUND" : v.Term();

    private const string Xsd = "^^<http://www.w3.org/2001/XMLSchema#";

    private static string ViaFilter(string expr)
    {
        var e = new FilterEvaluator(expr.AsSpan());
        return Render(e.EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null, ReadOnlySpan<char>.Empty));
    }

    // Frozen conformant output forms (ADR-049), as the term each binds (ADR-056): every shared built-in's exact
    // output, lexical form and datatype, is pinned.
    [Fact]
    public void Functions_ProduceConformantForms()
    {
        var cases = new (string expr, string expected)[]
        {
            ("3 + 4 * 2", "\"11\"" + Xsd + "integer>"),
            ("10 - 3", "\"7\"" + Xsd + "integer>"),
            ("7 / 2", "\"3.5\"" + Xsd + "decimal>"), // integer division is a decimal (XPath op:numeric-divide)
            ("2 * 3 + 1", "\"7\"" + Xsd + "integer>"),
            ("ABS(0 - 5)", "\"5\"" + Xsd + "integer>"),
            ("CEIL(1.5)", "\"2.0\"" + Xsd + "decimal>"), // 1.5 is a decimal, and so is its ceiling
            ("FLOOR(1.5)", "\"1.0\"" + Xsd + "decimal>"),
            ("ROUND(2.5)", "\"3.0\"" + Xsd + "decimal>"),
            ("ROUND(2.4)", "\"2.0\"" + Xsd + "decimal>"),
            ("ROUND(-2.5)", "\"-2.0\"" + Xsd + "decimal>"), // fn:round rounds a half towards positive infinity
            ("CEIL(4)", "\"4\"" + Xsd + "integer>"),
            ("FLOOR(7)", "\"7\"" + Xsd + "integer>"),
            ("STR(42)", "\"42\""),
            ("STR(01)", "\"01\""), // a constant is its term: the lexical form as written
            ("STRLEN(\"hello\")", "\"5\"" + Xsd + "integer>"),
            ("UCASE(\"abc\")", "\"ABC\""),
            ("LCASE(\"ABC\")", "\"abc\""),
            ("CONCAT(\"a\", \"b\")", "\"ab\""),
            ("SUBSTR(\"abcdef\", 2, 3)", "\"bcd\""),
            ("STRBEFORE(\"abcdef\", \"cd\")", "\"ab\""),
            ("STRAFTER(\"abcdef\", \"cd\")", "\"ef\""),
            ("REPLACE(\"abracadabra\", \"a\", \"X\")", "\"XbrXcXdXbrX\""),
            ("IRI(\"http://example.org/x\")", "<http://example.org/x>"),
            ("URI(\"http://example.org/y\")", "<http://example.org/y>"),
            ("YEAR(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"2010\"" + Xsd + "integer>"),
            ("MONTH(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"6\"" + Xsd + "integer>"),
            ("DAY(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"21\"" + Xsd + "integer>"),
            ("HOURS(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"11\"" + Xsd + "integer>"),
            ("MINUTES(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"28\"" + Xsd + "integer>"),
            ("SECONDS(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"1.123456\"" + Xsd + "decimal>"),
            ("TZ(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"Z\""),
            ("TIMEZONE(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"PT0S\"" + Xsd + "dayTimeDuration>"),
            // non-Z offsets: components come from the lexical form, no timezone conversion
            ("HOURS(\"2010-12-21T15:38:02-08:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"15\"" + Xsd + "integer>"),
            ("DAY(\"2010-12-21T15:38:02-08:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"21\"" + Xsd + "integer>"),
            ("TIMEZONE(\"2010-12-21T15:38:02-08:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "\"-PT8H\"" + Xsd + "dayTimeDuration>"),
            ("IF(1 > 0, 10, 20)", "\"10\"" + Xsd + "integer>"),
            ("IF(1 < 0, 10, 20)", "\"20\"" + Xsd + "integer>"),
            ("COALESCE(42, 0)", "\"42\"" + Xsd + "integer>"),
            ("STR(3.14)", "\"3.14\""),
            ("DATATYPE(\"5\"^^<http://www.w3.org/2001/XMLSchema#integer>)", "<http://www.w3.org/2001/XMLSchema#integer>"),
            ("DATATYPE(\"5\"^^<http://www.w3.org/2001/XMLSchema#short>)", "<http://www.w3.org/2001/XMLSchema#short>"),
            ("DATATYPE(1.0)", "<http://www.w3.org/2001/XMLSchema#decimal>"),
            ("DATATYPE(1e3)", "<http://www.w3.org/2001/XMLSchema#double>"),
            ("DATATYPE(\"plain\")", "<http://www.w3.org/2001/XMLSchema#string>"),
            ("DATATYPE(\"hi\"@en)", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#langString>"),
            ("STRLANG(\"hi\", \"en\")", "\"hi\"@en"),
            ("UCASE(\"abc\"@en)", "\"ABC\"@en"),
            ("LCASE(\"ABC\"@en)", "\"abc\"@en"),
            ("CONCAT(\"a\"@en, \"b\"@en)", "\"ab\"@en"),
            ("SUBSTR(\"abcdef\", 3)", "\"cdef\""),
            ("ENCODE_FOR_URI(\"a b\")", "\"a%20b\""),
            ("MD5(\"abc\")", "\"900150983cd24fb0d6963f7d28e17f72\""),
            ("SHA1(\"abc\")", "\"a9993e364706816aba3e25717850c26c9cd0d89d\""),
            ("SHA256(\"abc\")", "\"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\""),
            ("SHA512(\"abc\")", "\"ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f\""),
            ("ABS(5)", "\"5\"" + Xsd + "integer>"),
            ("STRLEN(\"héllo\")", "\"5\"" + Xsd + "integer>"),
            ("DATATYPE(\"PT0S\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>)", "<http://www.w3.org/2001/XMLSchema#dayTimeDuration>"),
            // No prologue here, so xsd: is undeclared, and an undeclared datatype prefix is an error (ADR-056 D2)
            ("DATATYPE(\"2024-01-01\"^^xsd:date)", "UNBOUND"),
        };

        var regressions = new List<string>();
        foreach (var (expr, expected) in cases)
        {
            string f;
            try { f = ViaFilter(expr); } catch (Exception ex) { f = "EX:" + ex.GetType().Name; }
            if (f != expected)
                regressions.Add($"  {expr,-70}  got={f,-40}  expected={expected}");
        }

        Assert.True(regressions.Count == 0, "Function-form regressions:\n" + string.Join("\n", regressions));
    }

    // Non-deterministic functions: assert the conformant output FORM (can't equality-check time/uuid).
    [Fact]
    public void NonDeterministicFunctions_ProduceConformantForms()
    {
        var now = ViaFilter("NOW()");
        Assert.StartsWith("\"", now);
        Assert.Contains("^^<http://www.w3.org/2001/XMLSchema#dateTime>", now);

        var uuid = ViaFilter("UUID()");
        Assert.StartsWith("<urn:uuid:", uuid);
        Assert.EndsWith(">", uuid);

        var struuid = ViaFilter("STRUUID()");
        Assert.StartsWith("\"", struuid);
        Assert.EndsWith("\"", struuid);

        var bnode = ViaFilter("BNODE(\"x\")");
        Assert.Matches(@"^_:g[-0-9A-Z_a-z]{22}r\d+h[0-9a-f]{16}$", bnode); // the process scope's token, row, label hash

        var rand = ViaFilter("RAND()");
        Assert.EndsWith(Xsd + "double>", rand);
        var r = double.Parse(rand[1..rand.IndexOf('"', 1)], CultureInfo.InvariantCulture);
        Assert.InRange(r, 0.0, 1.0);
    }

    // ADR-049: BIND now evaluates the full [110] Expression via EvaluateToValue, including ||/&&.
    // Before the unification the BIND evaluator had no [111] ConditionalOrExpression and silently
    // dropped the right operand, so (x || y) was non-conformant. Locked as the demonstrated-gap test.
    [Fact]
    public void Bind_WithLogicalOr_Evaluates()
    {
        Assert.Equal("\"true\"" + Xsd + "boolean>", ViaFilter("(5 > 100 || 5 < 100)"));
        Assert.Equal("\"false\"" + Xsd + "boolean>", ViaFilter("(5 > 100 || 5 > 100)"));
        Assert.Equal("\"true\"" + Xsd + "boolean>", ViaFilter("(5 < 100 && 5 > 0)"));
    }

    // LANG: spec is the oracle (the deleted BindExpressionEvaluator returned UNBOUND, which is wrong).
    // LANG of a lang-tagged literal is its tag; LANG of any other literal is the empty string; LANG of an IRI is an
    // error.
    [Fact]
    public void LangFunction_ConformsToSpec()
    {
        Assert.Equal("\"en\"", ViaFilter("LANG(\"hi\"@en)"));
        Assert.Equal("\"\"", ViaFilter("LANG(\"plain\")"));
        Assert.Equal("UNBOUND", ViaFilter("LANG(<http://example.org/x>)")); // only a literal has a tag (§17.4.2.6)
    }

    // STRDT with an inline full-IRI datatype: spec is the oracle. FilterEvaluator emits a single
    // angle-bracket pair; the deleted BindExpressionEvaluator double-bracketed (^^<<…>>) — its bug.
    [Fact]
    public void Strdt_InlineIriDatatype_SingleBracketed()
    {
        Assert.Equal("\"5\"^^<http://www.w3.org/2001/XMLSchema#integer>",
            ViaFilter("STRDT(\"5\", <http://www.w3.org/2001/XMLSchema#integer>)"));
    }

    // Base-IRI parity (ADR-049 step 2): a relative ref in IRI()/URI() resolves against the base;
    // an absolute ref ignores it.
    [Fact]
    public void Iri_ResolvesRelativeAgainstBase()
    {
        var e1 = new FilterEvaluator("IRI(\"foo\")".AsSpan());
        Assert.Equal("<http://example.org/foo>", Render(e1.EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null,
            ReadOnlySpan<char>.Empty, "http://example.org/".AsSpan())));

        var e2 = new FilterEvaluator("IRI(\"http://other.org/x\")".AsSpan());
        Assert.Equal("<http://other.org/x>", Render(e2.EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null,
            ReadOnlySpan<char>.Empty, "http://example.org/".AsSpan())));
    }
}
