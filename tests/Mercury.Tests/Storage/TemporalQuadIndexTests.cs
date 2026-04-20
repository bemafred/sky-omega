using System;
using System.IO;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for TemporalQuadIndex - B+Tree index with bitemporal semantics.
/// </summary>
public class TemporalQuadIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testPath;
    private TemporalQuadIndex? _index;

    public TemporalQuadIndexTests()
    {
        var tempPath = TempPath.Test("index");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _testPath = Path.Combine(_testDir, "test.tdb");
    }

    public void Dispose()
    {
        _index?.Dispose();
        TempPath.SafeCleanup(_testDir);
    }

    private TemporalQuadIndex CreateIndex()
    {
        _index?.Dispose();
        _index = new TemporalQuadIndex(_testPath);
        return _index;
    }

    #region Basic Add and Query

    [Fact]
    public void AddCurrent_SingleTriple_CanQuery()
    {
        var index = CreateIndex();

        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var results = index.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        Assert.True(results.MoveNext());
        Assert.False(results.MoveNext());
    }

    [Fact]
    public void AddCurrent_MultipleTriples_AllQueryable()
    {
        var index = CreateIndex();

        for (int i = 0; i < 10; i++)
        {
            index.AddCurrent($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        var count = 0;
        var results = index.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
        while (results.MoveNext()) count++;

        Assert.Equal(10, count);
    }

    [Fact]
    public void QueryCurrent_NonExistent_ReturnsEmpty()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var results = index.QueryCurrent("<http://ex.org/nonexistent>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.False(results.MoveNext());
    }

    #endregion

    #region Temporal Queries

    [Fact]
    public void AddHistorical_QueryAsOf_ReturnsCorrectly()
    {
        var index = CreateIndex();

        var validFrom = DateTimeOffset.UtcNow.AddDays(-10);
        var validTo = DateTimeOffset.UtcNow.AddDays(10);

        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        // Query as of now - should match
        var results = index.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", DateTimeOffset.UtcNow);
        Assert.True(results.MoveNext());
    }

    [Fact]
    public void AddHistorical_QueryAsOfBeforeValidFrom_ReturnsEmpty()
    {
        var index = CreateIndex();

        var validFrom = DateTimeOffset.UtcNow;
        var validTo = DateTimeOffset.UtcNow.AddDays(10);

        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        // Query as of yesterday - should not match
        var results = index.QueryAsOf(
            "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            DateTimeOffset.UtcNow.AddDays(-1));
        Assert.False(results.MoveNext());
    }

    [Fact]
    public void AddHistorical_QueryAsOfAfterValidTo_ReturnsEmpty()
    {
        var index = CreateIndex();

        var validFrom = DateTimeOffset.UtcNow.AddDays(-10);
        var validTo = DateTimeOffset.UtcNow.AddDays(-5);

        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        // Query as of now - should not match (validTo passed)
        var results = index.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", DateTimeOffset.UtcNow);
        Assert.False(results.MoveNext());
    }

    [Fact]
    public void QueryRange_OverlappingRecords_ReturnsAll()
    {
        var index = CreateIndex();

        // Add records with overlapping time ranges
        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "\"v1\"",
            DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(-10));
        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "\"v2\"",
            DateTimeOffset.UtcNow.AddDays(-15), DateTimeOffset.UtcNow.AddDays(-5));

        // Query range that overlaps both
        var results = index.QueryRange(
            "<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty,
            DateTimeOffset.UtcNow.AddDays(-25),
            DateTimeOffset.UtcNow);

        var count = 0;
        while (results.MoveNext()) count++;
        Assert.Equal(2, count);
    }

    [Fact]
    public void QueryHistory_ReturnsAllVersions()
    {
        var index = CreateIndex();

        // Add multiple versions with different time bounds
        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "\"v1\"",
            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-20));
        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "\"v2\"",
            DateTimeOffset.UtcNow.AddDays(-15), DateTimeOffset.UtcNow.AddDays(-5));
        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "\"v3\"",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

        var results = index.QueryHistory("<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
        var count = 0;
        while (results.MoveNext()) count++;
        Assert.Equal(3, count);
    }

    #endregion

    #region B+Tree Operations (Page Splits)

    [Fact]
    public void Add_ManyTriples_CausesPageSplits()
    {
        var index = CreateIndex();

        // Add enough triples to cause page splits (NodeDegree = 204)
        for (int i = 0; i < 500; i++)
        {
            index.AddCurrent($"<http://ex.org/s{i:D5}>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Verify all can still be queried
        var count = 0;
        var results = index.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
        while (results.MoveNext()) count++;

        Assert.Equal(500, count);
    }

    [Fact]
    public void Add_ManyTriples_OrderPreserved()
    {
        var index = CreateIndex();

        // Add triples in specific order
        for (int i = 0; i < 100; i++)
        {
            index.AddCurrent($"<http://ex.org/s{i:D3}>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Query all and verify order
        var results = index.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
        var lastSubject = "";
        while (results.MoveNext())
        {
            var current = index.Atoms.GetAtomString(results.Current.Primary);
            Assert.True(string.CompareOrdinal(current, lastSubject) >= 0,
                $"Out of order: {lastSubject} should come before {current}");
            lastSubject = current;
        }
    }

    #endregion

    #region Atom Store Integration

    [Fact]
    public void Atoms_Property_ReturnsSameAtomStore()
    {
        var index = CreateIndex();

        var atoms1 = index.Atoms;
        var atoms2 = index.Atoms;

        Assert.Same(atoms1, atoms2);
    }

    [Fact]
    public void Query_ReturnsAtomIds_NotStrings()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var results = index.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        Assert.True(results.MoveNext());

        var triple = results.Current;
        Assert.True(triple.Primary > 0);
        Assert.True(triple.Secondary > 0);
        Assert.True(triple.Tertiary > 0);
    }

    [Fact]
    public void AtomIds_Consistent_AcrossQueries()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var results1 = index.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        results1.MoveNext();
        var subjectId1 = results1.Current.Primary;

        var results2 = index.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        results2.MoveNext();
        var subjectId2 = results2.Current.Primary;

        Assert.Equal(subjectId1, subjectId2);
    }

    #endregion

    #region QuadCount

    [Fact]
    public void QuadCount_EmptyIndex_ReturnsZero()
    {
        var index = CreateIndex();

        Assert.Equal(0, index.QuadCount);
    }

    [Fact]
    public void QuadCount_AfterAdds_ReflectsCount()
    {
        var index = CreateIndex();

        index.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        index.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
        index.AddCurrent("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.Equal(3, index.QuadCount);
    }

    #endregion

    #region Persistence

    [Fact]
    public void Persistence_ReopenIndex_DataSurvives()
    {
        // First session
        using (var index1 = new TemporalQuadIndex(_testPath))
        {
            index1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Second session
        using (var index2 = new TemporalQuadIndex(_testPath))
        {
            var results = index2.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            Assert.True(results.MoveNext());
        }
    }

    [Fact]
    public void Persistence_QuadCountSurvives()
    {
        // First session
        using (var index1 = new TemporalQuadIndex(_testPath))
        {
            for (int i = 0; i < 10; i++)
            {
                index1.AddCurrent($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
            }
        }

        // Second session
        using (var index2 = new TemporalQuadIndex(_testPath))
        {
            Assert.Equal(10, index2.QuadCount);
        }
    }

    #endregion

    #region Shared AtomStore

    [Fact]
    public void Constructor_SharedAtomStore_UsesProvided()
    {
        var atomsPath = _testPath + ".shared.atoms";
        try
        {
            using var sharedAtoms = new AtomStore(atomsPath);
            using var index = new TemporalQuadIndex(_testPath, sharedAtoms);

            Assert.Same(sharedAtoms, index.Atoms);

            index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

            // Verify atoms were stored in shared store
            Assert.True(sharedAtoms.AtomCount > 0);
        }
        finally
        {
            foreach (var ext in new[] { ".atoms", ".atomidx", ".offsets" })
            {
                var path = atomsPath.Replace(".atoms", ext);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Add_DuplicateTriple_NoException()
    {
        var index = CreateIndex();

        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        // Implementation may allow duplicates or handle as update
        // Just verify no exception
    }

    [Fact]
    public void Add_UnicodeIRIs_Works()
    {
        var index = CreateIndex();

        index.AddCurrent("<http://example.org/日本語>", "<http://example.org/名前>", "\"テスト\"");

        var results = index.QueryCurrent("<http://example.org/日本語>", "<http://example.org/名前>", ReadOnlySpan<char>.Empty);
        Assert.True(results.MoveNext());
    }

    [Fact]
    public void Add_LargeNumberOfDistinctPredicates_Works()
    {
        var index = CreateIndex();

        // Add triples with many different predicates
        for (int i = 0; i < 100; i++)
        {
            index.AddCurrent("<http://ex.org/s>", $"<http://ex.org/p{i}>", $"<http://ex.org/o{i}>");
        }

        // Query all predicates for subject
        var results = index.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        var count = 0;
        while (results.MoveNext()) count++;

        Assert.Equal(100, count);
    }

    #endregion

    #region Temporal Triple Enumerator

    [Fact]
    public void TemporalTripleEnumerator_ValidFromValidTo_Set()
    {
        var index = CreateIndex();

        var validFrom = DateTimeOffset.UtcNow.AddDays(-5);
        var validTo = DateTimeOffset.UtcNow.AddDays(5);

        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        var results = index.QueryHistory("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        Assert.True(results.MoveNext());

        var triple = results.Current;
        Assert.True(triple.ValidFrom > 0);
        Assert.True(triple.ValidTo > triple.ValidFrom);
        Assert.True(triple.TransactionTime > 0);
    }

    [Fact]
    public void TemporalTripleEnumerator_CurrentOnlyValidDuringIteration()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var results = index.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        // Before MoveNext, Current is default
        Assert.True(results.MoveNext());

        // After MoveNext, Current has values
        var triple = results.Current;
        Assert.True(triple.Primary > 0);
    }

    #endregion

    #region Delete Operations

    [Fact]
    public void Delete_ExistingTriple_ReturnsTrue()
    {
        var index = CreateIndex();
        var validFrom = DateTimeOffset.UtcNow.AddDays(-10);
        var validTo = DateTimeOffset.UtcNow.AddDays(10);

        index.AddHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        var deleted = index.DeleteHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        Assert.True(deleted);
    }

    [Fact]
    public void Delete_NonExistentTriple_ReturnsFalse()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted = index.DeleteHistorical("<http://ex.org/other>", "<http://ex.org/p>", "<http://ex.org/o>",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.False(deleted);
    }

    [Fact]
    public void Delete_AfterDelete_NotInQueryCurrent()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        index.DeleteHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        var results = index.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        Assert.False(results.MoveNext(), "Deleted triple should not appear in QueryCurrent");
    }

    [Fact]
    public void Delete_NonExistentAtom_ReturnsFalse()
    {
        var index = CreateIndex();

        // Try to delete something that was never added
        var deleted = index.DeleteHistorical("<http://ex.org/never-added>", "<http://ex.org/p>", "<http://ex.org/o>",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.False(deleted);
    }

    [Fact]
    public void Delete_AlreadyDeleted_ReturnsFalse()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted1 = index.DeleteHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        var deleted2 = index.DeleteHistorical("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.True(deleted1);
        Assert.False(deleted2, "Deleting already-deleted should return false");
    }

    #endregion

    #region TimeFirst Sort Order (ADR-022)

    [Fact]
    public void TimeFirst_SortOrder_EnumeratesByValidFromFirst()
    {
        // Create a TimeFirst index
        var timeFirstPath = Path.Combine(_testDir, "timefirst.tdb");
        using var index = new TemporalQuadIndex(timeFirstPath, null, sortOrder: TemporalQuadIndex.KeySortOrder.TimeFirst);

        // Entry A: low entity ID, HIGH time
        var highTime = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Entry B: high entity ID, LOW time
        var lowTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Insert A first (low entity, high time)
        index.AddHistorical("<http://ex.org/aaa>", "<http://ex.org/p>", "<http://ex.org/o>",
            highTime, highTime.AddYears(1));

        // Insert B second (high entity, low time)
        index.AddHistorical("<http://ex.org/zzz>", "<http://ex.org/p>", "<http://ex.org/o>",
            lowTime, lowTime.AddYears(1));

        // Enumerate all entries — TimeFirst should yield B (lowTime) before A (highTime)
        var results = index.QueryHistory(
            ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);

        Assert.True(results.MoveNext(), "Expected first result");
        var first = results.Current;

        Assert.True(results.MoveNext(), "Expected second result");
        var second = results.Current;

        Assert.False(results.MoveNext(), "Expected only two results");

        // TimeFirst: ValidFrom should be ascending across results
        Assert.True(first.ValidFrom < second.ValidFrom,
            $"TimeFirst sort should yield lower ValidFrom first. Got {first.ValidFrom} then {second.ValidFrom}");

        // The first result should be the zzz entry (low time) and second should be aaa (high time)
        var firstPrimary = index.Atoms.GetAtomString(first.Primary);
        var secondPrimary = index.Atoms.GetAtomString(second.Primary);
        Assert.Equal("<http://ex.org/zzz>", firstPrimary);
        Assert.Equal("<http://ex.org/aaa>", secondPrimary);
    }

    [Fact]
    public void EntityFirst_SortOrder_EnumeratesByEntityFirst()
    {
        // Create an EntityFirst index for comparison
        var entityFirstPath = Path.Combine(_testDir, "entityfirst.tdb");
        using var index = new TemporalQuadIndex(entityFirstPath, null, sortOrder: TemporalQuadIndex.KeySortOrder.EntityFirst);

        // Same data as above
        var highTime = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lowTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        index.AddHistorical("<http://ex.org/aaa>", "<http://ex.org/p>", "<http://ex.org/o>",
            highTime, highTime.AddYears(1));
        index.AddHistorical("<http://ex.org/zzz>", "<http://ex.org/p>", "<http://ex.org/o>",
            lowTime, lowTime.AddYears(1));

        // Enumerate — EntityFirst should yield aaa before zzz (entity order)
        var results = index.QueryHistory(
            ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);

        Assert.True(results.MoveNext());
        var firstPrimary = index.Atoms.GetAtomString(results.Current.Primary);

        Assert.True(results.MoveNext());
        var secondPrimary = index.Atoms.GetAtomString(results.Current.Primary);

        Assert.Equal("<http://ex.org/aaa>", firstPrimary);
        Assert.Equal("<http://ex.org/zzz>", secondPrimary);
    }

#if DEBUG
    [Fact]
    public void TimeFirst_TemporalRangeQuery_VisitsFewerPages()
    {
        // Create two indexes with the same data: one EntityFirst, one TimeFirst
        var entityPath = Path.Combine(_testDir, "pages_entity.tdb");
        var timePath = Path.Combine(_testDir, "pages_time.tdb");
        using var entityIndex = new TemporalQuadIndex(entityPath, null, sortOrder: TemporalQuadIndex.KeySortOrder.EntityFirst);
        using var timeIndex = new TemporalQuadIndex(timePath, null, sortOrder: TemporalQuadIndex.KeySortOrder.TimeFirst);

        // Insert 5000 entries spread across 10 years (2020-2030)
        // ~27 leaf pages (5000 / 185 entries per page)
        var baseTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 5000; i++)
        {
            var validFrom = baseTime.AddHours(i * 18); // ~18-hour intervals over 10 years
            var validTo = validFrom.AddDays(7);         // each valid for 7 days

            entityIndex.AddHistorical(
                $"<http://ex.org/s{i:D5}>", "<http://ex.org/p>", "<http://ex.org/o>",
                validFrom, validTo);
            timeIndex.AddHistorical(
                $"<http://ex.org/s{i:D5}>", "<http://ex.org/p>", "<http://ex.org/o>",
                validFrom, validTo);
        }

        // Query a narrow window near the START: [2020-03-01, 2020-06-01]
        // With maxKey.ValidFrom = rangeEnd, the TimeFirst index stops scanning
        // after ValidFrom passes 2020-06-01 (~5% of the 10-year range).
        // EntityFirst has maxKey.ValidFrom = MAX and must scan all leaf pages.
        var rangeStart = new DateTimeOffset(2020, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // Reset counters before querying
        entityIndex.ResetPageAccessCount();
        timeIndex.ResetPageAccessCount();

        // Query EntityFirst index
        var entityResults = entityIndex.QueryRange(
            ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty,
            rangeStart, rangeEnd);
        var entityCount = 0;
        while (entityResults.MoveNext()) entityCount++;
        var entityPages = entityIndex.PageAccessCount;

        // Query TimeFirst index
        var timeResults = timeIndex.QueryRange(
            ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty,
            rangeStart, rangeEnd);
        var timeCount = 0;
        while (timeResults.MoveNext()) timeCount++;
        var timePages = timeIndex.PageAccessCount;

        // Both must return the same results
        Assert.Equal(entityCount, timeCount);
        Assert.True(entityCount > 0, "Query should match some entries");

        // TimeFirst should visit fewer pages than EntityFirst
        Assert.True(timePages < entityPages,
            $"TimeFirst should visit fewer pages than EntityFirst. " +
            $"TimeFirst: {timePages}, EntityFirst: {entityPages} " +
            $"(matched {entityCount} entries out of 5000)");
    }
#endif

    #endregion
}
