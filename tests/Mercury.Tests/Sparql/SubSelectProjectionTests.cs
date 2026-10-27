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
}
