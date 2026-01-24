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
    public void Execute_OptionalMatches_ExtendsBindings()
    {
        // Alice has both name and age, so OPTIONAL should match
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name OPTIONAL { ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify OPTIONAL was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasOptionalPatterns);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Assert.Contains(values, v => ExtractNumericValue(v) == "30");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionSinglePatterns()
    {
        // Simple UNION of two single-pattern branches
        var query = "SELECT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://xmlns.com/foaf/0.1/knows> ?o } }";
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

            // 3 names + 1 knows = 4 results
            Assert.Equal(4, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionWithDistinct()
    {
        // UNION with DISTINCT to remove duplicates
        var query = "SELECT DISTINCT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://xmlns.com/foaf/0.1/name> ?o } }";
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

            // Same pattern twice, but DISTINCT removes duplicates - should get 3
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

            // Same pattern twice in UNION, but REDUCED removes duplicates - should get 3
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionWithLimit()
    {
        // UNION with LIMIT
        var query = "SELECT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://xmlns.com/foaf/0.1/age> ?o } } LIMIT 4";
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

            // 6 total (3 names + 3 ages), but limited to 4
            Assert.Equal(4, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionEmptyBranch()
    {
        // UNION where one branch has no matches
        var query = "SELECT * WHERE { { ?s <http://xmlns.com/foaf/0.1/name> ?o } UNION { ?s <http://example.org/nonexistent> ?o } }";
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

            // Only first branch matches (3 names)
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionBothBranchesEmpty()
    {
        // UNION where both branches have no matches
        var query = "SELECT * WHERE { { ?s <http://example.org/x> ?o } UNION { ?s <http://example.org/y> ?o } }";
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
    public void Execute_UnionWithBindOnlyBranches()
    {
        // W3C bind07 test case: UNION branches contain only BIND expressions (no triple patterns)
        // Each solution from the base pattern should be duplicated with different BIND values
        // Base pattern matches 7 triples (3 names + 3 ages + 1 knows), each should produce 2 rows
        var query = @"SELECT ?s ?p ?o ?z WHERE {
            ?s ?p ?o .
            { BIND(?o AS ?z) } UNION { BIND(""union2"" AS ?z) }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify UNION was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasUnion);

        // Verify pattern structure
        var gp = parsedQuery.WhereClause.Pattern;
        // PatternCount only counts triple patterns: 1 triple outside UNION
        Assert.Equal(1, gp.PatternCount);
        // BINDs are tracked separately: 2 total (one per UNION branch)
        Assert.Equal(2, gp.BindCount);
        Assert.Equal(1, gp.FirstBranchBindCount);
        Assert.Equal(1, gp.UnionBranchBindCount);
        // UnionBranchPatternCount = PatternCount - UnionStartIndex = 1 - 1 = 0
        Assert.Equal(0, gp.UnionBranchPatternCount);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var rows = new List<(string s, string z)>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("s".AsSpan());
                var zIdx = results.Current.FindBinding("?z".AsSpan());
                if (zIdx < 0) zIdx = results.Current.FindBinding("z".AsSpan());

                var s = sIdx >= 0 ? results.Current.GetString(sIdx).ToString() : "";
                var z = zIdx >= 0 ? results.Current.GetString(zIdx).ToString() : "";
                rows.Add((s, z));
            }
            results.Dispose();

            // Should get 14 rows: 7 triples × 2 UNION branches
            Assert.Equal(14, rows.Count);

            // Count rows where z = "union2" (second branch)
            var union2Count = rows.Count(r => r.z == "union2");
            Assert.Equal(7, union2Count);  // 7 triples with second branch BIND

            // Count rows where z != "union2" (first branch - z = value of ?o)
            var union1Count = rows.Count(r => r.z != "union2");
            Assert.Equal(7, union1Count);  // 7 triples with first branch BIND
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindConstant()
    {
        // BIND a constant value
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name BIND(42 AS ?answer) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindArithmetic()
    {
        // BIND with arithmetic on a variable
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND(?age + 10 AS ?agePlus10) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var computed = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                var computedIdx = results.Current.FindBinding("?agePlus10".AsSpan());
                Assert.True(computedIdx >= 0, "?agePlus10 should be bound");

                var age = ExtractNumericValue(results.Current.GetString(ageIdx).ToString());
                var agePlus10 = results.Current.GetString(computedIdx).ToString();
                computed[age] = agePlus10;
            }
            results.Dispose();

            // Alice=30 -> 40, Bob=25 -> 35, Charlie=35 -> 45
            Assert.Equal(3, computed.Count);
            Assert.Equal("40", ExtractNumericValue(computed["30"]));
            Assert.Equal("35", ExtractNumericValue(computed["25"]));
            Assert.Equal("45", ExtractNumericValue(computed["35"]));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindMultiplication()
    {
        // BIND with multiplication
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND(?age * 2 AS ?doubled) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var computed = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                var computedIdx = results.Current.FindBinding("?doubled".AsSpan());
                Assert.True(computedIdx >= 0, "?doubled should be bound");

                var age = ExtractNumericValue(results.Current.GetString(ageIdx).ToString());
                var doubled = results.Current.GetString(computedIdx).ToString();
                computed[age] = doubled;
            }
            results.Dispose();

            // Alice=30 -> 60, Bob=25 -> 50, Charlie=35 -> 70
            Assert.Equal(3, computed.Count);
            Assert.Equal("60", ExtractNumericValue(computed["30"]));
            Assert.Equal("50", ExtractNumericValue(computed["25"]));
            Assert.Equal("70", ExtractNumericValue(computed["35"]));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindWithParentheses()
    {
        // BIND with complex expression using parentheses
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND((?age + 5) * 2 AS ?computed) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var computed = new Dictionary<string, string>();
            while (results.MoveNext())
            {
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                var computedIdx = results.Current.FindBinding("?computed".AsSpan());
                Assert.True(computedIdx >= 0, "?computed should be bound");

                var age = ExtractNumericValue(results.Current.GetString(ageIdx).ToString());
                var result = results.Current.GetString(computedIdx).ToString();
                computed[age] = result;
            }
            results.Dispose();

            // Alice=30 -> (30+5)*2 = 70
            // Bob=25 -> (25+5)*2 = 60
            // Charlie=35 -> (35+5)*2 = 80
            Assert.Equal(3, computed.Count);
            Assert.Equal("70", ExtractNumericValue(computed["30"]));
            Assert.Equal("60", ExtractNumericValue(computed["25"]));
            Assert.Equal("80", ExtractNumericValue(computed["35"]));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindStringLiteral()
    {
        // BIND a string literal
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name BIND(\"greeting\" AS ?msg) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_BindWithFilter()
    {
        // BIND combined with FILTER
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age BIND(?age * 2 AS ?doubled) FILTER(?doubled > 60) }";
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
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                ages.Add(results.Current.GetString(ageIdx).ToString());
            }
            results.Dispose();

            // 30*2=60 (not > 60), 25*2=50 (no), 35*2=70 (yes)
            // Only Charlie with age=35 should pass
            Assert.Single(ages);
            Assert.Equal("35", ExtractNumericValue(ages[0]));
        }
        finally
        {
            Store.ReleaseReadLock();
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

            // Alice knows Bob, so Alice is excluded
            // Bob and Charlie don't know anyone, so they remain
            Assert.Equal(2, names.Count);
            Assert.Contains("\"Bob\"", names);
            Assert.Contains("\"Charlie\"", names);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusNoMatch()
    {
        // MINUS pattern that matches nothing - all results should remain
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name MINUS { ?person <http://example.org/nonexistent> ?x } }";
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

            // No MINUS matches, so all 3 people remain
            Assert.Equal(3, names.Count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusAllMatch()
    {
        // MINUS pattern that matches everything - no results
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name MINUS { ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
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

            // Everyone has an age, so MINUS excludes all
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithConstant()
    {
        // MINUS with a constant value (data stores age as plain "30" not quoted)
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name MINUS { ?person <http://xmlns.com/foaf/0.1/age> 30 } }";
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

            // Alice has age=30, so she is excluded
            // Bob (age=25) and Charlie (age=35) remain
            Assert.Equal(2, names.Count);
            Assert.Contains("\"Bob\"", names);
            Assert.Contains("\"Charlie\"", names);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithFilter()
    {
        // MINUS combined with FILTER
        var query = "SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 20) MINUS { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }";
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

            // All ages > 20: Alice=30, Bob=25, Charlie=35
            // Alice knows Bob, so Alice is excluded
            // Remaining: Bob=25, Charlie=35
            Assert.Equal(2, ages.Count);
            Assert.Contains(ages, a => ExtractNumericValue(a) == "25");
            Assert.Contains(ages, a => ExtractNumericValue(a) == "35");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithFilterInside_ExcludesMatchingByFilter()
    {
        // Add extra data for this specific test - different types for Alice, Bob, Charlie
        // We'll use "role" predicate with values that we can filter on
        Store.AddCurrent("<http://example.org/Alice>", "<http://example.org/role>", "<http://example.org/Manager>");
        Store.AddCurrent("<http://example.org/Bob>", "<http://example.org/role>", "<http://example.org/Developer>");
        Store.AddCurrent("<http://example.org/Charlie>", "<http://example.org/role>", "<http://example.org/Intern>");

        // Query: find people with names MINUS those with role Developer OR Intern
        // Expected: only Alice (Manager)
        // Using full URIs in FILTER instead of prefixes to simplify testing
        var query = @"SELECT ?person WHERE {
  ?person <http://xmlns.com/foaf/0.1/name> ?name
  MINUS {
    ?person <http://example.org/role> ?role
    FILTER(?role = <http://example.org/Developer> || ?role = <http://example.org/Intern>)
  }
}";
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

            // Only Alice should remain (Manager is not Developer or Intern)
            Assert.Single(persons);
            Assert.Contains("Alice", persons[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithFilterInsidePrefixed_ExcludesMatchingByFilter()
    {
        // Same test but with prefixed names to verify prefix expansion works in MINUS FILTER
        Store.AddCurrent("<http://example.org/Alice>", "<http://example.org/role>", "<http://example.org/Manager>");
        Store.AddCurrent("<http://example.org/Bob>", "<http://example.org/role>", "<http://example.org/Developer>");
        Store.AddCurrent("<http://example.org/Charlie>", "<http://example.org/role>", "<http://example.org/Intern>");

        // Query with prefixes: find people with names MINUS those with role Developer OR Intern
        var query = @"PREFIX ex: <http://example.org/>
PREFIX foaf: <http://xmlns.com/foaf/0.1/>
SELECT ?person WHERE {
  ?person foaf:name ?name
  MINUS {
    ?person ex:role ?role
    FILTER(?role = ex:Developer || ?role = ex:Intern)
  }
}";
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

            // Only Alice should remain (Manager is not Developer or Intern)
            Assert.Single(persons);
            Assert.Contains("Alice", persons[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusWithOptionalInside_FullMinuend()
    {
        // W3C full-minuend test: MINUS with OPTIONAL patterns
        // Data setup matches the W3C test case
        Store.AddCurrent("<http://example/a0>", "<http://example/p1>", "<http://example/b0>");
        Store.AddCurrent("<http://example/a0>", "<http://example/p2>", "<http://example/c0>");
        Store.AddCurrent("<http://example/a1>", "<http://example/p1>", "<http://example/b1>");
        Store.AddCurrent("<http://example/a1>", "<http://example/p2>", "<http://example/c1>");
        Store.AddCurrent("<http://example/a2>", "<http://example/p1>", "<http://example/b2>");
        Store.AddCurrent("<http://example/a2>", "<http://example/p2>", "<http://example/c2>");
        Store.AddCurrent("<http://example/a3>", "<http://example/p1>", "<http://example/b3>");
        Store.AddCurrent("<http://example/a3>", "<http://example/p2>", "<http://example/c3>");

        // MINUS subtrahend data
        Store.AddCurrent("<http://example/d0>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Sub>");
        // d0 has no q1/q2 - OPTIONAL won't match, so ?b/?c unbound in MINUS

        Store.AddCurrent("<http://example/d1>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Sub>");
        Store.AddCurrent("<http://example/d1>", "<http://example/q1>", "<http://example/b1>");
        Store.AddCurrent("<http://example/d1>", "<http://example/q2>", "<http://example/c1>");
        // d1: ?b=b1, ?c=c1 -> excludes a1 (both match)

        Store.AddCurrent("<http://example/d2>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Sub>");
        Store.AddCurrent("<http://example/d2>", "<http://example/q1>", "<http://example/b2>");
        // d2: ?b=b2, ?c=unbound -> excludes a2 (?b matches)

        Store.AddCurrent("<http://example/d3>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://example/Sub>");
        Store.AddCurrent("<http://example/d3>", "<http://example/q1>", "<http://example/b3>");
        Store.AddCurrent("<http://example/d3>", "<http://example/q2>", "<http://example/cx>");
        // d3: ?b=b3, ?c=cx -> does NOT exclude a3 (c3 != cx)

        var query = @"PREFIX : <http://example/>
SELECT ?a ?b ?c {
  ?a :p1 ?b ; :p2 ?c
  MINUS {
    ?d a :Sub
    OPTIONAL { ?d :q1 ?b }
    OPTIONAL { ?d :q2 ?c }
  }
}";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify OPTIONAL inside MINUS was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasMinusOptionalPatterns);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var solutions = new List<string>();
            while (results.MoveNext())
            {
                var aIdx = results.Current.FindBinding("?a".AsSpan());
                Assert.True(aIdx >= 0);
                solutions.Add(results.Current.GetString(aIdx).ToString());
            }
            results.Dispose();

            // Expected: a0 (d0 has no ?b/?c bindings, domain disjoint) and a3 (?c values differ)
            Assert.Equal(2, solutions.Count);
            Assert.Contains(solutions, s => s.Contains("a0"));
            Assert.Contains(solutions, s => s.Contains("a3"));
            Assert.DoesNotContain(solutions, s => s.Contains("a1")); // excluded: ?b=b1, ?c=c1 match
            Assert.DoesNotContain(solutions, s => s.Contains("a2")); // excluded: ?b=b2 matches
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    // ========== ASK Tests ==========

}
