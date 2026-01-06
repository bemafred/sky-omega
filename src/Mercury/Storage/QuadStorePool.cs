using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Bounded pool of reusable QuadStore instances.
/// Limits concurrent stores to prevent disk exhaustion.
/// Thread-safe.
/// </summary>
/// <remarks>
/// <para>Use this pool in testing, SERVICE materialization, or any scenario
/// where many temporary stores are created and destroyed.</para>
///
/// <para><strong>Why pooling:</strong></para>
/// <list type="bullet">
/// <item><description>Creating/deleting store files is expensive</description></item>
/// <item><description>Parallel test execution can exhaust disk space</description></item>
/// <item><description>Clear() is cheap (reset counters) vs Dispose+Create (file I/O)</description></item>
/// </list>
///
/// <para><strong>Cross-process coordination:</strong></para>
/// <para>
/// When <c>useCrossProcessGate</c> is enabled (default for testing), the pool coordinates
/// with other processes via <see cref="CrossProcessStoreGate"/>. This prevents disk exhaustion
/// when multiple test runner processes (e.g., NCrunch) create pools simultaneously.
/// </para>
///
/// <para><strong>Usage:</strong></para>
/// <code>
/// using var pool = new QuadStorePool(maxConcurrent: 4);
///
/// // Option 1: Manual rent/return
/// var store = pool.Rent();
/// try { /* use store */ }
/// finally { pool.Return(store); }
///
/// // Option 2: Scoped (RAII)
/// using var lease = pool.RentScoped();
/// var store = lease.Store;
/// // Automatically returned when lease is disposed
/// </code>
/// </remarks>
public sealed class QuadStorePool : IDisposable
{
    /// <summary>
    /// Default fraction of available disk space to use as budget (33%).
    /// </summary>
    public const double DefaultDiskBudgetFraction = 0.33;

    /// <summary>
    /// Fixed hash table size in bytes (512MB) - not configurable, included in estimates.
    /// </summary>
    private const long HashTableSizeBytes = 512L << 20;

    private readonly ConcurrentBag<QuadStore> _available = new();
    private readonly List<(TempPath path, QuadStore store)> _all = new();
    private readonly SemaphoreSlim _gate;
    private readonly object _createLock = new();
    private readonly string _purpose;
    private readonly StorageOptions _storageOptions;
    private readonly bool _useCrossProcessGate;
    private int _globalSlotsHeld;
    private bool _disposed;

    /// <summary>
    /// Creates a pool with bounded concurrency.
    /// </summary>
    /// <param name="maxConcurrent">Maximum concurrent stores. Defaults to ProcessorCount.</param>
    /// <param name="purpose">Category for TempPath naming (e.g., "test", "service").</param>
    /// <param name="useCrossProcessGate">Enable cross-process coordination via <see cref="CrossProcessStoreGate"/>. Default: false.</param>
    public QuadStorePool(int maxConcurrent = 0, string purpose = "pooled", bool useCrossProcessGate = false)
        : this(null, DefaultDiskBudgetFraction, maxConcurrent, purpose, useCrossProcessGate)
    {
    }

    /// <summary>
    /// Creates a pool with disk-budget-aware concurrency limiting.
    /// The pool size is calculated as the minimum of:
    /// - CPU count (or explicit maxConcurrent)
    /// - Available disk space × diskBudgetFraction ÷ estimated per-store size
    /// </summary>
    /// <param name="storageOptions">Storage options including initial sizes. Use StorageOptions.ForTesting for minimal footprint.</param>
    /// <param name="diskBudgetFraction">Fraction of available disk space to use as budget (0.0-1.0). Default: 0.33 (33%).</param>
    /// <param name="maxConcurrent">Maximum concurrent stores (0 = ProcessorCount). Disk budget may reduce this further.</param>
    /// <param name="purpose">Category for TempPath naming (e.g., "test", "service").</param>
    /// <param name="useCrossProcessGate">
    /// Enable cross-process coordination via <see cref="CrossProcessStoreGate"/>.
    /// When enabled, store creation blocks until a global slot is available across ALL processes
    /// on the machine. This prevents disk exhaustion when multiple test runners (e.g., NCrunch)
    /// create pools simultaneously. Default: false.
    /// </param>
    public QuadStorePool(StorageOptions? storageOptions,
                         double diskBudgetFraction = DefaultDiskBudgetFraction,
                         int maxConcurrent = 0,
                         string purpose = "pooled",
                         bool useCrossProcessGate = false)
    {
        if (diskBudgetFraction <= 0 || diskBudgetFraction > 1.0)
            throw new ArgumentOutOfRangeException(nameof(diskBudgetFraction),
                "Disk budget fraction must be between 0 (exclusive) and 1.0 (inclusive)");

        _storageOptions = storageOptions ?? StorageOptions.Default;
        _purpose = purpose;
        _useCrossProcessGate = useCrossProcessGate;

        var cpuLimit = maxConcurrent > 0 ? maxConcurrent : Environment.ProcessorCount;
        var diskLimit = CalculateDiskLimit(_storageOptions, diskBudgetFraction);

        var max = Math.Max(1, Math.Min(cpuLimit, diskLimit));
        _gate = new SemaphoreSlim(max, max);
    }

    /// <summary>
    /// Maximum number of concurrent stores this pool allows.
    /// </summary>
    public int MaxConcurrent => _gate.CurrentCount + (TotalCreated - AvailableCount);

