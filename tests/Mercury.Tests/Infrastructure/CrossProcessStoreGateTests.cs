using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Infrastructure;

/// <summary>
/// Tests for CrossProcessStoreGate - cross-process coordination for QuadStore pool.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Important:</strong> On Windows, all CrossProcessStoreGate instances share
/// a single named semaphore. The first instance to create it determines the max count.
/// This is BY DESIGN - it enables true cross-process coordination.
/// </para>
/// <para>
/// Tests that need specific max counts should use the singleton's max, not create
/// isolated gates with different max values.
/// </para>
/// </remarks>
public class CrossProcessStoreGateTests
{
    [Fact]
    public void Singleton_AcquireAndRelease_Works()
    {
        var gate = CrossProcessStoreGate.Instance;

        // Note: Other tests/processes may be using the singleton concurrently,
        // so we only verify relative changes, not absolute counts.
        // Use longer timeout to handle contention from other NCrunch processes.
        var countBefore = gate.AcquiredCount;

        if (!gate.Acquire(TimeSpan.FromSeconds(30)))
        {
            // All global slots held by other processes — test is not meaningful
            return;
        }

        try
        {
            var countAfterAcquire = gate.AcquiredCount;
            Assert.True(countAfterAcquire > countBefore, "Count should increase after acquire");

            gate.Release();
            var countAfterRelease = gate.AcquiredCount;
            Assert.True(countAfterRelease < countAfterAcquire, "Count should decrease after release");
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    [Fact]
    public void Singleton_HasReasonableLimit()
    {
        var gate = CrossProcessStoreGate.Instance;

        // The singleton should have a reasonable max based on disk space
        Assert.True(gate.MaxGlobalStores >= 2, $"Max too low: {gate.MaxGlobalStores}");
        Assert.True(gate.MaxGlobalStores <= 12, $"Max too high: {gate.MaxGlobalStores}");
    }

    [Fact]
    public void Singleton_UsesExpectedStrategy()
    {
        var gate = CrossProcessStoreGate.Instance;

        Assert.Equal("FileBased", gate.StrategyName);
    }

    [Fact]
    public void Singleton_AcquireMultiple_Works()
    {
        var gate = CrossProcessStoreGate.Instance;
        var initialCount = gate.AcquiredCount;

        // Try to acquire up to 3 slots with timeout (other processes may hold slots)
        var acquired = 0;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                if (!gate.Acquire(TimeSpan.FromSeconds(5)))
                    break; // Can't get more — other processes hold them
                acquired++;
            }

            if (acquired == 0)
            {
                // All global slots held by other processes — test is not meaningful
                return;
            }

            Assert.Equal(initialCount + acquired, gate.AcquiredCount);
        }
        finally
        {
            // Release all acquired slots
            for (int i = 0; i < acquired; i++)
            {
                gate.Release();
            }
        }

        Assert.Equal(initialCount, gate.AcquiredCount);
    }

    [Fact]
    public void Singleton_ReleaseUnblocksWaiter()
    {
        var gate = CrossProcessStoreGate.Instance;

        // Try to acquire all available slots with timeout (other processes may hold slots)
        var acquired = 0;
        try
        {
            while (gate.Acquire(TimeSpan.FromSeconds(5)))
            {
                acquired++;
                if (acquired >= gate.MaxGlobalStores)
                    break; // Safety limit
            }

            if (acquired < 1)
            {
                // Can't saturate the gate — other processes hold all slots
                return;
            }

            // Now all slots are held (by us + other processes).
            // Start a waiter thread that will try to acquire a slot.
            var waiterAcquired = false;
            var waiterStarted = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                waiterStarted.Set();
                waiterAcquired = gate.Acquire(TimeSpan.FromSeconds(10));
                if (waiterAcquired) gate.Release();
            });
            thread.Start();

            // Wait for thread to start
            waiterStarted.Wait();
            Thread.Sleep(50); // Give it time to attempt acquire

            // Release one slot — should unblock the waiter
            gate.Release();
            acquired--;

            // Thread should complete
            thread.Join(5000);
            Assert.True(waiterAcquired, "Waiter should have acquired a slot after release");
        }
        finally
        {
            // Release remaining slots
            for (int i = 0; i < acquired; i++)
            {
                gate.Release();
            }
        }
    }

    [Fact]
    public async Task Singleton_ConcurrentAccess_ThreadSafe()
    {
        var gate = CrossProcessStoreGate.Instance;
        var initialCount = gate.AcquiredCount;

        var tasks = new List<Task>();
        var successCount = 0;
        var failCount = 0;

        // Multiple tasks acquiring and releasing — use longer timeout for NCrunch contention
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                if (gate.Acquire(TimeSpan.FromSeconds(30)))
                {
                    Interlocked.Increment(ref successCount);
                    Thread.Sleep(5); // Brief hold
                    gate.Release();
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // All should eventually succeed (they acquire and release)
        Assert.Equal(20, successCount);
        Assert.Equal(0, failCount);
        Assert.Equal(initialCount, gate.AcquiredCount);
    }

    [Fact]
    public void Singleton_ExceedsMax_BlocksAndTimesOut()
    {
        var gate = CrossProcessStoreGate.Instance;

        // Try to acquire up to 3 slots with timeout (other processes may hold slots)
        var acquired = 0;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                if (!gate.Acquire(TimeSpan.FromSeconds(5)))
                    break;
                acquired++;
            }

            // Only test overflow behavior if we hold ALL global slots
            if (gate.AcquiredCount >= gate.MaxGlobalStores)
            {
                // Next acquire should timeout
                Assert.False(gate.Acquire(TimeSpan.FromMilliseconds(200)));
            }
            // Otherwise, other processes hold some slots — we can't reliably test overflow
        }
        finally
        {
            // Release all acquired slots
            for (int i = 0; i < acquired; i++)
            {
                gate.Release();
            }
        }
    }

    // --- QuadStorePool integration tests ---

    [Fact]
    public void Pool_WithCrossProcessGate_TracksGlobalSlots()
    {
        using var pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test-track",
            useCrossProcessGate: true);

        // Initially no slots held by this pool
        Assert.Equal(0, pool.GlobalSlotsHeld);

        // Rent a store - should acquire global slot
        // May throw TimeoutException if all global slots are held by other processes
        QuadStore store1;
        try
        {
            store1 = pool.Rent();
        }
        catch (TimeoutException)
        {
            // All global slots held by other processes — test is not meaningful
            return;
        }

        Assert.Equal(1, pool.GlobalSlotsHeld);

        // Rent another
        QuadStore store2;
        try
        {
            store2 = pool.Rent();
        }
        catch (TimeoutException)
        {
            // Only got 1 slot — still valid, just verify what we have
            pool.Return(store1);
            Assert.Equal(1, pool.GlobalSlotsHeld);
            return;
        }

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
            purpose: "test-reuse",
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
            purpose: "test-nogate",
            useCrossProcessGate: false);

        var store = pool.Rent();
        pool.Return(store);

        // No global slots when gate is disabled
        Assert.Equal(0, pool.GlobalSlotsHeld);
    }

    [Fact]
    public void Pool_Dispose_ReleasesGlobalSlots()
    {
        var pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            maxConcurrent: 2,
            purpose: "test-dispose",
            useCrossProcessGate: true);

        var store = pool.Rent();
        pool.Return(store);

        Assert.Equal(1, pool.GlobalSlotsHeld);

        pool.Dispose();

        // Global slots should be released
        Assert.Equal(0, pool.GlobalSlotsHeld);
    }
}
