using System;
using System.IO;
using System.Linq;
using System.Text;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-034 Phase 1B-1 + 1B-2: SortedAtomStore read-side correctness against vocabularies
/// produced by SortedAtomStoreBuilder. Verifies binary-search lookup, dense ID assignment,
/// round-trip GetAtomSpan/GetAtomIdUtf8, and the read-only contract (write methods throw).
/// </summary>
public class SortedAtomStoreTests : IDisposable
{
    private readonly string _testDir;

    public SortedAtomStoreTests()
    {
        var tempPath = TempPath.Test("sorted_atom_store");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void Build_Empty_ProducesZeroAtomStore()
    {
        var basePath = Path.Combine(_testDir, "empty");
        var result = SortedAtomStoreBuilder.Build(basePath, Array.Empty<string>());
        Assert.Equal(0, result.AtomCount);
        Assert.Equal(0, result.DataBytes);

        using var store = new SortedAtomStore(basePath);
        Assert.Equal(0, store.AtomCount);
        Assert.Equal(0, store.GetAtomIdUtf8(Encoding.UTF8.GetBytes("anything")));
        Assert.True(store.GetAtomSpan(1).IsEmpty);
    }

    [Fact]
    public void Build_SingleAtom_RoundTrips()
    {
        var basePath = Path.Combine(_testDir, "single");
        var result = SortedAtomStoreBuilder.Build(basePath, new[] { "hello" });
        Assert.Equal(1, result.AtomCount);
        Assert.Equal(5, result.DataBytes);
        Assert.Equal(new long[] { 1 }, result.AssignedIds);

        using var store = new SortedAtomStore(basePath);
        Assert.Equal(1, store.AtomCount);
        Assert.Equal("hello", store.GetAtomString(1));
        Assert.Equal(1, store.GetAtomId("hello"));
        Assert.Equal(0, store.GetAtomId("world"));  // not present
        Assert.True(store.GetAtomSpan(2).IsEmpty);  // out of range
        Assert.True(store.GetAtomSpan(0).IsEmpty);  // sentinel
    }

    [Fact]
    public void Build_MultipleAtoms_AssignsDenseSortedIds()
    {
        // Input order is jumbled; sorted byte order is "alpha", "bravo", "charlie".
        var basePath = Path.Combine(_testDir, "multi");
        var inputs = new[] { "charlie", "alpha", "bravo" };
        var result = SortedAtomStoreBuilder.Build(basePath, inputs);

        Assert.Equal(3, result.AtomCount);
        // AssignedIds is in INPUT order — input[0]="charlie" got ID 3 (last in sort),
        // input[1]="alpha" got ID 1 (first in sort), input[2]="bravo" got ID 2.
        Assert.Equal(new long[] { 3, 1, 2 }, result.AssignedIds);

        using var store = new SortedAtomStore(basePath);
        Assert.Equal(3, store.AtomCount);
        Assert.Equal("alpha", store.GetAtomString(1));
        Assert.Equal("bravo", store.GetAtomString(2));
        Assert.Equal("charlie", store.GetAtomString(3));

        // Reverse lookup uses binary search.
        Assert.Equal(1, store.GetAtomId("alpha"));
        Assert.Equal(2, store.GetAtomId("bravo"));
        Assert.Equal(3, store.GetAtomId("charlie"));
        Assert.Equal(0, store.GetAtomId("delta"));  // not present
    }

    [Fact]
    public void Build_DuplicatesDeduped()
    {
        var basePath = Path.Combine(_testDir, "dedup");
        var inputs = new[] { "a", "b", "a", "c", "b", "a" };
        var result = SortedAtomStoreBuilder.Build(basePath, inputs);

        Assert.Equal(3, result.AtomCount);
        // Same input string maps to the same atom ID across all occurrences.
        Assert.Equal(new long[] { 1, 2, 1, 3, 2, 1 }, result.AssignedIds);
    }

    [Fact]
    public void Build_LargeAlphabet_BinarySearchCorrect()
    {
        // 1000 distinct strings, jumbled order. Round-trip every one via binary search.
        var basePath = Path.Combine(_testDir, "large");
        var rng = new Random(7);
        var generated = new string[1000];
        for (int i = 0; i < generated.Length; i++)
            generated[i] = $"http://wikidata.org/entity/Q{rng.Next(0, 100_000_000)}";

        var result = SortedAtomStoreBuilder.Build(basePath, generated);
        Assert.True(result.AtomCount <= 1000);  // dedup may reduce
        Assert.True(result.AtomCount > 990);   // 1000 random Q-numbers, very low collision

        using var store = new SortedAtomStore(basePath);
        // Every input string round-trips to its assigned ID.
        for (int i = 0; i < generated.Length; i++)
        {
            long expected = result.AssignedIds[i];
            long actual = store.GetAtomId(generated[i]);
            Assert.Equal(expected, actual);
            Assert.Equal(generated[i], store.GetAtomString(actual));
        }
    }

    [Fact]
    public void Intern_OnExistingAtom_ReturnsId()
    {
        var basePath = Path.Combine(_testDir, "intern_existing");
        SortedAtomStoreBuilder.Build(basePath, new[] { "alpha", "bravo" });
        using var store = new SortedAtomStore(basePath);

        Assert.Equal(1, store.Intern("alpha"));
        Assert.Equal(2, store.Intern("bravo"));
    }

    [Fact]
    public void Intern_OnNewAtom_ThrowsNotSupported()
    {
        var basePath = Path.Combine(_testDir, "intern_new");
        SortedAtomStoreBuilder.Build(basePath, new[] { "alpha" });
        using var store = new SortedAtomStore(basePath);
        Assert.Throws<NotSupportedException>(() => store.Intern("bravo"));
    }

    [Fact]
    public void Clear_ThrowsNotSupported()
    {
        var basePath = Path.Combine(_testDir, "clear");
        SortedAtomStoreBuilder.Build(basePath, new[] { "x" });
        using var store = new SortedAtomStore(basePath);
        Assert.Throws<NotSupportedException>(() => store.Clear());
    }

    [Fact]
    public void OpenMissingFiles_Throws()
    {
        var basePath = Path.Combine(_testDir, "missing");
        Assert.Throws<FileNotFoundException>(() => new SortedAtomStore(basePath));
    }

    [Fact]
    public void Statistics_ReportCounts()
    {
        var basePath = Path.Combine(_testDir, "stats");
        SortedAtomStoreBuilder.Build(basePath, new[] { "abc", "de", "fghij" });
        using var store = new SortedAtomStore(basePath);
        var (count, bytes, avg) = store.GetStatistics();
        Assert.Equal(3, count);
        Assert.Equal(10, bytes);  // "abc" + "de" + "fghij" = 3 + 2 + 5 = 10
        Assert.Equal(10.0 / 3, avg);
    }

    [Fact]
    public void BinarySearch_OnLongCommonPrefixes_DistinguishesCorrectly()
    {
        // Wikidata-style URIs with long common prefixes — exercises the SequenceCompareTo
        // in the binary-search probe.
        var basePath = Path.Combine(_testDir, "prefix");
        var inputs = new[]
        {
            "http://www.wikidata.org/entity/Q1",
            "http://www.wikidata.org/entity/Q10",
            "http://www.wikidata.org/entity/Q100",
            "http://www.wikidata.org/entity/Q1000",
            "http://www.wikidata.org/entity/Q11",
            "http://www.wikidata.org/entity/Q2",
        };
        SortedAtomStoreBuilder.Build(basePath, inputs);
        using var store = new SortedAtomStore(basePath);

        // Lexicographic byte order: "Q1" < "Q10" < "Q100" < "Q1000" < "Q11" < "Q2"
        Assert.Equal("http://www.wikidata.org/entity/Q1", store.GetAtomString(1));
        Assert.Equal("http://www.wikidata.org/entity/Q10", store.GetAtomString(2));
        Assert.Equal("http://www.wikidata.org/entity/Q100", store.GetAtomString(3));
        Assert.Equal("http://www.wikidata.org/entity/Q1000", store.GetAtomString(4));
        Assert.Equal("http://www.wikidata.org/entity/Q11", store.GetAtomString(5));
        Assert.Equal("http://www.wikidata.org/entity/Q2", store.GetAtomString(6));

        foreach (var s in inputs)
        {
            long id = store.GetAtomId(s);
            Assert.True(id > 0, $"binary search failed for: {s}");
            Assert.Equal(s, store.GetAtomString(id));
        }
    }
}
