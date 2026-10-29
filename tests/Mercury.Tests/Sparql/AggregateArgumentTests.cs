using System;
using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// [127] Aggregate takes any Expression as its argument, and a SELECT expression ([9]) is any Expression, an
/// aggregate alone or one with aggregates inside it. Through 1.8.31 an argument that began with a variable was read
/// as that variable alone, so COUNT(?o + 1) failed with "Aggregate expressions must use AS", and a SELECT expression
/// that began with an aggregate, (MAX(?o) - MIN(?o) AS ?span), failed the same way. One reader now reads an
/// aggregate's argument, for a SELECT expression and for an aggregate inside one. Each test seeds the same four
/// triples: a -p-> 1, a -q-> 4, b -p-> 2 and c -p-> 3.
/// </summary>
[Collection("QuadStore")]
public class AggregateArgumentTests : PooledStoreTestBase
{
    public AggregateArgumentTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT (COUNT(?o + 1) AS ?n) WHERE { ?s :p ?o }", new[] { "n=3" })]
    [InlineData("SELECT (COUNT(?o / 0) AS ?n) WHERE { ?s :p ?o }", new[] { "n=0" })]                  // each is an error
    [InlineData("SELECT (SUM(?o / 2) AS ?n) WHERE { ?s :p ?o }", new[] { "n=3.0" })]
    [InlineData("SELECT (COUNT(DISTINCT ?o + 1) AS ?n) WHERE { ?s ?p ?o }", new[] { "n=4" })]
    [InlineData("SELECT (MAX(?o) - MIN(?o) AS ?span) WHERE { ?s :p ?o }", new[] { "span=2" })]       // led by an aggregate
    [InlineData("SELECT (MAX(?o + 1) * 2 AS ?x) WHERE { ?s :p ?o }", new[] { "x=8" })]
    [InlineData("SELECT ?s (SUM(?o * 10) AS ?t) WHERE { ?s ?p ?o } GROUP BY ?s HAVING (SUM(?o * 10) > 20)",
        new[] { "s=a t=50", "s=c t=30" })]
    [InlineData("SELECT (GROUP_CONCAT(STR(?o) ; SEPARATOR = \"\"\", \"\"\") AS ?g) WHERE { ?s :p ?o }",
        new[] { "g=1, 2, 3" })]                                                                       // a long string
    [InlineData("SELECT (GROUP_CONCAT(?o ; SEPARATOR = \"|\") AS ?g) WHERE { ?s :p ?o }", new[] { "g=1|2|3" })]
    public void Aggregate_TakesAnExpressionArgument(string query, string[] expected)
    {
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/q>", "\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/c>", "<http://example.org/p>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, "PREFIX : <http://example.org/> " + query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        // A row as its bindings by name, each value without its numeric datatype, the quotes and http://example.org/
        var rows = result.Rows!
            .Select(row => string.Join(" ", row.OrderBy(binding => binding.Key, StringComparer.Ordinal).Select(binding =>
                binding.Key + "=" + binding.Value.Replace("^^<http://www.w3.org/2001/XMLSchema#integer>", "")
                    .Replace("^^<http://www.w3.org/2001/XMLSchema#decimal>", "")
                    .Replace("\"", "").Replace("<http://example.org/", "").Replace(">", ""))))
            .OrderBy(row => row, StringComparer.Ordinal);
        Assert.Equal(expected, rows);
    }

    [Theory]
    [InlineData("SELECT (SUM(?o ; SEPARATOR = \",\") AS ?x) WHERE { ?s ?p ?o }")]   // SEPARATOR is GROUP_CONCAT's
    [InlineData("SELECT (COUNT() AS ?x) WHERE { ?s ?p ?o }")]                      // no argument
    [InlineData("SELECT (SUM(?o, ?s) AS ?x) WHERE { ?s ?p ?o }")]                  // syn-bad-06: two arguments
    [InlineData("SELECT (SUM(?o AS ?x) WHERE { ?s ?p ?o }")]                       // the aggregate is not closed
    [InlineData("SELECT (COUNT(?o)) WHERE { ?s ?p ?o }")]                          // syn-bad-05: no AS
    [InlineData("SELECT (?o + 1) WHERE { ?s ?p ?o }")]                             // syn-bad-04: no AS
    [InlineData("SELECT (COUNT(?o) AS) WHERE { ?s ?p ?o }")]                       // no variable after AS
    public void SelectExpression_OutsideTheGrammar_IsASyntaxError(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }
}
