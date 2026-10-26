using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for QuadStorePool - bounded pool of reusable QuadStore instances.
/// </summary>
/// <remarks>
/// Every pool here uses <see cref="StorageOptions.ForTesting"/> stores. The pool sizes its
/// capacity from the disk budget, and a default store is estimated at ~5.9 GB: on a machine
/// whose temp filesystem is a small tmpfs, that leaves room for one store, and a test renting
/// two at once waits forever. Every pool also reports its events to xunit's diagnostic messages.
/// </remarks>
public class QuadStorePoolTests : IDisposable
{
    private QuadStorePool? _pool;

    /// <summary>
    /// Records a pool's events so a test can follow its state instead of guessing it from the
    /// clock, and passes each one on to the diagnostic messages.
    /// </summary>
    private sealed class PoolEventRecorder : IObservabilityListener
    {
        public StorePoolCapacityEvent? Capacity { get; private set; }
        public ManualResetEventSlim RentBlocked { get; } = new();
        public ManualResetEventSlim RentResumed { get; } = new();

        public void OnStorePoolCapacity(in StorePoolCapacityEvent ev)
        {
            Capacity = ev;
            DiagnosticMessageListener.Instance.OnStorePoolCapacity(ev);
        }

        public void OnStorePoolRentBlocked(in StorePoolRentBlockedEvent ev)
        {
            DiagnosticMessageListener.Instance.OnStorePoolRentBlocked(ev);
            RentBlocked.Set();
        }

        public void OnStorePoolRentResumed(in StorePoolRentResumedEvent ev)
        {
            DiagnosticMessageListener.Instance.OnStorePoolRentResumed(ev);
            RentResumed.Set();
        }
    }

    public void Dispose()
    {
        _pool?.Dispose();
    }

