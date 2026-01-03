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
    #region Property Path Sequence Tests

    [Fact]
    public void Execute_SequencePath_BasicTwoStep()
    {
        // Add data for a 2-step path: Alice knows Bob, Bob worksAt AcmeCorp
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Bob>", "<http://example.org/worksAt>", "<http://example.org/AcmeCorp>");
        Store.CommitBatch();

        // Query: ?s knows/worksAt ?company - find companies where someone Alice knows works
        var query = "SELECT ?s ?company WHERE { ?s <http://xmlns.com/foaf/0.1/knows>/<http://example.org/worksAt> ?company }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify sequence path was expanded to 2 patterns
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.PatternCount);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

            // No matches expected
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SequencePath_WithBoundSubject()
    {
        // Add data for a 2-step path
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Bob>", "<http://example.org/worksAt>", "<http://example.org/AcmeCorp>");
        Store.AddCurrentBatched("<http://example.org/Charlie>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Bob>");
        Store.CommitBatch();

        // Query starting from specific subject: Alice knows/worksAt ?company
        var query = "SELECT ?company WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>/<http://example.org/worksAt> ?company }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify sequence path was expanded to 2 patterns
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.PatternCount);

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_SequencePath_MultipleResults()
    {
        // Add data where multiple paths exist
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Bob>", "<http://example.org/worksAt>", "<http://example.org/AcmeCorp>");
        Store.AddCurrentBatched("<http://example.org/Dave>", "<http://xmlns.com/foaf/0.1/name>", "\"Dave\"");
        Store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Dave>");
        Store.AddCurrentBatched("<http://example.org/Dave>", "<http://example.org/worksAt>", "<http://example.org/TechCorp>");
        Store.CommitBatch();

        // Query: ?s knows/worksAt ?company
        var query = "SELECT ?s ?company WHERE { ?s <http://xmlns.com/foaf/0.1/knows>/<http://example.org/worksAt> ?company }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_AlternativePath_WithBoundSubject()
    {
        // Query with specific subject: Alice name|age ?o
        var query = "SELECT ?o WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/name>|<http://xmlns.com/foaf/0.1/age> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_AlternativePath_NoMatches()
    {
        // Query with predicates that don't exist
        var query = "SELECT ?s ?o WHERE { ?s <http://example.org/nonexistent1>|<http://example.org/nonexistent2> ?o }";
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
    public void Execute_AlternativePath_OnlyFirstPredicateMatches()
    {
        // Only name exists, not nonexistent
        var query = "SELECT ?s ?o WHERE { ?s <http://xmlns.com/foaf/0.1/name>|<http://example.org/nonexistent> ?o }";
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

            // 3 names (Alice, Bob, Charlie)
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_AlternativePath_OnlySecondPredicateMatches()
    {
        // Only age exists (second alternative), not nonexistent (first)
        var query = "SELECT ?s ?o WHERE { ?s <http://example.org/nonexistent>|<http://xmlns.com/foaf/0.1/age> ?o }";
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

            // 3 ages (Alice, Bob, Charlie)
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

            // No one knows Charlie
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ZeroOrMorePath_MultiHop()
    {
        // Test multi-hop transitive closure with a chain: Alice -> Bob -> Charlie
        // First add the extra triple
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Charlie>");
        Store.CommitBatch();

        var query = "SELECT ?x WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>* ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OneOrMorePath_MultiHop()
    {
        // Test multi-hop with p+ (no reflexive): Alice -> Bob -> Charlie
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Bob>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Charlie>");
        Store.CommitBatch();

        var query = "SELECT ?x WHERE { <http://example.org/Alice> <http://xmlns.com/foaf/0.1/knows>+ ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ZeroOrMorePath_NoMatches()
    {
        // Start from Charlie who knows no one - p* should return just Charlie (reflexive)
        var query = "SELECT ?x WHERE { <http://example.org/Charlie> <http://xmlns.com/foaf/0.1/knows>* ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OneOrMorePath_NoMatches()
    {
        // Start from Charlie who knows no one - p+ should return nothing
        var query = "SELECT ?x WHERE { <http://example.org/Charlie> <http://xmlns.com/foaf/0.1/knows>+ ?x }";
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

            // p+ with no outgoing edges: empty result
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
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

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ZeroOrOnePath_NoMatch()
    {
        // p? with no outgoing edges: only reflexive case
        var query = "SELECT ?x WHERE { <http://example.org/Charlie> <http://xmlns.com/foaf/0.1/knows>? ?x }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
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
            Store.ReleaseReadLock();
        }
    }

    #endregion
}
