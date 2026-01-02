using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Stress tests for concurrent access to QuadStore.
/// Tests thread-safety of ReaderWriterLockSlim-based concurrency.
/// </summary>
public class ConcurrentAccessTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public ConcurrentAccessTests()
    {
        var tempPath = TempPath.Test("concurrent");
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

    private void SeedStore(QuadStore store, int tripleCount)
    {
        store.BeginBatch();
        for (int i = 0; i < tripleCount; i++)
        {
            store.AddCurrentBatched(
                $"<http://example.org/s{i}>",
                "<http://example.org/value>",
                $"\"{i}\"");
        }
        store.CommitBatch();
    }

    #region Concurrent Reads

    [Fact]
    public void ConcurrentReads_MultipleThreads_AllSucceed()
    {
        var store = CreateStore();
        SeedStore(store, 100);

        const int threadCount = 8;
        const int readsPerThread = 50;
        var errors = new ConcurrentBag<Exception>();
        var readCounts = new ConcurrentBag<int>();

        var threads = new Thread[threadCount];
        var startBarrier = new Barrier(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait(); // Synchronize start

                    for (int i = 0; i < readsPerThread; i++)
                    {
                        store.AcquireReadLock();
                        try
                        {
                            var results = store.QueryCurrent(null, "<http://example.org/value>", null);
                            try
                            {
                                int count = 0;
                                while (results.MoveNext()) count++;
                                readCounts.Add(count);
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
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);
        Assert.Equal(threadCount * readsPerThread, readCounts.Count);
        Assert.All(readCounts, count => Assert.Equal(100, count));
    }

    [Fact]
    public async Task ConcurrentReads_AsyncTasks_AllSucceed()
    {
        var store = CreateStore();
        SeedStore(store, 100);

        const int taskCount = 16;
        var readCounts = new ConcurrentBag<int>();

        var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
        {
            store.AcquireReadLock();
            try
            {
                var results = store.QueryCurrent(null, "<http://example.org/value>", null);
                try
                {
                    int count = 0;
                    while (results.MoveNext()) count++;
                    readCounts.Add(count);
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
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(taskCount, readCounts.Count);
        Assert.All(readCounts, count => Assert.Equal(100, count));
    }

    #endregion

    #region Concurrent Writes

    [Fact]
    public void ConcurrentWrites_MultipleThreads_AllSucceed()
    {
        var store = CreateStore();

        const int threadCount = 4;
        const int writesPerThread = 100;
        var errors = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        var startBarrier = new Barrier(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();

                    for (int i = 0; i < writesPerThread; i++)
                    {
                        store.AddCurrent(
                            $"<http://example.org/t{threadId}/s{i}>",
                            "<http://example.org/value>",
                            $"\"{threadId * 1000 + i}\"");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);

        // Verify all writes succeeded
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(null, "<http://example.org/value>", null);
            try
            {
                int count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(threadCount * writesPerThread, count);
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
    public void ConcurrentBatchWrites_MultipleThreads_Serialized()
    {
        var store = CreateStore();

        const int threadCount = 4;
        const int writesPerBatch = 50;
        var errors = new ConcurrentBag<Exception>();
        var successfulBatches = new ConcurrentBag<int>();

        var threads = new Thread[threadCount];
        var startBarrier = new Barrier(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();

                    store.BeginBatch();
                    try
                    {
                        for (int i = 0; i < writesPerBatch; i++)
                        {
                            store.AddCurrentBatched(
                                $"<http://example.org/batch{threadId}/s{i}>",
                                "<http://example.org/batchValue>",
                                $"\"{threadId * 1000 + i}\"");
                        }
                        store.CommitBatch();
                        successfulBatches.Add(threadId);
                    }
                    catch
                    {
                        store.RollbackBatch();
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);
        Assert.Equal(threadCount, successfulBatches.Count);

        // Verify all batches committed
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(null, "<http://example.org/batchValue>", null);
            try
            {
                int count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(threadCount * writesPerBatch, count);
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

    #region Mixed Read/Write

    [Fact]
    public void MixedReadWrite_ConcurrentOperations_NoDeadlock()
    {
        var store = CreateStore();
        SeedStore(store, 50);

        const int readerCount = 4;
        const int writerCount = 2;
        const int operationsPerThread = 100;
        var errors = new ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var threads = new List<Thread>();
        var startBarrier = new Barrier(readerCount + writerCount);

        // Reader threads
        for (int r = 0; r < readerCount; r++)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();

                    for (int i = 0; i < operationsPerThread && !cts.Token.IsCancellationRequested; i++)
                    {
                        store.AcquireReadLock();
                        try
                        {
                            var results = store.QueryCurrent(null, null, null);
                            try
                            {
                                while (results.MoveNext()) { }
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
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(ex);
                }
            });
            threads.Add(thread);
        }

        // Writer threads
        for (int w = 0; w < writerCount; w++)
        {
            int writerId = w;
            var thread = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();

                    for (int i = 0; i < operationsPerThread && !cts.Token.IsCancellationRequested; i++)
                    {
                        store.AddCurrent(
                            $"<http://example.org/w{writerId}/new{i}>",
                            "<http://example.org/added>",
                            $"\"{i}\"");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(ex);
                }
            });
            threads.Add(thread);
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);
        Assert.False(cts.IsCancellationRequested, "Test timed out - possible deadlock");
    }

    [Fact]
    public void MixedReadWrite_WritersBlockReaders_EventuallyComplete()
    {
        var store = CreateStore();
        SeedStore(store, 10);

        var readerStarted = new ManualResetEventSlim(false);
        var writerCompleted = new ManualResetEventSlim(false);
        var errors = new ConcurrentBag<Exception>();
        var readerCompletedCount = 0;

        // Start a reader that will be blocked by writer
        var readerThread = new Thread(() =>
        {
            try
            {
                store.AcquireReadLock();
                try
                {
                    readerStarted.Set();
                    var results = store.QueryCurrent(null, null, null);
                    try
                    {
                        while (results.MoveNext()) { }
                    }
                    finally
                    {
                        results.Dispose();
                    }
                    Interlocked.Increment(ref readerCompletedCount);
                }
                finally
                {
                    store.ReleaseReadLock();
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        var writerThread = new Thread(() =>
        {
            try
            {
                // Wait for reader to start, then write (will wait for read lock release)
                readerStarted.Wait();
                Thread.Sleep(10); // Small delay to ensure reader has lock

                for (int i = 0; i < 10; i++)
                {
                    store.AddCurrent(
                        $"<http://example.org/blocking/s{i}>",
                        "<http://example.org/blocked>",
                        $"\"{i}\"");
                }
                writerCompleted.Set();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        readerThread.Start();
        writerThread.Start();

        Assert.True(readerThread.Join(TimeSpan.FromSeconds(10)), "Reader thread timed out");
        Assert.True(writerThread.Join(TimeSpan.FromSeconds(10)), "Writer thread timed out");

        Assert.Empty(errors);
        Assert.Equal(1, readerCompletedCount);
    }

    #endregion

    #region Stress Tests

    [Fact]
    public void StressTest_HighConcurrency_NoExceptions()
    {
        var store = CreateStore();

        const int threadCount = 16;
        const int operationsPerThread = 200;
        var errors = new ConcurrentBag<Exception>();
        var operationCounts = new ConcurrentDictionary<string, int>();

        var threads = new Thread[threadCount];
        var startBarrier = new Barrier(threadCount);
        var random = new Random(42); // Deterministic for reproducibility

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            bool isWriter = t % 4 == 0; // 25% writers

            threads[t] = new Thread(() =>
            {
                var localRandom = new Random(threadId);
                try
                {
                    startBarrier.SignalAndWait();

                    for (int i = 0; i < operationsPerThread; i++)
                    {
                        if (isWriter)
                        {
                            store.AddCurrent(
                                $"<http://example.org/stress/t{threadId}/s{i}>",
                                "<http://example.org/stress>",
                                $"\"{threadId * 10000 + i}\"");
                            operationCounts.AddOrUpdate("writes", 1, (_, v) => v + 1);
                        }
                        else
                        {
                            store.AcquireReadLock();
                            try
                            {
                                var results = store.QueryCurrent(null, "<http://example.org/stress>", null);
                                try
                                {
                                    while (results.MoveNext()) { }
                                    operationCounts.AddOrUpdate("reads", 1, (_, v) => v + 1);
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

                        // Occasional small delay to increase interleaving
                        if (localRandom.Next(10) == 0)
                            Thread.Yield();
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);

        // Verify operations completed
        var totalOps = operationCounts.Values.Sum();
        Assert.Equal(threadCount * operationsPerThread, totalOps);
    }

    [Fact]
    public void StressTest_RapidLockAcquisition_NoDeadlock()
    {
        var store = CreateStore();
        SeedStore(store, 10);

        const int threadCount = 8;
        const int iterations = 1000;
        var errors = new ConcurrentBag<Exception>();
        var completedIterations = new int[threadCount];

        var threads = new Thread[threadCount];
        var startBarrier = new Barrier(threadCount);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();

                    for (int i = 0; i < iterations && !cts.Token.IsCancellationRequested; i++)
                    {
                        // Rapidly acquire and release locks
                        store.AcquireReadLock();
                        store.ReleaseReadLock();
                        completedIterations[threadId]++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);
        Assert.False(cts.IsCancellationRequested, "Test timed out - possible deadlock");
        Assert.All(completedIterations, c => Assert.Equal(iterations, c));
    }

    [Fact]
    public void StressTest_BatchWithConcurrentReads_Consistency()
    {
        var store = CreateStore();
        SeedStore(store, 50);

        const int batchSize = 100;
        const int readerCount = 4;
        var errors = new ConcurrentBag<Exception>();
        var readerCounts = new ConcurrentBag<int>();
        var batchCompleted = new ManualResetEventSlim(false);
        var startReading = new ManualResetEventSlim(false);

        // Reader threads - should see either pre-batch or post-batch state, never partial
        var readerThreads = new Thread[readerCount];
        for (int r = 0; r < readerCount; r++)
        {
            readerThreads[r] = new Thread(() =>
            {
                try
                {
                    startReading.Wait();

                    while (!batchCompleted.IsSet)
                    {
                        store.AcquireReadLock();
                        try
                        {
                            var results = store.QueryCurrent(null, "<http://example.org/batchItem>", null);
                            try
                            {
                                int count = 0;
                                while (results.MoveNext()) count++;
                                // Should be either 0 (before batch) or batchSize (after batch), never partial
                                if (count != 0 && count != batchSize)
                                {
                                    errors.Add(new Exception($"Inconsistent read: expected 0 or {batchSize}, got {count}"));
                                }
                                readerCounts.Add(count);
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

                        Thread.Yield();
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        // Batch writer thread
        var batchThread = new Thread(() =>
        {
            try
            {
                store.BeginBatch();
                startReading.Set(); // Signal readers to start

                for (int i = 0; i < batchSize; i++)
                {
                    store.AddCurrentBatched(
                        $"<http://example.org/batchItem/s{i}>",
                        "<http://example.org/batchItem>",
                        $"\"{i}\"");
                    Thread.Yield(); // Give readers a chance to try acquiring lock
                }

                store.CommitBatch();
                batchCompleted.Set();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                batchCompleted.Set();
            }
        });

        foreach (var thread in readerThreads) thread.Start();
        batchThread.Start();

        batchThread.Join();
        foreach (var thread in readerThreads) thread.Join();

        Assert.Empty(errors);
        // After batch, readers should see all items
        Assert.Contains(batchSize, readerCounts);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void NestedReadLock_SameThread_Throws()
    {
        var store = CreateStore();

        store.AcquireReadLock();
        try
        {
            // ReaderWriterLockSlim with NoRecursion policy should throw
            Assert.Throws<LockRecursionException>(() => store.AcquireReadLock());
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void ReleaseWithoutAcquire_Throws()
    {
        var store = CreateStore();

        Assert.Throws<SynchronizationLockException>(() => store.ReleaseReadLock());
    }

    [Fact]
    public void WriteAfterDispose_Throws()
    {
        var store = CreateStore();
        store.Dispose();
        _store = null; // Prevent double dispose in cleanup

        Assert.Throws<ObjectDisposedException>(() =>
            store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"));
    }

    [Fact]
    public void ReadAfterDispose_Throws()
    {
        var store = CreateStore();
        store.Dispose();
        _store = null;

        Assert.Throws<ObjectDisposedException>(() => store.AcquireReadLock());
    }

    #endregion

    #region QuadStore Concurrent Atom Interning

    // Note: AtomStore expects external locking from QuadStore (see AtomStore.cs line 25-26).
    // These tests verify concurrent atom interning through QuadStore's write operations.

    [Fact]
    public void QuadStore_ConcurrentWrites_AtomInterningConsistent()
    {
        var store = CreateStore();

        const int threadCount = 4;
        const int writesPerThread = 50;
        var errors = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        var startBarrier = new Barrier(threadCount);

        // All threads write triples with the same predicate - tests predicate atom reuse
        const string sharedPredicate = "<http://example.org/sharedPredicate>";

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();

                    for (int i = 0; i < writesPerThread; i++)
                    {
                        store.AddCurrent(
                            $"<http://example.org/t{threadId}/s{i}>",
                            sharedPredicate,
                            $"\"{threadId * 1000 + i}\"");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);

        // Verify all writes succeeded and predicate is consistent
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(null, sharedPredicate, null);
            try
            {
                int count = 0;
                while (results.MoveNext())
                {
                    // All results should have the same predicate
                    Assert.Equal(sharedPredicate, results.Current.Predicate.ToString());
                    count++;
                }
                Assert.Equal(threadCount * writesPerThread, count);
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
    public void QuadStore_ConcurrentWrites_DuplicateSubjects_Handled()
    {
        var store = CreateStore();

        const int threadCount = 4;
        const int iterations = 20;
        var errors = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        var startBarrier = new Barrier(threadCount);

        // All threads write to the same subject - tests subject atom deduplication
        const string sharedSubject = "<http://example.org/sharedSubject>";

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();

                    for (int i = 0; i < iterations; i++)
                    {
                        store.AddCurrent(
                            sharedSubject,
                            $"<http://example.org/p{threadId}/{i}>",
                            $"\"{threadId * 1000 + i}\"");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);

        // Verify all writes succeeded
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(sharedSubject, null, null);
            try
            {
                int count = 0;
                while (results.MoveNext())
                {
                    Assert.Equal(sharedSubject, results.Current.Subject.ToString());
                    count++;
                }
                Assert.Equal(threadCount * iterations, count);
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
