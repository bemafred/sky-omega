using System;
using System.IO;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for TripleStore - multi-index RDF store with WAL durability.
/// </summary>
public class TripleStoreTests : IDisposable
{
    private readonly string _testPath;
    private TripleStore? _store;

    public TripleStoreTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), $"triplestore_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        _store?.Dispose();
        CleanupDirectory();
    }

    private void CleanupDirectory()
    {
        if (Directory.Exists(_testPath))
            Directory.Delete(_testPath, true);
    }

    private TripleStore CreateStore()
    {
        _store?.Dispose();
        _store = new TripleStore(_testPath);
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

    #region Temporal Queries

    [Fact]
    public void Add_WithTemporalBounds_QueryAsOf()
    {
        var store = CreateStore();

        var validFrom = DateTimeOffset.UtcNow.AddDays(-10);
        var validTo = DateTimeOffset.UtcNow.AddDays(10);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query as of now (should match)
            var results = store.QueryAsOf(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                DateTimeOffset.UtcNow);
            try
            {
                Assert.True(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }

            // Query as of 20 days ago (before validFrom, should not match)
            var pastResults = store.QueryAsOf(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                DateTimeOffset.UtcNow.AddDays(-20));
            try
            {
                Assert.False(pastResults.MoveNext());
            }
            finally
            {
                pastResults.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryEvolution_ReturnsAllVersions()
    {
        var store = CreateStore();

        // Add multiple triples with different objects (different SPO) to have distinct versions
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"version1\"",
            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-20));
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"version2\"",
            DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));

        store.AcquireReadLock();
        try
        {
            // Query all triples for this subject/predicate (any object)
            var results = store.QueryEvolution("<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
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
    public void TimeTravelTo_ReturnsStateAtTime()
    {
        var store = CreateStore();

        var past = DateTimeOffset.UtcNow.AddDays(-5);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"old value\"",
            DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-3));
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"new value\"",
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.MaxValue);

        store.AcquireReadLock();
        try
        {
            var results = store.TimeTravelTo(past, "<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("\"old value\"", results.Current.Object.ToString());
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
        using (var store1 = new TripleStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Second session
        using (var store2 = new TripleStore(_testPath))
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
        using (var store1 = new TripleStore(_testPath))
        {
            store1.BeginBatch();
            for (int i = 0; i < 50; i++)
            {
                store1.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
            }
            store1.CommitBatch();
        }

        // Second session
        using (var store2 = new TripleStore(_testPath))
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
}
