using System.Collections.Generic;
using System.Linq;
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
    public void Execute_ExistsFilter_ReturnsMatchingRows()
    {
        // Find people who know someone
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER EXISTS { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify EXISTS was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasExists);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ExistsFilterCount);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ExistsWithBoundVariable()
    {
        // EXISTS with variable bound from outer pattern
        var query = "SELECT ?person ?name WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER EXISTS { ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_NotExistsExcludesAll()
    {
        // NOT EXISTS that matches nothing (everyone has a name)
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER NOT EXISTS { ?person <http://xmlns.com/foaf/0.1/name> ?name } }";
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

            // Everyone has a name, so NOT EXISTS excludes all
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ExistsWithConstant()
    {
        // EXISTS checking for a specific relationship
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER EXISTS { ?person <http://xmlns.com/foaf/0.1/knows> <http://example.org/Bob> } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Assert.True(ages.Any(a => ExtractNumericValue(a) == "25"));
            Assert.True(ages.Any(a => ExtractNumericValue(a) == "35"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterNotIn_ExcludesValues()
    {
        // Filter ages NOT IN list
        var query = "SELECT ?person ?age WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age NOT IN (25, 35)) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Alice is 30 - only one NOT in (25, 35)
            Assert.Single(ages);
            Assert.True(ages.Any(a => ExtractNumericValue(a) == "30"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterInCombinedWithOtherFilter()
    {
        // IN combined with another condition
        var query = "SELECT ?person ?age WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age IN (25, 30) && ?age >= 25) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Both 25 and 30 are in list and >= 25
            Assert.Equal(2, ages.Count);
            Assert.True(ages.Any(a => ExtractNumericValue(a) == "25"));
            Assert.True(ages.Any(a => ExtractNumericValue(a) == "30"));
        }
        finally
        {
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterNotBound_FiltersOptional()
    {
        // Filter for unbound OPTIONAL results
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name OPTIONAL { ?person <http://xmlns.com/foaf/0.1/knows> ?other } FILTER(!BOUND(?other)) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithIf()
    {
        // Use IF to categorize ages
        var query = "SELECT ?person ?age WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(IF(?age >= 30, 1, 0) == 1) }";
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
                var idx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(idx >= 0);
                ages.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Alice is 30, Charlie is 35 - both >= 30
            Assert.Equal(2, ages.Count);
            Assert.True(ages.Any(a => ExtractNumericValue(a) == "30"));
            Assert.True(ages.Any(a => ExtractNumericValue(a) == "35"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithCoalesce()
    {
        // Use COALESCE with default value
        var query = "SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER(COALESCE(?age, 0) == 0) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithRegex_MatchesPattern()
    {
        // Match names starting with 'A' (stored as "Alice" with quotes, so pattern is ^.A)
        var query = "SELECT ?person ?name WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER(REGEX(?name, \"^.A\")) }";
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FilterWithRegex_CaseInsensitive()
    {
        // Match names containing 'bob' case-insensitively
        var query = "SELECT ?person ?name WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name FILTER(REGEX(?name, \"bob\", \"i\")) }";
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ExistsWithPrefixedNamesAndAKeyword()
    {
        // Reproduce W3C test "Positive EXISTS 1" from negation/exists-01.rq
        // Data: Sets with members, filter for sets that have member 9
        using var lease = _fixture.Pool.RentScoped();
        var store = lease.Store;

        // Add data similar to W3C set-data.ttl
        // :a, :c, :e - sets without member 9
        // :b, :d - sets with member 9
        // Use typed literals as Turtle parser would produce them
        store.BeginBatch();
        store.AddCurrentBatched("<http://example/a>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Set>");
        store.AddCurrentBatched("<http://example/a>", "<http://example/member>", "\"1\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        store.AddCurrentBatched("<http://example/b>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Set>");
        store.AddCurrentBatched("<http://example/b>", "<http://example/member>", "\"9\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        store.AddCurrentBatched("<http://example/c>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Set>");
        store.AddCurrentBatched("<http://example/c>", "<http://example/member>", "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        store.AddCurrentBatched("<http://example/d>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Set>");
        store.AddCurrentBatched("<http://example/d>", "<http://example/member>", "\"9\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        store.AddCurrentBatched("<http://example/e>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Set>");
        store.AddCurrentBatched("<http://example/e>", "<http://example/member>", "\"3\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        store.CommitBatch();

        // Query using 'a' keyword and prefixed names
        var query = @"PREFIX : <http://example/>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
SELECT ?set
WHERE {
    ?set a :Set .
    FILTER EXISTS { ?set :member 9 }
}";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var sets = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?set".AsSpan());
                Assert.True(idx >= 0);
                sets.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            // Should return :b and :d (the sets with member 9)
            Assert.Equal(2, sets.Count);
            Assert.Contains("<http://example/b>", sets);
            Assert.Contains("<http://example/d>", sets);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

}
