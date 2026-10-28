using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A CONSTRUCT template is instantiated over the WHERE's solutions after the solution modifiers (SPARQL 1.1 §16.2),
/// read from the same path as a SELECT's rows. CONSTRUCT had its own row source, which applied a LIMIT only when the
/// scan could stop early: ORDER BY with LIMIT, and OFFSET, were ignored.
/// </summary>
[Collection("QuadStore")]
public class ConstructSolutionTests : PooledStoreTestBase
{
    public ConstructSolutionTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("CONSTRUCT { ?s <http://ex/z> ?o } WHERE { ?s <http://ex/p> ?o } ORDER BY DESC(?s) LIMIT 1")]
    [InlineData("CONSTRUCT { ?s <http://ex/z> ?o } WHERE { ?s <http://ex/p> ?o } ORDER BY ?s OFFSET 1")]
    public void Construct_AppliesItsSolutionModifiers(string query)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"y\"");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(("<http://ex/b>", "<http://ex/z>", "\"y\""), Assert.Single(result.Triples!));
    }

    [Fact]
    public void Construct_ALongLiteral_IsReturnedWhole()
    {
        // The output buffer held 2,048 characters: a longer triple failed with "Destination is too short"
        var literal = "\"" + new string('z', 5000) + "\"";
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", literal);

        var result = SparqlEngine.Query(Store, "CONSTRUCT { ?s <http://ex/z> ?o } WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(literal, Assert.Single(result.Triples!).Object);
    }
}
