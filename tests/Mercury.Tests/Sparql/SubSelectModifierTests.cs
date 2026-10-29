using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A sub-SELECT's solution modifiers are [18] SolutionModifier, the production a query's are, and are parsed and
/// checked by the same code. It used to have its own copy, which read one HAVING condition and skipped the GROUP BY
/// rules, so it rejected valid queries and accepted invalid ones.
/// </summary>
[Collection("QuadStore")]
public class SubSelectModifierTests : PooledStoreTestBase
{
    public SubSelectModifierTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void SubSelect_HavingWithTwoConditions_AppliesBoth()
    {
        // [22] HavingClause ::= 'HAVING' HavingCondition+, the conditions joined by AND: ?x 1 has 2 rows, ?x 2 has 3
        var result = SparqlEngine.Query(Store,
            "SELECT ?x ?n WHERE { { SELECT ?x (COUNT(?y) AS ?n) WHERE { VALUES (?x ?y) { (1 1) (1 2) (2 1) (2 2) (2 3) } } " +
            "GROUP BY ?x HAVING (COUNT(?y) > 1) (COUNT(?y) < 3) } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>", row["x"]);
        Assert.Equal("\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>", row["n"]);
    }

    [Theory]
    [InlineData("SELECT * WHERE { { SELECT ?x ?y WHERE { VALUES (?x ?y) { (1 1) } } GROUP BY ?x } }")]
    [InlineData("SELECT * WHERE { { SELECT * WHERE { VALUES ?x { 1 } } GROUP BY ?x } }")]
    public void SubSelect_BreakingTheGroupByRules_IsAnError(string query)
    {
        // A projected variable must be grouped, and SELECT * cannot be grouped (SPARQL 1.1 §11.4, §18.2.4.1)
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public void SubSelect_TrailingValues_JoinsAfterItsAggregate()
    {
        // The sub-SELECT's VALUES is parsed by the query's [28] ValuesClause parser. The empty WHERE is one solution in
        // one group, so COUNT(*) is 1; the VALUES is joined with the group after aggregating (§18.2.4.3), once per data row
        var result = SparqlEngine.Query(Store,
            "SELECT ?n ?x WHERE { { SELECT ?x (COUNT(*) AS ?n) WHERE { } VALUES ?x { 1 2 3 } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
            new[] { "1", "2", "3" },
            result.Rows!.Select(row => row["x"].Split('"')[1]).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(result.Rows!, row => Assert.Equal("\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>", row["n"]));
    }
}
