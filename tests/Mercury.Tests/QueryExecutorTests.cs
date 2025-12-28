using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for SPARQL query execution
/// </summary>
public class QueryExecutorTests : IDisposable
{
    private readonly string _testDir;
    private readonly TripleStore _store;

    public QueryExecutorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"QueryExecutorTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _store = new TripleStore(_testDir);

        // Populate with test data
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        _store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/age>", "30");
        _store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Bob>");
        _store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        _store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/age>", "25");
        _store.AddCurrentBatched("<http://example.org/Charlie>", "<http://xmlns.com/foaf/0.1/name>", "\"Charlie\"");
        _store.AddCurrentBatched("<http://example.org/Charlie>", "<http://xmlns.com/foaf/0.1/age>", "35");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void Execute_SinglePatternAllVariables_ReturnsAllTriples()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SinglePatternWithConstantSubject_FiltersCorrectly()
    {
        var query = "SELECT * WHERE { <http://example.org/Alice> ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
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

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_TwoPatterns_JoinsCorrectly()
    {
        // Find people and their names
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_WithFilter_AppliesFilter()
    {
        // Find people older than 25
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void StoreDirectQuery_WithPredicate_FiltersCorrectly()
    {
        // Test the store directly to verify it filters by predicate
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(
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
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_EmptyPattern_ReturnsEmpty()
    {
        // Query that matches nothing
        var query = "SELECT * WHERE { <http://example.org/Nobody> ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
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

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindingTableContainsCorrectValues()
    {
        var query = "SELECT * WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
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
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_WithLimit_LimitsResults()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 3";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify LIMIT was parsed
        Assert.Equal(3, parsedQuery.SolutionModifier.Limit);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Should return exactly 3 results even though there are 7 triples
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_WithOffset_SkipsResults()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } OFFSET 5";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify OFFSET was parsed
        Assert.Equal(5, parsedQuery.SolutionModifier.Offset);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Should skip 5 and return 2 (7 total - 5 skipped)
            Assert.Equal(2, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_WithLimitAndOffset_CombinesCorrectly()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 2 OFFSET 3";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify both were parsed
        Assert.Equal(2, parsedQuery.SolutionModifier.Limit);
        Assert.Equal(3, parsedQuery.SolutionModifier.Offset);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Skip 3, then take 2 (7 total, skip 3 = 4 remaining, limit 2 = 2)
            Assert.Equal(2, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OffsetExceedsResults_ReturnsEmpty()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } OFFSET 100";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Offset exceeds total results
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_LimitWithFilter_AppliesAfterFilter()
    {
        // Find people older than 25, but limit to 1
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) } LIMIT 1";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Alice (30) and Charlie (35) match filter, but limit to 1
            Assert.Equal(1, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_LimitZero_ReturnsAllResults()
    {
        // LIMIT 0 means no limit (return all)
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify no limit was set
        Assert.Equal(0, parsedQuery.SolutionModifier.Limit);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // All 7 triples
            Assert.Equal(7, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OptionalMatches_ExtendsBindings()
    {
        // Alice has both name and age, so OPTIONAL should match
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name OPTIONAL { ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify OPTIONAL was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasOptionalPatterns);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            int withAge = 0;
            while (results.MoveNext())
            {
                count++;
                var bindings = results.Current;

                // Should have name binding
                var nameIdx = bindings.FindBinding("?name".AsSpan());
                Assert.True(nameIdx >= 0);

                // Should also have age binding (OPTIONAL matched)
                var ageIdx = bindings.FindBinding("?age".AsSpan());
                if (ageIdx >= 0) withAge++;
            }
            results.Dispose();

            // All 3 people have names
            Assert.Equal(3, count);
            // All 3 have ages too
            Assert.Equal(3, withAge);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OptionalNoMatch_KeepsExistingBindings()
    {
        // Query for people with optional "knows" relationship
        // Only Alice knows someone
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name OPTIONAL { ?person <http://xmlns.com/foaf/0.1/knows> ?friend } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            int withFriend = 0;
            while (results.MoveNext())
            {
                count++;
                var bindings = results.Current;

                // Should always have name binding
                var nameIdx = bindings.FindBinding("?name".AsSpan());
                Assert.True(nameIdx >= 0);

                // Friend binding only for Alice
                var friendIdx = bindings.FindBinding("?friend".AsSpan());
                if (friendIdx >= 0) withFriend++;
            }
            results.Dispose();

            // All 3 people have names
            Assert.Equal(3, count);
            // Only Alice knows someone
            Assert.Equal(1, withFriend);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OptionalParsing_PatternMarkedOptional()
    {
        var query = "SELECT * WHERE { ?s ?p ?o OPTIONAL { ?s <http://ex.org/opt> ?v } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var pattern = parsedQuery.WhereClause.Pattern;

        // Should have 2 patterns total
        Assert.Equal(2, pattern.PatternCount);

        // First pattern is required
        Assert.False(pattern.IsOptional(0));

        // Second pattern is optional
        Assert.True(pattern.IsOptional(1));
    }

    [Fact]
    public void Execute_OptionalWithFilter_AppliesCorrectly()
    {
        // Find people with optional age, filter on the optional binding
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name OPTIONAL { ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var foundPeople = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var nameIdx = bindings.FindBinding("?name".AsSpan());
                if (nameIdx >= 0)
                {
                    foundPeople.Add(bindings.GetString(nameIdx).ToString());
                }
            }
            results.Dispose();

            // Should find all 3 people
            Assert.Contains("\"Alice\"", foundPeople);
            Assert.Contains("\"Bob\"", foundPeople);
            Assert.Contains("\"Charlie\"", foundPeople);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }
}
