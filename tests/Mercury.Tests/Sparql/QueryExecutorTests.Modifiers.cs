using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

public partial class QueryExecutorTests
{
    [Fact]
    public void Execute_WithLimit_LimitsResults()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 3";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify LIMIT was parsed
        Assert.Equal(3, parsedQuery.SolutionModifier.Limit);

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

            // Should return exactly 3 results even though there are 7 triples
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

            // Should skip 5 and return 2 (7 total - 5 skipped)
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

            // Skip 3, then take 2 (7 total, skip 3 = 4 remaining, limit 2 = 2)
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OffsetExceedsResults_ReturnsEmpty()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } OFFSET 100";
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

            // Offset exceeds total results
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_LimitWithFilter_AppliesAfterFilter()
    {
        // Find people older than 25, but limit to 1
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) } LIMIT 1";
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

            // Alice (30) and Charlie (35) match filter, but limit to 1
            Assert.Equal(1, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

            // All 7 triples
            Assert.Equal(7, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

            // 3 name triples, all unique
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

            // All 7 triples
            Assert.Equal(7, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_DistinctWithLimit_AppliesLimitAfterDistinct()
    {
        // Get all results but limit to 2
        var query = "SELECT DISTINCT * WHERE { ?s ?p ?o } LIMIT 2";
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

            // Should get exactly 2 results
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_DistinctWithOffset_AppliesOffsetAfterDistinct()
    {
        // Get all results but skip 5
        var query = "SELECT DISTINCT * WHERE { ?s ?p ?o } OFFSET 5";
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

            // 7 unique results - 5 skipped = 2
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByDescending()
    {
        // Order by age descending
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } ORDER BY DESC(?age)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByWithLimit()
    {
        // Order by age and limit to 2
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } ORDER BY ?age LIMIT 2";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByWithOffset()
    {
        // Order by age and skip 1
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } ORDER BY ?age OFFSET 1";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByString()
    {
        // Order by name (string comparison)
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } ORDER BY ?name";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OrderByWithFilter()
    {
        // Order by age with filter
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) } ORDER BY DESC(?age)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    // ========== BIND Tests ==========
}
