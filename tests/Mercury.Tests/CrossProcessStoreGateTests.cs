using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for CrossProcessStoreGate - cross-process coordination for QuadStore pool.
/// </summary>
public class CrossProcessStoreGateTests
{
    [Fact]
    public void Gate_AcquireAndRelease_Works()
    {
        using var gate = new CrossProcessStoreGate(3);

        Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, gate.AcquiredCount);

        gate.Release();
        Assert.Equal(0, gate.AcquiredCount);
    }

    [Fact]
    public void Gate_AcquireMultiple_UpToMax()
    {
        using var gate = new CrossProcessStoreGate(3);

        Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)));
        Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)));
        Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)));

        Assert.Equal(3, gate.AcquiredCount);
    }

    [Fact]
    public void Gate_ExceedsMax_BlocksAndTimesOut()
    {
        using var gate = new CrossProcessStoreGate(2);

        // Acquire both slots
        Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)));
        Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)));

        // Third should timeout
        Assert.False(gate.Acquire(TimeSpan.FromMilliseconds(100)));

        Assert.Equal(2, gate.AcquiredCount);
    }

    [Fact]
    public void Gate_ReleaseUnblocksWaiter()
    {
        using var gate = new CrossProcessStoreGate(2);

        // Acquire both slots
        gate.Acquire();
        gate.Acquire();

        var acquired = false;
        var waiterStarted = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            waiterStarted.Set();
            acquired = gate.Acquire(TimeSpan.FromSeconds(5));
        });
        thread.Start();

        // Wait for thread to start
        waiterStarted.Wait();
        Thread.Sleep(50); // Give it time to block on Acquire

        // Release one slot
        gate.Release();

        // Thread should complete
        thread.Join(2000);
        Assert.True(acquired);
    }

    [Fact]
    public void Gate_Dispose_ReleasesHeldSlots()
    {
        var gate = new CrossProcessStoreGate(3);

        gate.Acquire();
        gate.Acquire();
        Assert.Equal(2, gate.AcquiredCount);

        gate.Dispose();

        // After dispose, AcquiredCount should be 0
        Assert.Equal(0, gate.AcquiredCount);
    }

    [Fact]
    public void Gate_UsesExpectedStrategy()
    {
        using var gate = new CrossProcessStoreGate(3);

        // Strategy should be one of the known types
        Assert.True(
            gate.StrategyName == "NamedSemaphore" ||
            gate.StrategyName == "FileBased",
            $"Unexpected strategy: {gate.StrategyName}");
    }

    [Fact]
    public void Gate_SingletonInstance_HasReasonableLimit()
    {
        // The singleton should have a reasonable max
        Assert.True(CrossProcessStoreGate.Instance.MaxGlobalStores >= 2);
        Assert.True(CrossProcessStoreGate.Instance.MaxGlobalStores <= 12);
    }

    [Fact]
    public async Task Gate_ConcurrentAccess_ThreadSafe()
    {
        using var gate = new CrossProcessStoreGate(4);

        var tasks = new List<Task>();
        var successCount = 0;
        var failCount = 0;

        // 20 tasks trying to acquire from a pool of 4
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                if (gate.Acquire(TimeSpan.FromSeconds(5)))
                {
                    Interlocked.Increment(ref successCount);
                    Thread.Sleep(10); // Simulate some work
                    gate.Release();
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // All should eventually succeed
        Assert.Equal(20, successCount);
        Assert.Equal(0, failCount);
    }

    [Fact]
    public void Pool_WithCrossProcessGate_TracksGlobalSlots()
    {
        using var pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            useCrossProcessGate: true);

        // Initially no slots held
        Assert.Equal(0, pool.GlobalSlotsHeld);

        // Rent a store - should acquire global slot
        var store1 = pool.Rent();
        Assert.Equal(1, pool.GlobalSlotsHeld);

        // Rent another
        var store2 = pool.Rent();
        Assert.Equal(2, pool.GlobalSlotsHeld);

        // Return to pool - slots still held (store exists)
        pool.Return(store1);
        pool.Return(store2);

        // Slots are held until pool is disposed
        Assert.Equal(2, pool.GlobalSlotsHeld);
    }

    [Fact]
    public void Pool_WithCrossProcessGate_ReusesStoresWithoutNewSlots()
    {
        using var pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            useCrossProcessGate: true);

        // Rent and return
        var store1 = pool.Rent();
        pool.Return(store1);
        Assert.Equal(1, pool.GlobalSlotsHeld);

        // Rent again - should reuse, not create new
        var store2 = pool.Rent();
        pool.Return(store2);

        // Still only 1 slot held (reused existing store)
        Assert.Equal(1, pool.GlobalSlotsHeld);
        Assert.Equal(1, pool.TotalCreated);
    }

    [Fact]
    public void Pool_WithoutCrossProcessGate_DoesNotHoldGlobalSlots()
    {
        using var pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            useCrossProcessGate: false);

        var store = pool.Rent();
        pool.Return(store);

        // No global slots when gate is disabled
        Assert.Equal(0, pool.GlobalSlotsHeld);
    }

    [Fact]
    public void Pool_Dispose_ReleasesGlobalSlots()
    {
        var initialHeld = CrossProcessStoreGate.Instance.AcquiredCount;

        var pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test",
            useCrossProcessGate: true);

        var store = pool.Rent();
        pool.Return(store);

        Assert.Equal(1, pool.GlobalSlotsHeld);

        pool.Dispose();

        // Global slots should be released back to singleton
        Assert.Equal(0, pool.GlobalSlotsHeld);
    }
}
