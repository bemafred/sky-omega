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

    // Frozen conformant output forms (ADR-049). Every shared built-in's exact lexical output is
    // pinned; G17 is used for doubles so any precision change is caught.
    [Fact]
    public void Functions_ProduceConformantForms()
    {
        var cases = new (string expr, string expected)[]
        {
            ("3 + 4 * 2", "INT:11"),
            ("10 - 3", "INT:7"),
            ("7 / 2", "DBL:3.5"),
            ("2 * 3 + 1", "INT:7"),
            ("ABS(0 - 5)", "INT:5"),
            ("CEIL(1.5)", "DBL:2"),
            ("FLOOR(1.5)", "DBL:1"),
            ("ROUND(2.5)", "DBL:3"),
            ("ROUND(2.4)", "DBL:2"),
            ("CEIL(4)", "INT:4"),
            ("FLOOR(7)", "INT:7"),
            ("STR(42)", "STR:42"),
            ("STRLEN(\"hello\")", "INT:5"),
            ("UCASE(\"abc\")", "STR:ABC"),
            ("LCASE(\"ABC\")", "STR:abc"),
            ("CONCAT(\"a\", \"b\")", "STR:ab"),
            ("SUBSTR(\"abcdef\", 2, 3)", "STR:bcd"),
            ("STRBEFORE(\"abcdef\", \"cd\")", "STR:ab"),
            ("STRAFTER(\"abcdef\", \"cd\")", "STR:ef"),
            ("REPLACE(\"abracadabra\", \"a\", \"X\")", "STR:\"XbrXcXdXbrX\""),
            ("IRI(\"http://example.org/x\")", "URI:<http://example.org/x>"),
            ("URI(\"http://example.org/y\")", "URI:<http://example.org/y>"),
            ("YEAR(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "INT:2010"),
            ("MONTH(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "INT:6"),
            ("DAY(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "INT:21"),
            ("HOURS(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "INT:11"),
            ("MINUTES(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "INT:28"),
            ("SECONDS(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "DBL:1.123456"),
            ("TZ(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "STR:\"Z\""),
            ("TIMEZONE(\"2010-06-21T11:28:01.123456Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "STR:\"PT0S\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>"),
            // non-Z offsets: components come from the lexical form, no timezone conversion
            ("HOURS(\"2010-12-21T15:38:02-08:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "INT:15"),
            ("DAY(\"2010-12-21T15:38:02-08:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "INT:21"),
            ("TIMEZONE(\"2010-12-21T15:38:02-08:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>)", "STR:\"-PT8H\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>"),
            ("IF(1 > 0, 10, 20)", "INT:10"),
            ("IF(1 < 0, 10, 20)", "INT:20"),
            ("COALESCE(42, 0)", "INT:42"),
            ("STR(3.14)", "STR:3.14"),
            ("DATATYPE(\"5\"^^<http://www.w3.org/2001/XMLSchema#integer>)", "URI:<http://www.w3.org/2001/XMLSchema#integer>"),
            ("DATATYPE(\"plain\")", "URI:<http://www.w3.org/2001/XMLSchema#string>"),
            ("DATATYPE(\"hi\"@en)", "URI:<http://www.w3.org/1999/02/22-rdf-syntax-ns#langString>"),
            ("STRLANG(\"hi\", \"en\")", "STR:\"hi\"@en"),
            ("UCASE(\"abc\"@en)", "STR:\"ABC\"@en"),
            ("LCASE(\"ABC\"@en)", "STR:\"abc\"@en"),
            ("CONCAT(\"a\"@en, \"b\"@en)", "STR:\"ab\"@en"),
            ("SUBSTR(\"abcdef\", 3)", "STR:cdef"),
            ("ENCODE_FOR_URI(\"a b\")", "STR:a%20b"),
            ("MD5(\"abc\")", "STR:900150983cd24fb0d6963f7d28e17f72"),
            ("SHA1(\"abc\")", "STR:a9993e364706816aba3e25717850c26c9cd0d89d"),
            ("SHA256(\"abc\")", "STR:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            ("SHA512(\"abc\")", "STR:ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f"),
            ("ABS(5)", "INT:5"),
            ("STRLEN(\"héllo\")", "INT:5"),
            ("DATATYPE(\"PT0S\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>)", "URI:<http://www.w3.org/2001/XMLSchema#dayTimeDuration>"),
            ("DATATYPE(\"2024-01-01\"^^xsd:date)", "URI:<http://www.w3.org/2001/XMLSchema#date>"),
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

    // ADR-049: BIND now evaluates the full [110] Expression via EvaluateToValue, including ||/&&.
    // Before the unification the BIND evaluator had no [111] ConditionalOrExpression and silently
    // dropped the right operand, so (x || y) was non-conformant. Locked as the demonstrated-gap test.
    [Fact]
    public void Bind_WithLogicalOr_Evaluates()
    {
        Assert.Equal("BOOL:true", ViaFilter("(5 > 100 || 5 < 100)"));
        Assert.Equal("BOOL:false", ViaFilter("(5 > 100 || 5 > 100)"));
        Assert.Equal("BOOL:true", ViaFilter("(5 < 100 && 5 > 0)"));
    }

    // LANG: spec is the oracle (the deleted BindExpressionEvaluator returned UNBOUND, which is wrong).
    // LANG of a lang-tagged literal is its tag; LANG of any other literal is the empty string.
    [Fact]
    public void LangFunction_ConformsToSpec()
    {
        Assert.Equal("STR:en", ViaFilter("LANG(\"hi\"@en)"));
        Assert.Equal("STR:", ViaFilter("LANG(\"plain\")"));
    }

    // STRDT with an inline full-IRI datatype: spec is the oracle. FilterEvaluator emits a single
    // angle-bracket pair; the deleted BindExpressionEvaluator double-bracketed (^^<<…>>) — its bug.
    [Fact]
    public void Strdt_InlineIriDatatype_SingleBracketed()
    {
        Assert.Equal("STR:\"5\"^^<http://www.w3.org/2001/XMLSchema#integer>",
            ViaFilter("STRDT(\"5\", <http://www.w3.org/2001/XMLSchema#integer>)"));
    }

    // Base-IRI parity (ADR-049 step 2): a relative ref in IRI()/URI() resolves against the base;
    // an absolute ref ignores it.
    [Fact]
    public void Iri_ResolvesRelativeAgainstBase()
    {
        var e1 = new FilterEvaluator("IRI(\"foo\")".AsSpan());
        Assert.Equal("URI:<http://example.org/foo>", Render(e1.EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null,
            ReadOnlySpan<char>.Empty, "http://example.org/".AsSpan())));

        var e2 = new FilterEvaluator("IRI(\"http://other.org/x\")".AsSpan());
        Assert.Equal("URI:<http://other.org/x>", Render(e2.EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null,
            ReadOnlySpan<char>.Empty, "http://example.org/".AsSpan())));
    }
}
