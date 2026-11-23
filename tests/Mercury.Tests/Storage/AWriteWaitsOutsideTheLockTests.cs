// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// A write waits for the lock outside it, woken by every release, and a store closed under the lock is found closed by
/// every read and write that comes after (ADR-066 D1, D2).
/// </summary>
/// <remarks>
/// Each test waits for a state of the lock, read through the store's count of the writes waiting, never for a time, so
/// its outcome does not depend on the scheduler. A wake-up that does not come leaves a write waiting with nothing left
/// to release, and the test fails at its bound. A batch, and each read, begins and ends on one thread: the test's
/// thread awaits nothing while it holds a read.
/// </remarks>
public sealed class AWriteWaitsOutsideTheLockTests : IDisposable
{
    /// <summary>How long an operation that waits on nothing outside its store may take here.</summary>
    private static readonly TimeSpan Progress = TimeSpan.FromSeconds(10);

    private readonly TempPath _dir;
    private readonly List<QuadStore> _closeLast = [];

    public AWriteWaitsOutsideTheLockTests()
    {
        _dir = TempPath.Test("write-waits");
        _dir.MarkOwnership();
    }

    public void Dispose()
    {
        foreach (var store in _closeLast)
            store.Dispose();
        _dir.Cleanup();
    }

    [Fact]
    public async Task EveryRelease_AWritesAndAReads_WakesTheWaitingWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("wake");
        using var begun = new ManualResetEventSlim();
        using var commit = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        using var end = new ManualResetEventSlim();
        var batch = OnAThreadOfItsOwn(() =>
        {
            store.BeginBatch();
            store.AddCurrentBatched("<urn:s>", "<urn:p>", "\"batch\"");
            begun.Set();
            commit.Wait(ct);
            store.CommitBatch();
        });
        try
        {
            Assert.True(begun.Wait(Progress, ct));

            // Two writes wait while the batch holds the write lock.
            var first = OnAThreadOfItsOwn(() => store.AddCurrent("<urn:s>", "<urn:p>", "\"first\""));
            var second = OnAThreadOfItsOwn(() => store.AddCurrent("<urn:s>", "<urn:p>", "\"second\""));
            Assert.True(SpinWait.SpinUntil(() => store.WritesWaiting == 2, Progress));

            // The batch's release wakes both. One enters, and its release wakes the other.
            commit.Set();
            await Task.WhenAll(batch, first, second).WaitAsync(Progress, ct);
            Assert.True(await first);
            Assert.True(await second);

            // A read holds the lock, and a write waits for it.
            store.AcquireReadLock();
            var third = OnAThreadOfItsOwn(() => store.AddCurrent("<urn:s>", "<urn:p>", "\"third\""));
            Assert.True(SpinWait.SpinUntil(() => store.WritesWaiting == 1, Progress));

            // A second read takes the lock at once beside the first: it does not wait behind the waiting write (I3).
            var secondRead = OnAThreadOfItsOwn(() =>
            {
                store.AcquireReadLock();
                holding.Set();
                end.Wait(ct);
                store.ReleaseReadLock();
            });
            Assert.True(holding.Wait(Progress, ct));

            // The first read ends. Its release wakes the write, which cannot enter: the second read holds the lock.
            store.ReleaseReadLock();
            Assert.False(third.IsCompleted);

            // The second read ends, and its release wakes the write, which enters.
            end.Set();
            await Task.WhenAll(secondRead, third).WaitAsync(Progress, ct);
            Assert.True(await third);
        }
        finally
        {
            // A test that fails lets its threads end, so that the store closes.
            commit.Set();
            end.Set();
        }

