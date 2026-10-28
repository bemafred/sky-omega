using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// An EXISTS inside a FILTER expression, as in <c>FILTER(?x || EXISTS { … })</c>, is evaluated per row from its group.
/// Through 1.8.28 the group's end was found by counting braces in the text, so a brace inside a string literal in the
/// group ended it early. The group is now read by the parser that reads every other group.
/// </summary>
[Collection("QuadStore")]
public class EmbeddedExistsTests : PooledStoreTestBase
{
    public EmbeddedExistsTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void EmbeddedExists_WithABraceInAStringLiteral_ReadsTheWholeGroup()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/y>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "\"}\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = <http://ex/x> || EXISTS { ?s <http://ex/q> \"}\" }) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void TwoEmbeddedExists_AreEachEvaluated()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/y>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "\"{\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/r>", "<http://ex/z>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(EXISTS { ?s <http://ex/q> \"{\" } && !EXISTS { ?s <http://ex/r> ?z }) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }
}
