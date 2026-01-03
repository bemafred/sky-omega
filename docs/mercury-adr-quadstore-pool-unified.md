# ADR: Unified QuadStorePool for Lifecycle Management

## Status

Proposed (2026-01-03)

## Related ADRs

- [QuadStore Pooling and Clear()](mercury-adr-quadstore-pooling-and-clear.md) - Foundation for pooled stores
- [SERVICE Execution via IScan Interface](mercury-adr-service-scan-interface.md) - Consumer of pooled stores

## Problem

Current store management has several pain points:

1. **Pruning requires manual orchestration**
   - Caller creates two QuadStores explicitly
   - Caller manages paths, cleanup, switch-over
   - No atomic switch semantics

2. **Test/production impedance mismatch**
   - Tests use `TempPath` for individual stores
   - Production uses explicit paths
   - Different patterns, different bugs

3. **SERVICE temp stores are separate concern**
   - `QuadStorePool` exists only for SERVICE materialization
   - Production pools might also need temp stores
   - Duplicated disk budget concerns

4. **No unified disk management**
   - Each store manages its own space
   - No aggregate budget enforcement
   - Hard to reason about total disk usage

## Solution: Unified QuadStorePool

A single pool type that manages:
- **Named stores** ("primary", "secondary", "staging", etc.)
- **Pooled anonymous stores** (for SERVICE, temp work)
- **Shared disk budget** across all stores
- **Active designation** for query routing
- **Atomic switch** for pruning workflows

### Directory Structure

All store directories use GUID v7 naming (time-ordered):

```
{basePath}/
  pool.json                      # Metadata: mappings, active, settings
  stores/
    0194a3f8c2e1.../             # "primary" maps here
    0194a3f9b7d4.../             # "secondary" maps here
    0194b2c1a2b3.../             # "staging" maps here
  pooled/
    0194c3d4e5f6.../             # Anonymous pooled store
    0194c3d4e5f7.../
    ...
```

GUID v7 benefits:
- Time-ordered: `ls` shows creation order
- Collision-free: no naming conflicts
- Test/prod parity: same pattern everywhere
- Debuggable: timestamp embedded in ID

### Pool Metadata (pool.json)

```json
{
  "version": 1,
  "active": "primary",
  "stores": {
    "primary": "0194a3f8c2e1",
    "secondary": "0194a3f9b7d4",
    "staging": "0194b2c1a2b3"
  },
  "settings": {
    "maxDiskBytes": 10737418240,
    "maxPooledStores": 8
  }
}
```

## API Design

```csharp
public sealed class QuadStorePool : IDisposable
{
    // === Construction ===

    /// <summary>
    /// Open or create a named pool at the specified path.
    /// </summary>
    public QuadStorePool(string basePath, QuadStorePoolOptions? options = null);

    /// <summary>
    /// Create an anonymous temporary pool (auto-cleanup on Dispose).
    /// Uses TempPath for crash-safe lifecycle management.
    /// </summary>
    public static QuadStorePool CreateTemp(string? purpose = null, QuadStorePoolOptions? options = null);

    // === Named Store Access ===

    /// <summary>
    /// Access a named store. Creates on first access with GUID v7 directory.
    /// </summary>
    public QuadStore this[string name] { get; }

    /// <summary>
    /// The currently active store (for query routing).
    /// </summary>
    public QuadStore Active { get; }

    /// <summary>
    /// Name of the currently active store.
    /// </summary>
    public string ActiveName { get; }

    /// <summary>
    /// All named store names.
    /// </summary>
    public IReadOnlyList<string> StoreNames { get; }

    // === Active Management ===

    /// <summary>
    /// Set which named store is active.
    /// </summary>
    public void SetActive(string name);

    /// <summary>
    /// Atomically swap the directory mappings of two named stores.
    /// If either is active, active follows its logical name.
    /// </summary>
    public void Switch(string a, string b);

    // === Pooled Anonymous Stores ===

    /// <summary>
    /// Rent an anonymous store from the pool (for SERVICE, temp work).
    /// </summary>
    public QuadStore Rent();

    /// <summary>
    /// Return a rented store to the pool. Clear() called on next Rent().
    /// </summary>
    public void Return(QuadStore store);

    // === Lifecycle ===

    /// <summary>
    /// Delete a named store and its data.
    /// </summary>
    public void Delete(string name);

    /// <summary>
    /// Clear a named store's data but keep the store.
    /// </summary>
    public void Clear(string name);

    // === Disk Management ===

    /// <summary>
    /// Total disk usage across all stores (named + pooled).
    /// </summary>
    public long TotalDiskUsage { get; }

    /// <summary>
    /// Maximum allowed disk usage (from options).
    /// </summary>
    public long MaxDiskBytes { get; }

    /// <summary>
    /// Base path of this pool.
    /// </summary>
    public string BasePath { get; }

    /// <summary>
    /// Whether this is a temporary pool (will cleanup on Dispose).
    /// </summary>
    public bool IsTemporary { get; }
}

public sealed class QuadStorePoolOptions
{
    /// <summary>
    /// Maximum disk bytes across all stores. Default: unlimited.
    /// </summary>
    public long MaxDiskBytes { get; set; } = long.MaxValue;

    /// <summary>
    /// Maximum number of pooled anonymous stores. Default: ProcessorCount * 2.
    /// </summary>
    public int MaxPooledStores { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>
    /// Default active store name. Default: "primary".
    /// </summary>
    public string DefaultActive { get; set; } = "primary";
}
```