        Assert.Equal(new[] { "\"batch\"", "\"first\"", "\"second\"", "\"third\"" }, Objects(store));
    }

    [Fact]
    public void AReleaseBetweenAWritesTryAndItsWait_IsNotLost()
    {
        var store = NewStore("lost");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"before\"");

        bool written = false;
        Exception? failed = null;
        var writer = new Thread(() =>
        {
            try { written = store.AddCurrent("<urn:s>", "<urn:p>", "\"written\""); }
            catch (Exception ex) { failed = ex; }
        }) { IsBackground = true };

        // The test holds a read, and the monitor the writes wait on: the write stops at the monitor, before its try.
        store.AcquireReadLock();
        lock (store.WriteWait)
        {
            writer.Start();
            Assert.True(SpinWait.SpinUntil(() => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0, Progress));

            // The read's release leaves the lock and wakes no one, since no write waits yet.
            store.ReleaseReadLock();
        }

        // Given the monitor, the write tries, finds the lock free and enters. A write that tried outside the monitor
        // would have failed its try before the release, missed its wake-up, and waited for ever.
        Assert.True(writer.Join(Progress));
        Assert.Null(failed);
        Assert.True(written);
    }

    [Fact]
    public async Task ACloseAndAWriteQueuedBehindARead_BothEnd_AndTheWriteTookEffectOrFoundTheStoreClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir.FullPath, "race");
        var store = new QuadStore(path, null, null, StorageOptions.ForTesting);
        _closeLast.Add(store);

        // A read holds the lock; a write and a close wait for it.
        store.AcquireReadLock();
        var write = OnAThreadOfItsOwn(() => store.AddCurrent("<urn:s>", "<urn:p>", "\"queued\""));
        var close = OnAThreadOfItsOwn(store.Dispose);
        Assert.True(SpinWait.SpinUntil(() => store.WritesWaiting == 2, Progress));

        // The read ends: the write and the close enter one after the other, in no order the store promises.
        store.ReleaseReadLock();
        await close.WaitAsync(Progress, ct);
        await Task.WhenAny(write).WaitAsync(Progress, ct);

        // Entering first, the write took effect; entering after the close, it found the store closed and wrote nothing.
        bool tookEffect = write.IsCompletedSuccessfully;
        if (tookEffect)
            Assert.True(await write);
        else
            Assert.Equal("The store is closed.",
                Assert.IsType<ObjectDisposedException>(write.Exception!.InnerException).Message);

        // A read and a write after the close find the store closed.
        Assert.Equal("The store is closed.", Assert.Throws<ObjectDisposedException>(store.AcquireReadLock).Message);
        Assert.Equal("The store is closed.", Assert.Throws<ObjectDisposedException>(
            () => store.AddCurrent("<urn:s>", "<urn:p>", "\"after\"")).Message);

        // The store, opened again, holds the write exactly when it took effect.
        var reopened = new QuadStore(path, null, null, StorageOptions.ForTesting);
        _closeLast.Add(reopened);
        Assert.Equal(tookEffect ? new[] { "\"queued\"" } : [], Objects(reopened));
    }

    [Fact]
    public async Task AReaderOutsideAQuery_WhileTheStoreCloses_ReadsBeforeTheClose_AndOneAfterFindsTheStoreClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("graphs");
        store.AddCurrent("<urn:s>", "<urn:p>", "<urn:o>", "<urn:g>");

        // A reader that is not a query holds the read lock, as GetNamedGraphs asks of its caller; a close waits for it.
        store.AcquireReadLock();
        var close = OnAThreadOfItsOwn(store.Dispose);
        Assert.True(SpinWait.SpinUntil(() => store.WritesWaiting == 1, Progress));

        // The reader reads the store as it is: the close has not happened.
        var graphs = new List<string>();
        foreach (var graph in store.GetNamedGraphs())
            graphs.Add(graph.ToString());
        store.ReleaseReadLock();
        Assert.Equal("<urn:g>", Assert.Single(graphs));

        // The close ends after the reader, and a reader after it finds the store closed before it reads anything.
        await close.WaitAsync(Progress, ct);
        Assert.Equal("The store is closed.", Assert.Throws<ObjectDisposedException>(store.AcquireReadLock).Message);
    }

    private QuadStore NewStore(string name)
    {
        var store = new QuadStore(Path.Combine(_dir.FullPath, name), null, null, StorageOptions.ForTesting);
        _closeLast.Add(store);
        return store;
    }

    /// <summary>The objects of <c>&lt;urn:s&gt; &lt;urn:p&gt;</c> the store remembers, in order.</summary>
    private static string[] Objects(QuadStore store)
    {
        var result = SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o } ORDER BY ?o");
        Assert.True(result.Success, result.ErrorMessage);
        return result.Rows!.Select(row => row["o"]).ToArray();
    }

    private static Task OnAThreadOfItsOwn(Action action) =>
        Task.Factory.StartNew(action, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static Task<T> OnAThreadOfItsOwn<T>(Func<T> function) =>
        Task.Factory.StartNew(function, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
}
