using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Where an expression ends is found with one token rule (SparqlParser.SkipExpressionToken): string literals, IRIs and
/// names are whole tokens, and brackets inside them are not counted. Through 1.8.28 each clause counted brackets on its
/// own, so a ')' or '(' inside a string literal broke FILTER, BIND, SELECT expressions, aggregate arguments and HAVING;
/// BIND and SELECT read the "as" at the end of ?gas as the AS keyword; and ORDER BY read variables only.
/// </summary>
[Collection("QuadStore")]
public class ExpressionBoundaryTests : PooledStoreTestBase
{
    public ExpressionBoundaryTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = \")\") }")]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o != \"(\") }")]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER CONTAINS(?o, \")\") }")]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = ')') }")]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = \"\"\")\"\"\") }")]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = \"\\\")\\\"\" || ?o = \")\") }")]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = \")\" # a ( comment\n) }")]
    public void Filter_WithABracketInAString_KeepsTheRow(string query)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\")\"");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void Bind_WithABracketInAString_BindsTheValue()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?y WHERE { ?s <http://ex/p> ?o BIND(CONCAT(?o, \")\") AS ?y) }", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"x)\"", Assert.Single(result.Rows!)["y"]); // a computed string is bound as its term (ADR-056 D6)
    }

    [Fact]
    public void Bind_AVariableEndingInAs_IsNotTheAsKeyword()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"abc\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?x WHERE { ?s <http://ex/p> ?o BIND(STRLEN(?o) AS ?gas) BIND(?gas + 1 AS ?x) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>", Assert.Single(result.Rows!)["x"]);
    }

    [Fact]
    public void SelectExpression_WithABracketInAString_IsProjected()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");

        var result = SparqlEngine.Query(Store,
            "SELECT (CONCAT(?o, \")\") AS ?y) (STRLEN(?has) AS ?n) WHERE { ?s <http://ex/p> ?o BIND(?o AS ?has) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\"x)\"", row["y"]);
        Assert.Equal("\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>", row["n"]);
    }

    [Fact]
    public void AggregateArgumentAndHaving_WithABracketInAString_Group()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\")\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\")\"");
        Store.AddCurrent("<http://ex/c>", "<http://ex/p>", "\"z\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?o (COUNT(IF(?o = \")\", 1, 0)) AS ?n) WHERE { ?s <http://ex/p> ?o } GROUP BY ?o HAVING (?o = \")\")",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\")\"", row["o"]);
        Assert.StartsWith("\"2\"", row["n"]);
    }

    [Fact]
    public void SelectExpression_WithAnAggregateNameInAString_AddsNoAggregate()
    {
        // "COUNT(" in a literal was taken for an aggregate inside the expression
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s (CONCAT(?o, \"COUNT(x)\") AS ?y) WHERE { ?s <http://ex/p> ?o }", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"xCOUNT(x)\"", Assert.Single(result.Rows!)["y"]);
    }

    [Theory]
    [InlineData("ORDER BY (?n)", new[] { "a", "b", "c" })]
    [InlineData("ORDER BY DESC(?n + 0)", new[] { "c", "b", "a" })]
    [InlineData("ORDER BY STRLEN(?l)", new[] { "c", "b", "a" })]
    [InlineData("ORDER BY DESC(STRLEN(?l)) ?n", new[] { "a", "b", "c" })]
    [InlineData("ORDER BY xsd:integer(?n)", new[] { "a", "b", "c" })]
    [InlineData("ORDER BY (CONCAT(?l, \")\"))", new[] { "a", "b", "c" })]
    public void OrderBy_AnExpression_SortsByItsValue(string orderBy, string[] expected)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/n>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/l>", "\"aaa\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/n>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/l>", "\"bb\"");
        Store.AddCurrent("<http://ex/c>", "<http://ex/n>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/l>", "\"c\"");

        var result = SparqlEngine.Query(Store,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s WHERE { ?s <http://ex/n> ?n ; <http://ex/l> ?l } " + orderBy,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected.Select(x => $"<http://ex/{x}>"), result.Rows!.Select(r => r["s"]));
    }

    [Fact]
    public void OrderBy_AnExpressionWithLimit_TakesTheTopRows()
    {
        // ORDER BY + LIMIT over a flat pattern streams through a bounded top-K heap
        Store.AddCurrent("<http://ex/a>", "<http://ex/l>", "\"aaa\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/l>", "\"bb\"");
        Store.AddCurrent("<http://ex/c>", "<http://ex/l>", "\"c\"");

        var result = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/l> ?l } ORDER BY STRLEN(?l) LIMIT 2",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["<http://ex/c>", "<http://ex/b>"], result.Rows!.Select(r => r["s"]));
    }

    [Fact]
    public void OrderBy_FiveConditions_UsesTheFifth()
    {
        // The clause kept four conditions and ignored the rest
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/z>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s ?p ?o BIND(1 AS ?k1) BIND(1 AS ?k2) BIND(1 AS ?k3) BIND(1 AS ?k4) } ORDER BY ?k1 ?k2 ?k3 ?k4 ?o",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["<http://ex/b>", "<http://ex/a>"], result.Rows!.Select(r => r["s"]));
    }
}
