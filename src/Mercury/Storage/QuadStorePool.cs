// QuadStorePool.cs
// Bounded pool of reusable QuadStore instances with named store support.
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Bounded pool of reusable QuadStore instances with named store support.
/// Limits concurrent stores to prevent disk exhaustion. Thread-safe.
/// </summary>
/// <remarks>
/// <para>Use this pool in testing, SERVICE materialization, pruning workflows,
/// or any scenario where stores are created, switched, and destroyed.</para>
///
/// <para><strong>Two usage modes:</strong></para>
/// <list type="number">
/// <item><description><b>Anonymous pooling</b> via <see cref="Rent"/>/<see cref="Return"/>:
/// Temporary stores for transient operations (e.g., SERVICE clause materialization).</description></item>
/// <item><description><b>Named stores</b> via <see cref="this[string]"/> indexer:
/// Persistent stores with logical names (e.g., "primary", "secondary" for pruning).</description></item>
/// </list>
///
/// <para><strong>Directory structure:</strong></para>
/// <code>
/// {basePath}/
///   pool.json                 # Metadata: name->GUID mappings, active store, settings
///   stores/
///     0194a3f8c2e1.../        # "primary" maps here (GUID v7 prefix)
///     0194a3f9b7d4.../        # "secondary" maps here
///   pooled/
///     0194c3d4e5f6.../        # Anonymous pooled stores
/// </code>
///
/// <para><strong>Pruning workflow example:</strong></para>
/// <code>
/// using var pool = new QuadStorePool("/data/my-kb");
/// LoadRdf(pool["primary"], sourceFile);  // Load data
/// // ... soft deletes accumulate ...
/// new PruningTransfer(pool["primary"], pool["secondary"]).Execute();
/// pool.Switch("primary", "secondary");   // Atomic swap
/// pool.Clear("secondary");               // Ready for next cycle
/// </code>
///
/// <para><strong>Cross-process coordination:</strong></para>
/// <para>
/// When <c>UseCrossProcessGate</c> is enabled, the pool coordinates with other processes
/// via <see cref="CrossProcessStoreGate"/>. This prevents disk exhaustion when multiple
/// test runner processes (e.g., NCrunch) create pools simultaneously.
/// </para>
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

    private const string PoolJsonFileName = "pool.json";
    private const string StoresDirectoryName = "stores";
    private const string PooledDirectoryName = "pooled";

    // === Anonymous pool state (Rent/Return) ===
    private readonly ConcurrentBag<QuadStore> _available = new();
    private readonly List<(string path, QuadStore store)> _pooledStores = new();
    private readonly SemaphoreSlim _poolGate;
    private readonly object _poolLock = new();

    // === Named store state ===
    private readonly Dictionary<string, QuadStore> _namedStores = new();
    private readonly object _namedLock = new();
    private PoolMetadata _metadata;
    private string? _activeName;

    // === Common state ===
    private readonly string? _basePath;
    private bool _isTemporary;
    private readonly StorageOptions _storageOptions;
    private readonly bool _useCrossProcessGate;
    private readonly long _maxDiskBytes;
    private int _globalSlotsHeld;
    private bool _disposed;

    #region Constructors

    /// <summary>
    /// Creates a temporary pool with bounded concurrency (legacy API).
    /// Stores are created in system temp directory and cleaned up on dispose.
    /// </summary>
    /// <param name="maxConcurrent">Maximum concurrent stores. Defaults to ProcessorCount.</param>
    /// <param name="purpose">Category for TempPath naming (e.g., "test", "service").</param>
    /// <param name="useCrossProcessGate">Enable cross-process coordination. Default: false.</param>
    public QuadStorePool(int maxConcurrent = 0, string purpose = "pooled", bool useCrossProcessGate = false)
        : this(null, DefaultDiskBudgetFraction, maxConcurrent, purpose, useCrossProcessGate)
    {
    }

    /// <summary>
    /// Creates a temporary pool with disk-budget-aware concurrency limiting (legacy API).
    /// </summary>
    /// <param name="storageOptions">Storage options including initial sizes.</param>
    /// <param name="diskBudgetFraction">Fraction of available disk space to use (0.0-1.0).</param>
    /// <param name="maxConcurrent">Maximum concurrent stores (0 = ProcessorCount).</param>
    /// <param name="purpose">Category for TempPath naming.</param>
    /// <param name="useCrossProcessGate">Enable cross-process coordination.</param>
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
        _useCrossProcessGate = useCrossProcessGate;
        _isTemporary = true;
        _basePath = null;
        _metadata = new PoolMetadata();

        // Create temp base path for this pool instance
        var tempPath = TempPath.Create(purpose, Guid.CreateVersion7().ToString("N")[..12], unique: false);
        tempPath.EnsureClean();
        tempPath.MarkOwnership();
        _basePath = tempPath.FullPath;

        var cpuLimit = maxConcurrent > 0 ? maxConcurrent : Environment.ProcessorCount;
        var diskLimit = CalculateDiskLimit(_storageOptions, diskBudgetFraction, _basePath);
        var max = Math.Max(1, Math.Min(cpuLimit, diskLimit));
        _poolGate = new SemaphoreSlim(max, max);
        _maxDiskBytes = (long)(DiskSpaceChecker.GetAvailableSpace(_basePath) * diskBudgetFraction);
    }

    /// <summary>
    /// Creates a persistent pool at the specified base path with named store support.
    /// </summary>
    /// <param name="basePath">Base directory for the pool. Created if it doesn't exist.</param>
    /// <param name="options">Pool configuration options. Default: <see cref="QuadStorePoolOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="basePath"/> is null.</exception>
    public QuadStorePool(string basePath, QuadStorePoolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(basePath);

        var opts = options ?? QuadStorePoolOptions.Default;
        _basePath = Path.GetFullPath(basePath);
        _isTemporary = false;
        _storageOptions = opts.StorageOptions;
        _useCrossProcessGate = opts.UseCrossProcessGate;

        // Ensure directories exist
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(StoresPath);
        Directory.CreateDirectory(PooledPath);

        // Load or create metadata
        _metadata = PoolMetadata.Load(PoolJsonPath);
        _activeName = _metadata.Active;

        // Calculate disk budget
        if (opts.MaxDiskBytes > 0)
        {
            _maxDiskBytes = opts.MaxDiskBytes;
        }
        else
        {
            var available = DiskSpaceChecker.GetAvailableSpace(_basePath);
            _maxDiskBytes = available > 0 ? (long)(available * opts.DiskBudgetFraction) : long.MaxValue;
        }

        // Initialize pool gate
        var poolLimit = opts.MaxPooledStores > 0 ? opts.MaxPooledStores : 8;
        _poolGate = new SemaphoreSlim(poolLimit, poolLimit);

        // Rehydrate named stores from metadata
        RehydrateNamedStores();
    }

    /// <summary>
    /// Creates a temporary pool for isolated testing or transient operations.
    /// The pool is created in the system temp directory with a unique path
    /// and automatically cleaned up on dispose.
    /// </summary>
    /// <param name="purpose">Descriptive name for the temp path (e.g., "unit-test", "prune").</param>
    /// <param name="options">Pool configuration options.</param>
    /// <returns>A new temporary pool.</returns>
    public static QuadStorePool CreateTemp(string? purpose = null, QuadStorePoolOptions? options = null)
    {
        var opts = options ?? QuadStorePoolOptions.Default;
        var category = purpose ?? "pool";
        var tempPath = TempPath.Create(category, Guid.CreateVersion7().ToString("N")[..12], unique: false);
        tempPath.EnsureClean();
        tempPath.MarkOwnership();

        var pool = new QuadStorePool(tempPath.FullPath, opts);
        pool._isTemporary = true;
        return pool;
    }

    #endregion

    #region Named Store API

    /// <summary>
    /// Gets a named store by name. Creates the store on first access.
    /// </summary>
    /// <param name="name">Logical name for the store (e.g., "primary", "secondary").</param>
    /// <returns>The QuadStore instance.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if pool is disposed.</exception>
    /// <exception cref="InsufficientDiskSpaceException">Thrown if disk budget would be exceeded.</exception>
    public QuadStore this[string name]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            lock (_namedLock)
            {
                if (_namedStores.TryGetValue(name, out var existing))
                    return existing;

                // Create new named store
                var guid = Guid.CreateVersion7().ToString("N")[..12];
                var storePath = Path.Combine(StoresPath, guid);

                // Check disk budget before creation
                EnsureDiskBudget(storePath, "CreateNamedStore");

                Directory.CreateDirectory(storePath);
                var store = new QuadStore(storePath, null, null, _storageOptions);

                _namedStores[name] = store;
                _metadata.Stores[name] = guid;

                // Set as active if this is the first store
                if (_activeName == null)
                {
                    _activeName = name;
                    _metadata.Active = name;
                }

                SaveMetadata();
                return store;
            }
        }
    }

    /// <summary>
    /// Gets the currently active store.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no active store is set.</exception>
    public QuadStore Active
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var name = _activeName ?? throw new InvalidOperationException("No active store is set.");
            return this[name];
        }
    }

    /// <summary>
    /// Gets the name of the currently active store, or null if not set.
    /// </summary>
    public string? ActiveName => _activeName;

    /// <summary>
    /// Gets the names of all named stores.
    /// </summary>
    public IReadOnlyList<string> StoreNames
    {
        get
        {
            lock (_namedLock)
            {
                return _metadata.Stores.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// Sets the active store by name.
    /// </summary>
    /// <param name="name">Name of the store to make active.</param>
    /// <exception cref="KeyNotFoundException">Thrown if the store doesn't exist.</exception>
    public void SetActive(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_namedLock)
        {
            if (!_metadata.Stores.ContainsKey(name))
                throw new KeyNotFoundException($"Store '{name}' does not exist.");

            _activeName = name;
            _metadata.Active = name;
            SaveMetadata();
        }
    }

    /// <summary>
    /// Atomically swaps the physical stores for two logical names.
    /// After this operation, accessing store "a" returns what was "b" and vice versa.
    /// </summary>
    /// <param name="a">First store name.</param>
    /// <param name="b">Second store name.</param>
    /// <remarks>
    /// <para>This is a metadata-only operation. The physical directories remain unchanged;
    /// only the name→GUID mappings are swapped in pool.json.</para>
    /// <para>Existing QuadStore references remain valid but will now be accessed via the
    /// swapped name. This is intentional for pruning workflows where you want
    /// <c>pool["primary"]</c> to return the freshly-pruned data.</para>
    /// </remarks>
    /// <exception cref="KeyNotFoundException">Thrown if either store doesn't exist.</exception>
    public void Switch(string a, string b)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(a);
        ArgumentException.ThrowIfNullOrWhiteSpace(b);

        if (a == b)
            return; // No-op

        lock (_namedLock)
        {
            // Ensure both stores exist (creates if needed)
            _ = this[a];
            _ = this[b];

            // Swap GUID mappings
            (_metadata.Stores[a], _metadata.Stores[b]) = (_metadata.Stores[b], _metadata.Stores[a]);

            // Swap in-memory references
            (_namedStores[a], _namedStores[b]) = (_namedStores[b], _namedStores[a]);

            SaveMetadata();
        }
    }

    /// <summary>
    /// Deletes a named store and its physical directory.
    /// </summary>
    /// <param name="name">Name of the store to delete.</param>
    /// <exception cref="InvalidOperationException">Thrown if attempting to delete the active store.</exception>
    /// <exception cref="KeyNotFoundException">Thrown if the store doesn't exist.</exception>
    public void Delete(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_namedLock)
        {
            if (!_metadata.Stores.TryGetValue(name, out var guid))
                throw new KeyNotFoundException($"Store '{name}' does not exist.");

            if (_activeName == name)
                throw new InvalidOperationException($"Cannot delete the active store '{name}'. Set a different store as active first.");

            // Dispose and remove from memory
            if (_namedStores.TryGetValue(name, out var store))
            {
                store.Dispose();
                _namedStores.Remove(name);
            }

            // Remove from metadata
            _metadata.Stores.Remove(name);
            SaveMetadata();

            // Delete physical directory
            var storePath = Path.Combine(StoresPath, guid);
            TempPath.SafeCleanup(storePath);
        }
    }

    /// <summary>
    /// Clears all data from a named store without deleting it.
    /// </summary>
    /// <param name="name">Name of the store to clear.</param>
    /// <exception cref="KeyNotFoundException">Thrown if the store doesn't exist.</exception>
    public void Clear(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var store = this[name]; // Creates if doesn't exist
        store.Clear();
    }

    #endregion

    #region Anonymous Pool API (Rent/Return)

    /// <summary>
    /// Rent a store from the pool. Blocks if pool is exhausted.
    /// Store is cleared before return - guaranteed empty.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if pool is disposed.</exception>
    /// <exception cref="TimeoutException">Thrown if cross-process gate acquisition times out.</exception>
    public QuadStore Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _poolGate.Wait();

        if (_available.TryTake(out var store))
        {
            store.Clear();
            return store;
        }

        // Create new pooled store
        if (_useCrossProcessGate)
        {
            if (!CrossProcessStoreGate.Instance.Acquire(TimeSpan.FromSeconds(60)))
            {
                _poolGate.Release();
                throw new TimeoutException(
                    $"Timed out waiting for cross-process store slot. " +
                    $"Max global stores: {CrossProcessStoreGate.Instance.MaxGlobalStores}. " +
                    $"This usually means too many test processes are running in parallel.");
            }

            Interlocked.Increment(ref _globalSlotsHeld);
        }

        try
        {
            lock (_poolLock)
            {
                var guid = Guid.CreateVersion7().ToString("N")[..12];
                var storePath = Path.Combine(PooledPath, guid);

                EnsureDiskBudget(storePath, "RentStore");

                Directory.CreateDirectory(storePath);
                var newStore = new QuadStore(storePath, null, null, _storageOptions);
                _pooledStores.Add((storePath, newStore));
                return newStore;
            }
        }
        catch
        {
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
    /// This amortizes the clear cost and allows inspection during debugging.
    /// </remarks>
    public void Return(QuadStore store)
    {
        if (_disposed)
            return;

        _available.Add(store);
        _poolGate.Release();
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
    /// Total number of pooled stores created (not including named stores).
    /// </summary>
    public int TotalCreated
    {
        get
        {
            lock (_poolLock)
            {
                return _pooledStores.Count;
            }
        }
    }

    /// <summary>
    /// Maximum number of concurrent pooled stores this pool allows.
    /// </summary>
    public int MaxConcurrent => _poolGate.CurrentCount + (TotalCreated - AvailableCount);

    /// <summary>
    /// Number of cross-process slots currently held by this pool.
    /// </summary>
    public int GlobalSlotsHeld => _globalSlotsHeld;

    #endregion

    #region Disk Management

    /// <summary>
    /// Gets the total disk usage of all stores (named + pooled) in bytes.
    /// </summary>
    public long TotalDiskUsage
    {
        get
        {
            if (_basePath == null)
                return 0;

            var total = 0L;

            // Named stores
            if (Directory.Exists(StoresPath))
            {
                foreach (var dir in Directory.GetDirectories(StoresPath))
                {
                    total += GetDirectorySize(dir);
                }
            }

            // Pooled stores
            if (Directory.Exists(PooledPath))
            {
                foreach (var dir in Directory.GetDirectories(PooledPath))
                {
                    total += GetDirectorySize(dir);
                }
            }

            return total;
        }
    }

    /// <summary>
    /// Gets the maximum disk space budget for this pool in bytes.
    /// </summary>
    public long MaxDiskBytes => _maxDiskBytes;

    /// <summary>
    /// Gets the base path for this pool, or null if temporary mode without persistence.
    /// </summary>
    public string? BasePath => _basePath;

    /// <summary>
    /// Gets whether this pool is temporary (will be cleaned up on dispose).
    /// </summary>
    public bool IsTemporary => _isTemporary;

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain available stores
        while (_available.TryTake(out _)) { }

        // Dispose named stores
        lock (_namedLock)
        {
            foreach (var store in _namedStores.Values)
            {
                store.Dispose();
            }
            _namedStores.Clear();
        }

        // Dispose pooled stores and cleanup paths
        lock (_poolLock)
        {
            foreach (var (path, store) in _pooledStores)
            {
                store.Dispose();
                if (_isTemporary)
                {
                    TempPath.SafeCleanup(path);
                }
            }
            _pooledStores.Clear();
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

        // Cleanup temporary base path
        if (_isTemporary && _basePath != null)
        {
            TempPath.SafeCleanup(_basePath);
        }

        _poolGate.Dispose();
    }

    #endregion

    #region Private Helpers

    private string StoresPath => Path.Combine(_basePath!, StoresDirectoryName);
    private string PooledPath => Path.Combine(_basePath!, PooledDirectoryName);
    private string PoolJsonPath => Path.Combine(_basePath!, PoolJsonFileName);

    private void SaveMetadata()
    {
        if (_basePath != null)
        {
            _metadata.Save(PoolJsonPath);
        }
    }

    private void RehydrateNamedStores()
    {
        // Open existing stores from metadata
        foreach (var (name, guid) in _metadata.Stores)
        {
            var storePath = Path.Combine(StoresPath, guid);
            if (Directory.Exists(storePath))
            {
                var store = new QuadStore(storePath, null, null, _storageOptions);
                _namedStores[name] = store;
            }
        }
    }

    private void EnsureDiskBudget(string path, string operation)
    {
        if (_maxDiskBytes <= 0 || _maxDiskBytes == long.MaxValue)
            return;

        var estimatedSize = EstimateStoreSize(_storageOptions);
        var currentUsage = TotalDiskUsage;
        var projectedUsage = currentUsage + estimatedSize;

        if (projectedUsage > _maxDiskBytes)
        {
            throw new InsufficientDiskSpaceException(
                path,
                estimatedSize,
                _maxDiskBytes - currentUsage,
                operation);
        }
    }

    private static int CalculateDiskLimit(StorageOptions options, double fraction, string? basePath)
    {
        var checkPath = basePath ?? Path.GetTempPath();
        var available = DiskSpaceChecker.GetAvailableSpace(checkPath);

        if (available < 0)
            return int.MaxValue;

        var budget = (long)(available * fraction);
        var perStoreEstimate = EstimateStoreSize(options);

        if (perStoreEstimate <= 0)
            return int.MaxValue;

        return (int)(budget / perStoreEstimate);
    }

    private static long EstimateStoreSize(StorageOptions options)
    {
        return (options.IndexInitialSizeBytes * 4)
             + options.AtomDataInitialSizeBytes
             + HashTableSizeBytes
             + (options.AtomOffsetInitialCapacity * sizeof(long));
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        var total = 0L;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    #endregion
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
