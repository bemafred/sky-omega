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

    [Fact]
    public void Execute_NotExistsWithMultipleJoinedPatterns_TemporalProximity()
    {
        // W3C temporal-proximity-by-exclusion-nex-1 test
        // Find the closest pre-operative physical examination (no other exam between it and operation)

        var ex = "http://www.w3.org/2009/sparql/docs/tests/data-sparql11/negation#";
        var dc = "http://purl.org/dc/elements/1.1/";
        var xsd = "http://www.w3.org/2001/XMLSchema#";
        var rdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        // examination1: date 2010-01-10, precedes operation1, follows examination2
        Store.AddCurrent($"<{ex}examination1>", $"<{rdfType}>", $"<{ex}PhysicalExamination>");
        Store.AddCurrent($"<{ex}examination1>", $"<{dc}date>", $"\"2010-01-10\"^^<{xsd}date>");
        Store.AddCurrent($"<{ex}examination1>", $"<{ex}precedes>", $"<{ex}operation1>");
        Store.AddCurrent($"<{ex}examination1>", $"<{ex}follows>", $"<{ex}examination2>");

        // operation1: date 2010-01-15, follows examination1 and examination2
        Store.AddCurrent($"<{ex}operation1>", $"<{rdfType}>", $"<{ex}SurgicalProcedure>");
        Store.AddCurrent($"<{ex}operation1>", $"<{dc}date>", $"\"2010-01-15\"^^<{xsd}date>");
        Store.AddCurrent($"<{ex}operation1>", $"<{ex}follows>", $"<{ex}examination1>");
        Store.AddCurrent($"<{ex}operation1>", $"<{ex}follows>", $"<{ex}examination2>");

        // examination2: date 2010-01-02, precedes operation1 and examination1
        Store.AddCurrent($"<{ex}examination2>", $"<{rdfType}>", $"<{ex}PhysicalExamination>");
        Store.AddCurrent($"<{ex}examination2>", $"<{dc}date>", $"\"2010-01-02\"^^<{xsd}date>");
        Store.AddCurrent($"<{ex}examination2>", $"<{ex}precedes>", $"<{ex}operation1>");
        Store.AddCurrent($"<{ex}examination2>", $"<{ex}precedes>", $"<{ex}examination1>");

        var query = @"PREFIX ex: <http://www.w3.org/2009/sparql/docs/tests/data-sparql11/negation#>
PREFIX dc: <http://purl.org/dc/elements/1.1/>
SELECT ?exam ?date {
  ?exam a ex:PhysicalExamination;
        dc:date ?date;
        ex:precedes ex:operation1 .
  ?op a ex:SurgicalProcedure; dc:date ?opDT .
  FILTER NOT EXISTS {
    ?otherExam a ex:PhysicalExamination;
               ex:follows ?exam;
               ex:precedes ex:operation1
  }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify NOT EXISTS was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasExists);
        var existsFilter = parsedQuery.WhereClause.Pattern.GetExistsFilter(0);
        Assert.True(existsFilter.Negated);
        Assert.Equal(3, existsFilter.PatternCount); // Should have 3 patterns in NOT EXISTS

        Store.AcquireReadLock();
        try
        {
            // Use ExecuteToMaterialized() to avoid stack overflow from ~22KB QueryResults struct
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteToMaterialized();

            var solutions = new List<(string exam, string date)>();
            while (results.MoveNext())
            {
                var examIdx = results.Current.FindBinding("?exam".AsSpan());
                var dateIdx = results.Current.FindBinding("?date".AsSpan());
                Assert.True(examIdx >= 0);
                Assert.True(dateIdx >= 0);
                solutions.Add((results.Current.GetString(examIdx).ToString(), results.Current.GetString(dateIdx).ToString()));
            }
            results.Dispose();

            // Debug: First run without NOT EXISTS to verify outer patterns match
            var debugQuery = @"PREFIX ex: <http://www.w3.org/2009/sparql/docs/tests/data-sparql11/negation#>
PREFIX dc: <http://purl.org/dc/elements/1.1/>
SELECT ?exam ?date {
  ?exam a ex:PhysicalExamination;
        dc:date ?date;
        ex:precedes ex:operation1 .
  ?op a ex:SurgicalProcedure; dc:date ?opDT .
}";
            var debugParser = new SparqlParser(debugQuery.AsSpan());
            var debugParsedQuery = debugParser.ParseQuery();
            var debugExecutor = new QueryExecutor(Store, debugQuery.AsSpan(), debugParsedQuery);
            var debugResults = debugExecutor.ExecuteToMaterialized();
            var debugSolutions = new List<string>();
            while (debugResults.MoveNext())
            {
                var examIdx = debugResults.Current.FindBinding("?exam".AsSpan());
                debugSolutions.Add(debugResults.Current.GetString(examIdx).ToString());
            }
            debugResults.Dispose();

            // Should have 2 results without the FILTER NOT EXISTS
            Assert.Equal(2, debugSolutions.Count);

            // Logic:
            // - examination1 precedes operation1 ✓
            // - examination2 precedes operation1 ✓
            // - For examination1: NOT EXISTS { ?otherExam follows examination1 AND precedes operation1 } = TRUE
            //   (nothing follows examination1 that also precedes operation1)
            // - For examination2: NOT EXISTS { ?otherExam follows examination2 AND precedes operation1 }
            //   examination1 follows examination2 ✓ AND examination1 precedes operation1 ✓
            //   So EXISTS succeeds -> NOT EXISTS fails -> examination2 is excluded
            // Expected: only examination1

            Assert.Single(solutions);
            Assert.Contains(solutions, s => s.exam.Contains("examination1") && s.date.Contains("2010-01-10"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithNotExists_SetEquals1()
    {
        // W3C set-equals-1 test: Calculate which sets have the same elements
        // Uses two MINUS blocks, each with FILTER NOT EXISTS

        var ex = "http://example/";
        var rdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        // Set a: members 1, 2, 3
        Store.AddCurrent($"<{ex}a>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"2\"");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"3\"");

        // Set c: members 1, 2 (same as e)
        Store.AddCurrent($"<{ex}c>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}c>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}c>", $"<{ex}member>", "\"2\"");

        // Set e: members 1, 2 (same as c)
        Store.AddCurrent($"<{ex}e>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}e>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}e>", $"<{ex}member>", "\"2\"");

        var query = @"PREFIX :    <http://example/>
PREFIX  rdf:    <http://www.w3.org/1999/02/22-rdf-syntax-ns#>

SELECT DISTINCT ?s1 ?s2
WHERE
{
    ?s2 rdf:type :Set .
    ?s1 rdf:type :Set .
    FILTER(str(?s1) < str(?s2))
    MINUS
    {
        ?s1 rdf:type :Set .
        ?s2 rdf:type :Set .
        ?s1 :member ?x .
        FILTER NOT EXISTS { ?s2 :member ?x . }
    }
    MINUS
    {
        ?s1 rdf:type :Set .
        ?s2 rdf:type :Set .
        ?s2 :member ?x .
        FILTER NOT EXISTS { ?s1 :member ?x . }
    }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify MINUS with NOT EXISTS was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasMinus);
        var hasME = parsedQuery.WhereClause.Pattern.HasMinusExists;
        var meCount = parsedQuery.WhereClause.Pattern.MinusExistsCount;

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var solutions = new List<(string s1, string s2)>();
            while (results.MoveNext())
            {
                var s1Idx = results.Current.FindBinding("?s1".AsSpan());
                var s2Idx = results.Current.FindBinding("?s2".AsSpan());
                Assert.True(s1Idx >= 0);
                Assert.True(s2Idx >= 0);
                solutions.Add((results.Current.GetString(s1Idx).ToString(), results.Current.GetString(s2Idx).ToString()));
            }
            results.Dispose();

            // Expected: c=e and e=c, but filtered by str(?s1) < str(?s2) so only (c, e)
            var output = $"HasMinusExists: {hasME}, MinusExistsCount: {meCount}\n";
            output += $"Solutions ({solutions.Count}):\n";
            foreach (var (s1, s2) in solutions.OrderBy(s => s.s1).ThenBy(s => s.s2))
            {
                output += $"  {s1} = {s2}\n";
            }

            // Per W3C: c and e have same members
            Assert.True(solutions.Count >= 1, output);
            Assert.Contains(solutions, s => s.s1.Contains("c") && s.s2.Contains("e"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithNotExists_Subset01()
    {
        // W3C subset-01 test: Calculate subsets (include A subsetOf A)
        // Uses MINUS with FILTER NOT EXISTS inside

        var ex = "http://example/";
        var rdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        // Set a: members 1, 2, 3
        Store.AddCurrent($"<{ex}a>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"2\"");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"3\"");

        // Set b: members 1, 9
        Store.AddCurrent($"<{ex}b>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", "\"9\"");

        // Set c: members 1, 2
        Store.AddCurrent($"<{ex}c>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}c>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}c>", $"<{ex}member>", "\"2\"");

        // Set d: members 1, 9
        Store.AddCurrent($"<{ex}d>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}d>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}d>", $"<{ex}member>", "\"9\"");

        // Set e: members 1, 2
        Store.AddCurrent($"<{ex}e>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}e>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}e>", $"<{ex}member>", "\"2\"");

        // Empty set
        Store.AddCurrent($"<{ex}empty>", $"<{rdfType}>", $"<{ex}Set>");

        var query = @"PREFIX :    <http://example/>
PREFIX  rdf:    <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
SELECT (?s1 AS ?subset) (?s2 AS ?superset)
WHERE
{
    ?s2 rdf:type :Set .
    ?s1 rdf:type :Set .
    FILTER(?s1 != ?s2)
    MINUS
    {
        ?s1 rdf:type :Set .
        ?s2 rdf:type :Set .
        FILTER(?s1 != ?s2)

        ?s1 :member ?x .
        FILTER NOT EXISTS { ?s2 :member ?x . }
    }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify MINUS pattern was parsed with NOT EXISTS inside
        Assert.True(parsedQuery.WhereClause.Pattern.HasMinus);

        // Debug: Check if MINUS has EXISTS filter (stored on GraphPattern, not TriplePattern)
        var hasMinusExists = parsedQuery.WhereClause.Pattern.HasMinusExists;
        var minusExistsCount = parsedQuery.WhereClause.Pattern.MinusExistsCount;

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var solutions = new List<(string subset, string superset)>();
            while (results.MoveNext())
            {
                var subsetIdx = results.Current.FindBinding("?subset".AsSpan());
                var supersetIdx = results.Current.FindBinding("?superset".AsSpan());
                Assert.True(subsetIdx >= 0);
                Assert.True(supersetIdx >= 0);
                solutions.Add((results.Current.GetString(subsetIdx).ToString(), results.Current.GetString(supersetIdx).ToString()));
            }
            results.Dispose();

            // Expected results: proper subsets where all members of s1 are in s2
            // empty subsetOf everyone (a, b, c, d, e)
            // c subsetOf a (1,2 are in 1,2,3)
            // c subsetOf e (1,2 are in 1,2)
            // e subsetOf a (1,2 are in 1,2,3)
            // e subsetOf c (1,2 are in 1,2)
            // b subsetOf d (1,9 are in 1,9) - wait, this IS b = d
            // d subsetOf b (1,9 are in 1,9)

            // Debug output
            var output = $"HasMinusExists: {hasMinusExists}, MinusExistsCount: {minusExistsCount}\n";
            output += $"Solutions ({solutions.Count}):\n";
            foreach (var (subset, superset) in solutions.OrderBy(s => s.subset).ThenBy(s => s.superset))
            {
                output += $"  {subset} subsetOf {superset}\n";
            }

            // Per W3C expected results:
            // (:empty, :a), (:empty, :b), (:empty, :c), (:empty, :d), (:empty, :e)
            // (:c, :a), (:c, :e)
            // (:e, :a), (:e, :c)
            // (:b, :d), (:d, :b) - b and d have same members so subset of each other
            Assert.True(solutions.Count >= 10, output);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Parse_CompoundExistsRef_InMinusFilter()
    {
        // Test that compound EXISTS patterns are parsed correctly
        // Simulates subset-02's FILTER ( ?s1 = ?s2 || NOT EXISTS { ... } )

        var query = @"PREFIX :    <http://example/>
PREFIX  rdf:    <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
SELECT ?s1 ?s2
WHERE
{
    ?s2 rdf:type :Set .
    ?s1 rdf:type :Set .
    MINUS {
        ?s1 rdf:type :Set .
        ?s2 rdf:type :Set .
        ?s1 :member ?x .
        FILTER ( ?s1 = ?s2 || NOT EXISTS { ?s2 :member ?x . } )
    }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        var gp = parsedQuery.WhereClause.Pattern;

        // Debug: Print the filter expression and compound EXISTS ref positions
        var filterExprStr = query.Substring(gp.MinusFilter.Start, gp.MinusFilter.Length);
        var existsRefDebug = gp.GetCompoundExistsRef(0);

        // Build debug message
        var debugMsg = $"Filter expression: [{filterExprStr}]\n";
        debugMsg += $"CompoundExistsRef: start={existsRefDebug.StartInFilter}, len={existsRefDebug.Length}, negated={existsRefDebug.Negated}\n";
        if (existsRefDebug.StartInFilter >= 0 && existsRefDebug.StartInFilter + existsRefDebug.Length <= filterExprStr.Length)
        {
            var existsSubstr = filterExprStr.Substring(existsRefDebug.StartInFilter, existsRefDebug.Length);
            debugMsg += $"Portion to replace: [{existsSubstr}]\n";
        }

        // Check if the positions are valid before substring
        Assert.True(existsRefDebug.StartInFilter >= 0 && existsRefDebug.StartInFilter < filterExprStr.Length,
            $"Invalid StartInFilter: {existsRefDebug.StartInFilter}, filterExpr length: {filterExprStr.Length}\n{debugMsg}");
        Assert.True(existsRefDebug.StartInFilter + existsRefDebug.Length <= filterExprStr.Length,
            $"Invalid range: start={existsRefDebug.StartInFilter}, len={existsRefDebug.Length}, filterLen={filterExprStr.Length}\n{debugMsg}");

        // Should have 1 MINUS block
        Assert.Equal(1, gp.MinusBlockCount);

        // Should have the MINUS filter
        Assert.True(gp.HasMinusFilter);
        Assert.Equal(0, gp.MinusFilterBlock);  // Filter belongs to block 0

        // Should have 1 compound EXISTS ref (for the NOT EXISTS inside the filter)
        Assert.True(gp.HasCompoundExistsRefs);
        Assert.Equal(1, gp.CompoundExistsRefCount);

        var existsRef = gp.GetCompoundExistsRef(0);
        Assert.True(existsRef.Negated);  // It's NOT EXISTS
        Assert.Equal(0, existsRef.BlockIndex);  // Belongs to block 0

        // Should have 1 MinusExistsFilter (the pattern inside NOT EXISTS)
        Assert.True(gp.HasMinusExists);
        Assert.Equal(1, gp.MinusExistsCount);
        var existsFilter = gp.GetMinusExistsFilter(0);
        Assert.True(existsFilter.Negated);
        Assert.Equal(1, existsFilter.PatternCount);  // { ?s2 :member ?x }

        // Verify the substitution would work correctly
        // Create modified expression with "false" substituted for NOT EXISTS
        var sb = new System.Text.StringBuilder(filterExprStr);
        sb.Remove(existsRefDebug.StartInFilter, existsRefDebug.Length);
        sb.Insert(existsRefDebug.StartInFilter, "false");
        var modifiedExpr = sb.ToString();

        // Debug: show what the modified expression would look like
        Assert.True(modifiedExpr.Contains("false"),
            $"Modified expression should contain 'false': [{modifiedExpr}]\nOriginal: [{filterExprStr}]");

        // Verify the modified expression looks correct
        Assert.True(modifiedExpr.Contains("?s1 = ?s2") || modifiedExpr.Contains("?s1=?s2"),
            $"Modified expression should still contain ?s1 = ?s2: [{modifiedExpr}]");
    }

    [Fact]
    public void Execute_CompoundFilter_ShouldExcludePairs()
    {
        // Test subset-02 first MINUS block in isolation
        // FILTER ( ?s1 = ?s2 || NOT EXISTS { ?s2 :member ?x . } )
        // For (a, a) where a has members, ?s1 = ?s2 is true, so filter should be true
        // and the MINUS should exclude (a, a)

        var ex = "http://example/";
        var rdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        // Set a: members 1, 2
        Store.AddCurrent($"<{ex}a>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", $"<{ex}one>");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", $"<{ex}two>");

        // Set b: members 1, 3 (has 1 but not 2)
        Store.AddCurrent($"<{ex}b>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", $"<{ex}one>");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", $"<{ex}three>");

        // Query: Get all pairs, minus those where ?s1 = ?s2 OR ?s2 doesn't have ?s1's element
        var query = @"PREFIX :    <http://example/>
PREFIX  rdf:    <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
SELECT ?s1 ?s2
WHERE
{
    ?s2 rdf:type :Set .
    ?s1 rdf:type :Set .
    MINUS {
        ?s1 rdf:type :Set .
        ?s2 rdf:type :Set .
        ?s1 :member ?x .
        FILTER ( ?s1 = ?s2 || NOT EXISTS { ?s2 :member ?x . } )
    }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var solutions = new List<(string s1, string s2)>();
            while (results.MoveNext())
            {
                var s1Idx = results.Current.FindBinding("?s1".AsSpan());
                var s2Idx = results.Current.FindBinding("?s2".AsSpan());
                Assert.True(s1Idx >= 0);
                Assert.True(s2Idx >= 0);
                solutions.Add((results.Current.GetString(s1Idx).ToString(), results.Current.GetString(s2Idx).ToString()));
            }
            results.Dispose();

            // Expected:
            // (a, a) should be excluded - ?s1 = ?s2 is true
            // (b, b) should be excluded - ?s1 = ?s2 is true
            // (a, b) should be excluded - b doesn't have :two (a's member)
            // (b, a) should be excluded - a doesn't have :three (b's member)
            // So all pairs should be excluded!

            var output = $"Solutions ({solutions.Count}):\n";
            foreach (var (s1, s2) in solutions.OrderBy(s => s.s1).ThenBy(s => s.s2))
            {
                output += $"  {s1} -> {s2}\n";
            }

            // Expected:
            // (a, a) should be excluded - ?s1 = ?s2 is true
            // (b, b) should be excluded - ?s1 = ?s2 is true
            // (a, b) should be excluded - b doesn't have :two (a's member)
            // (b, a) should be excluded - a doesn't have :three (b's member)

            Assert.True(solutions.Count == 0, output);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SimpleNotExistsInMinusBlock_ExcludesCorrectly()
    {
        // Simpler test: MINUS with just NOT EXISTS (no compound expression)
        var ex = "http://example/";
        var rdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        // Set a: members 1, 2
        Store.AddCurrent($"<{ex}a>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", $"<{ex}one>");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", $"<{ex}two>");

        // Set b: members 1, 3 (different from a)
        Store.AddCurrent($"<{ex}b>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", $"<{ex}one>");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", $"<{ex}three>");

        // Query: pairs where s2 contains ALL of s1's members (subset logic without ?s1=?s2 exclusion)
        var query = @"PREFIX :    <http://example/>
PREFIX  rdf:    <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
SELECT ?s1 ?s2
WHERE
{
    ?s2 rdf:type :Set .
    ?s1 rdf:type :Set .
    FILTER(?s1 != ?s2)
    MINUS {
        ?s1 rdf:type :Set .
        ?s2 rdf:type :Set .
        FILTER(?s1 != ?s2)
        ?s1 :member ?x .
        FILTER NOT EXISTS { ?s2 :member ?x . }
    }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var solutions = new List<(string s1, string s2)>();
            while (results.MoveNext())
            {
                var s1Idx = results.Current.FindBinding("?s1".AsSpan());
                var s2Idx = results.Current.FindBinding("?s2".AsSpan());
                Assert.True(s1Idx >= 0);
                Assert.True(s2Idx >= 0);
                solutions.Add((results.Current.GetString(s1Idx).ToString(), results.Current.GetString(s2Idx).ToString()));
            }
            results.Dispose();

            var output = $"Solutions ({solutions.Count}):\n";
            foreach (var (s1, s2) in solutions.OrderBy(s => s.s1).ThenBy(s => s.s2))
            {
                output += $"  {s1} -> {s2}\n";
            }

            // Expected: all pairs where s2 contains all of s1's members (excluding s1=s2)
            // - (a, b): b has 1, but a has 1,2 and b doesn't have 2 → a NOT subsetOf b → excluded by MINUS
            // - (b, a): a has 1, but b has 1,3 and a doesn't have 3 → b NOT subsetOf a → excluded by MINUS
            // So NO pairs should remain!
            Assert.True(solutions.Count == 0, output);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_NestedMinus_Simple()
    {
        // Simpler nested MINUS test to verify basic functionality
        // Query: SELECT ?x WHERE { ?x :p ?y . MINUS { ?x :p ?y . MINUS { ?x :q ?z } } }
        // This should return subjects that have :p but NOT :q

        var ex = "http://example/";

        // Subject a has :p 1, :q 2 (has both p and q)
        Store.AddCurrent($"<{ex}a>", $"<{ex}p>", "\"1\"");
        Store.AddCurrent($"<{ex}a>", $"<{ex}q>", "\"2\"");

        // Subject b has only :p 1 (no :q)
        Store.AddCurrent($"<{ex}b>", $"<{ex}p>", "\"1\"");

        // Subject c has only :q 3 (no :p)
        Store.AddCurrent($"<{ex}c>", $"<{ex}q>", "\"3\"");

        var query = @"PREFIX : <http://example/>
SELECT ?x
WHERE
{
    ?x :p ?y .
    MINUS {
        ?x :p ?y .
        MINUS { ?x :q ?z }
    }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var gp = parsedQuery.WhereClause.Pattern;
        var output = $"PatternCount: {gp.PatternCount}\n";
        output += $"HasMinus: {gp.HasMinus}\n";
        output += $"HasNestedMinus: {gp.HasNestedMinus}\n";
        output += $"NestedMinusCount: {gp.NestedMinusCount}\n";
        output += $"NestedMinusPatternCount: {gp.NestedMinusPatternCount}\n";
        output += $"MinusPatternCount: {gp.MinusPatternCount}\n";
        output += $"MinusBlockCount: {gp.MinusBlockCount}\n";
        // Show main patterns
        for (int i = 0; i < gp.PatternCount; i++)
        {
            var p = gp.GetPattern(i);
            output += $"MainPattern[{i}]: s={query.Substring(p.Subject.Start, p.Subject.Length)}, p={query.Substring(p.Predicate.Start, p.Predicate.Length)}, o={query.Substring(p.Object.Start, p.Object.Length)}\n";
        }
        // Show MINUS patterns
        for (int i = 0; i < gp.MinusPatternCount; i++)
        {
            var p = gp.GetMinusPattern(i);
            output += $"MinusPattern[{i}]: s={query.Substring(p.Subject.Start, p.Subject.Length)}, p={query.Substring(p.Predicate.Start, p.Predicate.Length)}, o={query.Substring(p.Object.Start, p.Object.Length)}\n";
        }
        // Show nested MINUS block info
        for (int i = 0; i < gp.NestedMinusCount; i++)
        {
            output += $"NestedMinus[{i}]: parent={gp.GetNestedMinusParentBlock(i)}, start={gp.GetNestedMinusBlockStart(i)}, end={gp.GetNestedMinusBlockEnd(i)}\n";
            // Show the patterns in this nested MINUS block
            for (int j = gp.GetNestedMinusBlockStart(i); j < gp.GetNestedMinusBlockEnd(i); j++)
            {
                var pattern = gp.GetNestedMinusPattern(j);
                output += $"  Pattern[{j}]: s={query.Substring(pattern.Subject.Start, pattern.Subject.Length)}, p={query.Substring(pattern.Predicate.Start, pattern.Predicate.Length)}, o={query.Substring(pattern.Object.Start, pattern.Object.Length)}\n";
            }
        }
        // Show outer MINUS block info
        for (int i = 0; i < gp.MinusBlockCount; i++)
        {
            output += $"MinusBlock[{i}]: start={gp.GetMinusBlockStart(i)}, end={gp.GetMinusBlockEnd(i)}\n";
        }

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var solutions = new List<string>();
            while (results.MoveNext())
            {
                var xIdx = results.Current.FindBinding("?x".AsSpan());
                Assert.True(xIdx >= 0);
                solutions.Add(results.Current.GetString(xIdx).ToString());
            }
            results.Dispose();

            output += $"Solutions ({solutions.Count}): {string.Join(", ", solutions)}\n";

            // Expected: only 'b' should be returned
            // - 'a' has :p AND :q, so the inner MINUS { ?x :q ?z } matches
            //   which means the outer MINUS { ... MINUS { ?x :q ?z } } doesn't match
            //   so 'a' is NOT excluded → 'a' should survive?
            // Wait, let me think again...
            //
            // Outer MINUS content: { ?x :p ?y . MINUS { ?x :q ?z } }
            // For 'a': ?x :p ?y matches (a, p, 1)
            //          MINUS { ?x :q ?z } → a has :q 2, so this matches and removes 'a' from content
            //          Content is EMPTY for 'a'
            //          Outer MINUS doesn't match → 'a' survives
            // For 'b': ?x :p ?y matches (b, p, 1)
            //          MINUS { ?x :q ?z } → b has no :q, so this doesn't match
            //          Content is {b} (one solution for 'b')
            //          Outer MINUS matches → 'b' is excluded
            //
            // So expected result is: only 'a' (b is excluded, c has no :p)

            Assert.True(solutions.Count == 1, output);
            Assert.Contains(solutions, s => s.Contains("/a"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_NestedMinus_Subset03()
    {
        // W3C subset-03 test: Calculate proper subsets (exclude equal sets)
        // Uses nested MINUS inside MINUS to find pairs with same elements

        var ex = "http://example/";
        var rdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        // Set a: members 1, 2, 3
        Store.AddCurrent($"<{ex}a>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"2\"");
        Store.AddCurrent($"<{ex}a>", $"<{ex}member>", "\"3\"");

        // Set b: members 1, 9
        Store.AddCurrent($"<{ex}b>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}b>", $"<{ex}member>", "\"9\"");

        // Set c: members 1, 2
        Store.AddCurrent($"<{ex}c>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}c>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}c>", $"<{ex}member>", "\"2\"");

        // Set d: members 1, 9 (same as b)
        Store.AddCurrent($"<{ex}d>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}d>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}d>", $"<{ex}member>", "\"9\"");

        // Set e: members 1, 2 (same as c)
        Store.AddCurrent($"<{ex}e>", $"<{rdfType}>", $"<{ex}Set>");
        Store.AddCurrent($"<{ex}e>", $"<{ex}member>", "\"1\"");
        Store.AddCurrent($"<{ex}e>", $"<{ex}member>", "\"2\"");

        // Empty set
        Store.AddCurrent($"<{ex}empty>", $"<{rdfType}>", $"<{ex}Set>");

        // W3C subset-03 query: proper subsets (exclude equal sets)
        var query = @"PREFIX :    <http://example/>
PREFIX  rdf:    <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
SELECT (?s1 AS ?subset) (?s2 AS ?superset)
WHERE
{
    ?s2 rdf:type :Set .
    ?s1 rdf:type :Set .
    MINUS {
        ?s1 rdf:type :Set .
        ?s2 rdf:type :Set .
        ?s1 :member ?x .
        FILTER ( NOT EXISTS { ?s2 :member ?x . } )
    }
    MINUS {
        ?s2 rdf:type :Set .
        ?s1 rdf:type :Set .
        MINUS
        {
            ?s1 rdf:type :Set .
            ?s2 rdf:type :Set .
            ?s1 :member ?x .
            FILTER NOT EXISTS { ?s2 :member ?x . }
        }
        MINUS
        {
            ?s1 rdf:type :Set .
            ?s2 rdf:type :Set .
            ?s2 :member ?x .
            FILTER NOT EXISTS { ?s1 :member ?x . }
        }
    }
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify nested MINUS was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasMinus, "Should have MINUS");
        Assert.True(parsedQuery.WhereClause.Pattern.HasNestedMinus, "Should have nested MINUS");

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var solutions = new List<(string subset, string superset)>();
            while (results.MoveNext())
            {
                var subsetIdx = results.Current.FindBinding("?subset".AsSpan());
                var supersetIdx = results.Current.FindBinding("?superset".AsSpan());
                Assert.True(subsetIdx >= 0);
                Assert.True(supersetIdx >= 0);
                solutions.Add((results.Current.GetString(subsetIdx).ToString(), results.Current.GetString(supersetIdx).ToString()));
            }
            results.Dispose();

            // Debug output
            var output = $"NestedMinusCount: {parsedQuery.WhereClause.Pattern.NestedMinusCount}\n";
            output += $"NestedMinusPatternCount: {parsedQuery.WhereClause.Pattern.NestedMinusPatternCount}\n";
            output += $"Solutions ({solutions.Count}):\n";
            foreach (var (subset, superset) in solutions.OrderBy(s => s.subset).ThenBy(s => s.superset))
            {
                output += $"  {subset} subsetOf {superset}\n";
            }

            // Per W3C expected results (7 pairs - proper subsets, not equal sets):
            // (:empty, :a), (:empty, :b), (:empty, :c), (:empty, :d), (:empty, :e)
            // (:c, :a), (:e, :a)
            // Note: (b, d), (d, b), (c, e), (e, c) are EXCLUDED because those are equal sets
            Assert.True(solutions.Count == 7, output);

            // Verify specific expected pairs
            Assert.Contains(solutions, s => s.subset.Contains("empty") && s.superset.Contains("/a"));
            Assert.Contains(solutions, s => s.subset.Contains("empty") && s.superset.Contains("/b"));
            Assert.Contains(solutions, s => s.subset.Contains("empty") && s.superset.Contains("/c"));
            Assert.Contains(solutions, s => s.subset.Contains("empty") && s.superset.Contains("/d"));
            Assert.Contains(solutions, s => s.subset.Contains("empty") && s.superset.Contains("/e"));
            Assert.Contains(solutions, s => s.subset.Contains("/c") && s.superset.Contains("/a"));
            Assert.Contains(solutions, s => s.subset.Contains("/e") && s.superset.Contains("/a"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

}