    /// <summary>
    /// Calculates the maximum number of stores based on available disk space.
    /// </summary>
    private static int CalculateDiskLimit(StorageOptions options, double fraction)
    {
        var tempPath = Path.GetTempPath();
        var available = DiskSpaceChecker.GetAvailableSpace(tempPath);

        if (available < 0)
            return int.MaxValue; // Can't determine disk space, don't limit

        var budget = (long)(available * fraction);
        var perStoreEstimate = EstimateStoreSize(options);

        if (perStoreEstimate <= 0)
            return int.MaxValue;

        return (int)(budget / perStoreEstimate);
    }

    /// <summary>
    /// Estimates the disk footprint of a single QuadStore based on options.
    /// </summary>
    private static long EstimateStoreSize(StorageOptions options)
    {
        // 4 indexes + atom data + hash table (fixed 512MB) + atom offsets
        return (options.IndexInitialSizeBytes * 4)
             + options.AtomDataInitialSizeBytes
             + HashTableSizeBytes
             + (options.AtomOffsetInitialCapacity * sizeof(long));
    }

    /// <summary>
    /// Rent a store from the pool. Blocks if pool is exhausted.
    /// Store is cleared before return - guaranteed empty.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if pool is disposed.</exception>
    /// <exception cref="TimeoutException">Thrown if cross-process gate acquisition times out.</exception>
    public QuadStore Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _gate.Wait();

        if (_available.TryTake(out var store))
        {
            store.Clear();
            return store;
        }

        // Create new store - need global slot if cross-process gate is enabled
        if (_useCrossProcessGate)
        {
            if (!CrossProcessStoreGate.Instance.Acquire(TimeSpan.FromSeconds(60)))
            {
                _gate.Release(); // Release local slot since we failed
                throw new TimeoutException(
                    $"Timed out waiting for cross-process store slot. " +
                    $"Max global stores: {CrossProcessStoreGate.Instance.MaxGlobalStores}. " +
                    $"This usually means too many test processes are running in parallel.");
            }

            Interlocked.Increment(ref _globalSlotsHeld);
        }

        try
        {
            lock (_createLock)
            {
                var path = TempPath.Create(_purpose, Guid.NewGuid().ToString("N")[..8], unique: false);
                path.EnsureClean();
                path.MarkOwnership();
                var newStore = new QuadStore(path, null, null, _storageOptions);
                _all.Add((path, newStore));
                return newStore;
            }
        }
        catch
        {
            // Store creation failed - release global slot
            if (_useCrossProcessGate)
            {
                CrossProcessStoreGate.Instance.Release();
                Interlocked.Decrement(ref _globalSlotsHeld);
            }
            throw;
        }
    }

    /// <summary>
    /// Return a store to the pool for reuse.
    /// </summary>
    /// <param name="store">The store to return.</param>
    /// <remarks>
    /// The store is NOT cleared on return - clearing happens on next Rent().
    /// This amortizes the clear cost and allows inspection of returned store contents
    /// during debugging.
    /// </remarks>
    public void Return(QuadStore store)
    {
        if (_disposed)
        {
            // Pool is disposed - semaphore is also disposed, nothing to do
            // The store was already disposed by pool's Dispose() method
            return;
        }

        _available.Add(store);
        _gate.Release();
    }

    /// <summary>
    /// Rent with automatic return on dispose.
    /// </summary>
    /// <returns>A scoped lease that returns the store when disposed.</returns>
    public PooledStoreLease RentScoped() => new(this, Rent());

    /// <summary>
    /// Number of stores currently available in the pool.
    /// </summary>
    public int AvailableCount => _available.Count;

    /// <summary>
    /// Total number of stores created by this pool.
    /// </summary>
    public int TotalCreated
    {
        get
        {
            lock (_createLock)
            {
                return _all.Count;
            }
        }
    }

    /// <summary>
    /// Number of cross-process slots currently held by this pool.
    /// Only non-zero when <c>useCrossProcessGate</c> was enabled.
    /// </summary>
    public int GlobalSlotsHeld => _globalSlotsHeld;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain available stores (just for bookkeeping)
        while (_available.TryTake(out _)) { }

        // Dispose all stores and cleanup paths
        lock (_createLock)
        {
            foreach (var (path, store) in _all)
            {
                store.Dispose();
                TempPath.SafeCleanup(path);
            }
            _all.Clear();
        }

        // Release global slots
        if (_useCrossProcessGate)
        {
            var slotsToRelease = Interlocked.Exchange(ref _globalSlotsHeld, 0);
            for (int i = 0; i < slotsToRelease; i++)
            {
                CrossProcessStoreGate.Instance.Release();
            }
        }

        _gate.Dispose();
    }
}

/// <summary>
/// RAII wrapper for pooled store - returns to pool on dispose.
/// </summary>
public readonly struct PooledStoreLease : IDisposable
{
    private readonly QuadStorePool _pool;

    /// <summary>
    /// The rented store.
    /// </summary>
    public QuadStore Store { get; }

    internal PooledStoreLease(QuadStorePool pool, QuadStore store)
    {
        _pool = pool;
        Store = store;
    }

    /// <summary>
    /// Returns the store to the pool.
    /// </summary>
    public void Dispose() => _pool.Return(Store);
}
