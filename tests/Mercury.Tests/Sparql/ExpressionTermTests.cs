using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-056: an expression value is the RDF term it was read from, and the evaluator compares, converts and binds
/// terms by the SPARQL operator mapping (§17.3). Through 1.8.28 a numeric term kept only its number: "01"^^xsd:short
/// became the long 1, so BIND, STR, sameTerm and DATATYPE returned another term; comparisons had no error outcome, so
/// <c>!=</c> and <c>!</c> kept rows whose comparison has no answer.
/// </summary>
[Collection("QuadStore")]
public class ExpressionTermTests : PooledStoreTestBase
{
    public ExpressionTermTests(QuadStorePoolFixture fixture) : base(fixture) { }

    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    [Theory]
    [InlineData("\"01\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("\"1\"^^<http://www.w3.org/2001/XMLSchema#short>")]
    [InlineData("\"1.0\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    [InlineData("\"1.0e0\"^^<http://www.w3.org/2001/XMLSchema#double>")]
    [InlineData("\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>")]
    [InlineData("\"a\"^^<http://ex/type>")]
    [InlineData("\"abc\"@EN")]
    public void Bind_OfAStoredTerm_BindsThatTerm(string term)
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", term);

        var result = SparqlEngine.Query(Store, "SELECT ?x WHERE { ?s <http://ex/p> ?o BIND(?o AS ?x) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(term, Assert.Single(result.Rows!)["x"]);
    }

