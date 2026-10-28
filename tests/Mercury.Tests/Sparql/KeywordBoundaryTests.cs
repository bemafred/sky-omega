using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A keyword is a whole word: a prefixed name such as <c>graph:a</c> or <c>values:v</c> is a term, not GRAPH or VALUES.
/// Through 1.8.28 the group parser matched a keyword's letters at the start of a token, so a triple whose subject had
/// such a prefix was read as that clause.
/// </summary>
[Collection("QuadStore")]
public class KeywordBoundaryTests : PooledStoreTestBase
{
    public KeywordBoundaryTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("graph")]
    [InlineData("optional")]
    [InlineData("minus")]
    [InlineData("filter")]
    [InlineData("bind")]
    [InlineData("values")]
    [InlineData("service")]
    [InlineData("union")]
    [InlineData("select")]
    [InlineData("exists")]
    [InlineData("not")]
    public void APrefixNamedLikeAKeyword_IsATerm(string prefix)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");

        var result = SparqlEngine.Query(Store,
            $"PREFIX {prefix}: <http://ex/> SELECT ?o WHERE {{ {prefix}:a {prefix}:p ?o }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"x\"", Assert.Single(result.Rows!)["o"]);
    }

    [Fact]
    public void APrefixNamedLikeAKeyword_InAnUpdateTemplate_IsATerm()
    {
        var update = SparqlEngine.Update(Store,
            "PREFIX graph: <http://ex/> INSERT DATA { graph:a graph:p \"x\" } ; " +
            "PREFIX graph: <http://ex/> INSERT { graph:b graph:p ?o } WHERE { graph:a graph:p ?o }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> \"x\" } ORDER BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
    }

    [Fact]
    public void AnAggregateArgumentWithADistinctPrefix_IsATerm()
    {
        // distinct:o is a prefixed name, not COUNT(DISTINCT o)
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "PREFIX distinct: <http://ex/> SELECT (COUNT(?s) AS ?n) WHERE { ?s distinct:p distinct:o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.StartsWith("\"2\"", Assert.Single(result.Rows!)["n"]);
    }
}