    [Fact]
    public void Rent_ReturnsUsableStore()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        var store = _pool.Rent();
        try
        {
            // Store should be usable
            store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

            var (quadCount, _, _) = store.GetStatistics();
            Assert.Equal(1, quadCount);
        }
        finally
        {
            _pool.Return(store);
        }
    }

    [Fact]
    public void Rent_StoreIsCleared()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 1,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        // First rent - add some data
        var store1 = _pool.Rent();
        store1.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        _pool.Return(store1);

        // Second rent - should get same store but cleared
        var store2 = _pool.Rent();
        try
        {
            var (quadCount, _, _) = store2.GetStatistics();
            Assert.Equal(0, quadCount);
        }
        finally
        {
            _pool.Return(store2);
        }
    }

    [Fact]
    public void RentScoped_AutomaticallyReturns()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 1,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        // Rent scoped
        using (var lease = _pool.RentScoped())
        {
            lease.Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
            Assert.Equal(0, _pool.AvailableCount);
        }

        // After dispose, store should be back in pool
        Assert.Equal(1, _pool.AvailableCount);
    }

    [Fact]
    public void Pool_LimitsMaxConcurrent()
    {
        var events = new PoolEventRecorder();
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            observabilityListener: events);
        Assert.SkipUnless(_pool.MaxConcurrent == 2,
            $"The disk budget here fits {_pool.MaxConcurrent} test store(s), so a capacity of 2 cannot be tested.");

        var store1 = _pool.Rent();
        var store2 = _pool.Rent();

        // A third Rent must wait. The pool reports the wait, so the test follows that event
        // instead of sleeping and assuming the third Rent has blocked by then.
        var rentedThird = false;
        var thread = new Thread(() =>
        {
            var store3 = _pool.Rent();
            rentedThird = true;
            _pool.Return(store3);
        });
        thread.Start();

        events.RentBlocked.Wait(TestContext.Current.CancellationToken);
        Assert.False(rentedThird);
        Assert.False(events.RentResumed.IsSet);

        // Return one store: the waiting Rent resumes and gets it.
        _pool.Return(store1);
        events.RentResumed.Wait(TestContext.Current.CancellationToken);

        // Join before returning store2, so every store is back before Dispose() runs.
        thread.Join();
        Assert.True(rentedThird);

        _pool.Return(store2);
    }

    [Fact]
    public void Pool_ReusesStores()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        var store1 = _pool.Rent();
        _pool.Return(store1);

        var store2 = _pool.Rent();
        _pool.Return(store2);

        // Should have created only 1 store, reused it
        Assert.Equal(1, _pool.TotalCreated);
    }

    [Fact]
    public void Pool_CreatesUpToMaxConcurrent()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 3,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        // Renting 3 stores at once needs a capacity of 3. Before this check the test assumed it,
        // and where the disk budget allowed only 2 the third Rent waited forever (omega-ubuntu,
        // 2026-10-26).
        Assert.SkipUnless(_pool.MaxConcurrent == 3,
            $"The disk budget here fits {_pool.MaxConcurrent} test store(s), so creating 3 cannot be tested.");

        var stores = new List<QuadStore>();
        for (int i = 0; i < 3; i++)
        {
            stores.Add(_pool.Rent());
        }

        Assert.Equal(3, _pool.TotalCreated);
        Assert.Equal(0, _pool.AvailableCount);

        foreach (var store in stores)
        {
            _pool.Return(store);
        }

        Assert.Equal(3, _pool.AvailableCount);
    }

    [Fact]
    public void Dispose_CleansUpAllStores()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        var store1 = _pool.Rent();
        var store2 = _pool.Rent();
        _pool.Return(store1);
        _pool.Return(store2);

        _pool.Dispose();

        // Pool should be disposed
        Assert.Throws<ObjectDisposedException>(() => _pool.Rent());
    }

    [Fact]
    public void Return_AfterDispose_DoesNotThrow()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        var store = _pool.Rent();
        _pool.Dispose();

        // Return after dispose should not throw
        _pool.Return(store);
    }

    [Fact]
    public async Task Pool_ConcurrentAccess_ThreadSafe()
    {
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 4,
            purpose: "test",
            observabilityListener: DiagnosticMessageListener.Instance);

        var tasks = new List<Task>();
        var errors = new ConcurrentList<Exception>();

        for (int i = 0; i < 20; i++)
        {
            var taskId = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    using var lease = _pool.RentScoped();
                    lease.Store.AddCurrent(
                        $"<http://ex/s{taskId}>",
                        "<http://ex/p>",
                        $"<http://ex/o{taskId}>");

                    // Simulate some work
                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(0, errors.Count);
        Assert.True(_pool.TotalCreated <= _pool.MaxConcurrent); // Never more stores than the capacity
    }

    [Fact]
    public void Pool_ReportsCapacityDecision()
    {
        var events = new PoolEventRecorder();
        _pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 3,
            purpose: "test",
            observabilityListener: events);

        Assert.NotNull(events.Capacity);
        var capacity = events.Capacity!.Value;

        Assert.Equal(_pool.BasePath, capacity.PoolPath);
        Assert.Equal(3, capacity.RequestedConcurrent);
        Assert.Equal(3, capacity.ConcurrencyLimit);
        Assert.Equal(_pool.MaxConcurrent, capacity.Capacity);
        Assert.True(capacity.AvailableBytes > 0, "Free space on the pool's filesystem should be known.");

        // The capacity is the smaller of the two limits, and Bound names the one that applied.
        var expected = capacity.DiskLimit switch
        {
            < 0 => (3, StorePoolCapacityBound.Concurrency),
            0 => (1, StorePoolCapacityBound.DiskBudgetFloor),
            < 3 => (capacity.DiskLimit, StorePoolCapacityBound.DiskBudget),
            _ => (3, StorePoolCapacityBound.Concurrency),
        };
        Assert.Equal(expected, (capacity.Capacity, capacity.Bound));
    }

    // Helper for thread-safe exception collection
    private class ConcurrentList<T>
    {
        private readonly List<T> _list = new();
        private readonly object _lock = new();

        public void Add(T item)
        {
            lock (_lock) _list.Add(item);
        }

        public int Count
        {
            get { lock (_lock) return _list.Count; }
        }
    }
}
