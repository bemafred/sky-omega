using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for AtomStore - string interning with memory-mapped storage.
/// </summary>
public class AtomStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testPath;
    private AtomStore? _store;

    public AtomStoreTests()
    {
        var tempPath = TempPath.Test("atom");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _testPath = Path.Combine(_testDir, "atoms");
    }

    public void Dispose()
    {
        _store?.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private AtomStore CreateStore()
    {
        _store?.Dispose();
        _store = new AtomStore(_testPath);
        return _store;
    }

    #region Basic Interning

    [Fact]
    public void Intern_EmptyString_ReturnsZero()
    {
        var store = CreateStore();

        var id = store.Intern(ReadOnlySpan<char>.Empty);

        Assert.Equal(0, id);
    }

    [Fact]
    public void Intern_SimpleString_ReturnsPositiveId()
    {
        var store = CreateStore();

        var id = store.Intern("hello");

        Assert.True(id > 0);
    }

    [Fact]
    public void Intern_SameStringTwice_ReturnsSameId()
    {
        var store = CreateStore();

        var id1 = store.Intern("hello");
        var id2 = store.Intern("hello");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Intern_DifferentStrings_ReturnsDifferentIds()
    {
        var store = CreateStore();

        var id1 = store.Intern("hello");
        var id2 = store.Intern("world");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Intern_ManyStrings_AllUnique()
    {
        var store = CreateStore();
        var ids = new HashSet<long>();

        for (int i = 0; i < 1000; i++)
        {
            var id = store.Intern($"string_{i}");
            Assert.True(ids.Add(id), $"Duplicate ID for string_{i}");
        }

        Assert.Equal(1000, ids.Count);
    }

    #endregion

    #region UTF-8 Handling

    [Fact]
    public void Intern_UnicodeString_PreservesContent()
    {
        var store = CreateStore();
        var unicode = "こんにちは世界 🌍 émojis";

        var id = store.Intern(unicode);
        var retrieved = store.GetAtomString(id);

        Assert.Equal(unicode, retrieved);
    }

    [Fact]
    public void Intern_AsciiString_PreservesContent()
    {
        var store = CreateStore();
        var ascii = "Hello, World!";

        var id = store.Intern(ascii);
        var retrieved = store.GetAtomString(id);

        Assert.Equal(ascii, retrieved);
    }

    [Fact]
    public void InternUtf8_DirectUtf8Bytes_Works()
    {
        var store = CreateStore();
        var text = "hello";
        var utf8 = Encoding.UTF8.GetBytes(text);

        var id = store.InternUtf8(utf8);
        var retrieved = store.GetAtomString(id);

        Assert.Equal(text, retrieved);
    }

    [Fact]
    public void Intern_LongString_Works()
    {
        var store = CreateStore();
        var longString = new string('x', 10_000);

        var id = store.Intern(longString);
        var retrieved = store.GetAtomString(id);

        Assert.Equal(longString, retrieved);
    }

    #endregion

    #region GetAtomId (Lookup Without Interning)

    [Fact]
    public void GetAtomId_ExistingString_ReturnsId()
    {
        var store = CreateStore();
        var internedId = store.Intern("existing");

        var lookupId = store.GetAtomId("existing");

        Assert.Equal(internedId, lookupId);
    }

    [Fact]
    public void GetAtomId_NonExistingString_ReturnsZero()
    {
        var store = CreateStore();
        store.Intern("other");

        var lookupId = store.GetAtomId("nonexistent");

        Assert.Equal(0, lookupId);
    }

    [Fact]
    public void GetAtomId_EmptyString_ReturnsZero()
    {
        var store = CreateStore();

        var id = store.GetAtomId(ReadOnlySpan<char>.Empty);

        Assert.Equal(0, id);
    }

    [Fact]
    public void GetAtomIdUtf8_ExistingBytes_ReturnsId()
    {
        var store = CreateStore();
        var text = "test";
        var utf8 = Encoding.UTF8.GetBytes(text);
        var internedId = store.InternUtf8(utf8);

        var lookupId = store.GetAtomIdUtf8(utf8);

        Assert.Equal(internedId, lookupId);
    }

    #endregion

    #region GetAtomSpan / GetAtomString

    [Fact]
    public void GetAtomSpan_ValidId_ReturnsUtf8Bytes()
    {
        var store = CreateStore();
        var text = "hello";
        var id = store.Intern(text);

        var span = store.GetAtomSpan(id);

        Assert.Equal(text, Encoding.UTF8.GetString(span));
    }

    [Fact]
    public void GetAtomSpan_InvalidId_ReturnsEmpty()
    {
        var store = CreateStore();

        var span = store.GetAtomSpan(999);

        Assert.True(span.IsEmpty);
    }

    [Fact]
    public void GetAtomSpan_ZeroId_ReturnsEmpty()
    {
        var store = CreateStore();

        var span = store.GetAtomSpan(0);

        Assert.True(span.IsEmpty);
    }

    [Fact]
    public void GetAtomSpan_NegativeId_ReturnsEmpty()
    {
        var store = CreateStore();

        var span = store.GetAtomSpan(-1);

        Assert.True(span.IsEmpty);
    }

    [Fact]
    public void GetAtomString_ValidId_ReturnsString()
    {
        var store = CreateStore();
        var text = "test string";
        var id = store.Intern(text);

        var result = store.GetAtomString(id);

        Assert.Equal(text, result);
    }

    [Fact]
    public void GetAtomString_InvalidId_ReturnsEmptyString()
    {
        var store = CreateStore();

        var result = store.GetAtomString(999);

        Assert.Equal(string.Empty, result);
    }

    #endregion

    #region Statistics

    [Fact]
    public void GetStatistics_EmptyStore_ReturnsZero()
    {
        var store = CreateStore();

        var (atomCount, totalBytes, avgLength) = store.GetStatistics();

        Assert.Equal(0, atomCount);
        Assert.Equal(0, totalBytes);
        Assert.Equal(0, avgLength);
    }

    [Fact]
    public void GetStatistics_AfterInterning_ReflectsCount()
    {
        var store = CreateStore();
        store.Intern("one");
        store.Intern("two");
        store.Intern("three");

        var (atomCount, _, _) = store.GetStatistics();

        Assert.Equal(3, atomCount);
    }

    [Fact]
    public void AtomCount_Property_MatchesStatistics()
    {
        var store = CreateStore();
        store.Intern("a");
        store.Intern("b");

        Assert.Equal(2, store.AtomCount);
    }

    #endregion

    #region Persistence

    [Fact]
    public void Persistence_ReopenStore_DataSurvives()
    {
        long id1, id2;

        // First session: create atoms
        using (var store1 = new AtomStore(_testPath))
        {
            id1 = store1.Intern("persistent1");
            id2 = store1.Intern("persistent2");
            store1.Flush();
        }

        // Second session: read atoms
        using (var store2 = new AtomStore(_testPath))
        {
            Assert.Equal("persistent1", store2.GetAtomString(id1));
            Assert.Equal("persistent2", store2.GetAtomString(id2));
        }
    }

    [Fact]
    public void Persistence_ReopenStore_DeduplicationWorks()
    {
        long originalId;

        using (var store1 = new AtomStore(_testPath))
        {
            originalId = store1.Intern("dedup_test");
            store1.Flush();
        }

        using (var store2 = new AtomStore(_testPath))
        {
            var newId = store2.Intern("dedup_test");
            Assert.Equal(originalId, newId);
        }
    }

    [Fact]
    public void Persistence_AtomCountSurvivesReopen()
    {
        using (var store1 = new AtomStore(_testPath))
        {
            store1.Intern("one");
            store1.Intern("two");
            store1.Intern("three");
            store1.Flush();
        }

        using (var store2 = new AtomStore(_testPath))
        {
            Assert.Equal(3, store2.AtomCount);
        }
    }

    #endregion

    #region Hash Collisions

    [Fact]
    public void Intern_ManyStringsWithSimilarPrefix_HandlesCollisions()
    {
        var store = CreateStore();
        var ids = new Dictionary<string, long>();

        // Generate strings that might have hash collisions
        for (int i = 0; i < 500; i++)
        {
            var str = $"prefix_{i:D5}";
            ids[str] = store.Intern(str);
        }

        // Verify all can be retrieved correctly
        foreach (var (str, id) in ids)
        {
            Assert.Equal(str, store.GetAtomString(id));
        }
    }

    [Fact]
    public void Intern_StringsWithVariousLengths_AllRetrievable()
    {
        var store = CreateStore();
        var lengths = new[] { 1, 2, 5, 10, 50, 100, 500, 1000 };
        var ids = new Dictionary<int, long>();

        foreach (var len in lengths)
        {
            var str = new string('a', len);
            ids[len] = store.Intern(str);
        }

        foreach (var len in lengths)
        {
            var retrieved = store.GetAtomString(ids[len]);
            Assert.Equal(len, retrieved.Length);
            Assert.True(retrieved.All(c => c == 'a'));
        }
    }

    #endregion

    #region Quadratic Probing and High-Collision Scenarios

    [Fact]
    public void Intern_HighCollisionLoad_HandlesGracefully()
    {
        var store = CreateStore();
        var ids = new Dictionary<string, long>();

        // Intern 10,000 strings with similar patterns to stress collision handling
        for (int i = 0; i < 10_000; i++)
        {
            var str = $"collision_test_{i}";
            ids[str] = store.Intern(str);
        }

        // Verify all can be retrieved
        foreach (var (str, id) in ids)
        {
            Assert.Equal(str, store.GetAtomString(id));
        }

        // Verify deduplication still works
        foreach (var (str, expectedId) in ids)
        {
            var lookupId = store.Intern(str);
            Assert.Equal(expectedId, lookupId);
        }
    }

    [Fact]
    public void Intern_SameLength_DifferentContent_AllUnique()
    {
        var store = CreateStore();
        var ids = new HashSet<long>();

        // Same-length strings may have similar hash patterns
        for (int i = 0; i < 1000; i++)
        {
            // All strings are exactly 20 chars
            var str = $"test{i:D16}";
            var id = store.Intern(str);
            Assert.True(ids.Add(id), $"Duplicate ID for {str}");
        }

        Assert.Equal(1000, ids.Count);
    }

    [Fact]
    public void GetAtomId_AfterManyInserts_StillFindsEntries()
    {
        var store = CreateStore();
        var testStrings = new List<(string str, long id)>();

        // Insert many strings
        for (int i = 0; i < 5000; i++)
        {
            var str = $"search_test_{i}";
            var id = store.Intern(str);
            testStrings.Add((str, id));
        }

        // Verify lookup still works for all entries
        foreach (var (str, expectedId) in testStrings)
        {
            var foundId = store.GetAtomId(str);
            Assert.Equal(expectedId, foundId);
        }
    }

    [Fact]
    public void Intern_VeryLongStrings_MultipleInserts_Works()
    {
        var store = CreateStore();
        var ids = new Dictionary<string, long>();

        // Long strings with small variations
        for (int i = 0; i < 100; i++)
        {
            var str = new string('x', 5000) + i.ToString();
            ids[str] = store.Intern(str);
        }

        foreach (var (str, id) in ids)
        {
            Assert.Equal(str, store.GetAtomString(id));
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Intern_StringWithNullCharacters_Works()
    {
        var store = CreateStore();
        var str = "before\0after";

        var id = store.Intern(str);
        var retrieved = store.GetAtomString(id);

        Assert.Equal(str, retrieved);
    }

    [Fact]
    public void Intern_WhitespaceOnlyString_Works()
    {
        var store = CreateStore();
        var str = "   \t\n";

        var id = store.Intern(str);
        var retrieved = store.GetAtomString(id);

        Assert.Equal(str, retrieved);
    }

    [Fact]
    public void Intern_SingleCharacter_Works()
    {
        var store = CreateStore();

        var id = store.Intern("x");
        var retrieved = store.GetAtomString(id);

        Assert.Equal("x", retrieved);
    }

    #endregion

    #region Concurrent Access (Single Thread Simulation)

    [Fact]
    public void Intern_RapidSequentialCalls_NoDataCorruption()
    {
        var store = CreateStore();

        // Rapidly alternate between interning and reading
        for (int i = 0; i < 1000; i++)
        {
            var str = $"rapid_{i}";
            var id = store.Intern(str);
            var retrieved = store.GetAtomString(id);
            Assert.Equal(str, retrieved);
        }
    }

    #endregion
}
