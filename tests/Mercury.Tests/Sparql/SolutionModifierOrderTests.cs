using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §18.2.5 applies the solution modifiers in a fixed order: grouping and aggregation, then ORDER BY,
/// projection, DISTINCT, and the slice (OFFSET, LIMIT) last. With GROUP BY, Mercury ran DISTINCT on the solutions before
/// grouping (so COUNT counted fewer) and never on the grouped rows (so equal rows came back twice). Each test seeds three
/// triples inline: two subjects with object a, one with object b.
/// </summary>
[Collection("QuadStore")]
public class SolutionModifierOrderTests : PooledStoreTestBase
{
    public SolutionModifierOrderTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void Distinct_WithGroupBy_AggregatesEverySolution()
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/a>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/a>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/b>");

        var result = SparqlEngine.Query(Store,
            "SELECT DISTINCT ?o (COUNT(?s) AS ?n) WHERE { ?s <http://ex/p> ?o } GROUP BY ?o ORDER BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Equal("<http://ex/a>", result.Rows[0]["o"]);
        Assert.StartsWith("\"2\"", result.Rows[0]["n"]);
        Assert.Equal("<http://ex/b>", result.Rows[1]["o"]);
        Assert.StartsWith("\"1\"", result.Rows[1]["n"]);
    }

    [Fact]
    public void Distinct_OverGroupedRows_MergesEqualRows()
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/a>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/b>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/c>");

        var result = SparqlEngine.Query(Store,
            "SELECT DISTINCT (COUNT(?s) AS ?n) WHERE { ?s <http://ex/p> ?o } GROUP BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.StartsWith("\"1\"", row["n"]);
    }

    [Fact]
    public void Offset_AfterDistinct_OverGroupedRows()
    {
        // Groups a, b, c each count 1; DISTINCT leaves one row, and OFFSET 1 skips it.
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/a>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/b>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/c>");

        var result = SparqlEngine.Query(Store,
            "SELECT DISTINCT (COUNT(?s) AS ?n) WHERE { ?s <http://ex/p> ?o } GROUP BY ?o OFFSET 1",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Rows!);
    }

    [Fact]
    public void Offset_AfterDistinct_WithoutGrouping()
    {
        Store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/a>");
        Store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/a>");
        Store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "<http://ex/b>");

        var result = SparqlEngine.Query(Store,
            "SELECT DISTINCT ?o WHERE { ?s <http://ex/p> ?o } ORDER BY ?o OFFSET 1",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "<http://ex/b>" }, result.Rows!.Select(r => r["o"]).ToArray());
    }
}
