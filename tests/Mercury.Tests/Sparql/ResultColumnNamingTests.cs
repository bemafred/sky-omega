using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// The facade (<c>SparqlEngine.Query</c>, behind mercury_query, the HTTP endpoint and the CLI) names every result row by
/// its own bindings. It used to take the column names from the first row and read later rows by position, so a value
/// the first row lacked vanished from every row (OPTIONAL), and a row binding other variables came back under the first
/// row's names (UNION). Each test seeds its data inline and asserts the rows' names and values.
/// </summary>
[Collection("QuadStore")]
public class ResultColumnNamingTests : PooledStoreTestBase
{
    public ResultColumnNamingTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void Optional_FirstRowUnbound_LaterRowsKeepTheirValue()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/type>", "<http://ex/T>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/type>", "<http://ex/T>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/label>", "\"bee\"");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s ?l WHERE { ?s <http://ex/type> <http://ex/T> OPTIONAL { ?s <http://ex/label> ?l } } ORDER BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "s", "l" }, result.Variables);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Equal("<http://ex/a>", result.Rows[0]["s"]);
        Assert.False(result.Rows[0].ContainsKey("l"), "a has no label");
        Assert.Equal("<http://ex/b>", result.Rows[1]["s"]);
        Assert.Equal("\"bee\"", result.Rows[1]["l"]);
    }

    [Fact]
    public void Union_BranchesBindDifferentVariables_EachRowKeepsItsNames()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s ?x WHERE { { ?s ?p ?o } UNION { BIND(1 AS ?x) } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "s", "x" }, result.Variables);
        var withS = Assert.Single(result.Rows!, r => r.ContainsKey("s"));
        Assert.Equal("<http://ex/s>", withS["s"]);
        Assert.False(withS.ContainsKey("x"));
        var withX = Assert.Single(result.Rows!, r => r.ContainsKey("x"));
        Assert.Equal("1", withX["x"]);
        Assert.False(withX.ContainsKey("s"), "The BIND branch binds no ?s");
    }

    [Fact]
    public void SelectStar_UnionOfDisjointVariables_ListsEveryBoundVariable()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { { ?s ?p ?o } UNION { ?a ?b ?c } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "s", "p", "o", "a", "b", "c" }, result.Variables);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Single(result.Rows, r => r.Keys.OrderBy(k => k).SequenceEqual(new[] { "o", "p", "s" }));
        Assert.Single(result.Rows, r => r.Keys.OrderBy(k => k).SequenceEqual(new[] { "a", "b", "c" }));
    }

    [Fact]
    public void SelectStar_FirstRowLacksAnOptionalVariable_TheVariableIsStillListed()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/type>", "<http://ex/T>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/type>", "<http://ex/T>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/label>", "\"bee\"");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { ?s <http://ex/type> <http://ex/T> OPTIONAL { ?s <http://ex/label> ?l } } ORDER BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "s", "l" }, result.Variables);
        Assert.Equal("\"bee\"", result.Rows![1]["l"]);
    }

    [Fact]
    public void DollarVariables_AreNamedWithoutTheSigil()
    {
        // $s in a triple pattern binds under the hash of "$s"; the row names it "s". (A $ variable in the SELECT clause
        // itself is a parser gap, recorded with ADR-055.)
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { $s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "s", "o" }, result.Variables);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/s>", row["s"]);
        Assert.Equal("<http://ex/o>", row["o"]);
    }

    [Fact]
    public void ExplicitSelect_UnprojectedVariablesStayOut()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?o WHERE { ?s ?p ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "o" }, result.Variables);
        var row = Assert.Single(result.Rows!);
        Assert.Equal(new[] { "o" }, row.Keys.ToArray());
    }

    [Fact]
    public void AggregateAlias_IsNamed()
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?o (COUNT(?s) AS ?n) WHERE { ?s <http://ex/p> ?o } GROUP BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "o", "n" }, result.Variables);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/o>", row["o"]);
        Assert.Contains("2", row["n"]);
    }
}
