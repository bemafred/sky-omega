using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for ADR-023: Transactional Integrity — WAL boundaries, batch rollback, replay idempotence, transaction time.
/// </summary>
public class TransactionalIntegrityTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public TransactionalIntegrityTests()
    {
        var tempPath = TempPath.Test("txn-integrity");
        tempPath.MarkOwnership();
        _testPath = tempPath;
    }

    public void Dispose()
    {
        _store?.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    private QuadStore CreateStore()
    {
        _store?.Dispose();
        _store = new QuadStore(_testPath);
        return _store;
    }

    private QuadStore ReopenStore()
    {
        _store?.Dispose();
        _store = new QuadStore(_testPath);
        return _store;
    }

    private int CountResults(QuadStore store, string? subject = null, string? predicate = null, string? @object = null)
    {
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(
                subject ?? ReadOnlySpan<char>.Empty,
                predicate ?? ReadOnlySpan<char>.Empty,
                @object ?? ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                return count;
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

    #region WAL v2 Format

    [Fact]
    public void LogRecord_Is80Bytes()
    {
        Assert.Equal(80, Marshal.SizeOf<LogRecord>());
        Assert.Equal(80, WriteAheadLog.RecordSize);
    }

    #endregion

    #region Rollback Leaves Indexes Clean

    [Fact]
    public void Rollback_LeavesIndexesClean()
    {
        var store = CreateStore();

        store.BeginBatch();
        for (int i = 0; i < 100; i++)
            store.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.RollbackBatch();

        // After rollback, indexes must be clean — no data visible
        Assert.Equal(0, CountResults(store, predicate: "<http://ex.org/p>"));
    }

    [Fact]
    public void Rollback_ThenNewBatch_OnlyNewDataVisible()
    {
        var store = CreateStore();

        // First batch: rollback
        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/rolled-back>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.RollbackBatch();

        // Second batch: commit
        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/committed>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.CommitBatch();

        Assert.Equal(0, CountResults(store, subject: "<http://ex.org/rolled-back>"));
        Assert.Equal(1, CountResults(store, subject: "<http://ex.org/committed>"));
    }

    #endregion

    #region Crash Recovery — Uncommitted Discarded

    [Fact]
    public void CrashMidBatch_DiscardsUncommitted()
    {
        // Session 1: begin batch, add triples, simulate crash
        // We must release the lock before dispose (real crash would skip this)
        using (var store1 = new QuadStore(_testPath))
        {
            store1.BeginBatch();
            store1.AddCurrentBatched("<http://ex.org/uncommitted>", "<http://ex.org/p>", "<http://ex.org/o>");
            // Release lock to allow clean dispose, but do NOT commit
            store1.RollbackBatch();
            // The WAL still has the BeginTx + data records without CommitTx
        }

        // Session 2: reopen — recovery must discard uncommitted batch
        var store2 = ReopenStore();
        Assert.Equal(0, CountResults(store2, subject: "<http://ex.org/uncommitted>"));
    }

    [Fact]
    public void CrashAfterCommit_RecoversAllData()
    {
        // Session 1: commit a batch, then close without checkpoint
        using (var store1 = new QuadStore(_testPath))
        {
            store1.BeginBatch();
            for (int i = 0; i < 10; i++)
                store1.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
            store1.CommitBatch();
            // No checkpoint — relies on WAL recovery
        }

        // Session 2: reopen — committed data must be recovered
        var store2 = ReopenStore();
        Assert.Equal(10, CountResults(store2, predicate: "<http://ex.org/p>"));
    }

    [Fact]
    public void CommittedAndUncommitted_OnlyCommittedRecovered()
    {
        // Session 1: one committed batch, then one uncommitted batch
        using (var store1 = new QuadStore(_testPath))
        {
            // Committed batch
            store1.BeginBatch();
            store1.AddCurrentBatched("<http://ex.org/committed>", "<http://ex.org/p>", "<http://ex.org/o>");
            store1.CommitBatch();

            // Uncommitted batch — rollback releases lock but WAL has BeginTx without CommitTx
            store1.BeginBatch();
            store1.AddCurrentBatched("<http://ex.org/uncommitted>", "<http://ex.org/p>", "<http://ex.org/o>");
            store1.RollbackBatch();
        }

        // Session 2: only committed data should survive
        var store2 = ReopenStore();
        Assert.Equal(1, CountResults(store2, subject: "<http://ex.org/committed>"));
        Assert.Equal(0, CountResults(store2, subject: "<http://ex.org/uncommitted>"));
    }

    #endregion

    #region Replay Idempotence

    [Fact]
    public void ReplayIdempotence_NoDuplicateRows()
    {
        // Session 1: commit batch, close without checkpoint
        using (var store1 = new QuadStore(_testPath))
        {
            store1.BeginBatch();
            store1.AddCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            store1.CommitBatch();
        }

        // Session 2: recovery replays the committed batch — must not create duplicates
        var store2 = ReopenStore();
        Assert.Equal(1, CountResults(store2, subject: "<http://ex.org/s>"));
    }

    #endregion

    #region Transaction Time

    [Fact]
    public void TransactionTime_VariesPerWrite()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        System.Threading.Thread.Sleep(15); // Ensure different millisecond
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

        // Query ALL VERSIONS to see transaction times
        store.AcquireReadLock();
        try
        {
            var results1 = store.QueryEvolution("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
            var results2 = store.QueryEvolution("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

            DateTimeOffset tt1 = default, tt2 = default;
            try
            {
                if (results1.MoveNext())
                    tt1 = results1.Current.TransactionTime;
            }
            finally { results1.Dispose(); }

            try
            {
                if (results2.MoveNext())
                    tt2 = results2.Current.TransactionTime;
            }
            finally { results2.Dispose(); }

            Assert.NotEqual(default, tt1);
            Assert.NotEqual(default, tt2);
            Assert.NotEqual(tt1, tt2);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void TransactionTime_PreservedThroughRecovery()
    {
        DateTimeOffset originalTt;

        // Session 1: add data, record transaction time, close without checkpoint
        using (var store1 = new QuadStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

            store1.AcquireReadLock();
            try
            {
                var results = store1.QueryEvolution("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
                try
                {
                    Assert.True(results.MoveNext());
                    originalTt = results.Current.TransactionTime;
                    Assert.NotEqual(default, originalTt);
                }
                finally { results.Dispose(); }
            }
            finally { store1.ReleaseReadLock(); }
        }

        // Session 2: recovery replays — transaction time must match original
        System.Threading.Thread.Sleep(50); // Ensure recovery time differs
        var store2 = ReopenStore();

        store2.AcquireReadLock();
        try
        {
            var results = store2.QueryEvolution("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal(originalTt, results.Current.TransactionTime);
            }
            finally { results.Dispose(); }
        }
        finally { store2.ReleaseReadLock(); }
    }

    #endregion
}
