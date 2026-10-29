using System;
using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §18.2.4 evaluates a query level in the order grouping, aggregates, HAVING, the trailing VALUES, the
/// SELECT expressions; §18.2.5 then applies ORDER BY, projection, DISTINCT and the slice. Through 1.8.31 a trailing VALUES
/// was joined with the WHERE's solutions before grouping, so a grouped query could not project or use its variables,
/// and an aggregate counted each solution once per VALUES row; ORDER BY sorted before the SELECT expressions, so it
/// could not order by an aggregate's or expression's variable. And (expr AS ?v) was accepted when ?v was already in
/// scope (§18.2.4.4). Each test seeds the same four triples: a -p-> 1, a -q-> 4, b -p-> 2 and c -p-> 3.
/// </summary>
[Collection("QuadStore")]
public class TrailingValuesGroupedTests : PooledStoreTestBase
{
    public TrailingValuesGroupedTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT ?s ?v { ?s :p ?o } GROUP BY ?s VALUES ?v { 1 }",
        new[] { "s=a v=1", "s=b v=1", "s=c v=1" })]                                  // W3C values-group-by-projection
    [InlineData("SELECT ?s (STR(?v) AS ?w) { ?s :p ?o } GROUP BY ?s VALUES ?v { 1 }",
        new[] { "s=a w=1", "s=b w=1", "s=c w=1" })]                      // W3C values-group-by-select-expression
    [InlineData("SELECT ?s (COUNT(*) AS ?n) { ?s ?p ?o } GROUP BY ?s VALUES ?s { :a :d }",
        new[] { "n=2 s=a" })]                                                        // joined on the grouping variable
    [InlineData("SELECT (COUNT(*) AS ?n) { ?s :p ?o } VALUES ?x { 1 2 }",
        new[] { "n=3", "n=3" })]                                                     // one group, joined with two rows
    [InlineData("SELECT ?s (COUNT(*) AS ?n) { ?s ?p ?o } GROUP BY ?s HAVING (COUNT(*) > 1) VALUES ?x { 1 2 }",
        new[] { "n=2 s=a", "n=2 s=a" })]                                             // HAVING counts before the join
    [InlineData("SELECT ?o (COUNT(*) AS ?n) { ?s :p ?o } GROUP BY ?o VALUES (?o ?k) { (1 \"one\") (1 \"uno\") (UNDEF \"any\") }",
        new[] { "n=1 o=1", "n=1 o=1", "n=1 o=1", "n=1 o=2", "n=1 o=3" })]            // UNDEF joins every group
    [InlineData("SELECT ?s (COUNT(*) AS ?n) (?v + 1 AS ?w) { ?s :p ?o } GROUP BY ?s VALUES ?v { 1 }",
        new[] { "n=1 s=a w=2", "n=1 s=b w=2", "n=1 s=c w=2" })]
    [InlineData("SELECT * { { SELECT ?s ?v { ?s :p ?o } GROUP BY ?s VALUES ?v { 7 } } }",
        new[] { "s=a v=7", "s=b v=7", "s=c v=7" })]                                  // a sub-SELECT's own VALUES
    [InlineData("SELECT ?s ?o { ?s :p ?o } VALUES ?s { :a :b }",
        new[] { "o=1 s=a", "o=2 s=b" })]                                             // no grouping: joined with the WHERE's
    public void TrailingValues_InAGroupedQuery_IsJoinedWithTheGroups(string query, string[] expected)
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
        Assert.Equal(expected.OrderBy(row => row, StringComparer.Ordinal), rows);
    }

    [Fact]
    public void TrailingValues_InAGroupedConstruct_BindsTheTemplate()
    {
        // W3C values-construct-group-by: ?rank is bound only by the trailing VALUES, joined after grouping; :d has no group
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/q>", "\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/c>", "<http://example.org/p>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store,
            "PREFIX : <http://example.org/> CONSTRUCT { ?s :rank ?rank } WHERE { ?s ?p ?o } GROUP BY ?s VALUES (?s ?rank) { (:a 1) (:b 2) (:d 4) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
            new[]
            {
                "<http://example.org/a> \"1\"^^<http://www.w3.org/2001/XMLSchema#integer>",
                "<http://example.org/b> \"2\"^^<http://www.w3.org/2001/XMLSchema#integer>",
            },
            result.Triples!.Select(triple => $"{triple.Subject} {triple.Object}").OrderBy(triple => triple, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("SELECT ?s (COUNT(*) AS ?n) { ?s ?p ?o } GROUP BY ?s ORDER BY ?n ?s", new[] { "b", "c", "a" })]
    [InlineData("SELECT ?s (COUNT(*) AS ?n) { ?s ?p ?o } GROUP BY ?s ORDER BY DESC(?n) DESC(?s)", new[] { "a", "c", "b" })]
    [InlineData("SELECT ?s (COUNT(*) AS ?n) { ?s ?p ?o } GROUP BY ?s ORDER BY ?n ?s LIMIT 2 OFFSET 1", new[] { "c", "a" })]
    [InlineData("SELECT ?s (COUNT(*) AS ?count) { ?s ?p ?o } GROUP BY ?s ORDER BY LCASE(?name) VALUES (?s ?name) { (:a \"beta\") (:b \"Gamma\") (:c \"Alpha\") }",
        new[] { "c", "a", "b" })]                                                    // W3C values-group-order-by
    [InlineData("SELECT ?s (-?o AS ?neg) { ?s :p ?o } ORDER BY ?neg", new[] { "c", "b", "a" })]
    [InlineData("SELECT ?s (-?o AS ?neg) { ?s :p ?o } ORDER BY ?neg LIMIT 2", new[] { "c", "b" })]
    [InlineData("SELECT ?s { { SELECT ?s (-?o AS ?neg) { ?s :p ?o } ORDER BY ?neg LIMIT 1 } }", new[] { "c" })]
    [InlineData("SELECT ?s (STR(?o) AS ?x) { ?s :p ?o } ORDER BY DESC(?s) LIMIT 2", new[] { "c", "b" })]
    public void OrderBy_SortsTheSolutionsWithTheirSelectExpressions(string query, string[] expected)
    {
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/q>", "\"4\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://example.org/c>", "<http://example.org/p>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, "PREFIX : <http://example.org/> " + query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.Rows!.Select(row => row["s"].Replace("<http://example.org/", "").Replace(">", "")));
    }

    [Theory]
    [InlineData("SELECT (1 AS ?s) WHERE { ?s ?p ?o }")]                                    // a WHERE variable
    [InlineData("SELECT (COUNT(*) AS ?s) WHERE { ?s ?p ?o } GROUP BY ?s")]                 // a GROUP BY variable
    [InlineData("SELECT ?s (COUNT(*) AS ?c) WHERE { ?s ?p ?o } GROUP BY ?s VALUES ?c { 0 1 2 }")] // W3C group_by_values_scope_03
    [InlineData("SELECT (1 AS ?v) WHERE { ?s ?p ?o } VALUES ?v { 1 }")]                    // a trailing VALUES variable
    [InlineData("SELECT (1 AS ?X) { SELECT (2 AS ?X) {} }")]                               // W3C syntax-SELECTscope2
    [InlineData("SELECT * { { SELECT (1 AS ?s) { ?s ?p ?o } } }")]                         // inside a sub-SELECT
    public void SelectExpression_AssigningAVariableInScope_IsASyntaxError(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("already in scope", result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT (1 AS ?x) (?x + 1 AS ?y) {}")]                                     // an earlier expression's variable
    [InlineData("SELECT ?s ?v { ?s ?p ?o } GROUP BY ?s VALUES ?v { 1 }")]                  // W3C group_by_values_scope_01
    [InlineData("SELECT ?v (COUNT(*) AS ?n) { ?s ?p ?o } VALUES ?v { 1 }")]                // one group, projecting VALUES
    [InlineData("SELECT * WHERE { { SELECT (1 AS ?X) {} } { SELECT (1 AS ?X) {} } }")]     // W3C syntax-SELECTscope3
    public void SelectClause_WithinTheScopeRules_Parses(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }
}
