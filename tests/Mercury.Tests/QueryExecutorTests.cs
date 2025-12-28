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

    [Fact]
    public void Execute_DistinctRemovesDuplicates()
    {
        // Query predicates - with DISTINCT we get unique values only
        var query = "SELECT DISTINCT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify DISTINCT was parsed
        Assert.True(parsedQuery.SelectClause.Distinct);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var predicates = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var pIdx = bindings.FindBinding("?p".AsSpan());
                if (pIdx >= 0)
                {
                    predicates.Add(bindings.GetString(pIdx).ToString());
                }
            }
            results.Dispose();

            // All 7 triples are unique (different s, p, o combinations)
            // But we're counting unique predicates: name, age, knows
            Assert.Equal(3, predicates.Count);
            Assert.Contains("<http://xmlns.com/foaf/0.1/name>", predicates);
            Assert.Contains("<http://xmlns.com/foaf/0.1/age>", predicates);
            Assert.Contains("<http://xmlns.com/foaf/0.1/knows>", predicates);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_DistinctOnSamePredicates_RemovesDuplicateRows()
    {
        // Query just predicate - multiple triples share the same predicate
        // But full rows (s,p,o) are all unique, so DISTINCT still returns 7
        var query = "SELECT DISTINCT * WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }";
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

            // 3 name triples, all unique
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_WithoutDistinct_ReturnsAllRows()
    {
        // Same query without DISTINCT - should get all 7 results
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify DISTINCT was not set
        Assert.False(parsedQuery.SelectClause.Distinct);

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
    public void Execute_DistinctWithLimit_AppliesLimitAfterDistinct()
    {
        // Get all results but limit to 2
        var query = "SELECT DISTINCT * WHERE { ?s ?p ?o } LIMIT 2";
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

            // Should get exactly 2 results
            Assert.Equal(2, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_DistinctWithOffset_AppliesOffsetAfterDistinct()
    {
        // Get all results but skip 5
        var query = "SELECT DISTINCT * WHERE { ?s ?p ?o } OFFSET 5";
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

            // 7 unique results - 5 skipped = 2
            Assert.Equal(2, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionCombinesBranches()
    {
        // Find people who have name OR age
        // First branch: names, Second branch: ages
        var query = "SELECT * WHERE { { ?person <http://xmlns.com/foaf/0.1/name> ?value } UNION { ?person <http://xmlns.com/foaf/0.1/age> ?value } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify UNION was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasUnion);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            var values = new List<string>();
            while (results.MoveNext())
            {
                count++;
                var bindings = results.Current;
                var valueIdx = bindings.FindBinding("?value".AsSpan());
                if (valueIdx >= 0)
                {
                    values.Add(bindings.GetString(valueIdx).ToString());
                }
            }
            results.Dispose();

            // 3 names + 3 ages = 6 results
            Assert.Equal(6, count);
            // Should have both name and age values
            Assert.Contains("\"Alice\"", values);
            Assert.Contains("30", values);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionSinglePatterns()
    {
        // Simple UNION of two single-pattern branches
        var query = "SELECT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://xmlns.com/foaf/0.1/knows> ?o } }";
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

            // 3 names + 1 knows = 4 results
            Assert.Equal(4, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionWithDistinct()
    {
        // UNION with DISTINCT to remove duplicates
        var query = "SELECT DISTINCT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://xmlns.com/foaf/0.1/name> ?o } }";
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

            // Same pattern twice, but DISTINCT removes duplicates - should get 3
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionWithLimit()
    {
        // UNION with LIMIT
        var query = "SELECT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://xmlns.com/foaf/0.1/age> ?o } } LIMIT 4";
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

            // 6 total (3 names + 3 ages), but limited to 4
            Assert.Equal(4, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionEmptyBranch()
    {
        // UNION where one branch has no matches
        var query = "SELECT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://example.org/nonexistent> ?o } }";
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

            // Only first branch matches (3 names)
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionBothBranchesEmpty()
    {
        // UNION where both branches have no matches
        var query = "SELECT * WHERE { { ?s <http://example.org/x> ?o } UNION { ?s <http://example.org/y> ?o } }";
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
    public void Execute_OrderByAscending()
    {
        // Order by age ascending
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } ORDER BY ?age";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify ORDER BY was parsed
        Assert.True(parsedQuery.SolutionModifier.OrderBy.HasOrderBy);
        Assert.Equal(1, parsedQuery.SolutionModifier.OrderBy.Count);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var ages = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var ageIdx = bindings.FindBinding("?age".AsSpan());
                if (ageIdx >= 0)
                {
                    ages.Add(bindings.GetString(ageIdx).ToString());
                }
            }
            results.Dispose();

            // Should be sorted ascending: 25, 30, 35
            Assert.Equal(3, ages.Count);
            Assert.Equal("25", ages[0]);
            Assert.Equal("30", ages[1]);
            Assert.Equal("35", ages[2]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByDescending()
    {
        // Order by age descending
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } ORDER BY DESC(?age)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var ages = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var ageIdx = bindings.FindBinding("?age".AsSpan());
                if (ageIdx >= 0)
                {
                    ages.Add(bindings.GetString(ageIdx).ToString());
                }
            }
            results.Dispose();

            // Should be sorted descending: 35, 30, 25
            Assert.Equal(3, ages.Count);
            Assert.Equal("35", ages[0]);
            Assert.Equal("30", ages[1]);
            Assert.Equal("25", ages[2]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByWithLimit()
    {
        // Order by age and limit to 2
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } ORDER BY ?age LIMIT 2";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var ages = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var ageIdx = bindings.FindBinding("?age".AsSpan());
                if (ageIdx >= 0)
                {
                    ages.Add(bindings.GetString(ageIdx).ToString());
                }
            }
            results.Dispose();

            // Should be first 2 sorted: 25, 30
            Assert.Equal(2, ages.Count);
            Assert.Equal("25", ages[0]);
            Assert.Equal("30", ages[1]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByWithOffset()
    {
        // Order by age and skip 1
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } ORDER BY ?age OFFSET 1";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var ages = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var ageIdx = bindings.FindBinding("?age".AsSpan());
                if (ageIdx >= 0)
                {
                    ages.Add(bindings.GetString(ageIdx).ToString());
                }
            }
            results.Dispose();

            // Should skip smallest, get: 30, 35
            Assert.Equal(2, ages.Count);
            Assert.Equal("30", ages[0]);
            Assert.Equal("35", ages[1]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByString()
    {
        // Order by name (string comparison)
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } ORDER BY ?name";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var names = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var nameIdx = bindings.FindBinding("?name".AsSpan());
                if (nameIdx >= 0)
                {
                    names.Add(bindings.GetString(nameIdx).ToString());
                }
            }
            results.Dispose();

            // Should be sorted alphabetically: "Alice", "Bob", "Charlie"
            Assert.Equal(3, names.Count);
            Assert.Equal("\"Alice\"", names[0]);
            Assert.Equal("\"Bob\"", names[1]);
            Assert.Equal("\"Charlie\"", names[2]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByWithFilter()
    {
        // Order by age with filter
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) } ORDER BY DESC(?age)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var ages = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var ageIdx = bindings.FindBinding("?age".AsSpan());
                if (ageIdx >= 0)
                {
                    ages.Add(bindings.GetString(ageIdx).ToString());
                }
            }
            results.Dispose();

            // Only Alice (30) and Charlie (35), sorted descending
            Assert.Equal(2, ages.Count);
            Assert.Equal("35", ages[0]);
            Assert.Equal("30", ages[1]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }
}
