using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A WHERE without triple patterns is evaluated like any other group (SPARQL 1.1 §18.2): <c>{}</c> is one empty
/// solution, BIND extends it, FILTER removes it, UNION concatenates its branches. Mercury sent such a WHERE to a
/// separate evaluator that read the flat parse: it merged UNION branches into one row, counted nothing, dropped BINDs
/// past 8 and FILTERs past 16, and gave ASK its own third evaluator. Each test runs one query through the facade.
/// </summary>
[Collection("QuadStore")]
public class EmptyGroupTests : PooledStoreTestBase
{
    public EmptyGroupTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT * WHERE { }", 1)]
    [InlineData("SELECT * WHERE { MINUS { ?s ?p ?o } }", 1)]
    [InlineData("SELECT (1 AS ?x) WHERE { }", 1)]
    [InlineData("SELECT * WHERE { BIND(1 AS ?x) FILTER(?x = 2) }", 0)]
    [InlineData("SELECT * WHERE { { BIND(\"x\" AS ?a) } UNION { BIND(\"y\" AS ?b) } }", 2)]
    [InlineData("SELECT * WHERE { BIND(1 AS ?x) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 1) FILTER(?x = 2) }", 0)]
    public void Select_WhereWithoutTriples_HasTheSolutionsOfItsGroup(string query, int rows)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(rows, result.Rows!.Count);
    }

    [Fact]
    public void Select_TenBinds_BindsAllTen()
    {
        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { BIND(0 AS ?b0) BIND(1 AS ?b1) BIND(2 AS ?b2) BIND(3 AS ?b3) BIND(4 AS ?b4) BIND(5 AS ?b5) BIND(6 AS ?b6) BIND(7 AS ?b7) BIND(8 AS ?b8) BIND(9 AS ?b9) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal(10, row.Count);
        Assert.Equal("9", row["b9"]); // a bound integer is presented bare; an aggregate typed (ADR-053)
    }

    [Theory]
    [InlineData("SELECT (COUNT(*) AS ?n) WHERE { }", "\"1\"")]
    [InlineData("SELECT (COUNT(*) AS ?n) WHERE { { BIND(1 AS ?a) } UNION { BIND(2 AS ?b) } }", "\"2\"")]
    [InlineData("SELECT (COUNT(*) AS ?n) WHERE { BIND(1 AS ?x) FILTER(?x = 2) }", "\"0\"")]
    [InlineData("SELECT (COUNT(*) AS ?n) WHERE { ?s <http://ex/none> ?o FILTER(?o = 1) }", "\"0\"")]
    public void Aggregate_OverAGroup_ReturnsOneRow(string query, string countPrefix)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.StartsWith(countPrefix, row["n"]);
    }

    [Theory]
    [InlineData("ASK { }", true)]
    [InlineData("ASK { VALUES ?x { 1 } }", true)]
    [InlineData("ASK { BIND(1 AS ?x) FILTER(?x = 2) }", false)]
    [InlineData("ASK { BIND(0 AS ?b0) BIND(1 AS ?b1) BIND(2 AS ?b2) BIND(3 AS ?b3) BIND(4 AS ?b4) BIND(5 AS ?b5) BIND(6 AS ?b6) BIND(7 AS ?b7) BIND(8 AS ?b8) BIND(9 AS ?b9) FILTER(?b9 = 9) }", true)]
    [InlineData("ASK { { BIND(1 AS ?x) FILTER(?x = 2) } UNION { BIND(2 AS ?y) } }", true)]
    public void Ask_WhereWithoutTriples_IsTrueWhenItsGroupHasASolution(string query, bool expected)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.AskResult);
    }

    [Theory]
    [InlineData("CONSTRUCT { <http://ex/a> <http://ex/b> ?x } WHERE { BIND(1 AS ?x) }", 1)]
    [InlineData("CONSTRUCT { <http://ex/a> <http://ex/b> ?x } WHERE { VALUES ?x { 1 2 } }", 2)]
    [InlineData("CONSTRUCT { <http://ex/a> <http://ex/b> <http://ex/c> } WHERE { }", 1)]
    [InlineData("CONSTRUCT { ?s ?p ?o } WHERE { GRAPH ?g { ?s ?p ?o } }", 2)]
    public void Construct_WhereWithoutTopLevelTriples_InstantiatesPerSolution(string query, int triples)
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g>");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(triples, result.Triples!.Count);
    }
}