## Usage Examples

### Simple Single-Store (CLI)

```csharp
using var pool = new QuadStorePool("/data/my-kb");

// Active defaults to "primary", created on first access
pool.Active.AddCurrent("<s>", "<p>", "<o>");

var results = executor.Execute(pool.Active, query);
```

### Pruning Workflow

```csharp
using var pool = new QuadStorePool("/data/my-kb");

// Load data into primary
LoadRdf(pool["primary"], sourceFile);

// ... time passes, soft deletes accumulate ...

// Prune to secondary (physically removes soft-deleted)
var transfer = new PruningTransfer(pool["primary"], pool["secondary"], options);
transfer.Execute();

// Atomic switch: "primary" now maps to pruned data
pool.Switch("primary", "secondary");

// Old data (now named "secondary") can be cleared for next cycle
pool.Clear("secondary");
```

### SERVICE Queries in Production

```csharp
using var pool = new QuadStorePool("/data/my-kb");

// Query needs temp store for SERVICE materialization
var tempStore = pool.Rent();
try
{
    // Materialize SERVICE results
    materializer.LoadResults(tempStore, serviceResults);

    // Execute query joining Active with temp
    var results = executor.Execute(pool.Active, tempStore, query);
}
finally
{
    pool.Return(tempStore);  // Back to pool
}
```

### Testing

```csharp
public class PruningTests : IDisposable
{
    private readonly QuadStorePool _pool;

    public PruningTests()
    {
        _pool = QuadStorePool.CreateTemp("pruning-tests");
    }

    public void Dispose() => _pool.Dispose();  // Cleans up entire temp tree

    [Fact]
    public void Switch_SwapsStoreMappings()
    {
        // Setup: distinct data in each store
        _pool["a"].AddCurrent("<a>", "<p>", "<1>");
        _pool["b"].AddCurrent("<b>", "<p>", "<2>");
        _pool.SetActive("a");

        // Act
        _pool.Switch("a", "b");

        // Assert: names swapped, active follows logical name
        Assert.Equal("a", _pool.ActiveName);
        Assert.True(HasTriple(_pool.Active, "<b>", "<p>", "<2>"));  // "a" now has b's data
        Assert.True(HasTriple(_pool["b"], "<a>", "<p>", "<1>"));    // "b" now has a's data
    }

    [Fact]
    public void Prune_EndToEnd()
    {
        // Setup
        _pool["primary"].AddCurrent("<keep>", "<p>", "<o>");
        _pool["primary"].AddCurrent("<delete>", "<p>", "<o>");
        _pool["primary"].SoftDelete("<delete>", "<p>", "<o>");

        // Act
        new PruningTransfer(_pool["primary"], _pool["secondary"]).Execute();
        _pool.Switch("primary", "secondary");

        // Assert
        Assert.True(HasTriple(_pool.Active, "<keep>", "<p>", "<o>"));
        Assert.False(HasTriple(_pool.Active, "<delete>", "<p>", "<o>"));
    }

    [Fact]
    public void Rent_Return_ClearsOnNextRent()
    {
        var store = _pool.Rent();
        store.AddCurrent("<temp>", "<p>", "<o>");
        _pool.Return(store);

        var store2 = _pool.Rent();
        Assert.Equal(0, store2.Count);  // Cleared
        _pool.Return(store2);
    }
}
```

### Multi-Stage ETL Pipeline

