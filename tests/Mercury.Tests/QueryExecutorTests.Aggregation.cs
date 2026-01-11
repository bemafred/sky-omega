using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests;

public partial class QueryExecutorTests
{
    /// <summary>
    /// Test implicit aggregation: COUNT without GROUP BY.
    /// All results should be treated as a single group.
    /// </summary>
    [Fact]
    public void Execute_ImplicitAggregation_CountWithoutGroupBy()
    {
        // COUNT all triples - should return single row with count
        var query = "SELECT (COUNT(?o) AS ?count) WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify aggregate was parsed but no GROUP BY
        Assert.True(parsedQuery.SelectClause.HasAggregates);
        Assert.Equal(1, parsedQuery.SelectClause.AggregateCount);
        Assert.False(parsedQuery.SolutionModifier.GroupBy.HasGroupBy);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            // Should get exactly one row
            Assert.True(results.MoveNext());
            var countIdx = results.Current.FindBinding("?count".AsSpan());
            Assert.True(countIdx >= 0);

            var count = results.Current.GetString(countIdx).ToString();
            // Fixture has 7 triples (Alice: 3, Bob: 2, Charlie: 2)
            Assert.Equal("7", count);

            // Should not have a second row
            Assert.False(results.MoveNext());
            results.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    /// <summary>
    /// Test implicit aggregation with COUNT(*).
    /// </summary>
    [Fact]
    public void Execute_ImplicitAggregation_CountStar()
    {
        // COUNT(*) all triples - should return single row with count
        var query = "SELECT (COUNT(*) AS ?count) WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify aggregate was parsed
        Assert.True(parsedQuery.SelectClause.HasAggregates);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            // Should get exactly one row
            Assert.True(results.MoveNext());
            var countIdx = results.Current.FindBinding("?count".AsSpan());
            Assert.True(countIdx >= 0);

            var count = results.Current.GetString(countIdx).ToString();
            Assert.Equal("7", count);

            // Should not have a second row
            Assert.False(results.MoveNext());
            results.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByWithSum()
    {
        // Sum of ages (all people)
        var query = "SELECT (SUM(?age) AS ?totalAge) WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } GROUP BY ?unused";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByWithMinMax()
    {
        // Min and max ages
        var query = "SELECT (MIN(?age) AS ?minAge) (MAX(?age) AS ?maxAge) WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } GROUP BY ?unused";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByWithAvg()
    {
        // Average age
        var query = "SELECT (AVG(?age) AS ?avgAge) WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age } GROUP BY ?unused";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GroupByMultipleVariables()
    {
        // This test uses the predicate as a grouping variable
        var query = "SELECT ?p (COUNT(?o) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?p";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_HavingWithSum()
    {
        // Filter by sum - count predicate groups with count >= 3
        var query = "SELECT ?p (COUNT(?o) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?p HAVING(?count >= 3)";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_HavingExcludesAll()
    {
        // HAVING that excludes all groups
        var query = "SELECT ?person (COUNT(?p) AS ?count) WHERE { ?person ?p ?o } GROUP BY ?person HAVING(?count > 100)";
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

            // No one has more than 100 triples
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_HavingWithEquality()
    {
        // HAVING with equality check
        var query = "SELECT ?person (COUNT(?p) AS ?count) WHERE { ?person ?p ?o } GROUP BY ?person HAVING(?count = 2)";
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

}
