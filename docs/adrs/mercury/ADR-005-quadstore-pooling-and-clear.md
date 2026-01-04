# ADR-005: QuadStore Pooling and Clear() Operation

## Status

Accepted (implemented)

## Related ADRs

- [SERVICE Execution via IScan Interface](mercury-adr-service-scan-interface.md) - Primary consumer of QuadStorePool for federated queries

## Problem

Parallel test execution exhausts disk space:

```
Sequential:  Test1 → cleanup → Test2 → cleanup → Test3 → cleanup
             Peak: 1 store (~100MB)

Parallel:    Test1 ─────────────────→ cleanup
             Test2 ─────────────────→ cleanup
             Test3 ─────────────────→ cleanup
             ... (50+ concurrent)
             Peak: 50+ stores = DISK EXHAUSTED
```

Current mitigations (`TempPath.SafeCleanup`, `MarkOwnership`, `CleanupStale`) handle file locking and crash recovery, but do not limit concurrent store count.

## Solution

Two complementary changes:

1. **`QuadStore.Clear()`** - Reset store to empty state without recreating files
2. **`QuadStorePool`** - Bounded pool of reusable stores

### Why Clear() is the right primitive

| Approach | Cost | Disk I/O |
|----------|------|----------|
| Dispose + Create new | High | Delete files, create files, initialize |
| Clear() | Low | Truncate in place, reset counters |

Clear() is not "breaking append-only" - it's starting a new append-only sequence. The files remain, only their contents reset.

### Use cases beyond testing

| Use Case | Benefit |
|----------|---------|
| **Test pooling** | Reuse stores, bound disk usage |
| **SERVICE temp stores** | Pool instead of create/delete per query |
| **Pruning target** | Clear target before transfer, no ambiguity |
| **Dev/REPL** | "Start over" without process restart |
| **Benchmarks** | Consistent clean state between iterations |

## Design

### QuadStore.Clear()

```csharp
/// <summary>
/// Resets the store to empty state. All data is discarded.
/// Files are truncated in place - cheaper than delete + recreate.
/// Thread-safe: acquires write lock.
/// </summary>
public void Clear()
{
    _lock.EnterWriteLock();
    try
    {
        // 1. Clear WAL (truncate to empty, reset position)
        _wal.Clear();
        
        // 2. Clear all indexes (truncate, reinitialize root page)
        _gspoIndex.Clear();
        _gposIndex.Clear();
        _gospIndex.Clear();
        _tgspIndex.Clear();
        
        // 3. Clear atom store (truncate data, reset hash table)
        _atoms.Clear();
        
        // 4. Clear trigram index if present
        _trigramIndex?.Clear();
        
        // 5. Reset statistics
        _statistics.Clear();
        
        _logger.Info("Store cleared".AsSpan());
    }
    finally
    {
        _lock.ExitWriteLock();
    }
}
```

### Component Clear() implementations

**WriteAheadLog.Clear():**
```csharp
public void Clear()
{
    lock (_writeLock)
    {
        _file.SetLength(0);
        _file.Flush();
        _position = 0;
        _lastCheckpointPosition = 0;
    }
}
```

**QuadIndex.Clear():**
```csharp
public void Clear()
{
    // Truncate file to just header + empty root page
    _file.SetLength(HeaderSize + PageSize);
    _file.Flush();
    
    // Reinitialize root page as empty leaf
    InitializeRootPage();
    
    // Reset counter
    _quadCount = 0;
    WriteHeader();
}
```

**AtomStore.Clear():**
```csharp
public void Clear()
{
    lock (_resizeLock)
    {
        // Truncate data file to just header
        _dataFile.SetLength(DataHeaderSize);
        _dataPosition = DataHeaderSize;
        
        // Clear hash table (zero all buckets)
        var hashTableBytes = HashTableSize * sizeof(HashBucket);
        new Span<byte>(_hashTable, hashTableBytes).Clear();
        
        // Reset offset index
        _offsetFile.SetLength(OffsetHeaderSize);
        
        // Reset counters
        _nextAtomId = 1;  // 0 is reserved for default graph
        _atomCount = 0;
        
        WriteHeaders();
    }
}
```

### QuadStorePool

