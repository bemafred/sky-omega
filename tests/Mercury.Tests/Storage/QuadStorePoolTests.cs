using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for QuadStorePool - bounded pool of reusable QuadStore instances.
/// </summary>
public class QuadStorePoolTests : IDisposable
{
    private QuadStorePool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
    }

    [Fact]
    public void Rent_ReturnsUsableStore()
    {
        _pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");

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
        _pool = new QuadStorePool(maxConcurrent: 1, purpose: "test");

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
        _pool = new QuadStorePool(maxConcurrent: 1, purpose: "test");

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
        _pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");

        var store1 = _pool.Rent();
        var store2 = _pool.Rent();

        // Third rent should block - use TryRent pattern with timeout
        var rentedThird = false;
        var thread = new Thread(() =>
        {
            var store3 = _pool.Rent();
            rentedThird = true;
            _pool.Return(store3);
        });
        thread.Start();

        // Give it a moment to potentially rent
        Thread.Sleep(100);
        Assert.False(rentedThird); // Should still be blocked

        // Return one store
        _pool.Return(store1);

        // Wait for third to complete
        thread.Join(1000);
        Assert.True(rentedThird);

        _pool.Return(store2);
    }

    [Fact]
    public void Pool_ReusesStores()
    {
        _pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");

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
        _pool = new QuadStorePool(maxConcurrent: 3, purpose: "test");

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
        _pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");

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
        _pool = new QuadStorePool(maxConcurrent: 2, purpose: "test");

        var store = _pool.Rent();
        _pool.Dispose();

        // Return after dispose should not throw
        _pool.Return(store);
    }

    [Fact]
    public async Task Pool_ConcurrentAccess_ThreadSafe()
    {
        _pool = new QuadStorePool(maxConcurrent: 4, purpose: "test");

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
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(0, errors.Count);
        Assert.True(_pool.TotalCreated <= 4); // Should not create more than maxConcurrent
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
