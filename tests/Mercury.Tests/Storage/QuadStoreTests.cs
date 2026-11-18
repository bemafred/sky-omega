using System;
using System.IO;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for QuadStore - multi-index RDF store with WAL durability.
/// </summary>
public class QuadStoreTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public QuadStoreTests()
    {
        var tempPath = TempPath.Test("store");
        tempPath.MarkOwnership();
        _testPath = tempPath;
    }

    public void Dispose()
    {
        _store?.Dispose();
        CleanupDirectory();
    }

    private void CleanupDirectory()
    {
        TempPath.SafeCleanup(_testPath);
    }

    private QuadStore CreateStore()
    {
        _store?.Dispose();
        _store = new QuadStore(_testPath);
        return _store;
    }

    #region Basic Add and Query

    [Fact]
    public void AddCurrent_SingleTriple_CanQuery()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/s>", results.Current.Subject.ToString());
                Assert.Equal("<http://ex.org/p>", results.Current.Predicate.ToString());
                Assert.Equal("<http://ex.org/o>", results.Current.Object.ToString());
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_MultipleTriples_AllQueryable()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/type>", "<http://ex.org/Person>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/type>", "<http://ex.org/Person>");
        store.AddCurrent("<http://ex.org/s3>", "<http://ex.org/type>", "<http://ex.org/Organization>");

        store.AcquireReadLock();
        try
        {
            // Query all triples with type predicate
            var results = store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                "<http://ex.org/type>",
                ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(3, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_NonExistent_ReturnsEmpty()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/nonexistent>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Query with Unbound Variables

    [Fact]
    public void QueryCurrent_UnboundSubject_MatchesAll()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_UnboundPredicate_MatchesAll()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p1>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p2>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_AllUnbound_ReturnsAllTriples()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p1>", "<http://ex.org/o1>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p2>", "<http://ex.org/o2>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Batch Operations

    [Fact]
    public void BeginBatch_CommitBatch_AllTriplesAdded()
    {
        var store = CreateStore();

        store.BeginBatch();
        for (int i = 0; i < 100; i++)
        {
            store.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
        }
        store.CommitBatch();

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(100, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void IsBatchActive_ReflectsState()
    {
        var store = CreateStore();

        Assert.False(store.IsBatchActive);

        store.BeginBatch();
        Assert.True(store.IsBatchActive);

        store.CommitBatch();
        Assert.False(store.IsBatchActive);
    }

    [Fact]
    public void BeginBatch_WhileActive_ThrowsLockRecursion()
    {
        var store = CreateStore();

        store.BeginBatch();
        try
        {
            // Attempting to begin a second batch while one is active throws LockRecursionException
            // because we try to acquire write lock twice (NoRecursion policy)
            Assert.Throws<System.Threading.LockRecursionException>(() => store.BeginBatch());
        }
        finally
        {
            store.RollbackBatch();
        }
    }

    [Fact]
    public void CommitBatch_WithoutBegin_ThrowsSynchronizationLock()
    {
        var store = CreateStore();

        // Trying to commit without beginning throws because we try to release a lock we don't hold
        Assert.Throws<System.Threading.SynchronizationLockException>(() => store.CommitBatch());
    }

    [Fact]
    public void AddBatched_WithoutBegin_Throws()
    {
        var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() =>
            store.AddCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"));
    }

    [Fact]
    public void RollbackBatch_ReleasesLock()
    {
        var store = CreateStore();

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.RollbackBatch();

        Assert.False(store.IsBatchActive);

        // Should be able to write again
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
    }

    #endregion

    #region Statistics

    [Fact]
    public void GetStatistics_EmptyStore_ReturnsZero()
    {
        var store = CreateStore();

        var (tripleCount, _, _) = store.GetStatistics();

        Assert.Equal(0, tripleCount);
    }

    [Fact]
    public void GetStatistics_AfterAdds_ReflectsCount()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

        var (tripleCount, _, _) = store.GetStatistics();

        // Each AddCurrent adds to 4 indexes, but TripleCount comes from SPOT index
        Assert.Equal(2, tripleCount);
    }

    [Fact]
    public void GetWalStatistics_ReturnsValidData()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var (currentTxId, _, _) = store.GetWalStatistics();

        Assert.True(currentTxId > 0);
    }

    #endregion

    #region Checkpoint

    [Fact]
    public void Checkpoint_ManualCall_UpdatesWalStats()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        var (_, lastCheckpointBefore, _) = store.GetWalStatistics();

        store.Checkpoint();

        var (currentTxId, lastCheckpointAfter, _) = store.GetWalStatistics();

        Assert.Equal(currentTxId, lastCheckpointAfter);
    }

    #endregion

    #region Persistence and Recovery

    [Fact]
    public void Persistence_ReopenStore_DataSurvives()
    {
        // First session
        using (var store1 = new QuadStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Second session
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
                try
                {
                    Assert.True(results.MoveNext());
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void Persistence_BatchDataSurvives()
    {
        // First session with batch
        using (var store1 = new QuadStore(_testPath))
        {
            store1.BeginBatch();
            for (int i = 0; i < 50; i++)
            {
                store1.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
            }
            store1.CommitBatch();
        }

        // Second session
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
                try
                {
                    var count = 0;
                    while (results.MoveNext()) count++;
                    Assert.Equal(50, count);
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    #endregion

    #region Locking

    [Fact]
    public void AcquireReadLock_ReleaseReadLock_WorksCorrectly()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        // Should be able to query while holding read lock
        var results = store.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        var hasResult = results.MoveNext();
        results.Dispose();
        store.ReleaseReadLock();

        Assert.True(hasResult);

        // Should be able to write after releasing read lock
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_MultipleCalls_NoException()
    {
        var store = CreateStore();
        store.Dispose();
        store.Dispose(); // Should not throw
    }

    [Fact]
    public void AfterDispose_OperationsThrow()
    {
        var store = CreateStore();
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"));
    }

    #endregion

    #region Unicode and Special Characters

    [Fact]
    public void Add_UnicodeContent_PreservedCorrectly()
    {
        var store = CreateStore();
        var unicode = "\"こんにちは世界 🌍\"@ja";

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/label>", unicode);

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/label>", ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal(unicode, results.Current.Object.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Add_LongIRI_Works()
    {
        var store = CreateStore();
        var longIri = "<http://example.org/" + new string('x', 1000) + ">";

        store.AddCurrent(longIri, "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(longIri, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal(longIri, results.Current.Subject.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Delete Operations

    [Fact]
    public void DeleteCurrent_ExistingTriple_ReturnsTrue()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted = store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.True(deleted);
    }

    [Fact]
    public void DeleteCurrent_NonExistentTriple_ReturnsFalse()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted = store.DeleteCurrent("<http://ex.org/other>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.False(deleted);
    }

    [Fact]
    public void DeleteCurrent_AfterDelete_NotQueryable()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Deleted triple should not be queryable");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteBatched_InBatch_Works()
    {
        var store = CreateStore();

        // First add some triples
        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.CommitBatch();

        // Now delete in a batch
        store.BeginBatch();
        store.DeleteCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.CommitBatch();

        // Verify s1 and s3 remain, s2 is deleted
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteBatched_WithoutBatch_Throws()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.Throws<InvalidOperationException>(() =>
            store.DeleteCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"));
    }

    [Fact]
    public void Delete_Recovery_DeletedStatePreserved()
    {
        // First session: add and delete
        using (var store1 = new QuadStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            store1.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Second session: verify still deleted
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
                try
                {
                    Assert.False(results.MoveNext(), "Deleted triple should not survive recovery");
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void Delete_NonExistentAtom_ReturnsFalse()
    {
        var store = CreateStore();

        // Try to delete something that was never added (atoms don't exist)
        var deleted = store.DeleteCurrent("<http://ex.org/never-added>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.False(deleted);
    }

    [Fact]
    public void Delete_AlreadyDeleted_ReturnsFalse()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted1 = store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        var deleted2 = store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.True(deleted1);
        Assert.False(deleted2, "Deleting already-deleted triple should return false");
    }

    #endregion

    #region QueryHistory / Audit Trail

    [Fact]
    public void QueryMemoriesIncludingForgotten_ADeletedTriple_IsVisibleWithBothTimes()
    {
        // ADR-054 D1: a delete ends the version (ForgottenAt = the delete's time); it is not a retraction.
        // The fact is memorized in one clock reading and forgotten in a later one. In one reading it would leave no
        // period (ADR-061 M2), so the test supplies the readings.
        var memorized = new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var forgotten = memorized.AddMinutes(10);
        var clock = new SuppliedClock { Reading = memorized };
        _store?.Dispose();
        _store = new QuadStore(_testPath, null, null, new StorageOptions { Clock = clock });
        var store = _store;
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        clock.Reading = forgotten;
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryMemories("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", includingForgotten: true);
            try
            {
                Assert.True(results.MoveNext(), "Deleted triple should be visible with its forgotten memories");
                Assert.Equal(memorized, results.Current.MemorizedAt);
                Assert.Equal(forgotten, results.Current.ForgottenAt);
                Assert.False(results.MoveNext(), "The delete ends the version; it adds none");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryMemoriesIncludingForgotten_MixedDeletedAndActive_ShowsAll()
    {
        // Three facts are memorized in one clock reading, and one of them is forgotten in a later one. Forgotten in
        // the reading it was memorized in, it would leave no period (ADR-061 M2), so the test supplies the readings.
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero) };
        _store?.Dispose();
        _store = new QuadStore(_testPath, null, null, new StorageOptions { Clock = clock });
        var store = _store;

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o>");
        clock.Reading = clock.Reading.AddMinutes(10);
        store.DeleteCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            // Query all with predicate p
            var results = store.QueryMemories(default, "<http://ex.org/p>", default, includingForgotten: true);
            try
            {
                var count = 0;
                var endedCount = 0;
                while (results.MoveNext())
                {
                    count++;
                    if (results.Current.ForgottenAt is not null)
                        endedCount++;
                }

                Assert.Equal(3, count); // All three visible with the forgotten memories
                Assert.Equal(1, endedCount); // The deleted one has a forgotten time (ADR-054 D1, ADR-061 D2)
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_DeletedTriple_NotVisible()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Deleted triple should not be visible in QueryCurrent");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void WhatWasRemembered_AfterTheDelete_DoesNotHoldTheDeletedTriple()
    {
        // ADR-054 D1: the delete ends the triple's period at the reading of the delete. What was remembered at a time t
        // is two reads (ADR-061 Q10): what was memorized before t + 1 ms and is remembered, and what was memorized
        // before it and forgotten since it. The test supplies the readings.
        var memorized = new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var clock = new SuppliedClock { Reading = memorized };
        _store?.Dispose();
        _store = new QuadStore(_testPath, null, null, new StorageOptions { Clock = clock });
        var store = _store;
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        clock.Reading = memorized.AddMinutes(10);
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        var justAfter = memorized.AddMinutes(10).AddMilliseconds(1);

        store.AcquireReadLock();
        try
        {
            var remembered = store.QueryMemories("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", memorizedBefore: justAfter);
            Assert.False(remembered.MoveNext(), "It is not remembered");
            remembered.Dispose();
            var forgottenSince = store.QueryMemories("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                memorizedBefore: justAfter, forgottenSince: justAfter);
            Assert.False(forgottenSince.MoveNext(), "and it was forgotten before that time, not since it");
            forgottenSince.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryMemories_ARememberedTriple_HasNoForgottenTime()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryMemories("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", includingForgotten: true);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Null(results.Current.ForgottenAt); // absent, not a far-future value (ADR-061 I8)
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Named Graphs

    [Fact]
    public void AddCurrent_WithGraph_CanQueryByGraph()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            // Query with correct graph
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/graph1>", results.Current.Graph.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_DifferentGraphs_IsolatedQueries()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"value1\"",
            "<http://ex.org/graph1>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"value2\"",
            "<http://ex.org/graph2>");

        store.AcquireReadLock();
        try
        {
            // Query graph1
            var results1 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", default,
                "<http://ex.org/graph1>");
            try
            {
                Assert.True(results1.MoveNext());
                Assert.Equal("\"value1\"", results1.Current.Object.ToString());
                Assert.False(results1.MoveNext());
            }
            finally
            {
                results1.Dispose();
            }

            // Query graph2
            var results2 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", default,
                "<http://ex.org/graph2>");
            try
            {
                Assert.True(results2.MoveNext());
                Assert.Equal("\"value2\"", results2.Current.Object.ToString());
                Assert.False(results2.MoveNext());
            }
            finally
            {
                results2.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_DefaultGraph_EmptyGraphSpan()
    {
        var store = CreateStore();

        // Add to default graph (no graph specified)
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            // Query default graph (no graph specified)
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.True(results.Current.Graph.IsEmpty, "Default graph should have empty Graph span");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_NamedGraph_NotVisibleInDefaultGraph()
    {
        var store = CreateStore();

        // Add to named graph only
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            // Query default graph - should not find it
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Named graph triple should not be visible in default graph");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_DefaultGraph_NotVisibleInNamedGraph()
    {
        var store = CreateStore();

        // Add to default graph
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            // Query named graph - should not find it
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                Assert.False(results.MoveNext(), "Default graph triple should not be visible in named graph");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrent_NamedGraph_OnlyDeletesFromThatGraph()
    {
        var store = CreateStore();

        // Add same SPO to two different graphs
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph2>");

        // Delete from graph1 only
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            // Graph1 should be empty
            var results1 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                Assert.False(results1.MoveNext());
            }
            finally
            {
                results1.Dispose();
            }

            // Graph2 should still have the triple
            var results2 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph2>");
            try
            {
                Assert.True(results2.MoveNext());
            }
            finally
            {
                results2.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void BatchAdd_NamedGraphs_AllPersisted()
    {
        var store = CreateStore();

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");
        store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");
        store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph2>");
        store.CommitBatch();

        store.AcquireReadLock();
        try
        {
            // Graph1 should have 2 triples
            var results1 = store.QueryCurrent(
                default, "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                var count = 0;
                while (results1.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results1.Dispose();
            }

            // Graph2 should have 1 triple
            var results2 = store.QueryCurrent(
                default, "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph2>");
            try
            {
                var count = 0;
                while (results2.MoveNext()) count++;
                Assert.Equal(1, count);
            }
            finally
            {
                results2.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void NamedGraph_Persistence_SurvivesRestart()
    {
        // First session
        using (var store1 = new QuadStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
        }

        // Second session
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent(
                    "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                    "<http://ex.org/graph1>");
                try
                {
                    Assert.True(results.MoveNext());
                    Assert.Equal("<http://ex.org/graph1>", results.Current.Graph.ToString());
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void NamedGraph_QueryEvolution_IncludesGraph()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryMemories(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>", includingForgotten: true);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/graph1>", results.Current.Graph.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Clear Tests

    [Fact]
    public void Clear_EmptyStore_Succeeds()
    {
        var store = CreateStore();

        // Clear an empty store should not throw
        store.Clear();

        var (quadCount, atomCount, _) = store.GetStatistics();
        Assert.Equal(0, quadCount);
        Assert.Equal(0, atomCount);
    }

    [Fact]
    public void Clear_StoreWithData_RemovesAllQuads()
    {
        var store = CreateStore();

        // Add some data
        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>");
        store.AddCurrent("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o3>");

        var (quadCountBefore, _, _) = store.GetStatistics();
        Assert.Equal(3, quadCountBefore);

        // Clear the store
        store.Clear();

        // Verify empty
        var (quadCount, atomCount, _) = store.GetStatistics();
        Assert.Equal(0, quadCount);
        Assert.Equal(0, atomCount);

        // Query should return nothing
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty);
            try
            {
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Clear_StoreCanBeReusedAfterClear()
    {
        var store = CreateStore();

        // Add initial data
        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");

        // Clear
        store.Clear();

        // Add new data
        store.AddCurrent("<http://ex.org/new>", "<http://ex.org/newp>", "<http://ex.org/newo>");

        // Verify new data is queryable
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/new>", "<http://ex.org/newp>", "<http://ex.org/newo>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/new>", results.Current.Subject.ToString());
            }
            finally
            {
                results.Dispose();
            }

            // Old data should not exist
            var oldResults = store.QueryCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
            try
            {
                Assert.False(oldResults.MoveNext());
            }
            finally
            {
                oldResults.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Clear_NamedGraphs_AlsoCleared()
    {
        var store = CreateStore();

        // Add data to named graph
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/graph1>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/graph2>");

        // Clear
        store.Clear();

        // Verify named graphs are empty
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                "<http://ex.org/graph1>");
            try
            {
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Clear_MultipleTimes_Succeeds()
    {
        var store = CreateStore();

        // Clear multiple times should not throw
        store.Clear();
        store.Clear();
        store.Clear();

        // Add data after multiple clears
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var (quadCount, _, _) = store.GetStatistics();
        Assert.Equal(1, quadCount);
    }

    [Fact]
    public void Clear_StatisticsReset()
    {
        var store = CreateStore();

        // Add data and checkpoint to generate statistics
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.Checkpoint();

        // Verify statistics exist
        Assert.True(store.Statistics.HasStats);
        Assert.Equal(1, store.Statistics.TotalTripleCount);

        // Clear
        store.Clear();

        // Statistics should be cleared
        Assert.False(store.Statistics.HasStats);
        Assert.Equal(0, store.Statistics.TotalTripleCount);
    }

    #endregion
}
