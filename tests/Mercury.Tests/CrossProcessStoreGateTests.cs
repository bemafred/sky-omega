using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

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

        // Note: Other tests may be using the singleton concurrently,
        // so we only verify relative changes, not absolute counts
        var countBefore = gate.AcquiredCount;

        Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)));
        var countAfterAcquire = gate.AcquiredCount;
        Assert.True(countAfterAcquire > countBefore, "Count should increase after acquire");

        gate.Release();
        var countAfterRelease = gate.AcquiredCount;
        Assert.True(countAfterRelease < countAfterAcquire, "Count should decrease after release");
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

        // Strategy should be one of the known types
        // Windows: NamedSemaphore, Unix: FileBased (named semaphores not supported)
        Assert.True(
            gate.StrategyName == "NamedSemaphore" ||
            gate.StrategyName == "FileBased",
            $"Unexpected strategy: {gate.StrategyName}");
    }

    [Fact]
    public void Singleton_AcquireMultiple_Works()
    {
        var gate = CrossProcessStoreGate.Instance;
        var initialCount = gate.AcquiredCount;
        var toAcquire = Math.Min(3, gate.MaxGlobalStores - initialCount);

        // Acquire multiple slots
        for (int i = 0; i < toAcquire; i++)
        {
            Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)), $"Failed to acquire slot {i + 1}");
        }

        Assert.Equal(initialCount + toAcquire, gate.AcquiredCount);

        // Release all
        for (int i = 0; i < toAcquire; i++)
        {
            gate.Release();
        }

        Assert.Equal(initialCount, gate.AcquiredCount);
    }

    [Fact]
    public void Singleton_ReleaseUnblocksWaiter()
    {
        var gate = CrossProcessStoreGate.Instance;
        var initialCount = gate.AcquiredCount;
        var availableSlots = gate.MaxGlobalStores - initialCount;

        if (availableSlots < 2)
        {
            // Not enough slots available for this test
            return;
        }

        // Acquire all available slots except one
        var toAcquire = availableSlots;
        for (int i = 0; i < toAcquire; i++)
        {
            gate.Acquire();
        }

        var acquired = false;
        var waiterStarted = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            waiterStarted.Set();
            acquired = gate.Acquire(TimeSpan.FromSeconds(5));
            if (acquired) gate.Release();
        });
        thread.Start();

        // Wait for thread to start
        waiterStarted.Wait();
        Thread.Sleep(50); // Give it time to attempt acquire

        // Release one slot
        gate.Release();

        // Thread should complete
        thread.Join(2000);
        Assert.True(acquired);

        // Release remaining
        for (int i = 0; i < toAcquire - 1; i++)
        {
            gate.Release();
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

        // Multiple tasks acquiring and releasing
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                if (gate.Acquire(TimeSpan.FromSeconds(10)))
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
        var initialCount = gate.AcquiredCount;
        var availableSlots = gate.MaxGlobalStores - initialCount;

        if (availableSlots < 1)
        {
            // Can't test - no slots available
            return;
        }

        // Acquire all available slots
        for (int i = 0; i < availableSlots; i++)
        {
            Assert.True(gate.Acquire(TimeSpan.FromSeconds(1)), $"Failed to acquire slot {i + 1}");
        }

        // Next acquire should timeout
        Assert.False(gate.Acquire(TimeSpan.FromMilliseconds(100)));

        // Release all
        for (int i = 0; i < availableSlots; i++)
        {
            gate.Release();
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

/// <summary>
/// Cross-process integration tests that spawn child processes.
/// These tests verify that the gate actually coordinates across process boundaries.
/// </summary>
public class CrossProcessIntegrationTests
{
    /// <summary>
    /// Verifies cross-process coordination by spawning a child process that tries
    /// to acquire slots while the parent holds them.
    /// </summary>
    /// <remarks>
    /// This test only works on Windows where named semaphores are supported.
    /// On Unix, each process uses file-based locking with separate lock directories,
    /// so cross-process coordination works differently.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public void CrossProcess_Coordination_Works()
    {
        // This test only works on Windows with named semaphores
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Unix, named semaphores aren't supported, so the gate uses
            // file-based locking. Cross-process coordination still works but
            // through a different mechanism that's harder to test this way.
            return;
        }

        // Skip if test tool not available
        var testToolPath = FindTestTool();
        if (testToolPath == null)
        {
            return;
        }

        var gate = CrossProcessStoreGate.Instance;
        var availableSlots = gate.MaxGlobalStores - gate.AcquiredCount;

        if (availableSlots < 2)
        {
            // Not enough slots to test cross-process blocking
            return;
        }

        // Acquire most slots, leaving just 1
        var toAcquire = availableSlots - 1;
        for (int i = 0; i < toAcquire; i++)
        {
            gate.Acquire();
        }

        try
        {
            // Spawn child process that tries to acquire 2 slots
            // It should get 1 but block/timeout on the 2nd
            var result = RunTestTool(testToolPath, "crossprocess 2");

            // Child should report that it acquired 1 slot and blocked on the 2nd
            Assert.Contains("ACQUIRED", result);
            Assert.Contains("BLOCKED", result);
        }
        finally
        {
            // Release our slots
            for (int i = 0; i < toAcquire; i++)
            {
                gate.Release();
            }
        }
    }

    private static string? FindTestTool()
    {
        // Look for the test tool relative to the test assembly
        var assemblyDir = Path.GetDirectoryName(typeof(CrossProcessIntegrationTests).Assembly.Location);
        if (assemblyDir == null) return null;

        // Navigate up to repo root and find tools/SemaphoreTest
        var dir = new DirectoryInfo(assemblyDir);
        while (dir != null && dir.Name != "sky-omega")
        {
            dir = dir.Parent;
        }

        if (dir == null) return null;

        var toolProject = Path.Combine(dir.FullName, "tools", "SemaphoreTest", "SemaphoreTest.csproj");
        return File.Exists(toolProject) ? toolProject : null;
    }

    private static string RunTestTool(string projectPath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectPath}\" -- {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start test tool");

        // Send Enter to release slots and exit
        Task.Delay(500).ContinueWith(_ =>
        {
            try { process.StandardInput.WriteLine(); }
            catch { }
        });

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        return output;
    }
}
