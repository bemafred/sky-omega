using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for TrigramIndex full-text search functionality.
/// </summary>
public class TrigramIndexTests : IDisposable
{
    private readonly string _testDir;

    public TrigramIndexTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"TrigramTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    private string GetIndexPath() => Path.Combine(_testDir, "trigram");

    #region Trigram Extraction Tests

    [Fact]
    public void IndexAtom_ShortString_LessThan3Chars_NotIndexed()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // Strings < 3 chars should not be indexed (no trigrams possible)
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"ab\""));

        var stats = index.GetStatistics();
        Assert.Equal(0, stats.IndexedAtomCount);
    }

    [Fact]
    public void IndexAtom_Exactly3Chars_SingleTrigram()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"abc\""));

        var stats = index.GetStatistics();
        Assert.Equal(1, stats.IndexedAtomCount);
        Assert.Equal(1, stats.TotalTrigrams); // "abc" = 1 trigram
    }

    [Fact]
    public void IndexAtom_LongerString_MultipleTrigramsExtracted()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // "hello" = 3 trigrams: "hel", "ell", "llo"
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello\""));

        var stats = index.GetStatistics();
        Assert.Equal(1, stats.IndexedAtomCount);
        Assert.Equal(3, stats.TotalTrigrams);
    }

    [Fact]
    public void IndexAtom_CaseFolding_LowercasesAscii()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // "HELLO" should be indexed as "hello"
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"HELLO\""));

        // Query with lowercase should find it
        var candidates = index.QueryCandidates("hello").ToList();
        Assert.Contains(1L, candidates);
    }

    [Fact]
    public void IndexAtom_SwedishCharacters_CaseFoldingWorks()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // Swedish word "Göteborg" (uppercase G, ö)
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"Göteborg\""));

        // Query with lowercase should find it
        var candidates = index.QueryCandidates("göteborg").ToList();
        Assert.Contains(1L, candidates);
    }

    [Fact]
    public void IndexAtom_SwedishAaoCharacters_IndexedCorrectly()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // Swedish characters å, ä, ö
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"bröd\"")); // "bread" in Swedish

        // Query should find it
        var candidates = index.QueryCandidates("bröd").ToList();
        Assert.Contains(1L, candidates);
    }

    [Fact]
    public void IndexAtom_UppercaseSwedish_MatchesLowercase()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // Uppercase Swedish
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"ÅÄÖSTRÄND\""));

        // Query with lowercase should find it
        var candidates = index.QueryCandidates("åäöstränd").ToList();
        Assert.Contains(1L, candidates);
    }

    #endregion

    #region Indexing Tests

    [Fact]
    public void IndexAtom_MultipleAtoms_AllIndexed()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));
        index.IndexAtom(2, Encoding.UTF8.GetBytes("\"hello there\""));
        index.IndexAtom(3, Encoding.UTF8.GetBytes("\"goodbye world\""));

        var stats = index.GetStatistics();
        Assert.Equal(3, stats.IndexedAtomCount);
    }

    [Fact]
    public void IndexAtom_SameAtomTwice_Deduplicated()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello\""));
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello\""));

        // Should only count once
        var candidates = index.QueryCandidates("hello").ToList();
        Assert.Single(candidates);
    }

    [Fact]
    public void IndexAtom_DifferentTexts_SharedTrigrams()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // Both contain "the" trigram
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"the cat\""));
        index.IndexAtom(2, Encoding.UTF8.GetBytes("\"the dog\""));

        // Query for "the" should find both
        var candidates = index.QueryCandidates("the").ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Contains(1L, candidates);
        Assert.Contains(2L, candidates);
    }

    [Fact]
    public void IndexAtom_LiteralWithLanguageTag_ContentExtracted()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // RDF literal with language tag
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello\"@en"));

        // Should still be indexed (quotes stripped, language tag ignored)
        var candidates = index.QueryCandidates("hello").ToList();
        Assert.Contains(1L, candidates);
    }

    [Fact]
    public void IndexAtom_LiteralWithDatatype_ContentExtracted()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // RDF literal with datatype
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello\"^^<http://example.org/type>"));

        // Should still be indexed
        var candidates = index.QueryCandidates("hello").ToList();
        Assert.Contains(1L, candidates);
    }

    #endregion

    #region Query Tests

    [Fact]
    public void QueryCandidates_SingleMatch_ReturnsAtom()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));

        var candidates = index.QueryCandidates("hello").ToList();
        Assert.Single(candidates);
        Assert.Equal(1L, candidates[0]);
    }

    [Fact]
    public void QueryCandidates_NoMatch_ReturnsEmpty()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));

        var candidates = index.QueryCandidates("xyz123").ToList();
        Assert.Empty(candidates);
    }

    [Fact]
    public void QueryCandidates_PartialMatch_ReturnsCandidate()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));

        // "wor" should match "world"
        var candidates = index.QueryCandidates("wor").ToList();
        Assert.Contains(1L, candidates);
    }

    [Fact]
    public void QueryCandidates_MultipleMatches_ReturnsAll()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"Stockholm\""));
        index.IndexAtom(2, Encoding.UTF8.GetBytes("\"Stockholms län\""));
        index.IndexAtom(3, Encoding.UTF8.GetBytes("\"Göteborg\"")); // No match

        var candidates = index.QueryCandidates("Stockholm").ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Contains(1L, candidates);
        Assert.Contains(2L, candidates);
        Assert.DoesNotContain(3L, candidates);
    }

    [Fact]
    public void QueryCandidates_LongQuery_IntersectsAllTrigrams()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"programming language\""));
        index.IndexAtom(2, Encoding.UTF8.GetBytes("\"program manager\""));

        // "programming" has unique trigrams - should only match atom 1
        var candidates = index.QueryCandidates("programming").ToList();
        Assert.Single(candidates);
        Assert.Equal(1L, candidates[0]);
    }

    [Fact]
    public void QueryCandidates_ShortQuery_ReturnsEmpty()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));

        // Queries < 3 chars can't use trigrams
        var candidates = index.QueryCandidates("he").ToList();
        Assert.Empty(candidates);
    }

    [Fact]
    public void IsCandidateMatch_MatchingAtom_ReturnsTrue()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));

        Assert.True(index.IsCandidateMatch(1, "hello"));
    }

    [Fact]
    public void IsCandidateMatch_NonMatchingAtom_ReturnsFalse()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));
        index.IndexAtom(2, Encoding.UTF8.GetBytes("\"goodbye world\""));

        Assert.False(index.IsCandidateMatch(2, "hello"));
    }

    #endregion

    #region Persistence Tests

    [Fact]
    public void Persistence_ReopenIndex_DataSurvives()
    {
        var indexPath = GetIndexPath();

        // Create and populate index
        using (var index = new TrigramIndex(indexPath))
        {
            index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello world\""));
            index.IndexAtom(2, Encoding.UTF8.GetBytes("\"goodbye world\""));
            index.Flush();
        }

        // Reopen and query
        using (var index = new TrigramIndex(indexPath))
        {
            var stats = index.GetStatistics();
            Assert.Equal(2, stats.IndexedAtomCount);

            var candidates = index.QueryCandidates("hello").ToList();
            Assert.Contains(1L, candidates);
        }
    }

    [Fact]
    public void Persistence_MetadataPreserved()
    {
        var indexPath = GetIndexPath();

        // Create and populate
        using (var index = new TrigramIndex(indexPath))
        {
            index.IndexAtom(1, Encoding.UTF8.GetBytes("\"test string one\""));
            index.IndexAtom(2, Encoding.UTF8.GetBytes("\"test string two\""));
            index.IndexAtom(3, Encoding.UTF8.GetBytes("\"test string three\""));
            index.Flush();

            var stats = index.GetStatistics();
            Assert.Equal(3, stats.IndexedAtomCount);
        }

        // Reopen and verify
        using (var index = new TrigramIndex(indexPath))
        {
            var stats = index.GetStatistics();
            Assert.Equal(3, stats.IndexedAtomCount);
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void IndexAtom_EmptyString_NotIndexed()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"\""));

        var stats = index.GetStatistics();
        Assert.Equal(0, stats.IndexedAtomCount);
    }

    [Fact]
    public void IndexAtom_ZeroAtomId_Ignored()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(0, Encoding.UTF8.GetBytes("\"hello\""));

        var stats = index.GetStatistics();
        Assert.Equal(0, stats.IndexedAtomCount);
    }

    [Fact]
    public void IndexAtom_NegativeAtomId_Ignored()
    {
        using var index = new TrigramIndex(GetIndexPath());

        index.IndexAtom(-1, Encoding.UTF8.GetBytes("\"hello\""));

        var stats = index.GetStatistics();
        Assert.Equal(0, stats.IndexedAtomCount);
    }

    [Fact]
    public void IndexAtom_NonLiteral_Indexed()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // IRI (not a literal) - should still be indexed as-is
        index.IndexAtom(1, Encoding.UTF8.GetBytes("http://example.org/resource"));

        var stats = index.GetStatistics();
        Assert.Equal(1, stats.IndexedAtomCount);
    }

    [Fact]
    public void IndexAtom_EscapedQuotes_HandledCorrectly()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // Literal with escaped quote: "hello \"world\""
        index.IndexAtom(1, Encoding.UTF8.GetBytes("\"hello \\\"world\\\"\""));

        // Should find "hello"
        var candidates = index.QueryCandidates("hello").ToList();
        Assert.Contains(1L, candidates);
    }

    [Fact]
    public void QueryCandidates_ManyAtoms_HandlesLargePostingLists()
    {
        using var index = new TrigramIndex(GetIndexPath());

        // Index many atoms with shared trigram
        for (int i = 1; i <= 100; i++)
        {
            index.IndexAtom(i, Encoding.UTF8.GetBytes($"\"test number {i}\""));
        }

        // Query for shared trigram "tes"
        var candidates = index.QueryCandidates("test").ToList();
        Assert.Equal(100, candidates.Count);
    }

    #endregion
}
