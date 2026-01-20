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

    // ========== Post-Query VALUES Tests (W3C style - VALUES after WHERE clause) ==========
    [Fact]
    public void Execute_PostQueryValuesBasic()
    {
        // Post-query VALUES (after WHERE clause) - W3C style
        // This is different from inline VALUES which appears inside the WHERE clause
        var query = @"
PREFIX foaf: <http://xmlns.com/foaf/0.1/>
SELECT ?person ?age
WHERE { ?person foaf:age ?age }
VALUES ?age { 25 30 }";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify post-query VALUES was parsed (in query.Values, not in WhereClause.Pattern.Values)
        Assert.True(parsedQuery.Values.HasValues, "Post-query VALUES should be parsed");
        Assert.False(parsedQuery.WhereClause.Pattern.Values.HasValues, "Inline VALUES should not be set");

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
    public void Execute_PostQueryValuesWithPrefixedName()
    {
        // Post-query VALUES with prefixed name - requires prefix expansion
        var query = @"
PREFIX foaf: <http://xmlns.com/foaf/0.1/>
PREFIX ex: <http://example.org/>
SELECT ?person ?name
WHERE { ?person foaf:name ?name }
VALUES ?person { ex:Alice }";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.Values.HasValues, "Post-query VALUES should be parsed");
        Assert.Equal(1, parsedQuery.Values.ValueCount);

        // Debug: Check what the VALUES variable and value are
        var valVar = query.Substring(parsedQuery.Values.VarStart, parsedQuery.Values.VarLength);
        Assert.Equal("?person", valVar);

        var (valStart, valLen) = parsedQuery.Values.GetValue(0);
        var valValue = query.Substring(valStart, valLen);
        Assert.Equal("ex:Alice", valValue);

        // Debug: Check prefix mappings
        Assert.True(parsedQuery.Prologue.PrefixCount >= 2, $"Expected at least 2 prefixes, got {parsedQuery.Prologue.PrefixCount}");

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            // First, let's see what results we get without VALUES filtering
            var allResults = new List<(string person, string name)>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                allResults.Add((
                    personIdx >= 0 ? results.Current.GetString(personIdx).ToString() : "N/A",
                    nameIdx >= 0 ? results.Current.GetString(nameIdx).ToString() : "N/A"
                ));
            }
            results.Dispose();

            // Debug output
            Assert.True(allResults.Count > 0, $"Expected at least 1 result, but got 0. Without VALUES, there should be 3 results (Alice, Bob, Charlie).");

            // Only Alice should match
            Assert.Single(allResults);
            Assert.Contains(("<http://example.org/Alice>", "\"Alice\""), allResults);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_PostQueryValuesWithStringLiterals()
    {
        // Similar to W3C values03: Post-query VALUES with string literals
        // The issue: bound values from store have quotes, VALUES values must match

        // Use a simple filtered query on existing data
        // We know Alice exists with name "Alice"
        var query = @"
PREFIX foaf: <http://xmlns.com/foaf/0.1/>
SELECT ?person ?name
WHERE {
  ?person foaf:name ?name .
} VALUES ?name {
 ""Alice""
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.Values.HasValues, "Post-query VALUES should be parsed");
        Assert.Equal(1, parsedQuery.Values.VariableCount);
        Assert.Equal(1, parsedQuery.Values.ValueCount);

        // Debug: Check parsed VALUES
        var (v0Start, v0Len) = parsedQuery.Values.GetValue(0);
        var val0 = query.Substring(v0Start, v0Len);

        // The value should be "Alice" (with quotes)
        Assert.Equal("\"Alice\"", val0);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var rows = new List<(string person, string name)>();
            while (results.MoveNext())
            {
                var pIdx = results.Current.FindBinding("?person".AsSpan());
                var nIdx = results.Current.FindBinding("?name".AsSpan());
                rows.Add((
                    pIdx >= 0 ? results.Current.GetString(pIdx).ToString() : "N/A",
                    nIdx >= 0 ? results.Current.GetString(nIdx).ToString() : "N/A"
                ));
            }
            results.Dispose();

            // Should have exactly 1 result: Alice
            Assert.True(rows.Count == 1, $"Expected 1 row but got {rows.Count}. Parsed value='{val0}'");
            var row = rows[0];
            Assert.Equal("<http://example.org/Alice>", row.person);
            Assert.Equal("\"Alice\"", row.name);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_PostQueryValuesWithMultipleStringLiterals()
    {
        // Test multi-variable VALUES with string literals (like values03)

        // Add test data
        Store.AddCurrent(
            "<http://example.org/test>",
            "<http://xmlns.com/foaf/0.1/name>",
            "\"Alan\"");
        Store.AddCurrent(
            "<http://example.org/test>",
            "<http://xmlns.com/foaf/0.1/mbox>",
            "\"alan@example.org\"");

        var query = @"
PREFIX foaf: <http://xmlns.com/foaf/0.1/>
SELECT ?s ?o1 ?o2
WHERE {
  ?s foaf:name ?o1 .
  ?s foaf:mbox ?o2 .
} VALUES (?o1 ?o2) {
 (""Alan"" ""alan@example.org"")
}";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.Values.HasValues, "Post-query VALUES should be parsed");
        Assert.Equal(2, parsedQuery.Values.VariableCount);
        Assert.Equal(2, parsedQuery.Values.ValueCount);

        // Debug: Check parsed VALUES
        var (v0Start, v0Len) = parsedQuery.Values.GetValue(0);
        var (v1Start, v1Len) = parsedQuery.Values.GetValue(1);
        var val0 = query.Substring(v0Start, v0Len);
        var val1 = query.Substring(v1Start, v1Len);

        Assert.Equal("\"Alan\"", val0);
        Assert.Equal("\"alan@example.org\"", val1);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var rows = new List<(string s, string o1, string o2)>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var o1Idx = results.Current.FindBinding("?o1".AsSpan());
                var o2Idx = results.Current.FindBinding("?o2".AsSpan());
                rows.Add((
                    sIdx >= 0 ? results.Current.GetString(sIdx).ToString() : "N/A",
                    o1Idx >= 0 ? results.Current.GetString(o1Idx).ToString() : "N/A",
                    o2Idx >= 0 ? results.Current.GetString(o2Idx).ToString() : "N/A"
                ));
            }
            results.Dispose();

            // Should have exactly 1 result
            Assert.True(rows.Count == 1, $"Expected 1 row but got {rows.Count}. val0='{val0}', val1='{val1}'");
            var row = rows[0];
            Assert.Equal("<http://example.org/test>", row.s);
            Assert.Equal("\"Alan\"", row.o1);
            Assert.Equal("\"alan@example.org\"", row.o2);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_PostQueryValues_ExactlyLikeValues03()
    {
        // Test multi-variable VALUES filtering with string literals
        // Similar to values03.rq but with fixed predicates to avoid stack overflow
        // (The W3C conformance test suite handles this separately)

        // Add data that matches data03.ttl structure
        // :a foaf:name "Alan" .
        // :a foaf:mbox "alan@example.org" .
        Store.AddCurrent(
            "<http://example.org/a>",
            "<http://xmlns.com/foaf/0.1/name>",
            "\"Alan\"");
        Store.AddCurrent(
            "<http://example.org/a>",
            "<http://xmlns.com/foaf/0.1/mbox>",
            "\"alan@example.org\"");

        // Query with fixed predicates (avoids stack overflow from all-variable join)
        // This tests the VALUES filtering logic specifically
        var query = @"
PREFIX foaf: <http://xmlns.com/foaf/0.1/>

SELECT ?s ?o1 ?o2
WHERE {
  ?s foaf:name ?o1 .
  ?s foaf:mbox ?o2 .
} VALUES (?o1 ?o2) {
 (""Alan"" ""alan@example.org"")
}
";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify the parsed VALUES structure
        Assert.True(parsedQuery.Values.HasValues, "Post-query VALUES should be parsed");
        Assert.Equal(2, parsedQuery.Values.VariableCount);
        Assert.Equal(2, parsedQuery.Values.ValueCount);

        // Check variables
        var (var0Start, var0Len) = parsedQuery.Values.GetVariable(0);
        var (var1Start, var1Len) = parsedQuery.Values.GetVariable(1);
        var var0 = query.Substring(var0Start, var0Len);
        var var1 = query.Substring(var1Start, var1Len);
        Assert.Equal("?o1", var0);
        Assert.Equal("?o2", var1);

        // Check values
        var (val0Start, val0Len) = parsedQuery.Values.GetValue(0);
        var (val1Start, val1Len) = parsedQuery.Values.GetValue(1);
        var val0 = query.Substring(val0Start, val0Len);
        var val1 = query.Substring(val1Start, val1Len);
        Assert.Equal("\"Alan\"", val0);
        Assert.Equal("\"alan@example.org\"", val1);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var rows = new List<(string s, string o1, string o2)>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var o1Idx = results.Current.FindBinding("?o1".AsSpan());
                var o2Idx = results.Current.FindBinding("?o2".AsSpan());
                rows.Add((
                    sIdx >= 0 ? results.Current.GetString(sIdx).ToString() : "N/A",
                    o1Idx >= 0 ? results.Current.GetString(o1Idx).ToString() : "N/A",
                    o2Idx >= 0 ? results.Current.GetString(o2Idx).ToString() : "N/A"
                ));
            }
            results.Dispose();

            // Should have exactly 1 result where ?o1="Alan" and ?o2="alan@example.org"
            Assert.True(rows.Count == 1, $"Expected 1 row but got {rows.Count}. var0='{var0}', var1='{var1}', val0='{val0}', val1='{val1}'");
            var row = rows[0];
            Assert.Equal("<http://example.org/a>", row.s);
            Assert.Equal("\"Alan\"", row.o1);
            Assert.Equal("\"alan@example.org\"", row.o2);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_PostQueryValues_W3C_AllVariablePredicates()
    {
        // Test that replicates W3C values03 EXACTLY:
        // - Clean store (no fixture data)
        // - Variable predicates: ?s ?p1 ?o1 . ?s ?p2 ?o2
        // This tests if the issue is with variable predicates in joins

        // Use pool to get a fresh store without fixture data
        using var lease = _fixture.RentScoped();
        var cleanStore = lease.Store;

        // Add exactly the data from data03.ttl
        cleanStore.AddCurrent("<http://example.org/a>", "<http://xmlns.com/foaf/0.1/name>", "\"Alan\"");
        cleanStore.AddCurrent("<http://example.org/a>", "<http://xmlns.com/foaf/0.1/mbox>", "\"alan@example.org\"");
        cleanStore.AddCurrent("<http://example.org/b>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        cleanStore.AddCurrent("<http://example.org/b>", "<http://xmlns.com/foaf/0.1/mbox>", "\"bob@example.org\"");
        cleanStore.AddCurrent("<http://example.org/a>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/b>");

        // First, test query WITHOUT VALUES to verify join works
        var queryNoValues = @"
PREFIX : <http://example.org/>
SELECT ?s ?o1 ?o2
{
  ?s ?p1 ?o1 .
  ?s ?p2 ?o2 .
}
";

        int joinRowCount = 0;
        cleanStore.AcquireReadLock();
        try
        {
            var parser1 = new SparqlParser(queryNoValues.AsSpan());
            var parsed1 = parser1.ParseQuery();
            var executor1 = new QueryExecutor(cleanStore, queryNoValues.AsSpan(), parsed1);
            var results1 = executor1.Execute();

            var rowsNoValues = new List<(string s, string o1, string o2)>();
            while (results1.MoveNext())
            {
                var sIdx = results1.Current.FindBinding("?s".AsSpan());
                var o1Idx = results1.Current.FindBinding("?o1".AsSpan());
                var o2Idx = results1.Current.FindBinding("?o2".AsSpan());
                rowsNoValues.Add((
                    sIdx >= 0 ? results1.Current.GetString(sIdx).ToString() : "N/A",
                    o1Idx >= 0 ? results1.Current.GetString(o1Idx).ToString() : "N/A",
                    o2Idx >= 0 ? results1.Current.GetString(o2Idx).ToString() : "N/A"
                ));
            }
            results1.Dispose();
            joinRowCount = rowsNoValues.Count;

            // Should have rows from the join
            // :a has 3 triples: 3x3 = 9 combinations
            // :b has 2 triples: 2x2 = 4 combinations
            // Total: 13 rows (without VALUES filtering)
            Assert.True(rowsNoValues.Count > 0, $"Query without VALUES should return rows but got 0. Data: 5 triples added.");

            // Check for the specific row that should match VALUES
            var matchingRows = rowsNoValues.Where(r => r.o1 == "\"Alan\"" && r.o2 == "\"alan@example.org\"").ToList();
            Assert.True(matchingRows.Count >= 1, $"Should have at least 1 row with o1=\"Alan\" and o2=\"alan@example.org\". Got {matchingRows.Count} of {rowsNoValues.Count} total. First 5 rows: {string.Join(", ", rowsNoValues.Take(5).Select(r => $"({r.o1}, {r.o2})"))}");
        }
        finally
        {
            cleanStore.ReleaseReadLock();
        }

        // Now test WITH VALUES - exact replica of values03.rq
        var queryWithValues = @"
PREFIX : <http://example.org/>
SELECT ?s ?o1 ?o2
{
  ?s ?p1 ?o1 .
  ?s ?p2 ?o2 .
} VALUES (?o1 ?o2) {
 (""Alan"" ""alan@example.org"")
}
";

        cleanStore.AcquireReadLock();
        try
        {
            var parser2 = new SparqlParser(queryWithValues.AsSpan());
            var parsed2 = parser2.ParseQuery();

            // Debug: verify VALUES is parsed
            Assert.True(parsed2.Values.HasValues, "VALUES should be parsed");
            Assert.Equal(2, parsed2.Values.VariableCount);
            Assert.Equal(2, parsed2.Values.ValueCount);

            // Get the parsed VALUES values
            var (val0Start, val0Len) = parsed2.Values.GetValue(0);
            var (val1Start, val1Len) = parsed2.Values.GetValue(1);
            var parsedVal0 = queryWithValues.Substring(val0Start, val0Len);
            var parsedVal1 = queryWithValues.Substring(val1Start, val1Len);

            // Get the parsed VALUES variable names
            var (var0Start, var0Len) = parsed2.Values.GetVariable(0);
            var (var1Start, var1Len) = parsed2.Values.GetVariable(1);
            var parsedVar0 = queryWithValues.Substring(var0Start, var0Len);
            var parsedVar1 = queryWithValues.Substring(var1Start, var1Len);

            var executor2 = new QueryExecutor(cleanStore, queryWithValues.AsSpan(), parsed2);
            var results2 = executor2.Execute();

            var rowsWithValues = new List<(string s, string o1, string o2)>();
            while (results2.MoveNext())
            {
                var sIdx = results2.Current.FindBinding("?s".AsSpan());
                var o1Idx = results2.Current.FindBinding("?o1".AsSpan());
                var o2Idx = results2.Current.FindBinding("?o2".AsSpan());
                rowsWithValues.Add((
                    sIdx >= 0 ? results2.Current.GetString(sIdx).ToString() : "N/A",
                    o1Idx >= 0 ? results2.Current.GetString(o1Idx).ToString() : "N/A",
                    o2Idx >= 0 ? results2.Current.GetString(o2Idx).ToString() : "N/A"
                ));
            }
            results2.Dispose();

            // Should have exactly 1 row after VALUES filtering
            Assert.True(rowsWithValues.Count == 1, $"Query WITH VALUES should return 1 row but got {rowsWithValues.Count}. " +
                $"parsedVars=[{parsedVar0}, {parsedVar1}], parsedVals=[{parsedVal0}, {parsedVal1}], joinRows={joinRowCount}");
            var row = rowsWithValues[0];
            Assert.Equal("<http://example.org/a>", row.s);
            Assert.Equal("\"Alan\"", row.o1);
            Assert.Equal("\"alan@example.org\"", row.o2);
        }
        finally
        {
            cleanStore.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_PostQueryValues_HashVerification()
    {
        // Verify that the FNV-1a hash is computed consistently
        var varName = "?o1";
        uint hash = 2166136261;
        foreach (var ch in varName)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        var result = (int)hash;

        // Also compute for binding table variable names
        var hashS = ComputeTestHash("?s");
        var hashP1 = ComputeTestHash("?p1");
        var hashP2 = ComputeTestHash("?p2");
        var hashO1 = ComputeTestHash("?o1");
        var hashO2 = ComputeTestHash("?o2");

        // Hash verification - the hashes are consistent
        Assert.Equal("5EFA9E47", hashS.ToString("X8"));
        Assert.Equal("72417926", hashO1.ToString("X8"));
        Assert.Equal("71417793", hashO2.ToString("X8"));
    }

    private static int ComputeTestHash(string value)
    {
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }

    [Fact]
    public void Execute_PostQueryValues_DebugBindingLookup()
    {
        // Debug test to understand why VALUES filtering fails
        using var lease = _fixture.RentScoped();
        var cleanStore = lease.Store;

        cleanStore.AddCurrent("<http://example.org/a>", "<http://xmlns.com/foaf/0.1/name>", "\"Alan\"");
        cleanStore.AddCurrent("<http://example.org/a>", "<http://xmlns.com/foaf/0.1/mbox>", "\"alan@example.org\"");

        // Test two-pattern query and check ?o1, ?o2 bindings
        var query = @"
PREFIX : <http://example.org/>
SELECT ?s ?o1 ?o2
{
  ?s ?p1 ?o1 .
  ?s ?p2 ?o2 .
}
";

        cleanStore.AcquireReadLock();
        try
        {
            var parser = new SparqlParser(query.AsSpan());
            var parsed = parser.ParseQuery();
            var executor = new QueryExecutor(cleanStore, query.AsSpan(), parsed);
            var results = executor.Execute();

            var rows = new List<string>();
            while (results.MoveNext())
            {
                var table = results.Current;
                var o1Idx = table.FindBinding("?o1".AsSpan());
                var o2Idx = table.FindBinding("?o2".AsSpan());
                var o1Val = o1Idx >= 0 ? table.GetString(o1Idx).ToString() : "N/A";
                var o2Val = o2Idx >= 0 ? table.GetString(o2Idx).ToString() : "N/A";
                rows.Add($"o1Idx={o1Idx},o1={o1Val},o2Idx={o2Idx},o2={o2Val}");
            }
            results.Dispose();

            // Find the row that should match VALUES
            var matchRow = rows.FirstOrDefault(r => r.Contains("o1=\"Alan\"") && r.Contains("o2=\"alan@example.org\""));
            Assert.True(matchRow != null, $"Should have a matching row. Got {rows.Count} rows: {string.Join("; ", rows.Take(10))}");
        }
        finally
        {
            cleanStore.ReleaseReadLock();
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
