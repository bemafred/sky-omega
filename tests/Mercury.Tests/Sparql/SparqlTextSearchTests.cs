using System;
using System.IO;
using Xunit;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// End-to-end tests for SPARQL text:match function.
/// </summary>
public class SparqlTextSearchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly QuadStore _store;

    public SparqlTextSearchTests()
    {
        var tempPath = TempPath.Test("sparql-textsearch");
        tempPath.MarkOwnership();
        _tempDir = tempPath;

        _store = new QuadStore(_tempDir);

        // Add test data with Swedish city names
        AddTriple("<http://ex.org/stockholm>", "<http://ex.org/name>", "\"Stockholm\"");
        AddTriple("<http://ex.org/stockholm>", "<http://ex.org/description>", "\"The capital of Sweden\"");

        AddTriple("<http://ex.org/goteborg>", "<http://ex.org/name>", "\"Göteborg\"");
        AddTriple("<http://ex.org/goteborg>", "<http://ex.org/description>", "\"Second largest city in Sweden\"");

        AddTriple("<http://ex.org/malmo>", "<http://ex.org/name>", "\"Malmö\"");
        AddTriple("<http://ex.org/malmo>", "<http://ex.org/description>", "\"Third largest city in Sweden\"");

        AddTriple("<http://ex.org/kiruna>", "<http://ex.org/name>", "\"Kiruna\"");
        AddTriple("<http://ex.org/kiruna>", "<http://ex.org/description>", "\"Northernmost city in Sweden\"");
    }

    private void AddTriple(string subject, string predicate, string obj)
    {
        _store.AddCurrent(subject, predicate, obj);
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_tempDir);
    }

    private int ExecuteQueryCount(string sparql)
    {
        var parser = new SparqlParser(sparql.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, sparql.AsSpan(), query);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            return count;
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #region Basic text:match Tests

    [Fact]
    public void TextMatch_FindsByPartialName()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, ""Stock""))
            }";

        Assert.Equal(1, ExecuteQueryCount(query));
    }

    [Fact]
    public void TextMatch_FindsMultipleMatches()
    {
        var query = @"
            SELECT ?city ?desc WHERE {
                ?city <http://ex.org/description> ?desc .
                FILTER(text:match(?desc, ""Sweden""))
            }";

        // All cities have "Sweden" in description
        Assert.Equal(4, ExecuteQueryCount(query));
    }

    [Fact]
    public void TextMatch_NoMatch_ReturnsEmpty()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, ""NotACity""))
            }";

        Assert.Equal(0, ExecuteQueryCount(query));
    }

    [Fact]
    public void TextMatch_CaseInsensitive()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, ""STOCKHOLM""))
            }";

        Assert.Equal(1, ExecuteQueryCount(query));
    }

    #endregion

    #region Swedish Character Tests

    [Fact]
    public void TextMatch_SwedishCharacters_ExactMatch()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, ""Göteborg""))
            }";

        Assert.Equal(1, ExecuteQueryCount(query));
    }

    [Fact]
    public void TextMatch_SwedishCharacters_LowercaseMatch()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, ""göteborg""))
            }";

        Assert.Equal(1, ExecuteQueryCount(query));
    }

    [Fact]
    public void TextMatch_SwedishCharacters_PartialMatch()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, ""ö""))
            }";

        // Göteborg and Malmö both contain ö
        Assert.Equal(2, ExecuteQueryCount(query));
    }

    #endregion

    #region Combined Filter Tests

    [Fact]
    public void TextMatch_CombinedWithOtherFilters()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                ?city <http://ex.org/description> ?desc .
                FILTER(text:match(?desc, ""capital""))
            }";

        Assert.Equal(1, ExecuteQueryCount(query));
    }

    [Fact]
    public void TextMatch_CombinedWithOrFilter()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, ""Stock"") || text:match(?name, ""Malmö""))
            }";

        Assert.Equal(2, ExecuteQueryCount(query));
    }

    [Fact]
    public void TextMatch_Negated()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(!text:match(?name, ""Stock""))
            }";

        // All except Stockholm
        Assert.Equal(3, ExecuteQueryCount(query));
    }

    #endregion

    #region Alternative Syntax Tests

    [Fact]
    public void Match_AlternativeSyntax_Works()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(match(?name, ""Stock""))
            }";

        Assert.Equal(1, ExecuteQueryCount(query));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TextMatch_EmptyQuery_MatchesAll()
    {
        var query = @"
            SELECT ?city ?name WHERE {
                ?city <http://ex.org/name> ?name .
                FILTER(text:match(?name, """"))
            }";

        // Empty string is contained in all strings
        Assert.Equal(4, ExecuteQueryCount(query));
    }

    #endregion

    #region ADR-024: Trigram Pre-Filtering Verification

#if DEBUG
    [Fact]
    public void TextMatch_TrigramPreFiltering_ReducesEvaluations()
    {
        // Create a store with many literals — only a few match "stockholm"
        var tempPath = TempPath.Test("trigram-prefilter");
        tempPath.MarkOwnership();
        using var store = new QuadStore(tempPath);

        // Add 200 distinct literals, only 2 contain "stockholm"
        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/match1>", "<http://ex.org/name>", "\"City of Stockholm\"");
        store.AddCurrentBatched("<http://ex.org/match1>", "<http://ex.org/type>", "<http://ex.org/City>");
        store.AddCurrentBatched("<http://ex.org/match2>", "<http://ex.org/name>", "\"Stockholm Municipality\"");
        store.AddCurrentBatched("<http://ex.org/match2>", "<http://ex.org/type>", "<http://ex.org/City>");
        for (int i = 0; i < 198; i++)
        {
            store.AddCurrentBatched($"<http://ex.org/city{i}>", "<http://ex.org/name>", $"\"City number {i} in the world\"");
            store.AddCurrentBatched($"<http://ex.org/city{i}>", "<http://ex.org/type>", "<http://ex.org/City>");
        }
        store.CommitBatch();

        SkyOmega.Mercury.Sparql.Execution.Expressions.FilterEvaluator.ResetTextMatchEvaluationCount();

        // Two patterns needed to route through MultiPatternScan (which has trigram integration)
        var sparql = @"SELECT ?s ?name WHERE {
            ?s <http://ex.org/type> <http://ex.org/City> .
            ?s <http://ex.org/name> ?name .
            FILTER(text:match(?name, ""stockholm""))
        }";

        var parser = new SkyOmega.Mercury.Sparql.Parsing.SparqlParser(sparql.AsSpan());
        var query = parser.ParseQuery();

        store.AcquireReadLock();
        try
        {
            var executor = new SkyOmega.Mercury.Sparql.Execution.QueryExecutor(store, sparql.AsSpan(), query);
            var results = executor.Execute();
            int count = 0;
            while (results.MoveNext()) count++;
            results.Dispose();

            Assert.Equal(2, count); // Correctness: exactly 2 matches

            var evaluations = SkyOmega.Mercury.Sparql.Execution.Expressions.FilterEvaluator.TextMatchEvaluationCount;

            // With trigram pre-filtering, evaluations should be much less than 200
            // The trigram index should narrow to only a few candidates
            Assert.True(evaluations < 50,
                $"Expected < 50 text:match evaluations with trigram pre-filtering, got {evaluations}");
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void TextMatch_ShortQuery_FallsBackToBruteForce()
    {
        // Query "ab" (< 3 chars) cannot use trigram pre-filtering
        // This tests the single-pattern path (TriplePatternScan), which doesn't have
        // trigram integration yet — brute force is the expected behavior
        SkyOmega.Mercury.Sparql.Execution.Expressions.FilterEvaluator.ResetTextMatchEvaluationCount();

        var sparql = @"SELECT ?city ?name WHERE {
            ?city <http://ex.org/name> ?name .
            FILTER(text:match(?name, ""ab""))
        }";

        var resultCount = ExecuteQueryCount(sparql);

        var evaluations = SkyOmega.Mercury.Sparql.Execution.Expressions.FilterEvaluator.TextMatchEvaluationCount;

        // Short query: all 4 name bindings should be evaluated (brute force)
        Assert.Equal(0, resultCount); // No names contain "ab"
        Assert.True(evaluations >= 4,
            $"Short query should fall back to brute-force, but only {evaluations} evaluations (expected >= 4)");
    }
#endif

    #endregion
}
