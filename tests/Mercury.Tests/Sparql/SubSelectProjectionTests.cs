using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A sub-SELECT's projection is [9] SelectClause, the same production as a query's, and is parsed by the same code.
/// It used to have its own copy, which silently dropped projected variables and aggregates past 8: a sub-SELECT
/// projecting 10 variables returned 8 of them. The shared clause holds 16 variables and 8 expressions, and a query
/// that exceeds either is a syntax error, never a shorter result.
/// </summary>
[Collection("QuadStore")]
public class SubSelectProjectionTests : PooledStoreTestBase
{
    public SubSelectProjectionTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void SubSelect_ProjectingTenVariables_ReturnsAllTen()
    {
        for (int i = 0; i < 10; i++)
            Store.AddCurrent("<http://ex/s>", $"<http://ex/p{i}>", $"<http://ex/o{i}>");
        var vars = string.Join(" ", Enumerable.Range(0, 10).Select(i => $"?v{i}"));
        var bgp = string.Join(" . ", Enumerable.Range(0, 10).Select(i => $"<http://ex/s> <http://ex/p{i}> ?v{i}"));

        var result = SparqlEngine.Query(Store, $"SELECT * WHERE {{ {{ SELECT {vars} WHERE {{ {bgp} }} }} }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal(10, row.Count);
        Assert.Equal("<http://ex/o9>", row["v9"]);
    }

    [Fact]
    public void SubSelect_EightExpressions_AreAllProjected()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        var exprs = string.Join(" ", Enumerable.Range(0, 8).Select(i => $"(COUNT(?o) AS ?c{i})"));

        var result = SparqlEngine.Query(Store, $"SELECT * WHERE {{ {{ SELECT {exprs} WHERE {{ ?s <http://ex/p> ?o }} }} }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal(8, row.Count);
    }

    [Fact]
    public void SubSelect_NineExpressions_IsAnError_NotAShorterRow()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        var exprs = string.Join(" ", Enumerable.Range(0, 9).Select(i => $"(COUNT(?o) AS ?c{i})"));

        var result = SparqlEngine.Query(Store, $"SELECT * WHERE {{ {{ SELECT {exprs} WHERE {{ ?s <http://ex/p> ?o }} }} }}",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public void SubSelect_ReducedAndDuplicateAlias_FollowTheSelectClauseGrammar()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var reduced = SparqlEngine.Query(Store, "SELECT * WHERE { { SELECT REDUCED ?o WHERE { ?s <http://ex/p> ?o } } }",
            TestContext.Current.CancellationToken);
        var duplicate = SparqlEngine.Query(Store, "SELECT * WHERE { { SELECT ?o (1 AS ?o) WHERE { ?s <http://ex/p> ?o } } }",
            TestContext.Current.CancellationToken);

        Assert.True(reduced.Success, reduced.ErrorMessage);
        Assert.Single(reduced.Rows!);
        Assert.False(duplicate.Success);
    }

    [Theory]
    [InlineData("SELECT ?y WHERE { { SELECT (CONCAT(\"{\", STR(?o)) AS ?y) WHERE { ?s <http://ex/p> ?o } } }", "\"{http://ex/o\"")]
    [InlineData("SELECT ?y WHERE { { SELECT (CONCAT(\"}{\", STR(?o)) AS ?y) WHERE { ?s <http://ex/p> ?o } } }", "\"}{http://ex/o\"")]
    public void SubSelect_ProjectionContainingABrace_EvaluatesItsOwnWhere(string query, string expected)
    {
        // The sub-SELECT's WHERE starts where its parser found it, not at the first '{' after SELECT.
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Contains(expected.Trim('"'), row.Values.First());
    }

    [Fact]
    public void SubSelectStar_BraceInAStringInItsWhere_ProjectsEveryVariable()
    {
        // SELECT * projects its WHERE's variables. The WHERE used to end at the first '}' a character scan found,
        // the one inside the string, so ?z was not projected.
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/s>", "<http://ex/q>", "<http://ex/z>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?z WHERE { { SELECT * WHERE { ?s <http://ex/p> ?o FILTER(STR(?o) != \"}\") ?s <http://ex/q> ?z } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/z>", row["z"]);
    }

    [Theory]
    [InlineData("SELECT * WHERE { { SELECT ?o WHERE { ?s <http://ex/p> ?o } } BIND(1 AS ?o) }")]
    [InlineData("SELECT * WHERE { { SELECT (COUNT(?o) AS ?n) WHERE { ?s <http://ex/p> ?o } } BIND(1 AS ?n) }")]
    [InlineData("SELECT * WHERE { { SELECT * WHERE { ?s <http://ex/p> ?o } } BIND(1 AS ?o) }")]
    public void Bind_ToAVariableAnEarlierSubSelectProjects_IsAnError(string query)
    {
        // SPARQL 1.1 §18.2.1: a BIND's variable must not be in scope already
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public void Bind_ToAVariableAnEarlierSubSelectDoesNotProject_IsAllowed()
    {
        // ?s is in the sub-SELECT's WHERE but not in its projection, so it is not in scope outside it
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, "SELECT ?o ?s WHERE { { SELECT ?o WHERE { ?s <http://ex/p> ?o } } BIND(1 AS ?s) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/o>", row["o"]);
        Assert.Equal("\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>", row["s"]);
    }
}
