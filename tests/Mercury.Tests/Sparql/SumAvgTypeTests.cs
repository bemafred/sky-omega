using Xunit;
using SkyOmega.Mercury.Tests.Fixtures;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §18.5.1.3: Sum is op:numeric-add over the values, so its type is the widest of theirs (integer,
/// decimal, float, double); §18.5.1.4: Avg is Sum divided by Count (op:numeric-divide, so integers give a decimal), and
/// 0 for no values. A value that is no number is an error. Through 1.8.31 SUM and AVG read numbers with a reader of
/// their own and kept decimal or double totals, so SUM of integers was an xsd:decimal, and a simple literal "2" was
/// added as the number 2. They now add with the evaluator's arithmetic.
/// </summary>
[Collection("QuadStore")]
public class SumAvgTypeTests : PooledStoreTestBase
{
    public SumAvgTypeTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SUM", "1 2 3", "\"6\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("SUM", "\"1\"^^<http://www.w3.org/2001/XMLSchema#short> \"2\"^^<http://www.w3.org/2001/XMLSchema#int>",
        "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>")]                                     // derived types are integers
    [InlineData("SUM", "1 2.5", "\"3.5\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    [InlineData("SUM", "\"1.5\"^^<http://www.w3.org/2001/XMLSchema#float> 1", "\"2.5\"^^<http://www.w3.org/2001/XMLSchema#float>")]
    [InlineData("SUM", "1 2.5e0", "\"3.5\"^^<http://www.w3.org/2001/XMLSchema#double>")]
    [InlineData("SUM", "9223372036854775807 1", "\"9223372036854775808.0\"^^<http://www.w3.org/2001/XMLSchema#decimal>")] // past a long
    [InlineData("SUM", "", "\"0\"^^<http://www.w3.org/2001/XMLSchema#integer>")]               // no values
    [InlineData("AVG", "1 2 3", "\"2.0\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    [InlineData("AVG", "1 2.5", "\"1.75\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    [InlineData("AVG", "1 2.5e0", "\"1.75\"^^<http://www.w3.org/2001/XMLSchema#double>")]
    [InlineData("AVG", "", "\"0\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    public void SumAndAvg_HaveTheTypeOfTheirArithmetic(string aggregate, string values, string expected)
    {
        var result = SparqlEngine.Query(Store, $"SELECT ({aggregate}(?x) AS ?r) WHERE {{ VALUES ?x {{ {values} }} }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, Assert.Single(result.Rows!)["r"]);
    }

    [Theory]
    [InlineData("SUM")]
    [InlineData("AVG")]
    public void SumAndAvg_OfAStringAmongNumbers_AreUnbound(string aggregate)
    {
        // "2" is a simple literal, not a number: adding it is an error, so the aggregate has no value
        var result = SparqlEngine.Query(Store, $"SELECT ({aggregate}(?x) AS ?r) WHERE {{ VALUES ?x {{ 1 \"2\" }} }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Assert.Single(result.Rows!).ContainsKey("r"));
    }

    [Fact]
    public void Sum_AnIntegerTotal_StaysAnIntegerInAnExpression()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/p>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, "SELECT (SUM(?o) * 2 AS ?x) WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"12\"^^<http://www.w3.org/2001/XMLSchema#integer>", Assert.Single(result.Rows!)["x"]);
    }
}
