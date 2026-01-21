using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for SPARQL query execution
/// </summary>
[Collection("QuadStore")]
public partial class QueryExecutorTests : PooledStoreTestBase
{
    private readonly QuadStorePoolFixture _fixture;

    public QueryExecutorTests(QuadStorePoolFixture fixture) : base(fixture)
    {
        _fixture = fixture;
        // Populate with test data
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        Store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Bob>");
        Store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        Store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/age>", "\"25\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrentBatched("<http://example.org/Charlie>", "<http://xmlns.com/foaf/0.1/name>", "\"Charlie\"");
        Store.AddCurrentBatched("<http://example.org/Charlie>", "<http://xmlns.com/foaf/0.1/age>", "\"35\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.CommitBatch();
    }

    /// <summary>
    /// Parse a query directly to QueryBuffer, avoiding storage of large Query struct on stack.
    /// Uses [MethodImpl(NoInlining)] to ensure Query is in a separate stack frame that gets cleaned up.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static SkyOmega.Mercury.Sparql.Patterns.QueryBuffer ParseToBuffer(string query)
    {
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        return SkyOmega.Mercury.Sparql.Patterns.QueryBufferAdapter.FromQuery(in parsedQuery, query.AsSpan());
    }

    [Fact]
    public void Execute_SinglePatternAllVariables_ReturnsAllTriples()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
                // Verify we have bindings for s, p, o
                Assert.True(results.Current.Count >= 3);
            }
            results.Dispose();

            // 7 triples in test data
            Assert.Equal(7, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SinglePatternWithConstantSubject_FiltersCorrectly()
    {
        var query = "SELECT * WHERE { <http://example.org/Alice> ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Alice has 3 triples
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SinglePatternWithConstantPredicate_FiltersCorrectly()
    {
        var query = "SELECT * WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify the pattern was parsed correctly
        var pattern = parsedQuery.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Subject.IsVariable, "Subject should be variable");
        Assert.True(pattern.Predicate.IsIri, "Predicate should be IRI");
        Assert.True(pattern.Object.IsVariable, "Object should be variable");

        // Verify the predicate IRI content
        var predicateText = query.AsSpan().Slice(pattern.Predicate.Start, pattern.Predicate.Length).ToString();
        Assert.Equal("<http://xmlns.com/foaf/0.1/name>", predicateText);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // 3 people have names
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_TwoPatterns_JoinsCorrectly()
    {
        // Find people and their names
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
                // Should have bindings for person, name, age
                var bindings = results.Current;
                Assert.True(bindings.Count >= 3);
            }
            results.Dispose();

            // All 3 people have both name and age
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_WithFilter_AppliesFilter()
    {
        // Find people older than 25
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Alice (30) and Charlie (35) are older than 25
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void StoreDirectQuery_WithPredicate_FiltersCorrectly()
    {
        // Test the store directly to verify it filters by predicate
        Store.AcquireReadLock();
        try
        {
            var results = Store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                "<http://xmlns.com/foaf/0.1/name>".AsSpan(),
                ReadOnlySpan<char>.Empty);

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Should get 3 name triples
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_EmptyPattern_ReturnsEmpty()
    {
        // Query that matches nothing
        var query = "SELECT * WHERE { <http://example.org/Nobody> ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_JoinWithNoMatches_ReturnsEmpty()
    {
        // Alice knows Bob, but we're asking for who Alice knows who knows someone
        // Bob doesn't know anyone, so this should return empty
        var query = "SELECT * WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows> ?friend . ?friend <http://xmlns.com/foaf/0.1/knows> ?other }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindingTableContainsCorrectValues()
    {
        var query = "SELECT * WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            Assert.True(results.MoveNext());

            var bindings = results.Current;
            var nameIdx = bindings.FindBinding("?name".AsSpan());
            Assert.True(nameIdx >= 0);

            var nameValue = bindings.GetString(nameIdx).ToString();
            Assert.Equal("\"Alice\"", nameValue);

            results.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }
}