```csharp
/// <summary>
/// Bounded pool of reusable QuadStore instances.
/// Limits concurrent stores to prevent disk exhaustion.
/// Thread-safe.
/// </summary>
public sealed class QuadStorePool : IDisposable
{
    private readonly ConcurrentBag<QuadStore> _available = new();
    private readonly List<(TempPath path, QuadStore store)> _all = new();
    private readonly SemaphoreSlim _gate;
    private readonly object _createLock = new();
    private readonly string _purpose;
    private bool _disposed;

    /// <summary>
    /// Creates a pool with bounded concurrency.
    /// </summary>
    /// <param name="maxConcurrent">Max concurrent stores (default: ProcessorCount)</param>
    /// <param name="purpose">Category for TempPath naming</param>
    public QuadStorePool(int maxConcurrent = 0, string purpose = "pooled")
    {
        var max = maxConcurrent > 0 ? maxConcurrent : Environment.ProcessorCount;
        _gate = new SemaphoreSlim(max, max);
        _purpose = purpose;
    }

    /// <summary>
    /// Rent a store from the pool. Blocks if pool is exhausted.
    /// Store is cleared before return.
    /// </summary>
    public QuadStore Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        _gate.Wait();
        
        if (_available.TryTake(out var store))
        {
            store.Clear();
            return store;
        }
        
        // Create new store
        lock (_createLock)
        {
            var path = TempPath.Create(_purpose, Guid.NewGuid().ToString("N")[..8], unique: false);
            path.EnsureClean();
            path.MarkOwnership();
            var newStore = new QuadStore(path);
            _all.Add((path, newStore));
            return newStore;
        }
    }

    /// <summary>
    /// Return a store to the pool for reuse.
    /// </summary>
    public void Return(QuadStore store)
    {
        if (_disposed)
        {
            store.Dispose();
            return;
        }
        
        _available.Add(store);
        _gate.Release();
    }

    /// <summary>
    /// Rent with automatic return on dispose.
    /// </summary>
    public PooledStoreLease RentScoped() => new(this, Rent());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // Drain available stores
        while (_available.TryTake(out _)) { }
        
        // Dispose all stores and cleanup paths
        foreach (var (path, store) in _all)
        {
            store.Dispose();
            TempPath.SafeCleanup(path);
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
    public QuadStore Store { get; }

    internal PooledStoreLease(QuadStorePool pool, QuadStore store)
    {
        _pool = pool;
        Store = store;
    }

    public void Dispose() => _pool.Return(Store);
}
```

## Integration

### Test infrastructure

```csharp
// In test assembly - shared pool
public static class TestStores
{
    public static readonly QuadStorePool Pool = new(
        maxConcurrent: Environment.ProcessorCount,
        purpose: "test");
    
    // Called from AssemblyCleanup or similar
    public static void Cleanup() => Pool.Dispose();
}

// In individual test
[Fact]
public void MyTest()
{
    using var lease = TestStores.Pool.RentScoped();
    var store = lease.Store;
    
    store.Assert("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
    // ... test logic ...
}
// Store automatically returned to pool
```

### SERVICE materialization

```csharp
public sealed class ServiceMaterializer : IDisposable
{
    private readonly QuadStorePool _pool;
    private readonly List<QuadStore> _rented = new();

    public ServiceMaterializer(ISparqlServiceExecutor executor, QuadStorePool pool)
    {
        _pool = pool;
    }

    public QuadStore Materialize(ServiceClause clause, ...)
    {
        var store = _pool.Rent();
        _rented.Add(store);
        LoadResults(store, ...);
        return store;
    }

    public void Dispose()
    {
        foreach (var store in _rented)
            _pool.Return(store);
    }
}
```

### Pruning

```csharp
// Clear target before transfer - explicit clean slate
target.Clear();
var result = new PruningTransfer(source, target, options).Execute();
```

## File layout after Clear()

```
store-directory/
├── atoms.data     [header only, ~64 bytes]
├── atoms.index    [empty hash table]
├── atoms.offsets  [header only]
├── gspo.tdb       [header + empty root page, ~8KB]
├── gpos.tdb       [header + empty root page, ~8KB]
├── gosp.tdb       [header + empty root page, ~8KB]
├── tgsp.tdb       [header + empty root page, ~8KB]
├── wal.log        [empty, 0 bytes]
└── trigram.*      [cleared if present]
```

Total: ~32KB vs creating new store with same overhead.

## Consequences

| Before | After |
|--------|-------|
| Parallel tests exhaust disk | Bounded by pool size |
| Create/delete overhead per test | Clear() is cheap truncate |
| No store reuse | Pool enables reuse |
| SERVICE creates temp stores | Pool reuses stores |
| Pruning target ambiguity | Clear() before transfer |

## Implementation order

1. **Implement `WriteAheadLog.Clear()`** - simplest component
2. **Implement `QuadIndex.Clear()`** - truncate + reinitialize root
3. **Implement `AtomStore.Clear()`** - truncate + clear hash table
4. **Implement `QuadStore.Clear()`** - orchestrates components
5. **Add tests for Clear()** - verify empty state, reuse works
6. **Implement `QuadStorePool`** - uses Clear() internally
7. **Integrate pool in test infrastructure**
8. **Optional: Integrate pool in ServiceMaterializer**

## Success criteria

- [x] Clear() resets store to empty state
- [x] Cleared store passes same tests as fresh store
- [x] Pool limits concurrent stores to max
- [x] Pool reuses stores via Clear()
- [x] Parallel test runs don't exhaust disk (bounded by pool)
- [x] No file handle leaks after Clear() (memory mappings preserved)
- [x] Clear() is significantly faster than dispose+create (no file I/O, just memory resets)