```csharp
using var pool = new QuadStorePool("/data/etl");

// Stage 1: Raw ingestion
await LoadRdfAsync(pool["raw"], sourceFiles);

// Stage 2: Enrichment
await EnrichAsync(pool["raw"], pool["enriched"]);

// Stage 3: Validation
if (await ValidateAsync(pool["enriched"]))
{
    // Promote to production
    pool.Switch("production", "enriched");
    pool.Clear("enriched");  // Ready for next run
}
else
{
    // Keep enriched for debugging, don't promote
    Log.Error("Validation failed, see pool['enriched']");
}
```

## Implementation Details

### Switch Semantics

Switch swaps the name→GUID mappings in metadata only:

```csharp
public void Switch(string a, string b)
{
    if (!_nameToGuid.TryGetValue(a, out var guidA))
        throw new ArgumentException($"Store '{a}' does not exist");
    if (!_nameToGuid.TryGetValue(b, out var guidB))
        throw new ArgumentException($"Store '{b}' does not exist");

    // Swap mappings
    _nameToGuid[a] = guidB;
    _nameToGuid[b] = guidA;

    // Persist atomically (write-rename pattern)
    SaveMetadata();
}
```

No filesystem renames. Fast. Atomic (via write-rename). Recoverable.

### TempPath Integration

Temporary pools use TempPath for crash-safe lifecycle:

```csharp
public static QuadStorePool CreateTemp(string? purpose = null, QuadStorePoolOptions? options = null)
{
    var id = Guid.CreateVersion7().ToString("N")[..12];
    var tempPath = TempPath.Create("pool", purpose ?? id, unique: false);
    tempPath.EnsureClean();
    tempPath.MarkOwnership();

    return new QuadStorePool(tempPath.FullPath, options) { _tempPath = tempPath };
}

public void Dispose()
{
    // Dispose all stores
    foreach (var store in _stores.Values)
        store.Dispose();
    foreach (var store in _pooledStores)
        store.Dispose();

    // If temp pool, TempPath handles cleanup
    _tempPath?.Dispose();  // Removes directory tree
}
```

Crash recovery: `TempPath.CleanupStale()` finds orphaned temp pools on startup.

### Metadata Persistence

Atomic write via write-rename pattern:

```csharp
private void SaveMetadata()
{
    var json = JsonSerializer.Serialize(new PoolMetadata
    {
        Version = 1,
        Active = _activeName,
        Stores = _nameToGuid,
        Settings = _options
    });

    var tempFile = Path.Combine(_basePath, "pool.json.tmp");
    var finalFile = Path.Combine(_basePath, "pool.json");

    File.WriteAllText(tempFile, json);
    File.Move(tempFile, finalFile, overwrite: true);  // Atomic on POSIX
}
```

### GUID v7 Generation

```csharp
private static string NewStoreId() => Guid.CreateVersion7().ToString("N")[..12];
```

12 hex chars = 48 bits, sufficient uniqueness for local store directories while keeping paths readable.

## Migration Path

Existing code using explicit QuadStore paths can migrate incrementally:

```csharp
// Before
using var primary = new QuadStore("/data/kb/primary");
using var secondary = new QuadStore("/data/kb/secondary");
var transfer = new PruningTransfer(primary, secondary);

// After
using var pool = new QuadStorePool("/data/kb");
var transfer = new PruningTransfer(pool["primary"], pool["secondary"]);
pool.Switch("primary", "secondary");
```

## Success Criteria

- [ ] `QuadStorePool` class with named store access
- [ ] `CreateTemp()` factory with TempPath integration
- [ ] `Switch()` with atomic metadata update
- [ ] `Rent()`/`Return()` for pooled anonymous stores
- [ ] GUID v7 directory naming throughout
- [ ] `pool.json` metadata with write-rename persistence
- [ ] Tests for all switch scenarios
- [ ] Tests using `CreateTemp()` for isolation
- [ ] Mercury.Cli integration example
- [ ] Documentation updated

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Metadata corruption | Write-rename pattern for atomic updates |
| Crash during switch | Metadata is source of truth; either old or new state |
| Disk quota exceeded | Check before write; soft limit with warning or hard limit with exception |
| Multi-process access | File lock on pool.json during mutations (future consideration) |
| Debug difficulty with GUIDs | `pool.json` shows mappings; CLI helper for inspection |

## Future Considerations

1. **Multi-process locking** - File lock protocol for concurrent access
2. **Remote pools** - S3/Azure Blob backing store
3. **Replication** - Sync between pools
4. **Snapshots** - Point-in-time copies of named stores
