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
    public void ExecuteAsk_ReturnsTrue_WhenMatchExists()
    {
        // ASK if Alice exists
        var query = "ASK WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.Equal(QueryType.Ask, parsedQuery.Type);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            Assert.True(result);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_ReturnsFalse_WhenNoMatch()
    {
        // ASK for non-existent person
        var query = "ASK WHERE { <http://example.org/NonExistent> <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            Assert.False(result);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_WithMultiplePatterns()
    {
        // ASK with join - does Alice know someone?
        var query = "ASK WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows> ?other . ?other <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // Alice knows Bob, and Bob has a name
            Assert.True(result);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_WithFilter()
    {
        // ASK with FILTER - is there anyone older than 30?
        var query = "ASK WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 30) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // Charlie is 35
            Assert.True(result);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_WithFilter_NoMatch()
    {
        // ASK with FILTER that matches nothing
        var query = "ASK WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 100) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // No one is older than 100
            Assert.False(result);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteAsk_AllVariables()
    {
        // ASK if any triple exists
        var query = "ASK WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var result = executor.ExecuteAsk();

            // Store has triples
            Assert.True(result);
        }
        finally
        {
            Store.ReleaseReadLock();
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

            // Only Alice (30) and Bob (25), Charlie (35) is excluded
            Assert.Equal(2, ages.Count);
            Assert.Contains("25", ages);
            Assert.Contains("30", ages);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesSingleValue()
    {
        // VALUES with single value
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 35 } }";
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

            // Only Charlie (35)
            Assert.Single(ages);
            Assert.Equal("35", ages[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesNoMatch()
    {
        // VALUES that matches nothing
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 100 200 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesAllMatch()
    {
        // VALUES that matches all results
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 25 30 35 } }";
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

            // All 3 ages match
            Assert.Equal(3, ages.Count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesUnboundVariable()
    {
        // VALUES on a variable not in the pattern (should allow all results)
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name VALUES ?other { 1 2 3 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ValuesWithFilter()
    {
        // VALUES combined with FILTER
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age VALUES ?age { 25 30 35 } FILTER(?age > 25) }";
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

            // VALUES allows 25, 30, 35 but FILTER excludes 25
            Assert.Equal(2, ages.Count);
            Assert.Contains("30", ages);
            Assert.Contains("35", ages);
        }
        finally
        {
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_WithConstantInTemplate()
    {
        // CONSTRUCT with constant value in template
        var query = "CONSTRUCT { ?person <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> } WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_WithFilter()
    {
        // CONSTRUCT with FILTER in WHERE clause
        var query = "CONSTRUCT { ?person <http://example.org/adult> \"true\" } WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_EmptyResult()
    {
        // CONSTRUCT with no matches
        var query = "CONSTRUCT { ?person <http://example.org/type> <http://example.org/Person> } WHERE { ?person <http://example.org/nonexistent> ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ExecuteConstruct_WithJoin()
    {
        // CONSTRUCT with multiple patterns in WHERE (join)
        var query = "CONSTRUCT { ?person <http://example.org/profile> ?name } WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    // ========== GROUP BY Tests ==========
}
