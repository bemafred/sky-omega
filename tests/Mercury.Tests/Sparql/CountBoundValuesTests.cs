using System;
using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §18.5.1.2: COUNT counts the times its expression has a bound, non-error value in the group; COUNT(*)
/// counts the solutions. Through 1.8.31 COUNT(?x) counted a solution with ?x unbound, and COUNT(expr) one where expr is
/// an error. Each test seeds the same four triples: a -p-> 1, a -q-> 4, b -p-> 2 and c -p-> 3, so an OPTIONAL :q binds
/// ?x for a only.
/// </summary>
[Collection("QuadStore")]
public class CountBoundValuesTests : PooledStoreTestBase
{
    public CountBoundValuesTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT (COUNT(?x) AS ?n) WHERE { }", new[] { "n=0" })]                       // one solution, ?x unbound
    [InlineData("SELECT (COUNT(?x) AS ?n) (COUNT(*) AS ?all) WHERE { ?s :p ?o OPTIONAL { ?s :q ?x } }",
        new[] { "all=3 n=1" })]
    [InlineData("SELECT (COUNT(DISTINCT ?x) AS ?n) WHERE { ?s :p ?o OPTIONAL { ?s :q ?x } }", new[] { "n=1" })]
    [InlineData("SELECT ?s (COUNT(?x) AS ?n) WHERE { ?s :p ?o OPTIONAL { ?s :q ?x } } GROUP BY ?s",
        new[] { "n=0 s=b", "n=0 s=c", "n=1 s=a" })]
    [InlineData("SELECT (COUNT((?o / 0)) AS ?n) (COUNT((?o + 1)) AS ?m) WHERE { ?s :p ?o }",
        new[] { "m=3 n=0" })]                                                                      // an error is not counted
    [InlineData("SELECT (COUNT(?x) AS ?n) WHERE { ?s :p ?o OPTIONAL { ?s :q ?x } } HAVING (COUNT(?x) > 0)",
        new[] { "n=1" })]
    public void Count_CountsBoundNonErrorValues(string query, string[] expected)
    {
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/q>", "\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/c>", "<http://example.org/p>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, "PREFIX : <http://example.org/> " + query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        // A row as its bindings by name, each value without the integer datatype, the quotes and http://example.org/
        var rows = result.Rows!
            .Select(row => string.Join(" ", row.OrderBy(binding => binding.Key, StringComparer.Ordinal).Select(binding =>
                binding.Key + "=" + binding.Value.Replace("^^<http://www.w3.org/2001/XMLSchema#integer>", "")
                    .Replace("\"", "").Replace("<http://example.org/", "").Replace(">", ""))))
            .OrderBy(row => row, StringComparer.Ordinal);
        Assert.Equal(expected, rows);
    }
}
