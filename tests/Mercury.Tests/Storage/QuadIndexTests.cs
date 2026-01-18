using System;
using System.IO;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for QuadIndex - B+Tree index with bitemporal semantics.
/// </summary>
public class QuadIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testPath;
    private QuadIndex? _index;

    public QuadIndexTests()
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

    private QuadIndex CreateIndex()
    {
        _index?.Dispose();
        _index = new QuadIndex(_testPath);
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
            var current = index.Atoms.GetAtomString(results.Current.SubjectAtom);
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
        Assert.True(triple.SubjectAtom > 0);
        Assert.True(triple.PredicateAtom > 0);
        Assert.True(triple.ObjectAtom > 0);
    }

    [Fact]
    public void AtomIds_Consistent_AcrossQueries()
    {
        var index = CreateIndex();
        index.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var results1 = index.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        results1.MoveNext();
        var subjectId1 = results1.Current.SubjectAtom;

        var results2 = index.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        results2.MoveNext();
        var subjectId2 = results2.Current.SubjectAtom;

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
        using (var index1 = new QuadIndex(_testPath))
        {
            index1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Second session
        using (var index2 = new QuadIndex(_testPath))
        {
            var results = index2.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            Assert.True(results.MoveNext());
        }
    }

    [Fact]
    public void Persistence_QuadCountSurvives()
    {
        // First session
        using (var index1 = new QuadIndex(_testPath))
        {
            for (int i = 0; i < 10; i++)
            {
                index1.AddCurrent($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
            }
        }

        // Second session
        using (var index2 = new QuadIndex(_testPath))
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
            using var index = new QuadIndex(_testPath, sharedAtoms);

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
        Assert.True(triple.SubjectAtom > 0);
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
}
