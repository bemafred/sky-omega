using System;
using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §18.2.4.1 computes the aggregates of the SELECT clause, HAVING and ORDER BY for each group. Through
/// 1.8.31 only the SELECT clause's were computed: HAVING found an aggregate only when the SELECT clause had it (with a
/// COUNT(*) fallback), so HAVING (MAX(?o) > 1) kept no group, and ORDER BY COUNT(*) did not order the groups. The
/// parser now adds HAVING's and ORDER BY's aggregates to the SELECT clause, hidden, and one substitution gives a
/// SELECT expression, a HAVING condition and an ORDER BY condition the group's values. Each test seeds the same four
/// triples: a -p-> 1, a -q-> 4, b -p-> 2 and c -p-> 3.
/// </summary>
[Collection("QuadStore")]
public class ModifierAggregateTests : PooledStoreTestBase
{
    public ModifierAggregateTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT ?s (SAMPLE(?o) AS ?any) WHERE { ?s :p ?o } GROUP BY ?s HAVING (MAX(?o) > 1)", new[] { "b", "c" })]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s HAVING (COUNT(*) > 1)", new[] { "a" })]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s HAVING (count( * ) > 1) (SUM(?o) >= 5)", new[] { "a" })]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s HAVING (AVG(?o) > 2)", new[] { "a", "c" })]
    [InlineData("SELECT ?s (COUNT(?o) AS ?n) WHERE { ?s ?p ?o } GROUP BY ?s HAVING (COUNT($o) = ?n && ?n > 1)", new[] { "a" })]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s HAVING (STRLEN(GROUP_CONCAT(?o ; SEPARATOR = \"|\")) > 1)", new[] { "a" })]
    [InlineData("SELECT * { { SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s HAVING (COUNT(*) > 1) } }", new[] { "a" })]
    public void Having_ComputesItsAggregatesForEachGroup(string query, string[] expected)
    {
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/q>", "\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/c>", "<http://example.org/p>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, "PREFIX : <http://example.org/> " + query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.Rows!.Select(row => row["s"].Replace("<http://example.org/", "").Replace(">", ""))
            .OrderBy(s => s, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("SELECT ?s (COUNT(*) AS ?n) WHERE { ?s ?p ?o } GROUP BY ?s ORDER BY ASC(COUNT(*)) ?s", new[] { "b", "c", "a" })]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s ORDER BY DESC(SUM(?o))", new[] { "a", "c", "b" })]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s ORDER BY DESC(MAX(?o) - MIN(?o)) ?s", new[] { "a", "b", "c" })]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o } GROUP BY ?s ORDER BY DESC(COUNT(*)) DESC(?s) LIMIT 2", new[] { "a", "c" })]
    public void OrderBy_ComputesItsAggregatesForEachGroup(string query, string[] expected)
    {
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/q>", "\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/c>", "<http://example.org/p>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, "PREFIX : <http://example.org/> " + query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.Rows!.Select(row => row["s"].Replace("<http://example.org/", "").Replace(">", "")));
    }

    [Fact]
    public void Having_WithAnAggregate_GroupsAQueryWithoutGroupBy()
    {
        // An aggregate in HAVING makes the query one group (§11.4), so (1 AS ?x) is evaluated once, for the group
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, "SELECT (1 AS ?x) WHERE { ?s ?p ?o } HAVING (COUNT(*) = 2)",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>", Assert.Single(result.Rows!)["x"]);
    }

    [Fact]
    public void Having_InAGroupedConstruct_FiltersTheGroups()
    {
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/q>", "\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store,
            "PREFIX : <http://example.org/> CONSTRUCT { ?s :many :yes } WHERE { ?s ?p ?o } GROUP BY ?s HAVING (COUNT(*) > 1)",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://example.org/a>", Assert.Single(result.Triples!).Subject);
    }

    [Fact]
    public void OrderBy_WithAnAggregate_GroupsTheQuery()
    {
        // §11.4: an aggregate in ORDER BY makes the query group, so a projected variable must be a GROUP BY variable
        var result = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s ?p ?o } ORDER BY COUNT(*)",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }
}