    [Theory]
    [InlineData("01", "\"01\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("+1", "\"+1\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("1.0", "\"1.0\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    [InlineData(".5", "\".5\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    [InlineData("1e3", "\"1e3\"^^<http://www.w3.org/2001/XMLSchema#double>")]
    [InlineData("-1.5E-2", "\"-1.5E-2\"^^<http://www.w3.org/2001/XMLSchema#double>")]
    [InlineData("\"a\"", "\"a\"")]
    [InlineData("\"a\"@en", "\"a\"@en")]
    public void Bind_OfAConstant_BindsTheTermItAbbreviates(string constant, string term)
    {
        // A numeric constant is the literal with that lexical form ([146]-[155]), sign, leading '.' and exponent kept
        var result = SparqlEngine.Query(Store, $"SELECT ?x WHERE {{ BIND({constant} AS ?x) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(term, Assert.Single(result.Rows!)["x"]);
    }

    [Theory]
    [InlineData("STR(?o)", "\"01\"")]
    [InlineData("DATATYPE(?o)", "<http://www.w3.org/2001/XMLSchema#short>")]
    [InlineData("?o + ?o", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("xsd:string(?o)", "\"1\"")]
    [InlineData("xsd:float(?o)", "\"1\"^^<http://www.w3.org/2001/XMLSchema#float>")]
    [InlineData("xsd:decimal(?o)", "\"1.0\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    public void Expressions_OverAStoredShort_ReadItsTerm(string expression, string expected)
    {
        // "01"^^xsd:short: STR and DATATYPE read the stored term; arithmetic on two integer subtypes is xsd:integer;
        // a cast to xsd:string writes the value's canonical form (XPath casting)
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", $"\"01\"^^<{Xsd}short>");

        var result = SparqlEngine.Query(Store,
            $"PREFIX xsd: <{Xsd}> SELECT ?x WHERE {{ ?s <http://ex/p> ?o BIND({expression} AS ?x) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, Assert.Single(result.Rows!)["x"]);
    }

    [Theory]
    [InlineData("1 + 1", "integer")]
    [InlineData("7 / 2", "decimal")]
    [InlineData("1.5 + 1", "decimal")]
    [InlineData("xsd:float(1) + 1", "float")]
    [InlineData("xsd:float(1) + 1.5", "float")]
    [InlineData("xsd:float(1) + 1e0", "double")]
    [InlineData("1e0 * 2", "double")]
    public void Arithmetic_PromotesToTheWiderType(string expression, string datatype)
    {
        // XPath op:numeric-*: integer, then decimal, then float, then double; integer division is a decimal
        var result = SparqlEngine.Query(Store,
            $"PREFIX xsd: <{Xsd}> SELECT ?d WHERE {{ BIND(DATATYPE({expression}) AS ?d) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal($"<{Xsd}{datatype}>", Assert.Single(result.Rows!)["d"]);
    }

    [Fact]
    public void Arithmetic_OnAString_IsAnError()
    {
        // "5" + 1 has no value: no coercion of a string to a number, and an error leaves BIND's variable unbound
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { BIND(\"5\" + 1 AS ?x) BIND(1 AS ?y) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.False(row.ContainsKey("x"));
    }

    [Fact]
    public void DecimalDivisionByZero_IsAnError()
    {
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { BIND(1 / 0 AS ?x) BIND(1 AS ?y) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Assert.Single(result.Rows!).ContainsKey("x"));
    }

    [Theory]
    [InlineData("sameTerm(?o, 1)", false)]
    [InlineData("sameTerm(?o, 01)", true)]
    [InlineData("?o = 1", true)]
    [InlineData("STR(?o) = \"01\"", true)]
    public void SameTerm_ComparesTermsAndEqualsComparesValues(string test, bool holds)
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", $"\"01\"^^<{Xsd}integer>");

        var result = SparqlEngine.Query(Store, $"ASK {{ ?s <http://ex/p> ?o FILTER({test}) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(holds, result.AskResult);
    }

    [Theory]
    // Two values in different value spaces are not equal: != holds
    [InlineData("\"abc\"", "?o != 1", true)]
    [InlineData("<http://ex/i>", "?o != 1", true)]
    [InlineData("\"abc\"@en", "?o != \"abc\"", true)]
    // An unknown datatype or an ill-typed literal is equal only to the same term, and an error otherwise
    [InlineData("\"a\"^^<http://ex/type>", "?o != 1", false)]
    [InlineData("\"a\"^^<http://ex/type>", "?o = 1", false)]
    [InlineData("\"a\"^^<http://ex/type>", "?o != \"b\"^^<http://ex/type>", false)]
    [InlineData("\"a\"^^<http://ex/type>", "?o = \"a\"^^<http://ex/type>", true)]
    [InlineData("\"xyz\"^^<http://www.w3.org/2001/XMLSchema#integer>", "?o != 1", false)]
    // The error is what || sees: an error || true is true
    [InlineData("\"a\"^^<http://ex/type>", "?o != 1 || true", true)]
    [InlineData("\"a\"^^<http://ex/type>", "?o != 1 || false", false)]
    // Language tags compare ignoring case
    [InlineData("\"abc\"@EN", "?o = \"abc\"@en", true)]
    // Ordering is an error outside the operator mapping
    [InlineData("<http://ex/i>", "?o < <http://ex/j>", false)]
    [InlineData("<http://ex/i>", "!(?o < <http://ex/j>)", false)]
    [InlineData("\"10\"", "?o < \"9\"", true)]
    public void Comparison_FollowsTheOperatorMapping(string stored, string test, bool kept)
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", stored);

        var result = SparqlEngine.Query(Store, $"ASK {{ ?s <http://ex/p> ?o FILTER({test}) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(kept, result.AskResult);
    }

    [Theory]
    [InlineData("\"2006-08-23\"", "=", true)]
    [InlineData("\"2006-08-23Z\"", "=", false)] // a date with a timezone and one without, within 14 hours: no order
    [InlineData("\"2006-08-23Z\"", "!=", false)]
    [InlineData("\"2006-08-23Z\"", ">", false)]
    [InlineData("\"2006-08-25Z\"", ">", true)] // further apart than 14 hours: ordered
    [InlineData("\"2006-08-23T09:00:00+01:00\"^^<http://www.w3.org/2001/XMLSchema#dateTime>", "!=", true)]
    public void Dates_CompareByValueInXsdsPartialOrder(string stored, string op, bool kept)
    {
        // A stored date compared with the date 2006-08-23 (no timezone); a dateTime is in another value space
        var term = stored.Contains("^^") ? stored : stored + $"^^<{Xsd}date>";
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", term);

        var result = SparqlEngine.Query(Store,
            $"PREFIX xsd: <{Xsd}> ASK {{ ?s <http://ex/p> ?o FILTER(?o {op} \"2006-08-23\"^^xsd:date) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(kept, result.AskResult);
    }

    [Theory]
    [InlineData("\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>", true)]
    [InlineData("\"0\"^^<http://www.w3.org/2001/XMLSchema#integer>", false)]
    [InlineData("\"0.0\"^^<http://www.w3.org/2001/XMLSchema#double>", false)]
    [InlineData("\"NaN\"^^<http://www.w3.org/2001/XMLSchema#double>", false)]
    [InlineData("\"foo\"", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"abc\"^^<http://www.w3.org/2001/XMLSchema#integer>", false)] // ill-typed: false
    [InlineData("<http://ex/i>", false)] // an error
    [InlineData("\"a\"^^<http://ex/type>", false)] // an error
    public void Filter_KeepsARowWhenTheEffectiveBooleanValueIsTrue(string stored, bool kept)
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", stored);

        var result = SparqlEngine.Query(Store, "ASK { ?s <http://ex/p> ?o FILTER(?o) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(kept, result.AskResult);
    }

    [Fact]
    public void Not_OfAnUnboundVariable_IsAnError()
    {
        // !?w with ?w unbound is an error, not true, so FILTER keeps no row (W3C dawg-bev-6)
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"x\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o OPTIONAL { ?s <http://ex/q> ?w } FILTER(!?w) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Rows!);
    }

    [Fact]
    public void PrefixedDatatype_InAConstant_IsExpandedThroughThePrologue()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"a\"^^<http://ex/t#type1>");

        var result = SparqlEngine.Query(Store,
            "PREFIX t: <http://ex/t#> ASK { ?s <http://ex/p> ?o FILTER(?o = \"a\"^^t:type1) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.AskResult);
    }

    [Fact]
    public void UndeclaredDatatypePrefix_IsAnError()
    {
        // xsd: is not declared, so the constant has no datatype IRI: an error, and FILTER keeps no row (ADR-056 D2)
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", $"\"1\"^^<{Xsd}integer>");

        var result = SparqlEngine.Query(Store, "ASK { ?s <http://ex/p> ?o FILTER(?o = \"1\"^^xsd:integer) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.AskResult);
    }

    [Theory]
    [InlineData("LANG(?o)")]
    [InlineData("DATATYPE(?o)")]
    public void TermFunctions_OfAnIri_AreErrors(string expression)
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/i>");

        var result = SparqlEngine.Query(Store, $"SELECT ?s ?x WHERE {{ ?s <http://ex/p> ?o BIND({expression} AS ?x) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Assert.Single(result.Rows!).ContainsKey("x"));
    }

    [Theory]
    [InlineData("\"2026-04-19\"^^xsd:date", "?o > \"2026-04-01\"^^<http://www.w3.org/2001/XMLSchema#date>")]
    [InlineData("\"648\"^^xsd:integer", "?o = 648")]
    [InlineData("28", "?o = 28")]
    public void TermsOfEarlierWriters_ReadAsTheirXsdTerm(string stored, string test)
    {
        // A store written before ADR-053's migration holds a datatype as xsd:T and a number bare (ADR-056 D7): the
        // evaluator reads them as the XSD literal, so a recall FILTER on a stored date or number keeps working
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", stored);

        var result = SparqlEngine.Query(Store, $"ASK {{ ?s <http://ex/p> ?o FILTER({test}) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.AskResult);
    }

    [Fact]
    public void OrderBy_Str_OrdersAsStrings()
    {
        // STR gives simple literals, which < orders as strings: "10" < "2" < "300" (W3C dawg-sort-builtin)
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", $"\"2\"^^<{Xsd}integer>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", $"\"300\"^^<{Xsd}integer>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", $"\"10\"^^<{Xsd}integer>");

        var byString = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY STR(?o)",
            TestContext.Current.CancellationToken);
        var byValue = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(byString.Success, byString.ErrorMessage);
        Assert.Equal(new[] { "<http://ex/s3>", "<http://ex/s1>", "<http://ex/s2>" }, byString.Rows!.Select(r => r["s"]));
        Assert.Equal(new[] { "<http://ex/s1>", "<http://ex/s3>", "<http://ex/s2>" }, byValue.Rows!.Select(r => r["s"]));
    }

    [Fact]
    public void OrderBy_Iris_OrdersByTheirText()
    {
        // <…/s> before <…/s1> before <…/s10> (W3C subquery16); through 1.8.30 the comparison included the brackets, and
        // the '>' that ends <…/s> sorted after every digit
        Store.AddCurrent("<http://ex/s10>", "<http://ex/p>", "\"c\"");
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"a\"");
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"b\"");

        var ascending = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY ?s",
            TestContext.Current.CancellationToken);
        var lowest = SparqlEngine.Query(Store, "SELECT (MIN(?s) AS ?min) WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(ascending.Success, ascending.ErrorMessage);
        Assert.Equal(new[] { "<http://ex/s>", "<http://ex/s1>", "<http://ex/s10>" }, ascending.Rows!.Select(r => r["s"]));
        Assert.True(lowest.Success, lowest.ErrorMessage);
        Assert.Equal("<http://ex/s>", Assert.Single(lowest.Rows!)["min"]);
    }

    [Fact]
    public void MinMax_OfDates_AreTheEarliestAndLatestDate()
    {
        // MIN and MAX order by ORDER BY's order (§18.5.1.4–5), so they take dates; through 1.8.29 they read numbers
        // only, and a date made the aggregate an error. Found recalling memory over the MCP.
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", $"\"2026-10-03\"^^<{Xsd}date>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", $"\"2026-10-25\"^^<{Xsd}date>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", $"\"2026-05-23\"^^<{Xsd}date>");

        var result = SparqlEngine.Query(Store,
            "SELECT (MIN(?o) AS ?min) (MAX(?o) AS ?max) WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal($"\"2026-05-23\"^^<{Xsd}date>", row["min"]);
        Assert.Equal($"\"2026-10-25\"^^<{Xsd}date>", row["max"]);
    }

    [Fact]
    public void MinMax_OfMixedTerms_FollowTheOrderByOrder()
    {
        // IRIs sort before literals, and numbers before strings; the winner is the stored term itself ("02" kept)
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", $"\"02\"^^<{Xsd}integer>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"b\"");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/s4>", "<http://ex/q>", $"\"02\"^^<{Xsd}integer>");
        Store.AddCurrent("<http://ex/s5>", "<http://ex/q>", $"\"10\"^^<{Xsd}integer>");

        var mixed = SparqlEngine.Query(Store,
            "SELECT (MIN(?o) AS ?min) (MAX(?o) AS ?max) WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);
        var numbers = SparqlEngine.Query(Store,
            "SELECT (MIN(?o) AS ?min) (MAX(?o) AS ?max) WHERE { ?s <http://ex/q> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(mixed.Success, mixed.ErrorMessage);
        Assert.True(numbers.Success, numbers.ErrorMessage);
        var mixedRow = Assert.Single(mixed.Rows!);
        Assert.Equal("<http://ex/o>", mixedRow["min"]);
        Assert.Equal("\"b\"", mixedRow["max"]);
        var numberRow = Assert.Single(numbers.Rows!);
        Assert.Equal($"\"02\"^^<{Xsd}integer>", numberRow["min"]);
        Assert.Equal($"\"10\"^^<{Xsd}integer>", numberRow["max"]);
    }
}
