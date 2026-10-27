using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// [25] LimitOffsetClauses, SPARQL 1.1 §15.4–15.5. <c>LIMIT 0</c> returns no solutions, and LIMIT and OFFSET may come
/// in either order. Mercury used 0 as its no-limit value, so <c>LIMIT 0</c> returned every row (W3C sparql10
/// solution-seq limit-3, a suite Mercury does not run). Each test seeds three triples inline and queries through the
/// facade, so every execution route that applies a slice is covered: plain, ORDER BY, DISTINCT, GROUP BY, GRAPH,
/// sub-SELECT and a WHERE without triple patterns.
/// </summary>
[Collection("QuadStore")]
public class LimitOffsetTests : PooledStoreTestBase
{
    public LimitOffsetTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> ?o } LIMIT 0")]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> ?o } ORDER BY ?s LIMIT 0")]
    [InlineData("SELECT DISTINCT ?s WHERE { ?s <http://ex/p> ?o } LIMIT 0")]
    [InlineData("SELECT ?o (COUNT(?s) AS ?n) WHERE { ?s <http://ex/p> ?o } GROUP BY ?o LIMIT 0")]
    [InlineData("SELECT * WHERE { GRAPH ?g { ?s <http://ex/p> ?o } } LIMIT 0")]
    [InlineData("SELECT * WHERE { { SELECT ?s WHERE { ?s <http://ex/p> ?o } LIMIT 0 } }")]
    [InlineData("SELECT (1 AS ?x) WHERE { } LIMIT 0")]
    [InlineData("SELECT (1 AS ?x) WHERE { } OFFSET 1")]
    public void LimitZero_OrOffsetPastTheEnd_ReturnsNoRows(string query)
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Rows!);
    }

    [Theory]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY ?s LIMIT 1 OFFSET 1")]
    [InlineData("SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY ?s OFFSET 1 LIMIT 1")]
    [InlineData("SELECT ?s WHERE { { SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY ?s OFFSET 1 LIMIT 1 } }")]
    public void LimitAndOffset_InEitherOrder_SelectTheSecondRow(string query)
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/s2>", row["s"]);
    }

    [Fact]
    public void NoLimit_ReturnsEveryRow()
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.Rows!.Count);
    }

    [Fact]
    public void Explain_WithoutLimit_HasNoSlice_AndLimitZeroIsShown()
    {
        var withoutLimit = SparqlEngine.Explain("SELECT * WHERE { ?s ?p ?o }");
        var limitZero = SparqlEngine.Explain("SELECT * WHERE { ?s ?p ?o } LIMIT 0");

        Assert.DoesNotContain("LIMIT", withoutLimit);
        Assert.Contains("LIMIT 0", limitZero);
    }
}
