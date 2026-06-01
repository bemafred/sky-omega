using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

public partial class QueryExecutorTests
{
    // Regression: aggregation/GROUP BY was silently dropped on the top-level GRAPH
    // execution path. QueryExecutor.Execute()'s `HasGraph && TriplePatternCount == 0`
    // branch routed through QueryResults.FromMaterializedWithGraphContext, which passed
    // DEFAULT solution-modifier clauses — so _hasGroupBy stayed false and the aggregate
    // stage never ran: COUNT/GROUP BY queries scoped to a named graph returned raw,
    // ungrouped rows with the aggregate variable unbound. Surfaced 2026-06-01 dogfooding
    // semantic-memory recall (data lives entirely in named graphs); invisible to the
    // 421/421 W3C suite, which exercises default-graph aggregation. These tests lock
    // GRAPH-scoped aggregation to parity with the default-graph path.

    [Fact]
    public void Execute_GraphScopedCountStar_ReturnsSingleCount()
    {
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex/a>", "<http://ex/p>", "\"1\"", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/b>", "<http://ex/p>", "\"2\"", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/c>", "<http://ex/p>", "\"3\"", "<urn:agg:g1>");
        Store.CommitBatch();

        var query = "SELECT (COUNT(*) AS ?n) WHERE { GRAPH <urn:agg:g1> { ?s ?p ?o } }";
        var parsedQuery = new SparqlParser(query.AsSpan()).ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            // A bare scalar aggregate must collapse to exactly one row.
            Assert.True(results.MoveNext());
            var idx = results.Current.FindBinding("?n".AsSpan());
            Assert.True(idx >= 0);
            Assert.Equal("3", ExtractLiteralValue(results.Current.GetString(idx).ToString()));
            Assert.False(results.MoveNext());
            results.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphScopedGroupByInnerVar_GroupsCorrectly()
    {
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex/a>", "<http://ex/p1>", "\"x\"", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/b>", "<http://ex/p1>", "\"y\"", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/c>", "<http://ex/p2>", "\"z\"", "<urn:agg:g1>");
        Store.CommitBatch();

        var query = "SELECT ?p (COUNT(*) AS ?n) WHERE { GRAPH <urn:agg:g1> { ?s ?p ?o } } GROUP BY ?p";
        var parsedQuery = new SparqlParser(query.AsSpan()).ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var counts = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var pIdx = results.Current.FindBinding("?p".AsSpan());
                var nIdx = results.Current.FindBinding("?n".AsSpan());
                Assert.True(pIdx >= 0);
                Assert.True(nIdx >= 0);
                counts[results.Current.GetString(pIdx).ToString()] =
                    ExtractLiteralValue(results.Current.GetString(nIdx).ToString());
            }
            results.Dispose();

            // GROUP BY must collapse to one row per distinct predicate.
            Assert.Equal(2, counts.Count);
            Assert.Equal("2", counts["<http://ex/p1>"]);
            Assert.Equal("1", counts["<http://ex/p2>"]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphVariableGroupBy_CountsPerGraph()
    {
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex/a>", "<http://ex/p>", "\"1\"", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/b>", "<http://ex/p>", "\"2\"", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/c>", "<http://ex/p>", "\"3\"", "<urn:agg:g2>");
        Store.AddCurrentBatched("<http://ex/d>", "<http://ex/p>", "\"4\"", "<urn:agg:g2>");
        Store.AddCurrentBatched("<http://ex/e>", "<http://ex/p>", "\"5\"", "<urn:agg:g2>");
        Store.CommitBatch();

        var query = "SELECT ?g (COUNT(*) AS ?n) WHERE { GRAPH ?g { ?s ?p ?o } } GROUP BY ?g";
        var parsedQuery = new SparqlParser(query.AsSpan()).ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var perGraph = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var nIdx = results.Current.FindBinding("?n".AsSpan());
                Assert.True(gIdx >= 0);
                Assert.True(nIdx >= 0);
                perGraph[results.Current.GetString(gIdx).ToString()] =
                    ExtractLiteralValue(results.Current.GetString(nIdx).ToString());
            }
            results.Dispose();

            // Clean store per test: exactly the two named graphs we inserted.
            Assert.Equal(2, perGraph.Count);
            Assert.Equal("2", perGraph["<urn:agg:g1>"]);
            Assert.Equal("3", perGraph["<urn:agg:g2>"]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphScopedAggregateWithFilter_AppliesFilter()
    {
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex/a>", "<http://ex/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/b>", "<http://ex/age>", "\"25\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<urn:agg:g1>");
        Store.AddCurrentBatched("<http://ex/c>", "<http://ex/age>", "\"40\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<urn:agg:g1>");
        Store.CommitBatch();

        // The aggregate must be computed over FILTER-surviving rows only (ages > 28 → 30, 40).
        var query = "SELECT (COUNT(*) AS ?n) WHERE { GRAPH <urn:agg:g1> { ?s <http://ex/age> ?age . FILTER(?age > 28) } }";
        var parsedQuery = new SparqlParser(query.AsSpan()).ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            Assert.True(results.MoveNext());
            var idx = results.Current.FindBinding("?n".AsSpan());
            Assert.True(idx >= 0);
            Assert.Equal("2", ExtractLiteralValue(results.Current.GetString(idx).ToString()));
            Assert.False(results.MoveNext());
            results.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }
}
