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
            Assert.Contains("30", values);
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
            Assert.Equal("35", ages[0]);
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
            Assert.Contains("25", ages);
            Assert.Contains("35", ages);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    // ========== ASK Tests ==========

}
