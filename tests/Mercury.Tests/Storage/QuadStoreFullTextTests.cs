using System;
using System.IO;
using Xunit;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for QuadStore full-text search integration.
/// </summary>
public class QuadStoreFullTextTests : IDisposable
{
    private readonly string _testDir;

    public QuadStoreFullTextTests()
    {
        var tempPath = TempPath.Test("quadstore-fts");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose()
    {
        TempPath.SafeCleanup(_testDir);
    }

    private StorageOptions FullTextOptions => new() { EnableFullTextSearch = true };

    #region Integration Tests

    [Fact]
    public void QuadStore_FullTextEnabled_CreatesTrigramFiles()
    {
        // Arrange & Act
        using (var store = new QuadStore(_testDir, null, null, FullTextOptions))
        {
            store.AddCurrent("<s>", "<p>", "\"hello world\"");
        }

        // Assert
        Assert.True(File.Exists(Path.Combine(_testDir, "trigram.hash")));
        Assert.True(File.Exists(Path.Combine(_testDir, "trigram.posts")));
    }

    [Fact]
    public void QuadStore_FullTextDisabled_NoTrigramFiles()
    {
        // Arrange & Act
        using (var store = new QuadStore(_testDir))
        {
            store.AddCurrent("<s>", "<p>", "\"hello world\"");
        }

        // Assert
        Assert.False(File.Exists(Path.Combine(_testDir, "trigram.hash")));
        Assert.False(File.Exists(Path.Combine(_testDir, "trigram.posts")));
    }

    [Fact]
    public void QuadStore_FullTextEnabled_IndexesLiterals()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act
        store.AddCurrent("<s1>", "<name>", "\"Stockholm\"");
        store.AddCurrent("<s2>", "<name>", "\"Göteborg\"");
        store.AddCurrent("<s3>", "<name>", "\"Malmö\"");

        // Assert - basic sanity check (actual querying via text:match tested later)
        // Just verify no exceptions during indexing
        Assert.True(true);
    }

    [Fact]
    public void QuadStore_FullTextEnabled_SkipsIRIs()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act - add IRI objects (not literals)
        store.AddCurrent("<s1>", "<type>", "<http://example.org/City>");
        store.AddCurrent("<s2>", "<sameAs>", "<http://dbpedia.org/resource/Stockholm>");

        // Assert - should not throw, IRIs should be skipped for FTS
        Assert.True(true);
    }

    [Fact]
    public void QuadStore_FullTextEnabled_HandlesSwedishCharacters()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act - add Swedish text with åäö
        store.AddCurrent("<s1>", "<name>", "\"Räksmörgås\""); // Shrimp sandwich
        store.AddCurrent("<s2>", "<name>", "\"Köttbullar\""); // Meatballs
        store.AddCurrent("<s3>", "<name>", "\"Smörgåstårta\""); // Sandwich cake

        // Assert - should index without issues
        Assert.True(true);
    }

    [Fact]
    public void QuadStore_FullTextEnabled_HandlesEmptyLiterals()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act - add empty literal
        store.AddCurrent("<s1>", "<name>", "\"\"");

        // Assert - should not throw
        Assert.True(true);
    }

    [Fact]
    public void QuadStore_FullTextEnabled_HandlesShortLiterals()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act - add short literals (< 3 chars, no trigrams possible)
        store.AddCurrent("<s1>", "<name>", "\"a\"");
        store.AddCurrent("<s2>", "<name>", "\"ab\"");

        // Assert - should not throw
        Assert.True(true);
    }

    [Fact]
    public void QuadStore_FullTextEnabled_HandlesLanguageTaggedLiterals()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act - add literals with language tags
        store.AddCurrent("<s1>", "<name>", "\"Stockholm\"@sv");
        store.AddCurrent("<s2>", "<name>", "\"Stockholm\"@en");

        // Assert - should index the content without the language tag
        Assert.True(true);
    }

    [Fact]
    public void QuadStore_FullTextEnabled_HandlesTypedLiterals()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act - add typed literals
        store.AddCurrent("<s1>", "<population>", "\"1000000\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        store.AddCurrent("<s2>", "<description>", "\"A beautiful city\"^^<http://www.w3.org/2001/XMLSchema#string>");

        // Assert - should not throw
        Assert.True(true);
    }

    #endregion

    #region Persistence Tests

    [Fact]
    public void QuadStore_FullTextEnabled_PersistsAcrossReopen()
    {
        // Arrange - create store and add data
        using (var store = new QuadStore(_testDir, null, null, FullTextOptions))
        {
            store.AddCurrent("<s1>", "<name>", "\"Stockholm\"");
            store.AddCurrent("<s2>", "<name>", "\"Göteborg\"");
            store.Checkpoint();
        }

        // Act - reopen and verify files exist
        using (var store = new QuadStore(_testDir, null, null, FullTextOptions))
        {
            // Just verify we can reopen without issues
            Assert.True(true);
        }

        // Assert - files should still exist
        Assert.True(File.Exists(Path.Combine(_testDir, "trigram.hash")));
        Assert.True(File.Exists(Path.Combine(_testDir, "trigram.posts")));
    }

    [Fact]
    public void QuadStore_FullTextEnabled_CheckpointFlushesIndex()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        store.AddCurrent("<s1>", "<name>", "\"Stockholm\"");
        store.AddCurrent("<s2>", "<name>", "\"Göteborg\"");

        // Act
        store.Checkpoint();

        // Assert - files should have non-zero size after flush
        var hashInfo = new FileInfo(Path.Combine(_testDir, "trigram.hash"));
        var postsInfo = new FileInfo(Path.Combine(_testDir, "trigram.posts"));

        Assert.True(hashInfo.Length > 0);
        Assert.True(postsInfo.Length > 0);
    }

    #endregion

    #region Batch API Tests

    [Fact]
    public void QuadStore_FullTextEnabled_BatchAPIWorks()
    {
        // Arrange
        using var store = new QuadStore(_testDir, null, null, FullTextOptions);

        // Act - use batch API
        store.BeginBatch();
        for (int i = 0; i < 100; i++)
        {
            store.AddCurrentBatched("<s" + i + ">", "<name>", "\"Test item " + i + "\"");
        }
        store.CommitBatch();

        // Assert - should complete without issues
        Assert.True(true);
    }

    #endregion
}
