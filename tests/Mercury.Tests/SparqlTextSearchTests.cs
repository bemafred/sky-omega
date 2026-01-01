using System;
using System.IO;
using Xunit;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace Mercury.Tests;

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

        // Create store with full-text search enabled
        var options = new StorageOptions { EnableFullTextSearch = true };
        _store = new QuadStore(_tempDir, null, null, options);

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
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
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
}
