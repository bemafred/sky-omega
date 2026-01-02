using System.Collections.Generic;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for SPARQL query execution
/// </summary>
public class QueryExecutorTests : IDisposable
{
    private readonly string _testDir;
    private readonly QuadStore _store;

    public QueryExecutorTests()
    {
        var tempPath = TempPath.Test("queryexec");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _store = new QuadStore(_testDir);

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
        TempPath.SafeCleanup(_testDir);
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
    public void Execute_ReducedRemovesDuplicates()
    {
        // REDUCED is like DISTINCT - allows (but doesn't require) duplicate removal
        // Our implementation treats REDUCED the same as DISTINCT
        var query = "SELECT REDUCED * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://xmlns.com/foaf/0.1/name> ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify REDUCED was parsed
        Assert.True(parsedQuery.SelectClause.Reduced);
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

            // Same pattern twice in UNION, but REDUCED removes duplicates - should get 3
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

    // ========== BIND Tests ==========

    [Fact]
    public void Execute_BindConstant()
    {
        // BIND a constant value
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name BIND(42 AS ?answer) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var answers = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?answer".AsSpan());
                Assert.True(idx >= 0, "?answer should be bound");
                answers.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // All 3 people should have ?answer = 42
            Assert.Equal(3, answers.Count);
            Assert.All(answers, a => Assert.Equal("42", a));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindArithmetic()
    {
        // BIND with arithmetic on a variable
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND(?age + 10 AS ?agePlus10) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var computed = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                var computedIdx = results.Current.FindBinding("?agePlus10".AsSpan());
                Assert.True(computedIdx >= 0, "?agePlus10 should be bound");

                var age = results.Current.GetString(ageIdx).ToString();
                var agePlus10 = results.Current.GetString(computedIdx).ToString();
                computed[age] = agePlus10;
            }
            results.Dispose();

            // Alice=30 -> 40, Bob=25 -> 35, Charlie=35 -> 45
            Assert.Equal(3, computed.Count);
            Assert.Equal("40", computed["30"]);
            Assert.Equal("35", computed["25"]);
            Assert.Equal("45", computed["35"]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindMultiplication()
    {
        // BIND with multiplication
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND(?age * 2 AS ?doubled) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var computed = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                var computedIdx = results.Current.FindBinding("?doubled".AsSpan());
                Assert.True(computedIdx >= 0, "?doubled should be bound");

                var age = results.Current.GetString(ageIdx).ToString();
                var doubled = results.Current.GetString(computedIdx).ToString();
                computed[age] = doubled;
            }
            results.Dispose();

            // Alice=30 -> 60, Bob=25 -> 50, Charlie=35 -> 70
            Assert.Equal(3, computed.Count);
            Assert.Equal("60", computed["30"]);
            Assert.Equal("50", computed["25"]);
            Assert.Equal("70", computed["35"]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindWithParentheses()
    {
        // BIND with complex expression using parentheses
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND((?age + 5) * 2 AS ?computed) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var computed = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                var computedIdx = results.Current.FindBinding("?computed".AsSpan());
                Assert.True(computedIdx >= 0, "?computed should be bound");

                var age = results.Current.GetString(ageIdx).ToString();
                var result = results.Current.GetString(computedIdx).ToString();
                computed[age] = result;
            }
            results.Dispose();

            // Alice=30 -> (30+5)*2 = 70
            // Bob=25 -> (25+5)*2 = 60
            // Charlie=35 -> (35+5)*2 = 80
            Assert.Equal(3, computed.Count);
            Assert.Equal("70", computed["30"]);
            Assert.Equal("60", computed["25"]);
            Assert.Equal("80", computed["35"]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindStringLiteral()
    {
        // BIND a string literal
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name BIND(\"greeting\" AS ?msg) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var messages = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?msg".AsSpan());
                Assert.True(idx >= 0, "?msg should be bound");
                messages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            Assert.Equal(3, messages.Count);
            Assert.All(messages, m => Assert.Equal("greeting", m));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindWithFilter()
    {
        // BIND combined with FILTER
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND(?age * 2 AS ?doubled) FILTER(?doubled > 60) }";
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
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                ages.Add(results.Current.GetString(ageIdx).ToString());
            }
            results.Dispose();

            // 30*2=60 (not > 60), 25*2=50 (no), 35*2=70 (yes)
            // Only Charlie with age=35 should pass
            Assert.Single(ages);
            Assert.Equal("35", ages[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== MINUS Tests ==========

    [Fact]
    public void Execute_MinusBasic()
    {
        // Find people who have a name but don't have a "knows" relationship
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name MINUS { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }";
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
                var idx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(idx >= 0);
                names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Alice knows Bob, so Alice is excluded
            // Bob and Charlie don't know anyone, so they remain
            Assert.Equal(2, names.Count);
            Assert.Contains("\"Bob\"", names);
            Assert.Contains("\"Charlie\"", names);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusNoMatch()
    {
        // MINUS pattern that matches nothing - all results should remain
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name MINUS { ?person <http://example.org/nonexistent> ?x } }";
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
                var idx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(idx >= 0);
                names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // No MINUS matches, so all 3 people remain
            Assert.Equal(3, names.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusAllMatch()
    {
        // MINUS pattern that matches everything - no results
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name MINUS { ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Everyone has an age, so MINUS excludes all
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithConstant()
    {
        // MINUS with a constant value (data stores age as plain "30" not quoted)
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name MINUS { ?person <http://xmlns.com/foaf/0.1/age> 30 } }";
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
                var idx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(idx >= 0);
                names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Alice has age=30, so she is excluded
            // Bob (age=25) and Charlie (age=35) remain
            Assert.Equal(2, names.Count);
            Assert.Contains("\"Bob\"", names);
            Assert.Contains("\"Charlie\"", names);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithFilter()
    {
        // MINUS combined with FILTER
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 20) MINUS { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // All ages > 20: Alice=30, Bob=25, Charlie=35
            // Alice knows Bob, so Alice is excluded
            // Remaining: Bob=25, Charlie=35
            Assert.Equal(2, ages.Count);
            Assert.Contains("25", ages);
            Assert.Contains("35", ages);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== ASK Tests ==========

    [Fact]
    public void ExecuteAsk_ReturnsTrue_WhenMatchExists()
    {
        // ASK if Alice exists
        var query = "ASK WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.Equal(QueryType.Ask, parsedQuery.Type);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            Assert.True(result);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_ReturnsFalse_WhenNoMatch()
    {
        // ASK for non-existent person
        var query = "ASK WHERE { <http://example.org/NonExistent> <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            Assert.False(result);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_WithMultiplePatterns()
    {
        // ASK with join - does Alice know someone?
        var query = "ASK WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows> ?other . ?other <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // Alice knows Bob, and Bob has a name
            Assert.True(result);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_WithFilter()
    {
        // ASK with FILTER - is there anyone older than 30?
        var query = "ASK WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 30) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // Charlie is 35
            Assert.True(result);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_WithFilter_NoMatch()
    {
        // ASK with FILTER that matches nothing
        var query = "ASK WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 100) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // No one is older than 100
            Assert.False(result);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_AllVariables()
    {
        // ASK if any triple exists
        var query = "ASK WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // Store has triples
            Assert.True(result);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== VALUES Tests ==========

    [Fact]
    public void Execute_ValuesBasic()
    {
        // VALUES constraining age to 25 or 30
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 25 30 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify VALUES was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.Values.HasValues);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var ages = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Only Alice (30) and Bob (25), Charlie (35) is excluded
            Assert.Equal(2, ages.Count);
            Assert.Contains("25", ages);
            Assert.Contains("30", ages);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesSingleValue()
    {
        // VALUES with single value
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 35 } }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Only Charlie (35)
            Assert.Single(ages);
            Assert.Equal("35", ages[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesNoMatch()
    {
        // VALUES that matches nothing
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 100 200 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // No ages match 100 or 200
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesAllMatch()
    {
        // VALUES that matches all results
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 25 30 35 } }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // All 3 ages match
            Assert.Equal(3, ages.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesUnboundVariable()
    {
        // VALUES on a variable not in the pattern (should allow all results)
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name VALUES ?other { 1 2 3 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // ?other is never bound, so VALUES constraint allows all results
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesWithFilter()
    {
        // VALUES combined with FILTER
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 25 30 35 } FILTER(?age > 25) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // VALUES allows 25, 30, 35 but FILTER excludes 25
            Assert.Equal(2, ages.Count);
            Assert.Contains("30", ages);
            Assert.Contains("35", ages);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== CONSTRUCT Tests ==========

    [Fact]
    public void ExecuteConstruct_BasicTemplate()
    {
        // CONSTRUCT a new predicate from existing data
        var query = "CONSTRUCT { ?person <http://example.org/hasName> ?name } WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.Equal(QueryType.Construct, parsedQuery.Type);
        Assert.True(parsedQuery.ConstructTemplate.HasPatterns);
        Assert.Equal(1, parsedQuery.ConstructTemplate.PatternCount);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteConstruct();

            var triples = new List<(string s, string p, string o)>();
            while (results.MoveNext())
            {
                var t = results.Current;
                triples.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
            }
            results.Dispose();

            // 3 people with names -> 3 constructed triples
            Assert.Equal(3, triples.Count);
            Assert.All(triples, t => Assert.Equal("<http://example.org/hasName>", t.p));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_MultipleTemplatePatterns()
    {
        // CONSTRUCT multiple patterns per result
        var query = "CONSTRUCT { ?person <http://example.org/type> <http://example.org/Person> . ?person <http://example.org/hasAge> ?age } WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.Equal(2, parsedQuery.ConstructTemplate.PatternCount);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteConstruct();

            var triples = new List<(string s, string p, string o)>();
            while (results.MoveNext())
            {
                var t = results.Current;
                triples.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
            }
            results.Dispose();

            // 3 people with ages * 2 patterns = 6 triples
            Assert.Equal(6, triples.Count);

            // Should have both predicates
            var typeTriples = triples.Where(t => t.p == "<http://example.org/type>").ToList();
            var ageTriples = triples.Where(t => t.p == "<http://example.org/hasAge>").ToList();
            Assert.Equal(3, typeTriples.Count);
            Assert.Equal(3, ageTriples.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_WithConstantInTemplate()
    {
        // CONSTRUCT with constant value in template
        var query = "CONSTRUCT { ?person <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> } WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteConstruct();

            var triples = new List<(string s, string p, string o)>();
            while (results.MoveNext())
            {
                var t = results.Current;
                triples.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
            }
            results.Dispose();

            // 3 people with names -> 3 type triples
            Assert.Equal(3, triples.Count);
            Assert.All(triples, t =>
            {
                Assert.Equal("<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", t.p);
                Assert.Equal("<http://xmlns.com/foaf/0.1/Person>", t.o);
            });
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_WithFilter()
    {
        // CONSTRUCT with FILTER in WHERE clause
        var query = "CONSTRUCT { ?person <http://example.org/adult> \"true\" } WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteConstruct();

            var triples = new List<(string s, string p, string o)>();
            while (results.MoveNext())
            {
                var t = results.Current;
                triples.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
            }
            results.Dispose();

            // Alice (30) and Charlie (35) are > 25
            Assert.Equal(2, triples.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_EmptyResult()
    {
        // CONSTRUCT with no matches
        var query = "CONSTRUCT { ?person <http://example.org/type> <http://example.org/Person> } WHERE { ?person <http://example.org/nonexistent> ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteConstruct();

            var count = 0;
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
    public void ExecuteConstruct_WithJoin()
    {
        // CONSTRUCT with multiple patterns in WHERE (join)
        var query = "CONSTRUCT { ?person <http://example.org/profile> ?name } WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteConstruct();

            var triples = new List<(string s, string p, string o)>();
            while (results.MoveNext())
            {
                var t = results.Current;
                triples.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
            }
            results.Dispose();

            // All 3 people have both name and age
            Assert.Equal(3, triples.Count);
            Assert.All(triples, t => Assert.Equal("<http://example.org/profile>", t.p));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== GROUP BY Tests ==========

    [Fact]
    public void Execute_GroupByWithCount()
    {
        // Count how many triples each person has
        var query = "SELECT ?person (COUNT(?p) AS ?count) WHERE { ?person ?p ?o } GROUP BY ?person";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify GROUP BY was parsed
        Assert.True(parsedQuery.SolutionModifier.GroupBy.HasGroupBy);
        Assert.Equal(1, parsedQuery.SolutionModifier.GroupBy.Count);

        // Verify aggregate was parsed
        Assert.True(parsedQuery.SelectClause.HasAggregates);
        Assert.Equal(1, parsedQuery.SelectClause.AggregateCount);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var groups = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var countIdx = results.Current.FindBinding("?count".AsSpan());
                Assert.True(personIdx >= 0);
                Assert.True(countIdx >= 0);

                var person = results.Current.GetString(personIdx).ToString();
                var count = results.Current.GetString(countIdx).ToString();
                groups[person] = count;
            }
            results.Dispose();

            // Alice has 3 triples, Bob has 2, Charlie has 2
            Assert.Equal(3, groups.Count);
            Assert.Equal("3", groups["<http://example.org/Alice>"]);
            Assert.Equal("2", groups["<http://example.org/Bob>"]);
            Assert.Equal("2", groups["<http://example.org/Charlie>"]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByWithSum()
    {
        // Sum of ages (all people)
        var query = "SELECT (SUM(?age) AS ?totalAge) WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } GROUP BY ?unused";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            Assert.True(results.MoveNext());
            var sumIdx = results.Current.FindBinding("?totalAge".AsSpan());
            Assert.True(sumIdx >= 0);

            var sum = results.Current.GetString(sumIdx).ToString();
            // 30 + 25 + 35 = 90
            Assert.Equal("90", sum);

            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByWithMinMax()
    {
        // Min and max ages
        var query = "SELECT (MIN(?age) AS ?minAge) (MAX(?age) AS ?maxAge) WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } GROUP BY ?unused";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            Assert.True(results.MoveNext());

            var minIdx = results.Current.FindBinding("?minAge".AsSpan());
            var maxIdx = results.Current.FindBinding("?maxAge".AsSpan());
            Assert.True(minIdx >= 0);
            Assert.True(maxIdx >= 0);

            var min = results.Current.GetString(minIdx).ToString();
            var max = results.Current.GetString(maxIdx).ToString();

            Assert.Equal("25", min);  // Bob
            Assert.Equal("35", max);  // Charlie

            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByWithAvg()
    {
        // Average age
        var query = "SELECT (AVG(?age) AS ?avgAge) WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } GROUP BY ?unused";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            Assert.True(results.MoveNext());

            var avgIdx = results.Current.FindBinding("?avgAge".AsSpan());
            Assert.True(avgIdx >= 0);

            var avg = results.Current.GetString(avgIdx).ToString();
            // (30 + 25 + 35) / 3 = 30
            Assert.Equal("30", avg);

            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByMultipleVariables()
    {
        // This test uses the predicate as a grouping variable
        var query = "SELECT ?p (COUNT(?o) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?p";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var groups = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var pIdx = results.Current.FindBinding("?p".AsSpan());
                var countIdx = results.Current.FindBinding("?count".AsSpan());
                Assert.True(pIdx >= 0);
                Assert.True(countIdx >= 0);

                var p = results.Current.GetString(pIdx).ToString();
                var count = results.Current.GetString(countIdx).ToString();
                groups[p] = count;
            }
            results.Dispose();

            // 3 names, 3 ages, 1 knows
            Assert.Equal(3, groups.Count);
            Assert.Equal("3", groups["<http://xmlns.com/foaf/0.1/name>"]);
            Assert.Equal("3", groups["<http://xmlns.com/foaf/0.1/age>"]);
            Assert.Equal("1", groups["<http://xmlns.com/foaf/0.1/knows>"]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByParsingOnly()
    {
        // Just verify parsing works for various GROUP BY queries
        var query1 = "SELECT ?x (COUNT(?y) AS ?c) WHERE { ?x ?p ?y } GROUP BY ?x";
        var parser1 = new SparqlParser(query1.AsSpan());
        var parsed1 = parser1.ParseQuery();
        Assert.True(parsed1.SolutionModifier.GroupBy.HasGroupBy);
        Assert.Equal(AggregateFunction.Count, parsed1.SelectClause.GetAggregate(0).Function);

        var query2 = "SELECT (SUM(?val) AS ?total) WHERE { ?s ?p ?val } GROUP BY ?s";
        var parser2 = new SparqlParser(query2.AsSpan());
        var parsed2 = parser2.ParseQuery();
        Assert.Equal(AggregateFunction.Sum, parsed2.SelectClause.GetAggregate(0).Function);

        var query3 = "SELECT (AVG(?n) AS ?avg) WHERE { ?x <http://ex/val> ?n } GROUP BY ?x";
        var parser3 = new SparqlParser(query3.AsSpan());
        var parsed3 = parser3.ParseQuery();
        Assert.Equal(AggregateFunction.Avg, parsed3.SelectClause.GetAggregate(0).Function);
    }

    // ========== HAVING Tests ==========

    [Fact]
    public void Execute_HavingWithCount()
    {
        // Filter groups to only those with more than 2 triples
        var query = "SELECT ?person (COUNT(?p) AS ?count) WHERE { ?person ?p ?o } GROUP BY ?person HAVING(?count > 2)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify HAVING was parsed
        Assert.True(parsedQuery.SolutionModifier.Having.HasHaving);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var groups = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var countIdx = results.Current.FindBinding("?count".AsSpan());
                Assert.True(personIdx >= 0);
                Assert.True(countIdx >= 0);

                var person = results.Current.GetString(personIdx).ToString();
                var count = results.Current.GetString(countIdx).ToString();
                groups[person] = count;
            }
            results.Dispose();

            // Alice has 3 triples (name, age, knows), Bob has 2, Charlie has 2
            // Only Alice has count > 2
            Assert.Single(groups);
            Assert.Equal("3", groups["<http://example.org/Alice>"]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_HavingWithSum()
    {
        // Filter by sum - count predicate groups with count >= 3
        var query = "SELECT ?p (COUNT(?o) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?p HAVING(?count >= 3)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var groups = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var pIdx = results.Current.FindBinding("?p".AsSpan());
                var countIdx = results.Current.FindBinding("?count".AsSpan());
                Assert.True(pIdx >= 0);
                Assert.True(countIdx >= 0);

                var p = results.Current.GetString(pIdx).ToString();
                var count = results.Current.GetString(countIdx).ToString();
                groups[p] = count;
            }
            results.Dispose();

            // 3 names, 3 ages, 1 knows
            // Only name and age predicates have count >= 3
            Assert.Equal(2, groups.Count);
            Assert.Equal("3", groups["<http://xmlns.com/foaf/0.1/name>"]);
            Assert.Equal("3", groups["<http://xmlns.com/foaf/0.1/age>"]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_HavingExcludesAll()
    {
        // HAVING that excludes all groups
        var query = "SELECT ?person (COUNT(?p) AS ?count) WHERE { ?person ?p ?o } GROUP BY ?person HAVING(?count > 100)";
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

            // No one has more than 100 triples
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_HavingWithEquality()
    {
        // HAVING with equality check
        var query = "SELECT ?person (COUNT(?p) AS ?count) WHERE { ?person ?p ?o } GROUP BY ?person HAVING(?count = 2)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                Assert.True(personIdx >= 0);
                persons.Add(results.Current.GetString(personIdx).ToString());
            }
            results.Dispose();

            // Bob and Charlie each have exactly 2 triples
            Assert.Equal(2, persons.Count);
            Assert.Contains("<http://example.org/Bob>", persons);
            Assert.Contains("<http://example.org/Charlie>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== GROUP_CONCAT Tests ==========

    [Fact]
    public void Execute_GroupConcatBasic()
    {
        // Concatenate all predicates for each subject using default separator (space)
        var query = "SELECT ?s (GROUP_CONCAT(?p) AS ?predicates) WHERE { ?s ?p ?o } GROUP BY ?s";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify GROUP_CONCAT was parsed
        Assert.True(parsedQuery.SelectClause.HasAggregates);
        Assert.Equal(1, parsedQuery.SelectClause.AggregateCount);
        Assert.Equal(AggregateFunction.GroupConcat, parsedQuery.SelectClause.GetAggregate(0).Function);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var groups = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var predicatesIdx = results.Current.FindBinding("?predicates".AsSpan());
                Assert.True(sIdx >= 0);
                Assert.True(predicatesIdx >= 0);

                groups[results.Current.GetString(sIdx).ToString()] =
                    results.Current.GetString(predicatesIdx).ToString();
            }
            results.Dispose();

            // Alice has 3 predicates (name, age, knows)
            Assert.True(groups.ContainsKey("<http://example.org/Alice>"));
            var alicePredicates = groups["<http://example.org/Alice>"];
            Assert.Contains("<http://xmlns.com/foaf/0.1/name>", alicePredicates);
            Assert.Contains("<http://xmlns.com/foaf/0.1/age>", alicePredicates);
            Assert.Contains("<http://xmlns.com/foaf/0.1/knows>", alicePredicates);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupConcatWithSeparator()
    {
        // Concatenate names with comma separator
        var query = "SELECT (GROUP_CONCAT(?name ; SEPARATOR=\", \") AS ?names) WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name } GROUP BY ?unused";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify separator was parsed
        Assert.Equal(AggregateFunction.GroupConcat, parsedQuery.SelectClause.GetAggregate(0).Function);
        var agg = parsedQuery.SelectClause.GetAggregate(0);
        Assert.True(agg.SeparatorLength > 0);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            Assert.True(results.MoveNext());
            var namesIdx = results.Current.FindBinding("?names".AsSpan());
            Assert.True(namesIdx >= 0);

            var names = results.Current.GetString(namesIdx).ToString();
            // Should contain comma separator between names
            Assert.Contains(", ", names);
            // Should contain all 3 names
            Assert.Contains("\"Alice\"", names);
            Assert.Contains("\"Bob\"", names);
            Assert.Contains("\"Charlie\"", names);

            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupConcatDistinct()
    {
        // Test DISTINCT by using existing names - add duplicate Alice names
        // First verify we have 3 distinct names in the store
        var query = "SELECT ?person (GROUP_CONCAT(DISTINCT ?name ; SEPARATOR=\"|\") AS ?names) WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } GROUP BY ?person";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify DISTINCT was parsed
        var agg = parsedQuery.SelectClause.GetAggregate(0);
        Assert.True(agg.Distinct);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
                var namesIdx = results.Current.FindBinding("?names".AsSpan());
                Assert.True(namesIdx >= 0);

                var names = results.Current.GetString(namesIdx).ToString();
                // Each person has exactly one name, so no separator needed in result
                Assert.DoesNotContain("|", names);
            }

            // 3 people
            Assert.Equal(3, count);

            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupConcatParsingVariants()
    {
        // Test various GROUP_CONCAT syntax variations
        var query1 = "SELECT (GROUP_CONCAT(?x) AS ?c) WHERE { ?s ?p ?x } GROUP BY ?s";
        var parser1 = new SparqlParser(query1.AsSpan());
        var parsed1 = parser1.ParseQuery();
        Assert.Equal(AggregateFunction.GroupConcat, parsed1.SelectClause.GetAggregate(0).Function);
        Assert.Equal(0, parsed1.SelectClause.GetAggregate(0).SeparatorLength); // Default separator

        var query2 = "SELECT (GROUP_CONCAT(?x ; SEPARATOR=',') AS ?c) WHERE { ?s ?p ?x } GROUP BY ?s";
        var parser2 = new SparqlParser(query2.AsSpan());
        var parsed2 = parser2.ParseQuery();
        Assert.Equal(AggregateFunction.GroupConcat, parsed2.SelectClause.GetAggregate(0).Function);
        Assert.True(parsed2.SelectClause.GetAggregate(0).SeparatorLength > 0);

        var query3 = "SELECT (GROUP_CONCAT(DISTINCT ?x) AS ?c) WHERE { ?s ?p ?x } GROUP BY ?s";
        var parser3 = new SparqlParser(query3.AsSpan());
        var parsed3 = parser3.ParseQuery();
        Assert.Equal(AggregateFunction.GroupConcat, parsed3.SelectClause.GetAggregate(0).Function);
        Assert.True(parsed3.SelectClause.GetAggregate(0).Distinct);
    }

    // ========== SAMPLE Tests ==========

    [Fact]
    public void Execute_SampleBasic()
    {
        // SAMPLE returns an arbitrary value from each group
        var query = "SELECT ?person (SAMPLE(?p) AS ?somePredicate) WHERE { ?person ?p ?o } GROUP BY ?person";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify SAMPLE was parsed
        Assert.True(parsedQuery.SelectClause.HasAggregates);
        Assert.Equal(1, parsedQuery.SelectClause.AggregateCount);
        Assert.Equal(AggregateFunction.Sample, parsedQuery.SelectClause.GetAggregate(0).Function);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var samples = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var sampleIdx = results.Current.FindBinding("?somePredicate".AsSpan());
                Assert.True(personIdx >= 0);
                Assert.True(sampleIdx >= 0);

                var person = results.Current.GetString(personIdx).ToString();
                var sample = results.Current.GetString(sampleIdx).ToString();
                samples[person] = sample;

                // Sample should return a valid predicate IRI
                Assert.StartsWith("<http://", sample);
            }
            results.Dispose();

            // Should have samples for all 3 people
            Assert.Equal(3, samples.Count);
            Assert.True(samples.ContainsKey("<http://example.org/Alice>"));
            Assert.True(samples.ContainsKey("<http://example.org/Bob>"));
            Assert.True(samples.ContainsKey("<http://example.org/Charlie>"));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SampleWithDistinct()
    {
        // SAMPLE with DISTINCT - should still return one arbitrary value
        var query = "SELECT ?person (SAMPLE(DISTINCT ?p) AS ?pred) WHERE { ?person ?p ?o } GROUP BY ?person";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify DISTINCT was parsed
        var agg = parsedQuery.SelectClause.GetAggregate(0);
        Assert.Equal(AggregateFunction.Sample, agg.Function);
        Assert.True(agg.Distinct);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
                var predIdx = results.Current.FindBinding("?pred".AsSpan());
                Assert.True(predIdx >= 0);
                // Should have a non-empty value
                Assert.False(string.IsNullOrEmpty(results.Current.GetString(predIdx).ToString()));
            }
            results.Dispose();

            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SampleParsingVariants()
    {
        // Test various SAMPLE syntax variations
        var query1 = "SELECT (SAMPLE(?x) AS ?s) WHERE { ?a ?b ?x } GROUP BY ?a";
        var parser1 = new SparqlParser(query1.AsSpan());
        var parsed1 = parser1.ParseQuery();
        Assert.Equal(AggregateFunction.Sample, parsed1.SelectClause.GetAggregate(0).Function);
        Assert.False(parsed1.SelectClause.GetAggregate(0).Distinct);

        var query2 = "SELECT (SAMPLE(DISTINCT ?x) AS ?s) WHERE { ?a ?b ?x } GROUP BY ?a";
        var parser2 = new SparqlParser(query2.AsSpan());
        var parsed2 = parser2.ParseQuery();
        Assert.Equal(AggregateFunction.Sample, parsed2.SelectClause.GetAggregate(0).Function);
        Assert.True(parsed2.SelectClause.GetAggregate(0).Distinct);
    }

    // ========== EXISTS/NOT EXISTS Tests ==========

    [Fact]
    public void Execute_ExistsFilter_ReturnsMatchingRows()
    {
        // Find people who know someone
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER EXISTS { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify EXISTS was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasExists);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ExistsFilterCount);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?person".AsSpan());
                Assert.True(idx >= 0);
                persons.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Only Alice knows someone (Alice knows Bob)
            Assert.Single(persons);
            Assert.Contains("<http://example.org/Alice>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_NotExistsFilter_ReturnsNonMatchingRows()
    {
        // Find people who don't know anyone
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER NOT EXISTS { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify NOT EXISTS was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasExists);
        var existsFilter = parsedQuery.WhereClause.Pattern.GetExistsFilter(0);
        Assert.True(existsFilter.Negated);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?person".AsSpan());
                Assert.True(idx >= 0);
                persons.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Bob and Charlie don't know anyone
            Assert.Equal(2, persons.Count);
            Assert.Contains("<http://example.org/Bob>", persons);
            Assert.Contains("<http://example.org/Charlie>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ExistsWithBoundVariable()
    {
        // EXISTS with variable bound from outer pattern
        var query = "SELECT ?person ?name WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER EXISTS { ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
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
                var idx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(idx >= 0);
                names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // All people have ages, so all should match EXISTS
            Assert.Equal(3, names.Count);
            Assert.Contains("\"Alice\"", names);
            Assert.Contains("\"Bob\"", names);
            Assert.Contains("\"Charlie\"", names);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_NotExistsExcludesAll()
    {
        // NOT EXISTS that matches nothing (everyone has a name)
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER NOT EXISTS { ?person <http://xmlns.com/foaf/0.1/name> ?name } }";
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

            // Everyone has a name, so NOT EXISTS excludes all
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ExistsWithConstant()
    {
        // EXISTS checking for a specific relationship
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER EXISTS { ?person <http://xmlns.com/foaf/0.1/knows> <http://example.org/Bob> } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?person".AsSpan());
                Assert.True(idx >= 0);
                persons.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Only Alice knows Bob
            Assert.Single(persons);
            Assert.Contains("<http://example.org/Alice>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== IN/NOT IN Tests ==========

    [Fact]
    public void Execute_FilterIn_MatchesValues()
    {
        // Filter ages IN list
        var query = "SELECT ?person ?age WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age IN (25, 35)) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Bob is 25, Charlie is 35 - both match
            // Alice is 30 - excluded
            Assert.Equal(2, ages.Count);
            Assert.Contains("25", ages);
            Assert.Contains("35", ages);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterNotIn_ExcludesValues()
    {
        // Filter ages NOT IN list
        var query = "SELECT ?person ?age WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age NOT IN (25, 35)) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Alice is 30 - only one NOT in (25, 35)
            Assert.Single(ages);
            Assert.Contains("30", ages);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterInCombinedWithOtherFilter()
    {
        // IN combined with another condition
        var query = "SELECT ?person ?age WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age IN (25, 30) && ?age >= 25) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Both 25 and 30 are in list and >= 25
            Assert.Equal(2, ages.Count);
            Assert.Contains("25", ages);
            Assert.Contains("30", ages);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // ========== BOUND/IF/COALESCE Tests ==========

    [Fact]
    public void Execute_FilterBound_FiltersUnbound()
    {
        // Use OPTIONAL to create unbound variables, then filter with BOUND
        var query = "SELECT ?person ?other WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name OPTIONAL { ?person <http://xmlns.com/foaf/0.1/knows> ?other } FILTER(BOUND(?other)) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?person".AsSpan());
                Assert.True(idx >= 0);
                persons.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Only Alice has a knows relationship (to Bob)
            Assert.Single(persons);
            Assert.Contains("<http://example.org/Alice>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterNotBound_FiltersOptional()
    {
        // Filter for unbound OPTIONAL results
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name OPTIONAL { ?person <http://xmlns.com/foaf/0.1/knows> ?other } FILTER(!BOUND(?other)) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?person".AsSpan());
                Assert.True(idx >= 0);
                persons.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Bob and Charlie don't know anyone
            Assert.Equal(2, persons.Count);
            Assert.Contains("<http://example.org/Bob>", persons);
            Assert.Contains("<http://example.org/Charlie>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithIf()
    {
        // Use IF to categorize ages
        var query = "SELECT ?person ?age WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(IF(?age >= 30, 1, 0) == 1) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Alice is 30, Charlie is 35 - both >= 30
            Assert.Equal(2, ages.Count);
            Assert.Contains("30", ages);
            Assert.Contains("35", ages);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithCoalesce()
    {
        // Use COALESCE with default value
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER(COALESCE(?age, 0) == 0) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            // Since ?age is not bound in the pattern, COALESCE returns 0
            // All three people should match since ?age is unbound
            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithRegex_MatchesPattern()
    {
        // Match names starting with 'A' (stored as "Alice" with quotes, so pattern is ^.A)
        var query = "SELECT ?person ?name WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER(REGEX(?name, \"^.A\")) }";
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
                var idx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(idx >= 0);
                names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Only Alice's name starts with A (stored as "Alice" with quotes)
            Assert.Single(names);
            Assert.Contains("Alice", names[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithRegex_CaseInsensitive()
    {
        // Match names containing 'bob' case-insensitively
        var query = "SELECT ?person ?name WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER(REGEX(?name, \"bob\", \"i\")) }";
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
                var idx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(idx >= 0);
                names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Only Bob matches
            Assert.Single(names);
            Assert.Contains("Bob", names[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #region GRAPH Clause Execution

    [Fact]
    public void Execute_GraphClause_QueriesNamedGraph()
    {
        // Add data to a named graph
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/David>", "<http://xmlns.com/foaf/0.1/name>", "\"David\"", "<http://example.org/graph1>");
        _store.AddCurrentBatched("<http://example.org/David>", "<http://xmlns.com/foaf/0.1/age>", "40", "<http://example.org/graph1>");
        _store.CommitBatch();

        var query = "SELECT * WHERE { GRAPH <http://example.org/graph1> { ?s ?p ?o } }";

        // Use buffer-based constructor to avoid storing large Query struct
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            // Use ExecuteGraphToMaterialized() to avoid stack overflow from large QueryResults struct
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // David has 2 triples in graph1
            Assert.Equal(2, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_DoesNotQueryDefaultGraph()
    {
        // Add data to a named graph
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Eve>", "<http://xmlns.com/foaf/0.1/name>", "\"Eve\"", "<http://example.org/graph2>");
        _store.CommitBatch();

        // Query default graph - should NOT find Eve
        var query = "SELECT * WHERE { <http://example.org/Eve> ?p ?o }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Eve is in graph2, not default graph
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_BindsVariables()
    {
        // Add data to a named graph
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Frank>", "<http://xmlns.com/foaf/0.1/name>", "\"Frank\"", "<http://example.org/graph3>");
        _store.CommitBatch();

        var query = "SELECT ?name WHERE { GRAPH <http://example.org/graph3> { <http://example.org/Frank> <http://xmlns.com/foaf/0.1/name> ?name } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var names = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?name".AsSpan());
                if (idx >= 0)
                    names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            Assert.Single(names);
            Assert.Equal("\"Frank\"", names[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_MultiplePatterns()
    {
        // Add data to a named graph
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Grace>", "<http://xmlns.com/foaf/0.1/name>", "\"Grace\"", "<http://example.org/graph4>");
        _store.AddCurrentBatched("<http://example.org/Grace>", "<http://xmlns.com/foaf/0.1/age>", "28", "<http://example.org/graph4>");
        _store.CommitBatch();

        var query = "SELECT ?name ?age WHERE { GRAPH <http://example.org/graph4> { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(nameIdx >= 0);
                Assert.True(ageIdx >= 0);
                Assert.Equal("\"Grace\"", results.Current.GetString(nameIdx).ToString());
                Assert.Equal("28", results.Current.GetString(ageIdx).ToString());
                count++;
            }
            results.Dispose();

            Assert.Equal(1, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_NonExistentGraph_ReturnsEmpty()
    {
        var query = "SELECT * WHERE { GRAPH <http://example.org/nonexistent> { ?s ?p ?o } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

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
    public void Execute_VariableGraph_IteratesAllNamedGraphs()
    {
        // Add data to multiple named graphs
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Henry>", "<http://xmlns.com/foaf/0.1/name>", "\"Henry\"", "<http://example.org/graphA>");
        _store.AddCurrentBatched("<http://example.org/Irene>", "<http://xmlns.com/foaf/0.1/name>", "\"Irene\"", "<http://example.org/graphB>");
        _store.AddCurrentBatched("<http://example.org/Jack>", "<http://xmlns.com/foaf/0.1/name>", "\"Jack\"", "<http://example.org/graphC>");
        _store.CommitBatch();

        var query = "SELECT ?g ?s ?name WHERE { GRAPH ?g { ?s <http://xmlns.com/foaf/0.1/name> ?name } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var foundGraphs = new HashSet<string>();
            var foundNames = new HashSet<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                if (gIdx >= 0)
                    foundGraphs.Add(results.Current.GetString(gIdx).ToString());
                if (nameIdx >= 0)
                    foundNames.Add(results.Current.GetString(nameIdx).ToString());
            }
            results.Dispose();

            // Should find all 3 graphs
            Assert.Equal(3, foundGraphs.Count);
            Assert.Contains("<http://example.org/graphA>", foundGraphs);
            Assert.Contains("<http://example.org/graphB>", foundGraphs);
            Assert.Contains("<http://example.org/graphC>", foundGraphs);

            // Should find all 3 names
            Assert.Equal(3, foundNames.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_VariableGraph_BindsGraphVariable()
    {
        // Add data to a named graph
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Kate>", "<http://xmlns.com/foaf/0.1/name>", "\"Kate\"", "<http://example.org/graphK>");
        _store.CommitBatch();

        var query = "SELECT ?g WHERE { GRAPH ?g { ?s <http://xmlns.com/foaf/0.1/name> \"Kate\" } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var graphs = new List<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                if (gIdx >= 0)
                    graphs.Add(results.Current.GetString(gIdx).ToString());
            }
            results.Dispose();

            Assert.Single(graphs);
            Assert.Equal("<http://example.org/graphK>", graphs[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_VariableGraph_ExcludesDefaultGraph()
    {
        // Add data to default graph and named graph
        _store.BeginBatch();
        // Default graph data is already there from constructor
        _store.AddCurrentBatched("<http://example.org/Leo>", "<http://xmlns.com/foaf/0.1/name>", "\"Leo\"", "<http://example.org/graphL>");
        _store.CommitBatch();

        var query = "SELECT ?g ?s WHERE { GRAPH ?g { ?s ?p ?o } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var foundGraphs = new HashSet<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                if (gIdx >= 0)
                    foundGraphs.Add(results.Current.GetString(gIdx).ToString());
            }
            results.Dispose();

            // Should NOT find default graph (empty), only named graphs
            Assert.DoesNotContain("", foundGraphs);
            // Should find the named graph
            Assert.Contains("<http://example.org/graphL>", foundGraphs);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_VariableGraph_MultiplePatterns()
    {
        // Add person with name and age to named graph
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Mary>", "<http://xmlns.com/foaf/0.1/name>", "\"Mary\"", "<http://example.org/graphM>");
        _store.AddCurrentBatched("<http://example.org/Mary>", "<http://xmlns.com/foaf/0.1/age>", "32", "<http://example.org/graphM>");
        _store.CommitBatch();

        var query = "SELECT ?g ?name ?age WHERE { GRAPH ?g { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(gIdx >= 0);
                Assert.True(nameIdx >= 0);
                Assert.True(ageIdx >= 0);
                Assert.Equal("<http://example.org/graphM>", results.Current.GetString(gIdx).ToString());
                Assert.Equal("\"Mary\"", results.Current.GetString(nameIdx).ToString());
                Assert.Equal("32", results.Current.GetString(ageIdx).ToString());
                count++;
            }
            results.Dispose();

            Assert.Equal(1, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleGraphClauses_JoinsResults()
    {
        // Add data to two different named graphs with a shared subject
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Person1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"", "<http://example.org/namesGraph>");
        _store.AddCurrentBatched("<http://example.org/Person1>", "<http://xmlns.com/foaf/0.1/age>", "30", "<http://example.org/agesGraph>");
        _store.AddCurrentBatched("<http://example.org/Person2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"", "<http://example.org/namesGraph>");
        // Person2 has no age in agesGraph - should not appear in join
        _store.CommitBatch();

        // Query with two GRAPH clauses - should join on ?person
        var query = @"SELECT ?name ?age WHERE {
            GRAPH <http://example.org/namesGraph> { ?person <http://xmlns.com/foaf/0.1/name> ?name }
            GRAPH <http://example.org/agesGraph> { ?person <http://xmlns.com/foaf/0.1/age> ?age }
        }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var found = new List<(string name, string age)>();
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(nameIdx >= 0);
                Assert.True(ageIdx >= 0);
                found.Add((results.Current.GetString(nameIdx).ToString(), results.Current.GetString(ageIdx).ToString()));
            }
            results.Dispose();

            // Only Person1 should appear (has both name and age)
            Assert.Single(found);
            Assert.Equal("\"Alice\"", found[0].name);
            Assert.Equal("30", found[0].age);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleGraphClauses_WithVariableGraph()
    {
        // Add data with variable graph pattern
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Item1>", "<http://example.org/label>", "\"Widget\"", "<http://example.org/labelsGraph>");
        _store.AddCurrentBatched("<http://example.org/Item1>", "<http://example.org/price>", "100", "<http://example.org/pricesGraph>");
        _store.CommitBatch();

        // Mix of fixed and variable GRAPH clauses
        var query = @"SELECT ?g ?label ?price WHERE {
            GRAPH <http://example.org/labelsGraph> { ?item <http://example.org/label> ?label }
            GRAPH ?g { ?item <http://example.org/price> ?price }
        }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var found = new List<(string g, string label, string price)>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var labelIdx = results.Current.FindBinding("?label".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());
                Assert.True(gIdx >= 0);
                Assert.True(labelIdx >= 0);
                Assert.True(priceIdx >= 0);
                found.Add((
                    results.Current.GetString(gIdx).ToString(),
                    results.Current.GetString(labelIdx).ToString(),
                    results.Current.GetString(priceIdx).ToString()));
            }
            results.Dispose();

            Assert.Single(found);
            Assert.Equal("<http://example.org/pricesGraph>", found[0].g);
            Assert.Equal("\"Widget\"", found[0].label);
            Assert.Equal("100", found[0].price);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleGraphClauses_NoMatch_ReturnsEmpty()
    {
        // Add data with no shared subjects between graphs
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/A>", "<http://example.org/p>", "\"ValueA\"", "<http://example.org/graphA>");
        _store.AddCurrentBatched("<http://example.org/B>", "<http://example.org/q>", "\"ValueB\"", "<http://example.org/graphB>");
        _store.CommitBatch();

        // Query with two GRAPH clauses - join on ?s should return no results
        var query = @"SELECT * WHERE {
            GRAPH <http://example.org/graphA> { ?s <http://example.org/p> ?v1 }
            GRAPH <http://example.org/graphB> { ?s <http://example.org/q> ?v2 }
        }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

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

    #endregion

    #region FROM / FROM NAMED Dataset Clauses

    [Fact]
    public void Execute_SingleFromClause_QueriesSpecifiedGraph()
    {
        // Add data to named graphs
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Person1>", "<http://xmlns.com/foaf/0.1/name>", "\"Person1\"", "<http://example.org/fromGraph1>");
        _store.AddCurrentBatched("<http://example.org/Person2>", "<http://xmlns.com/foaf/0.1/name>", "\"Person2\"", "<http://example.org/fromGraph2>");
        _store.CommitBatch();

        // Query with FROM clause - should only get data from fromGraph1
        var query = "SELECT * FROM <http://example.org/fromGraph1> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var subjects = new List<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0)
                    subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should only find Person1 from fromGraph1
            Assert.Single(subjects);
            Assert.Contains("<http://example.org/Person1>", subjects);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleFromClauses_UnionsResults()
    {
        // Add data to multiple named graphs
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/PersonA>", "<http://xmlns.com/foaf/0.1/name>", "\"PersonA\"", "<http://example.org/unionGraph1>");
        _store.AddCurrentBatched("<http://example.org/PersonB>", "<http://xmlns.com/foaf/0.1/name>", "\"PersonB\"", "<http://example.org/unionGraph2>");
        _store.AddCurrentBatched("<http://example.org/PersonC>", "<http://xmlns.com/foaf/0.1/name>", "\"PersonC\"", "<http://example.org/unionGraph3>");
        _store.CommitBatch();

        // Query with multiple FROM clauses - should union graph1 and graph2
        var query = "SELECT * FROM <http://example.org/unionGraph1> FROM <http://example.org/unionGraph2> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var subjects = new HashSet<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0)
                    subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should find PersonA and PersonB but not PersonC
            Assert.Equal(2, subjects.Count);
            Assert.Contains("<http://example.org/PersonA>", subjects);
            Assert.Contains("<http://example.org/PersonB>", subjects);
            Assert.DoesNotContain("<http://example.org/PersonC>", subjects);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    // Note: FROM NAMED with GRAPH ?g hits stack size limitations due to large ref struct
    // combinations. This test verifies parsing works, actual execution with GRAPH ?g is
    // tested separately in the GRAPH clause tests (without FROM NAMED).
    [Fact]
    public void Parse_FromNamedClause_ParsesCorrectly()
    {
        // Query with FROM NAMED
        var query = "SELECT ?g ?s FROM NAMED <http://example.org/namedGraph1> FROM NAMED <http://example.org/namedGraph2> WHERE { GRAPH ?g { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify datasets parsed correctly
        Assert.Equal(2, parsedQuery.Datasets.Length);
        Assert.True(parsedQuery.Datasets[0].IsNamed);
        Assert.True(parsedQuery.Datasets[1].IsNamed);

        var graph1 = query.AsSpan().Slice(parsedQuery.Datasets[0].GraphIri.Start, parsedQuery.Datasets[0].GraphIri.Length).ToString();
        var graph2 = query.AsSpan().Slice(parsedQuery.Datasets[1].GraphIri.Start, parsedQuery.Datasets[1].GraphIri.Length).ToString();
        Assert.Equal("<http://example.org/namedGraph1>", graph1);
        Assert.Equal("<http://example.org/namedGraph2>", graph2);
    }

    [Fact]
    public void Execute_FromNamedClause_RestrictsGraphVariable()
    {
        // Add data to multiple named graphs
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Item1>", "<http://example.org/type>", "\"TypeA\"", "<http://example.org/restrictGraph1>");
        _store.AddCurrentBatched("<http://example.org/Item2>", "<http://example.org/type>", "\"TypeB\"", "<http://example.org/restrictGraph2>");
        _store.AddCurrentBatched("<http://example.org/Item3>", "<http://example.org/type>", "\"TypeC\"", "<http://example.org/restrictGraph3>");
        _store.CommitBatch();

        // Query with FROM NAMED - should only see restrictGraph1 and restrictGraph2
        var query = @"SELECT ?g ?s ?type
                      FROM NAMED <http://example.org/restrictGraph1>
                      FROM NAMED <http://example.org/restrictGraph2>
                      WHERE { GRAPH ?g { ?s <http://example.org/type> ?type } }";
        var buffer = ParseToBuffer(query);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var graphs = new HashSet<string>();
            var items = new HashSet<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (gIdx >= 0) graphs.Add(results.Current.GetString(gIdx).ToString());
                if (sIdx >= 0) items.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should only find Item1 and Item2, not Item3
            Assert.Equal(2, items.Count);
            Assert.Contains("<http://example.org/Item1>", items);
            Assert.Contains("<http://example.org/Item2>", items);
            Assert.DoesNotContain("<http://example.org/Item3>", items);

            // Should only bind restrictGraph1 and restrictGraph2
            Assert.Equal(2, graphs.Count);
            Assert.Contains("<http://example.org/restrictGraph1>", graphs);
            Assert.Contains("<http://example.org/restrictGraph2>", graphs);
            Assert.DoesNotContain("<http://example.org/restrictGraph3>", graphs);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_NoDatasetClauses_QueriesDefaultGraphAndAllNamed()
    {
        // This test verifies default behavior without FROM/FROM NAMED
        // Data was already added in constructor for default graph

        // Query without FROM - should get default graph data
        var query = "SELECT * WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }";
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

            // Alice, Bob, Charlie from default graph
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FromWithFilter_AppliesFilterToUnionedResults()
    {
        // Add data with varying ages to graphs
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/FilterPerson1>", "<http://xmlns.com/foaf/0.1/age>", "20", "<http://example.org/filterGraph1>");
        _store.AddCurrentBatched("<http://example.org/FilterPerson2>", "<http://xmlns.com/foaf/0.1/age>", "40", "<http://example.org/filterGraph1>");
        _store.AddCurrentBatched("<http://example.org/FilterPerson3>", "<http://xmlns.com/foaf/0.1/age>", "15", "<http://example.org/filterGraph2>");
        _store.AddCurrentBatched("<http://example.org/FilterPerson4>", "<http://xmlns.com/foaf/0.1/age>", "50", "<http://example.org/filterGraph2>");
        _store.CommitBatch();

        // Query with FROM and FILTER
        var query = @"SELECT ?s ?age
                      FROM <http://example.org/filterGraph1>
                      FROM <http://example.org/filterGraph2>
                      WHERE { ?s <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var subjects = new HashSet<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0) subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should find Person2 (age 40) and Person4 (age 50) but not Person1 (20) or Person3 (15)
            Assert.Equal(2, subjects.Count);
            Assert.Contains("<http://example.org/FilterPerson2>", subjects);
            Assert.Contains("<http://example.org/FilterPerson4>", subjects);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FromWithJoin_JoinsAcrossGraphs()
    {
        // Add related data across graphs
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/JoinPerson>", "<http://xmlns.com/foaf/0.1/name>", "\"JoinPerson\"", "<http://example.org/joinGraph1>");
        _store.AddCurrentBatched("<http://example.org/JoinPerson>", "<http://xmlns.com/foaf/0.1/age>", "35", "<http://example.org/joinGraph2>");
        _store.CommitBatch();

        // Query joining data from both graphs
        var query = @"SELECT ?name ?age
                      FROM <http://example.org/joinGraph1>
                      FROM <http://example.org/joinGraph2>
                      WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name . ?s <http://xmlns.com/foaf/0.1/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var foundJoin = false;
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                if (nameIdx >= 0 && ageIdx >= 0)
                {
                    var name = results.Current.GetString(nameIdx).ToString();
                    var age = results.Current.GetString(ageIdx).ToString();
                    if (name == "\"JoinPerson\"" && age == "35")
                        foundJoin = true;
                }
            }
            results.Dispose();

            // Should successfully join data across graphs
            Assert.True(foundJoin);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #endregion

    #region Subquery Tests

    [Fact]
    public void SubQuery_BasicParsing_ParsesCorrectly()
    {
        // Test that we can parse a simple subquery
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify we have a subquery
        Assert.True(parsedQuery.WhereClause.Pattern.HasSubQueries);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.SubQueryCount);

        // Verify the subquery has the correct pattern
        var subSelect = parsedQuery.WhereClause.Pattern.GetSubQuery(0);
        Assert.Equal(1, subSelect.PatternCount);

        var tp = subSelect.GetPattern(0);

        // Verify subject is a variable
        Assert.Equal(TermType.Variable, tp.Subject.Type);
        Assert.True(tp.Subject.Length > 0, "Subject variable should have non-zero length");
        var subjectVar = query.AsSpan().Slice(tp.Subject.Start, tp.Subject.Length).ToString();
        Assert.Equal("?person", subjectVar);

        // Verify predicate is an IRI
        Assert.Equal(TermType.Iri, tp.Predicate.Type);
        Assert.True(tp.Predicate.Length > 0, $"Predicate IRI should have non-zero length, got Start={tp.Predicate.Start}, Len={tp.Predicate.Length}");
        var predicateIri = query.AsSpan().Slice(tp.Predicate.Start, tp.Predicate.Length).ToString();
        Assert.Equal("<http://xmlns.com/foaf/0.1/name>", predicateIri);

        // Verify object is a variable
        Assert.Equal(TermType.Variable, tp.Object.Type);
        Assert.True(tp.Object.Length > 0, "Object variable should have non-zero length");
        var objectVar = query.AsSpan().Slice(tp.Object.Start, tp.Object.Length).ToString();
        Assert.Equal("?name", objectVar);
    }

    [Fact]
    public void SubQuery_SimpleExecution_ReturnsResults()
    {
        // Test basic subquery execution
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                if (personIdx >= 0)
                {
                    persons.Add(results.Current.GetString(personIdx).ToString());
                }
            }
            results.Dispose();

            // Should find Alice, Bob, Charlie (all have names)
            Assert.Equal(3, persons.Count);
            Assert.Contains("<http://example.org/Alice>", persons);
            Assert.Contains("<http://example.org/Bob>", persons);
            Assert.Contains("<http://example.org/Charlie>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_SelectAll_ReturnsAllInnerVariables()
    {
        // Test SELECT * in subquery
        var query = "SELECT ?person ?name WHERE { { SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(personIdx >= 0, "Should have ?person binding");
                Assert.True(nameIdx >= 0, "Should have ?name binding");
                count++;
            }
            results.Dispose();

            Assert.Equal(3, count); // Alice, Bob, Charlie
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithLimit_RespectsLimit()
    {
        // Test LIMIT in subquery
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } LIMIT 2 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(2, count); // Limited to 2
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithOffset_SkipsResults()
    {
        // Test OFFSET in subquery
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } OFFSET 1 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(2, count); // 3 total, minus 1 offset = 2
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_VariableProjection_OnlyProjectsSelectedVariables()
    {
        // Test that only SELECT-ed variables are projected to outer query
        var query = "SELECT ?person ?name WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(personIdx >= 0, "Should have ?person binding");
                // ?name should NOT be visible - it was not projected from subquery
                Assert.True(nameIdx < 0, "Should NOT have ?name binding (not projected from subquery)");
            }
            results.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_MultiplePatterns_JoinsCorrectly()
    {
        // Test subquery with multiple triple patterns
        var query = @"SELECT ?person ?name WHERE {
            {
                SELECT ?person ?name WHERE {
                    ?person <http://xmlns.com/foaf/0.1/name> ?name .
                    ?person <http://xmlns.com/foaf/0.1/age> ?age
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var names = new List<string>();
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                if (nameIdx >= 0)
                {
                    names.Add(results.Current.GetString(nameIdx).ToString());
                }
            }
            results.Dispose();

            // Alice, Bob, Charlie all have name AND age
            Assert.Equal(3, names.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithFilter_FiltersInnerResults()
    {
        // Test FILTER in subquery
        var query = @"SELECT ?person WHERE {
            {
                SELECT ?person WHERE {
                    ?person <http://xmlns.com/foaf/0.1/age> ?age
                    FILTER(?age > 28)
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                if (personIdx >= 0)
                {
                    persons.Add(results.Current.GetString(personIdx).ToString());
                }
            }
            results.Dispose();

            // Alice (30) and Charlie (35) have age > 28, Bob (25) does not
            Assert.Equal(2, persons.Count);
            Assert.Contains("<http://example.org/Alice>", persons);
            Assert.Contains("<http://example.org/Charlie>", persons);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithDistinct_RemovesDuplicates()
    {
        // Add duplicate entries for this test
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Charlie>");
        _store.CommitBatch();

        // Test DISTINCT in subquery - Alice knows both Bob and Charlie
        var query = @"SELECT ?knower WHERE {
            {
                SELECT DISTINCT ?knower WHERE {
                    ?knower <http://xmlns.com/foaf/0.1/knows> ?known
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var knowers = new List<string>();
            while (results.MoveNext())
            {
                var knowerIdx = results.Current.FindBinding("?knower".AsSpan());
                if (knowerIdx >= 0)
                {
                    knowers.Add(results.Current.GetString(knowerIdx).ToString());
                }
            }
            results.Dispose();

            // Alice knows two people but DISTINCT should return her only once
            Assert.Single(knowers);
            Assert.Equal("<http://example.org/Alice>", knowers[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithOuterPattern_JoinsCorrectly()
    {
        // Test subquery with outer pattern join
        // Subquery finds persons with names, outer pattern gets their ages
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<(string person, string age)>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(personIdx >= 0, "Should have ?person binding");
                Assert.True(ageIdx >= 0, "Should have ?age binding");

                resultList.Add((
                    results.Current.GetString(personIdx).ToString(),
                    results.Current.GetString(ageIdx).ToString()
                ));
            }
            results.Dispose();

            // All three persons have both name and age
            Assert.Equal(3, resultList.Count);
            Assert.Contains(resultList, r => r.person == "<http://example.org/Alice>" && r.age == "30");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Bob>" && r.age == "25");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Charlie>" && r.age == "35");
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithOuterPatternFilter_FiltersAfterJoin()
    {
        // Test filter on outer pattern variables after join
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } }
            FILTER(?age > 28)
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<(string person, string age)>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());

                resultList.Add((
                    results.Current.GetString(personIdx).ToString(),
                    results.Current.GetString(ageIdx).ToString()
                ));
            }
            results.Dispose();

            // Only Alice (30) and Charlie (35) have age > 28
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.person == "<http://example.org/Alice>");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Charlie>");
            Assert.DoesNotContain(resultList, r => r.person == "<http://example.org/Bob>");
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_JoinWithSubqueryFilter_RespectsSubqueryFilter()
    {
        // Test that subquery filter is respected, then outer join is applied
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE {
                ?person <http://xmlns.com/foaf/0.1/name> ?name
                FILTER(CONTAINS(?name, ""ob""))
            } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                resultList.Add(results.Current.GetString(personIdx).ToString());
            }
            results.Dispose();

            // Only Bob's name contains "ob"
            Assert.Single(resultList);
            Assert.Equal("<http://example.org/Bob>", resultList[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_EmptySubquery_ReturnsEmpty()
    {
        // Test that empty subquery results in empty outer results
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE {
                ?person <http://xmlns.com/foaf/0.1/name> ""NonExistent""
            } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

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
    public void SubQuery_MultipleOuterPatterns_JoinsAll()
    {
        // Test with multiple outer patterns
        var query = @"SELECT ?person ?name ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/name> ?name .
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<(string person, string name, string age)>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());

                resultList.Add((
                    results.Current.GetString(personIdx).ToString(),
                    results.Current.GetString(nameIdx).ToString(),
                    results.Current.GetString(ageIdx).ToString()
                ));
            }
            results.Dispose();

            // Only Alice knows someone
            Assert.Single(resultList);
            Assert.Equal("<http://example.org/Alice>", resultList[0].person);
            Assert.Equal("\"Alice\"", resultList[0].name);
            Assert.Equal("30", resultList[0].age);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleSubqueries_JoinsResults()
    {
        // Add data where two subqueries will join on a shared variable
        // Use unique predicates to avoid conflicts with test fixture data
        _store.BeginBatch();
        // First subquery: employees with nicknames
        _store.AddCurrentBatched("<http://example.org/emp100>", "<http://example.org/nickname>", "\"EmpAlice\"");
        _store.AddCurrentBatched("<http://example.org/emp200>", "<http://example.org/nickname>", "\"EmpBob\"");
        // Second subquery: employees with salaries (only emp100)
        _store.AddCurrentBatched("<http://example.org/emp100>", "<http://example.org/salary>", "50000");
        _store.CommitBatch();

        // Query with two subqueries - should join on ?employee
        var query = @"SELECT ?employee ?nick ?sal WHERE {
            { SELECT ?employee ?nick WHERE { ?employee <http://example.org/nickname> ?nick } }
            { SELECT ?employee ?sal WHERE { ?employee <http://example.org/salary> ?sal } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var found = new List<(string employee, string nick, string sal)>();
            while (results.MoveNext())
            {
                var empIdx = results.Current.FindBinding("?employee".AsSpan());
                var nickIdx = results.Current.FindBinding("?nick".AsSpan());
                var salIdx = results.Current.FindBinding("?sal".AsSpan());
                if (empIdx < 0 || nickIdx < 0 || salIdx < 0)
                {
                    var subQueryCount = parsedQuery.WhereClause.Pattern.SubQueryCount;
                    var requiredPatternCount = parsedQuery.WhereClause.Pattern.RequiredPatternCount;
                    Assert.Fail($"Missing binding: empIdx={empIdx}, nickIdx={nickIdx}, salIdx={salIdx}. SubQueryCount={subQueryCount}, RequiredPatternCount={requiredPatternCount}");
                }
                found.Add((
                    results.Current.GetString(empIdx).ToString(),
                    results.Current.GetString(nickIdx).ToString(),
                    results.Current.GetString(salIdx).ToString()));
            }
            results.Dispose();

            // Only emp100 should appear (has both nickname and salary)
            Assert.Single(found);
            Assert.Equal("<http://example.org/emp100>", found[0].employee);
            Assert.Equal("\"EmpAlice\"", found[0].nick);
            Assert.Equal("50000", found[0].sal);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleSubqueries_WithOuterPattern()
    {
        // Add data for subqueries plus outer pattern
        _store.BeginBatch();
        // Subquery 1: employees with departments
        _store.AddCurrentBatched("<http://example.org/emp1>", "<http://example.org/dept>", "<http://example.org/engineering>");
        _store.AddCurrentBatched("<http://example.org/emp2>", "<http://example.org/dept>", "<http://example.org/sales>");
        // Subquery 2: employees with names
        _store.AddCurrentBatched("<http://example.org/emp1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        _store.AddCurrentBatched("<http://example.org/emp2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        // Outer pattern: department labels (only engineering has label)
        _store.AddCurrentBatched("<http://example.org/engineering>", "<http://www.w3.org/2000/01/rdf-schema#label>", "\"Engineering Dept\"");
        _store.CommitBatch();

        // Query with two subqueries and an outer pattern
        var query = @"SELECT ?emp ?name ?deptLabel WHERE {
            { SELECT ?emp ?dept WHERE { ?emp <http://example.org/dept> ?dept } }
            { SELECT ?emp ?name WHERE { ?emp <http://xmlns.com/foaf/0.1/name> ?name } }
            ?dept <http://www.w3.org/2000/01/rdf-schema#label> ?deptLabel
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var found = new List<(string emp, string name, string label)>();
            while (results.MoveNext())
            {
                var empIdx = results.Current.FindBinding("?emp".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var labelIdx = results.Current.FindBinding("?deptLabel".AsSpan());
                Assert.True(empIdx >= 0);
                Assert.True(nameIdx >= 0);
                Assert.True(labelIdx >= 0);
                found.Add((
                    results.Current.GetString(empIdx).ToString(),
                    results.Current.GetString(nameIdx).ToString(),
                    results.Current.GetString(labelIdx).ToString()));
            }
            results.Dispose();

            // Only emp1 has department with a label
            Assert.Single(found);
            Assert.Equal("<http://example.org/emp1>", found[0].emp);
            Assert.Equal("\"Alice\"", found[0].name);
            Assert.Equal("\"Engineering Dept\"", found[0].label);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleSubqueries_NoMatch_ReturnsEmpty()
    {
        // Add data with no shared values between subqueries
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/A>", "<http://example.org/typeA>", "\"Value1\"");
        _store.AddCurrentBatched("<http://example.org/B>", "<http://example.org/typeB>", "\"Value2\"");
        _store.CommitBatch();

        // Query with two subqueries that have same variable but different subjects
        var query = @"SELECT ?s ?v1 ?v2 WHERE {
            { SELECT ?s ?v1 WHERE { ?s <http://example.org/typeA> ?v1 } }
            { SELECT ?s ?v2 WHERE { ?s <http://example.org/typeB> ?v2 } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Debug: Check how many subqueries were parsed
        var subQueryCount = parsedQuery.WhereClause.Pattern.SubQueryCount;

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            var bindingsInfo = new List<string>();
            while (results.MoveNext())
            {
                count++;
                // Collect all bindings for debug
                var bindingStrs = new List<string>();
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var v1Idx = results.Current.FindBinding("?v1".AsSpan());
                var v2Idx = results.Current.FindBinding("?v2".AsSpan());
                bindingStrs.Add($"?s idx={sIdx} val={(sIdx >= 0 ? results.Current.GetString(sIdx).ToString() : "N/A")}");
                bindingStrs.Add($"?v1 idx={v1Idx} val={(v1Idx >= 0 ? results.Current.GetString(v1Idx).ToString() : "N/A")}");
                bindingStrs.Add($"?v2 idx={v2Idx} val={(v2Idx >= 0 ? results.Current.GetString(v2Idx).ToString() : "N/A")}");
                bindingsInfo.Add(string.Join(", ", bindingStrs));
            }
            results.Dispose();

            // If count > 0, fail with debug info
            if (count > 0)
            {
                Assert.Fail($"Expected 0 results but got {count}. SubQueryCount={subQueryCount}. Bindings: [{string.Join("; ", bindingsInfo)}]");
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #endregion

    #region SERVICE clause tests

    [Fact]
    public void Parse_ServiceClause_Basic()
    {
        var query = "SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasService);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ServiceClauseCount);

        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.False(serviceClause.Silent);
        Assert.False(serviceClause.IsVariable);
        Assert.Equal(1, serviceClause.PatternCount);
    }

    [Fact]
    public void Parse_ServiceClause_Silent()
    {
        var query = "SELECT * WHERE { SERVICE SILENT <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasService);
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.Silent);
    }

    [Fact]
    public void Parse_ServiceClause_Variable()
    {
        var query = "SELECT * WHERE { SERVICE ?endpoint { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasService);
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.IsVariable);
    }

    [Fact]
    public void Parse_ServiceClause_MultiplePatterns()
    {
        var query = @"SELECT * WHERE {
            SERVICE <http://remote.example.org/sparql> {
                ?s <http://example.org/type> ?type .
                ?s <http://example.org/name> ?name
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.Equal(2, serviceClause.PatternCount);
    }

    [Fact]
    public void Execute_ServiceClause_WithMockExecutor()
    {
        var query = "SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Create mock executor with pre-configured results
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://remote.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("<http://remote.example.org/item1>", ServiceBindingType.Uri),
                ["p"] = ("<http://example.org/name>", ServiceBindingType.Uri),
                ["o"] = ("\"Item One\"", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("<http://remote.example.org/item2>", ServiceBindingType.Uri),
                ["p"] = ("<http://example.org/name>", ServiceBindingType.Uri),
                ["o"] = ("\"Item Two\"", ServiceBindingType.Literal)
            }
        });

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.ExecuteServiceToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var pIdx = results.Current.FindBinding("?p".AsSpan());
                var oIdx = results.Current.FindBinding("?o".AsSpan());

                Assert.True(sIdx >= 0, "?s should be bound");
                Assert.True(pIdx >= 0, "?p should be bound");
                Assert.True(oIdx >= 0, "?o should be bound");
            }
            results.Dispose();

            Assert.Equal(2, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceClause_Silent_ReturnsEmptyOnError()
    {
        var query = "SELECT * WHERE { SERVICE SILENT <http://failing.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Create mock executor that throws an error
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.SetErrorForEndpoint("http://failing.example.org/sparql", new SparqlServiceException("Simulated failure"));

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.ExecuteServiceToMaterialized();

            // SILENT should return empty results, not throw
            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(0, count); // No results due to error with SILENT
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceClause_NoExecutor_ThrowsInvalidOperation()
    {
        var query = "SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            // Don't provide a service executor
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);

            Assert.Throws<InvalidOperationException>(() =>
            {
                var results = executor.ExecuteServiceToMaterialized();
                results.Dispose();
            });
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_JoinsResults()
    {
        // Add local data
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://local/person1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        _store.AddCurrentBatched("<http://local/person2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        _store.CommitBatch();

        // Query that combines local pattern with SERVICE
        var query = @"SELECT ?s ?name ?remoteData WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            SERVICE <http://remote.example.org/sparql> {
                ?s <http://remote.org/data> ?remoteData
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify pattern structure
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.PatternCount); // 1 local pattern
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ServiceClauseCount); // 1 SERVICE

        // Create mock executor that returns data for person1 only
        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        // because ToRdfTerm() adds the RDF syntax
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://remote.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://local/person1", ServiceBindingType.Uri),
                ["remoteData"] = ("Remote data for Alice", ServiceBindingType.Literal)
            }
        });

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string s, string name, string data)>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var dataIdx = results.Current.FindBinding("?remoteData".AsSpan());

                Assert.True(sIdx >= 0);
                Assert.True(nameIdx >= 0);
                Assert.True(dataIdx >= 0);

                resultList.Add((
                    results.Current.GetString(sIdx).ToString(),
                    results.Current.GetString(nameIdx).ToString(),
                    results.Current.GetString(dataIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get results where local and SERVICE data join on ?s
            Assert.Single(resultList);
            Assert.Equal("<http://local/person1>", resultList[0].s);
            Assert.Equal("\"Alice\"", resultList[0].name);
            Assert.Equal("\"Remote data for Alice\"", resultList[0].data);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_MultipleLocalResults()
    {
        // Add local data with multiple results
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        _store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        _store.AddCurrentBatched("<http://local/item3>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        _store.CommitBatch();

        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            SERVICE <http://pricing.example.org/sparql> {
                ?item <http://ex.org/price> ?price
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Mock executor returns prices for items 1 and 3 (not 2)
        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        // because ToRdfTerm() adds the RDF syntax
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item1", ServiceBindingType.Uri),
                ["price"] = ("9.99", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item3", ServiceBindingType.Uri),
                ["price"] = ("19.99", ServiceBindingType.Literal)
            }
        });

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Should get 2 results (items 1 and 3 that have both local and remote data)
            Assert.Equal(2, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleServiceClauses_JoinsAllResults()
    {
        var query = @"SELECT ?s ?data1 ?data2 WHERE {
            SERVICE <http://endpoint1.example.org/sparql> {
                ?s <http://ex.org/prop1> ?data1
            }
            SERVICE <http://endpoint2.example.org/sparql> {
                ?s <http://ex.org/prop2> ?data2
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.Equal(2, parsedQuery.WhereClause.Pattern.ServiceClauseCount);

        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        // because ToRdfTerm() adds the RDF syntax
        var mockExecutor = new MockSparqlServiceExecutor();

        // First SERVICE returns data for items A and B
        mockExecutor.AddResult("http://endpoint1.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://ex.org/itemA", ServiceBindingType.Uri),
                ["data1"] = ("Data1-A", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://ex.org/itemB", ServiceBindingType.Uri),
                ["data1"] = ("Data1-B", ServiceBindingType.Literal)
            }
        });

        // Second SERVICE returns data for item A only
        mockExecutor.AddResult("http://endpoint2.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://ex.org/itemA", ServiceBindingType.Uri),
                ["data2"] = ("Data2-A", ServiceBindingType.Literal)
            }
        });

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                resultList.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should get results where both SERVICE clauses have matching ?s
            Assert.Single(resultList);
            Assert.Equal("<http://ex.org/itemA>", resultList[0]);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_Silent_HandlesErrors()
    {
        // Add local data
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://local/x>", "<http://ex.org/type>", "<http://ex.org/Thing>");
        _store.CommitBatch();

        var query = @"SELECT ?s ?data WHERE {
            ?s <http://ex.org/type> <http://ex.org/Thing> .
            SERVICE SILENT <http://failing.example.org/sparql> {
                ?s <http://ex.org/data> ?data
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.SetErrorForEndpoint("http://failing.example.org/sparql",
            new SparqlServiceException("Simulated network failure"));

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery, mockExecutor);

            // Should not throw due to SILENT modifier
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // SILENT means SERVICE errors are silently ignored, resulting in no matches
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_ServiceFirstStrategy()
    {
        // This test verifies that service-first strategy works correctly
        // when QueryPlanner determines SERVICE is more selective than local patterns.
        // The SERVICE has bound variables (highly selective), while local patterns
        // would produce many results (less selective).

        // Add many local items (low selectivity for local patterns)
        _store.BeginBatch();
        for (int i = 0; i < 100; i++)
        {
            _store.AddCurrentBatched($"<http://local/item{i}>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        }
        _store.CommitBatch();

        // Query where SERVICE has specific bound variable pattern (high selectivity)
        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            SERVICE <http://pricing.example.org/sparql> {
                ?item <http://ex.org/price> ?price
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Mock executor returns prices for only 2 specific items
        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item5", ServiceBindingType.Uri),
                ["price"] = ("9.99", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item42", ServiceBindingType.Uri),
                ["price"] = ("19.99", ServiceBindingType.Literal)
            }
        });

        // Create a QueryPlanner with statistics to influence strategy selection
        var planner = new QueryPlanner(_store.Statistics, _store.Atoms);

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery, mockExecutor, planner);
            var results = executor.Execute();

            var resultList = new List<(string item, string price)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());

                Assert.True(itemIdx >= 0);
                Assert.True(priceIdx >= 0);

                resultList.Add((
                    results.Current.GetString(itemIdx).ToString(),
                    results.Current.GetString(priceIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get 2 results (items 5 and 42 that have both local and remote data)
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item5>" && r.price == "\"9.99\"");
            Assert.Contains(resultList, r => r.item == "<http://local/item42>" && r.price == "\"19.99\"");
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #endregion

    #region Property Path Sequence Tests

    [Fact]
    public void Execute_SequencePath_BasicTwoStep()
    {
        // Add data for a 2-step path: Alice knows Bob, Bob worksAt AcmeCorp
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Bob>", "<http://example.org/worksAt>", "<http://example.org/AcmeCorp>");
        _store.CommitBatch();

        // Query: ?s knows/worksAt ?company - find companies where someone Alice knows works
        var query = "SELECT ?s ?company WHERE { ?s <http://xmlns.com/foaf/0.1/knows>/<http://example.org/worksAt> ?company }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify sequence path was expanded to 2 patterns
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.PatternCount);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            string? foundCompany = null;
            string? foundSubject = null;
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var companyIdx = results.Current.FindBinding("?company".AsSpan());
                Assert.True(sIdx >= 0);
                Assert.True(companyIdx >= 0);
                foundSubject = results.Current.GetString(sIdx).ToString();
                foundCompany = results.Current.GetString(companyIdx).ToString();
                count++;
            }
            results.Dispose();

            // Alice knows Bob, Bob worksAt AcmeCorp, so Alice -> AcmeCorp
            Assert.Equal(1, count);
            Assert.Equal("<http://example.org/Alice>", foundSubject);
            Assert.Equal("<http://example.org/AcmeCorp>", foundCompany);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SequencePath_NoMatches()
    {
        // Query with predicates that don't exist or don't form a chain
        var query = "SELECT ?s ?o WHERE { ?s <http://example.org/nonexistent>/<http://example.org/alsoNonexistent> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify sequence path was expanded to 2 patterns
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.PatternCount);

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

            // No matches expected
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SequencePath_WithBoundSubject()
    {
        // Add data for a 2-step path
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Bob>", "<http://example.org/worksAt>", "<http://example.org/AcmeCorp>");
        _store.AddCurrentBatched("<http://example.org/Charlie>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Bob>");
        _store.CommitBatch();

        // Query starting from specific subject: Alice knows/worksAt ?company
        var query = "SELECT ?company WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>/<http://example.org/worksAt> ?company }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify sequence path was expanded to 2 patterns
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.PatternCount);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            string? foundCompany = null;
            while (results.MoveNext())
            {
                var companyIdx = results.Current.FindBinding("?company".AsSpan());
                Assert.True(companyIdx >= 0);
                foundCompany = results.Current.GetString(companyIdx).ToString();
                count++;
            }
            results.Dispose();

            // Alice knows Bob, Bob worksAt AcmeCorp
            Assert.Equal(1, count);
            Assert.Equal("<http://example.org/AcmeCorp>", foundCompany);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SequencePath_MultipleResults()
    {
        // Add data where multiple paths exist
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Bob>", "<http://example.org/worksAt>", "<http://example.org/AcmeCorp>");
        _store.AddCurrentBatched("<http://example.org/Dave>", "<http://xmlns.com/foaf/0.1/name>", "\"Dave\"");
        _store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Dave>");
        _store.AddCurrentBatched("<http://example.org/Dave>", "<http://example.org/worksAt>", "<http://example.org/TechCorp>");
        _store.CommitBatch();

        // Query: ?s knows/worksAt ?company
        var query = "SELECT ?s ?company WHERE { ?s <http://xmlns.com/foaf/0.1/knows>/<http://example.org/worksAt> ?company }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var foundResults = new List<(string subject, string company)>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var companyIdx = results.Current.FindBinding("?company".AsSpan());
                foundResults.Add((
                    results.Current.GetString(sIdx).ToString(),
                    results.Current.GetString(companyIdx).ToString()
                ));
            }
            results.Dispose();

            // Alice knows Bob (worksAt AcmeCorp) and Dave (worksAt TechCorp)
            Assert.Equal(2, foundResults.Count);
            Assert.Contains(foundResults, r => r.subject == "<http://example.org/Alice>" && r.company == "<http://example.org/AcmeCorp>");
            Assert.Contains(foundResults, r => r.subject == "<http://example.org/Alice>" && r.company == "<http://example.org/TechCorp>");
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #endregion

    #region Property Path Alternative Tests

    [Fact]
    public void Execute_AlternativePath_MatchesEitherPredicate()
    {
        // Query: ?s name|age ?o - should match both name and age predicates
        var query = "SELECT ?s ?o WHERE { ?s <http://xmlns.com/foaf/0.1/name>|<http://xmlns.com/foaf/0.1/age> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify alternative path is preserved (not expanded like sequences)
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.PatternCount);
        var pattern = parsedQuery.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.Alternative, pattern.Path.Type);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var foundResults = new List<(string subject, string obj)>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var oIdx = results.Current.FindBinding("?o".AsSpan());
                foundResults.Add((
                    results.Current.GetString(sIdx).ToString(),
                    results.Current.GetString(oIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get 3 names + 3 ages = 6 results (Alice, Bob, Charlie each have name and age)
            Assert.Equal(6, foundResults.Count);

            // Verify we have both names and ages
            Assert.Contains(foundResults, r => r.subject == "<http://example.org/Alice>" && r.obj == "\"Alice\"");
            Assert.Contains(foundResults, r => r.subject == "<http://example.org/Alice>" && r.obj == "30");
            Assert.Contains(foundResults, r => r.subject == "<http://example.org/Bob>" && r.obj == "\"Bob\"");
            Assert.Contains(foundResults, r => r.subject == "<http://example.org/Bob>" && r.obj == "25");
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_AlternativePath_WithBoundSubject()
    {
        // Query with specific subject: Alice name|age ?o
        var query = "SELECT ?o WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/name>|<http://xmlns.com/foaf/0.1/age> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var foundValues = new List<string>();
            while (results.MoveNext())
            {
                var oIdx = results.Current.FindBinding("?o".AsSpan());
                foundValues.Add(results.Current.GetString(oIdx).ToString());
            }
            results.Dispose();

            // Alice has name "Alice" and age 30
            Assert.Equal(2, foundValues.Count);
            Assert.Contains("\"Alice\"", foundValues);
            Assert.Contains("30", foundValues);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_AlternativePath_NoMatches()
    {
        // Query with predicates that don't exist
        var query = "SELECT ?s ?o WHERE { ?s <http://example.org/nonexistent1>|<http://example.org/nonexistent2> ?o }";
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
    public void Execute_AlternativePath_OnlyFirstPredicateMatches()
    {
        // Only name exists, not nonexistent
        var query = "SELECT ?s ?o WHERE { ?s <http://xmlns.com/foaf/0.1/name>|<http://example.org/nonexistent> ?o }";
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

            // 3 names (Alice, Bob, Charlie)
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_AlternativePath_OnlySecondPredicateMatches()
    {
        // Only age exists (second alternative), not nonexistent (first)
        var query = "SELECT ?s ?o WHERE { ?s <http://example.org/nonexistent>|<http://xmlns.com/foaf/0.1/age> ?o }";
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

            // 3 ages (Alice, Bob, Charlie)
            Assert.Equal(3, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #endregion

    #region Property Path Inverse Tests

    [Fact]
    public void Execute_InversePath_BasicInverseTraversal()
    {
        // Alice knows Bob - inverse path semantics:
        // ?s ^<knows> ?o is equivalent to ?o <knows> ?s
        // So ?s ^<knows> <Alice> asks "who does Alice know?" = "find ?s where Alice knows ?s"
        // This should return Bob
        var query = "SELECT ?s WHERE { ?s ^<http://xmlns.com/foaf/0.1/knows> <http://example.org/Alice> }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var pattern = parsedQuery.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.Inverse, pattern.Path.Type);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var subjects = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var sIdx = bindings.FindBinding("?s".AsSpan());
                if (sIdx >= 0)
                {
                    subjects.Add(bindings.GetString(sIdx).ToString());
                }
            }
            results.Dispose();

            // Alice knows Bob, so inverse from Alice gives Bob
            Assert.Single(subjects);
            Assert.Contains("<http://example.org/Bob>", subjects);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_InversePath_FindAllKnown()
    {
        // Inverse path semantics: ?s ^<p> ?o is equivalent to ?o <p> ?s
        // ?known ^<knows> ?knower = ?knower <knows> ?known
        // This finds all triples with predicate knows, binding object to ?known
        // With Alice knows Bob: ?knower=Alice, ?known=Bob
        var query = "SELECT ?known WHERE { ?known ^<http://xmlns.com/foaf/0.1/knows> ?knower }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var known = new List<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?known".AsSpan());
                if (idx >= 0)
                {
                    known.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // Alice knows Bob, inverse gives Bob as known
            Assert.Single(known);
            Assert.Contains("<http://example.org/Bob>", known);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_InversePath_NoMatches()
    {
        // Inverse: <Charlie> ^<knows> ?knower = ?knower <knows> <Charlie>
        // This asks "who knows Charlie?" - no one in our data
        var query = "SELECT ?knower WHERE { <http://example.org/Charlie> ^<http://xmlns.com/foaf/0.1/knows> ?knower }";
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

            // No one knows Charlie
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_InversePath_WithDistinct()
    {
        // Inverse: ?name ^<name> ?person = ?person <name> ?name
        // This finds all distinct names - should return 3 (Alice, Bob, Charlie)
        var query = "SELECT DISTINCT ?name WHERE { ?name ^<http://xmlns.com/foaf/0.1/name> ?person }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var names = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?name".AsSpan());
                if (idx >= 0)
                {
                    names.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // All three persons have names
            Assert.Equal(3, names.Count);
            Assert.Contains("\"Alice\"", names);
            Assert.Contains("\"Bob\"", names);
            Assert.Contains("\"Charlie\"", names);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_InversePath_BothDirections()
    {
        // Combining forward and inverse paths:
        // Pattern 1: <Alice> <knows> ?friend -> finds Bob
        // Pattern 2: ?friend ^<knows> ?knower = ?knower <knows> ?friend
        //            With ?friend=Bob, asks "who knows Bob?" = Alice
        var query = "SELECT ?friend ?knower WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows> ?friend . ?friend ^<http://xmlns.com/foaf/0.1/knows> ?knower }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var pairs = new List<(string friend, string knower)>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var friendIdx = bindings.FindBinding("?friend".AsSpan());
                var knowerIdx = bindings.FindBinding("?knower".AsSpan());
                if (friendIdx >= 0 && knowerIdx >= 0)
                {
                    pairs.Add((bindings.GetString(friendIdx).ToString(), bindings.GetString(knowerIdx).ToString()));
                }
            }
            results.Dispose();

            // Alice knows Bob, and Bob is known by Alice
            Assert.Single(pairs);
            Assert.Equal("<http://example.org/Bob>", pairs[0].friend);
            Assert.Equal("<http://example.org/Alice>", pairs[0].knower);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #endregion

    #region Property Path Transitive Tests

    [Fact]
    public void Execute_ZeroOrMorePath_ReflexiveCase()
    {
        // p* includes reflexive case (0 hops): start node matches itself
        // Query: <Alice> <knows>* ?x should include Alice (0 hops) and Bob (1 hop)
        var query = "SELECT ?x WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>* ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var pattern = parsedQuery.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.ZeroOrMore, pattern.Path.Type);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var found = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    found.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // p* should include Alice (0 hops) and Bob (1 hop)
            Assert.Contains("<http://example.org/Alice>", found);
            Assert.Contains("<http://example.org/Bob>", found);
            Assert.Equal(2, found.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OneOrMorePath_NoReflexiveCase()
    {
        // p+ does NOT include reflexive case: requires at least 1 hop
        // Query: <Alice> <knows>+ ?x should only include Bob (1 hop), not Alice
        var query = "SELECT ?x WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>+ ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var pattern = parsedQuery.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.OneOrMore, pattern.Path.Type);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var found = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    found.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // p+ should only include Bob (1 hop), not Alice (0 hops)
            Assert.Contains("<http://example.org/Bob>", found);
            Assert.DoesNotContain("<http://example.org/Alice>", found);
            Assert.Single(found);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ZeroOrMorePath_MultiHop()
    {
        // Test multi-hop transitive closure with a chain: Alice -> Bob -> Charlie
        // First add the extra triple
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Charlie>");
        _store.CommitBatch();

        var query = "SELECT ?x WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>* ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var found = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    found.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // p* from Alice: Alice (0 hops), Bob (1 hop), Charlie (2 hops)
            Assert.Contains("<http://example.org/Alice>", found);
            Assert.Contains("<http://example.org/Bob>", found);
            Assert.Contains("<http://example.org/Charlie>", found);
            Assert.Equal(3, found.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OneOrMorePath_MultiHop()
    {
        // Test multi-hop with p+ (no reflexive): Alice -> Bob -> Charlie
        _store.BeginBatch();
        _store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Charlie>");
        _store.CommitBatch();

        var query = "SELECT ?x WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>+ ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var found = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    found.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // p+ from Alice: Bob (1 hop), Charlie (2 hops) - NOT Alice
            Assert.DoesNotContain("<http://example.org/Alice>", found);
            Assert.Contains("<http://example.org/Bob>", found);
            Assert.Contains("<http://example.org/Charlie>", found);
            Assert.Equal(2, found.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ZeroOrMorePath_NoMatches()
    {
        // Start from Charlie who knows no one - p* should return just Charlie (reflexive)
        var query = "SELECT ?x WHERE { <http://example.org/Charlie> <http://xmlns.com/foaf/0.1/knows>* ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var found = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    found.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // p* with no outgoing edges: only reflexive case
            Assert.Contains("<http://example.org/Charlie>", found);
            Assert.Single(found);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OneOrMorePath_NoMatches()
    {
        // Start from Charlie who knows no one - p+ should return nothing
        var query = "SELECT ?x WHERE { <http://example.org/Charlie> <http://xmlns.com/foaf/0.1/knows>+ ?x }";
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

            // p+ with no outgoing edges: empty result
            Assert.Equal(0, count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ZeroOrOnePath_WithMatch()
    {
        // p? matches 0 or 1 hop: <Alice> <knows>? ?x should give Alice and Bob
        var query = "SELECT ?x WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>? ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var pattern = parsedQuery.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.ZeroOrOne, pattern.Path.Type);

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var found = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    found.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // p? from Alice: Alice (0 hops) and Bob (1 hop)
            Assert.Contains("<http://example.org/Alice>", found);
            Assert.Contains("<http://example.org/Bob>", found);
            Assert.Equal(2, found.Count);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ZeroOrOnePath_NoMatch()
    {
        // p? with no outgoing edges: only reflexive case
        var query = "SELECT ?x WHERE { <http://example.org/Charlie> <http://xmlns.com/foaf/0.1/knows>? ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var found = new HashSet<string>();
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var idx = bindings.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    found.Add(bindings.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // p? with no outgoing edges: only Charlie (reflexive)
            Assert.Contains("<http://example.org/Charlie>", found);
            Assert.Single(found);
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #endregion
}

/// <summary>
/// Mock implementation of ISparqlServiceExecutor for testing.
/// </summary>
internal class MockSparqlServiceExecutor : ISparqlServiceExecutor
{
    private readonly Dictionary<string, List<Dictionary<string, (string value, ServiceBindingType type)>>> _results = new();
    private readonly Dictionary<string, Exception> _errors = new();

    public void AddResult(string endpoint, IEnumerable<Dictionary<string, (string value, ServiceBindingType type)>> rows)
    {
        _results[endpoint] = rows.ToList();
    }

    public void SetErrorForEndpoint(string endpoint, Exception error)
    {
        _errors[endpoint] = error;
    }

    public ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        if (_errors.TryGetValue(endpointUri, out var error))
        {
            throw error;
        }

        var resultRows = new List<ServiceResultRow>();

        if (_results.TryGetValue(endpointUri, out var rows))
        {
            foreach (var row in rows)
            {
                var resultRow = new ServiceResultRow();
                foreach (var kvp in row)
                {
                    resultRow.AddBinding(kvp.Key, new ServiceBinding(kvp.Value.type, kvp.Value.value));
                }
                resultRows.Add(resultRow);
            }
        }

        return ValueTask.FromResult(resultRows);
    }

    public ValueTask<bool> ExecuteAskAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        if (_errors.TryGetValue(endpointUri, out var error))
        {
            throw error;
        }

        return ValueTask.FromResult(_results.ContainsKey(endpointUri) && _results[endpointUri].Count > 0);
    }
}
