# ADR-006: SERVICE Execution via IScan Interface and Temp Stores

## Status

Accepted (Implemented 2026-01-03)

## Related ADRs

- [QuadStore Pooling and Clear()](mercury-adr-quadstore-pooling-and-clear.md) - Enables efficient temp store reuse

## Problem

SERVICE clause execution suffers from architectural incompatibility with local query execution:

1. **Access semantics differ fundamentally**
    - Local: synchronous index access, stack-based iteration, backtracking possible
    - SERVICE: async HTTP I/O, heap-allocated results, no backtracking

2. **UNION branch association is lost**
    - `ServiceClause` stored flat in `GraphPattern` with no branch affiliation
    - `InitializeUnionBranch()` only creates scans for `TriplePattern`, ignores SERVICE
    - Query `{ local } UNION { SERVICE ... }` silently drops SERVICE branch

3. **Stack overflow from mixed execution**
    - `ServiceScan` uses `.GetAwaiter().GetResult()` blocking
    - Complex nesting (UNION + SERVICE + OPTIONAL + FILTER) exceeds stack limits
    - Trial-and-error fixes cause cascading test failures

## Root Cause

SERVICE is treated as "just another operator" but it isn't. The current `ServiceScan` tries to mimic `TriplePatternScan` while hiding fundamentally different access patterns. This impedance mismatch creates problems that compound with query complexity.

## Solution: Two-Phase Refactoring

### Phase 1: Enable (Interface Extraction)

Extract `IScan` interface from `TriplePatternScan`. This is a **mechanical, testable refactoring** that changes no behavior.

```csharp
/// <summary>
/// Common interface for scan operators.
/// Enables uniform handling of local and materialized scans.
/// </summary>
internal interface IScan : IDisposable
{
    bool MoveNext(ref BindingTable bindings);
}
```

**C# ref struct Limitation:** `ref struct` types cannot implement interfaces directly. We use **duck typing** - all scan operators conform to the same method signatures without formal interface implementation:

```csharp
// All scan operators share this contract (duck typing):
internal ref struct TriplePatternScan
{
    public bool MoveNext(ref BindingTable bindings) { ... }
    public void Dispose() { ... }
}

internal ref struct ServicePatternScan
{
    public bool MoveNext(ref BindingTable bindings) { ... }
    public void Dispose() { ... }
}

// Generic methods can accept any scan via constraint (C# 13+):
void ProcessScan<TScan>(ref TScan scan, ref BindingTable bindings)
    where TScan : struct, IDisposable, allows ref struct
{
    while (scan.MoveNext(ref bindings)) { /* process */ }
}
```

The `IScan` interface serves as **documentation contract** - it defines the expected shape, even though `ref struct` operators use duck typing rather than formal implementation.

**Verification:** All existing tests pass. No behavior change.

### Phase 2: Implement (Temp Store Pattern)

SERVICE becomes a **materialization boundary**:

1. Execute HTTP request against remote endpoint
2. Load results into temporary `QuadStore` (using `TempPath`)
3. Create `ServicePatternScan` that wraps `TriplePatternScan` against temp store
4. Rest of execution sees only `IScan` - no difference from local patterns

```
┌─────────────────────────────────────────────────────────┐
│ Before Query Execution                                  │
│                                                         │
│  ServiceClause ──HTTP──► Remote Endpoint               │
│        │                        │                       │
│        │                        ▼                       │
│        │               ServiceResultRows                │
│        │                        │                       │
│        ▼                        ▼                       │
│  TempPath.Create("service")  LoadAsTriples()           │
│        │                        │                       │
│        └────────► QuadStore ◄───┘                      │
│                   (temp)                                │
├─────────────────────────────────────────────────────────┤
│ During Query Execution                                  │
│                                                         │
│  ServicePatternScan : IScan                            │
│        │                                                │
│        └──► TriplePatternScan(tempStore, pattern)      │
│                    │                                    │
│                    └──► MoveNext(ref bindings)         │
│                                                         │
│  (Indistinguishable from local scan to caller)         │
├─────────────────────────────────────────────────────────┤
│ After Query Execution                                   │
│                                                         │
│  ServiceStore.Dispose()                                │
│        │                                                │
│        ├──► QuadStore.Dispose()                        │
│        └──► TempPath.SafeCleanup()                     │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

## New Components

### Pool-Based Temp Store (Preferred)

With `QuadStorePool` (see [QuadStore Pooling ADR](mercury-adr-quadstore-pooling-and-clear.md)), SERVICE temp stores become simple pool rentals:

```csharp
// Shared pool for SERVICE materialization
public static class ServiceStorePool
{
    public static readonly QuadStorePool Instance = new(
        maxConcurrent: Environment.ProcessorCount * 2,
        purpose: "service");
}
```

**Why pooling is better than TempPath per-query:**

| Aspect | TempPath (Original) | QuadStorePool (New) |
|--------|---------------------|---------------------|
| Creation cost | Create files | Rent (instant) |
| Cleanup cost | Delete files | Return + Clear() (truncate) |
| Concurrent limit | Unbounded (disk exhaustion) | Bounded by pool size |
| Crash recovery | CleanupStale scans | Pool owns all stores |
| Code complexity | ServiceStore class | Direct pool usage |

### ServiceStore (Legacy/Fallback)

Retained for scenarios where pool is unavailable. Uses `TempPath` lifecycle:

```csharp
public sealed class ServiceStore : IDisposable
{
    private readonly TempPath _path;
    private readonly QuadStore _store;

    public QuadStore Store => _store;

    public ServiceStore(string identifier)
    {
        _path = TempPath.Create("service", identifier, unique: true);
        _path.EnsureClean();
        _path.MarkOwnership();  // Crash-safe: CleanupStale finds orphans
        _store = new QuadStore(_path.FullPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_path.FullPath);
    }
}
```

### ServicePatternScan

Scan operator that wraps temp store, implements `IScan`:

```csharp
internal ref struct ServicePatternScan : IScan
{
    private TriplePatternScan _inner;

    public ServicePatternScan(
        QuadStore tempStore,
        ReadOnlySpan<char> source,
        TriplePattern pattern,
        BindingTable initialBindings)
    {
        _inner = new TriplePatternScan(tempStore, source, pattern, initialBindings);
    }

    public bool MoveNext(ref BindingTable bindings) => _inner.MoveNext(ref bindings);
    public void Dispose() => _inner.Dispose();
}
```

### ServiceMaterializer

Orchestrates SERVICE materialization before query execution. **Pool-based implementation (preferred):**

```csharp
public sealed class ServiceMaterializer : IDisposable
{
    private readonly ISparqlServiceExecutor _executor;
    private readonly QuadStorePool _pool;
    private readonly List<QuadStore> _rentedStores = new();

    public ServiceMaterializer(ISparqlServiceExecutor executor, QuadStorePool? pool = null)
    {
        _executor = executor;
        _pool = pool ?? ServiceStorePool.Instance;
    }

    public QuadStore Materialize(ServiceClause clause, ReadOnlySpan<char> source,
        BindingTable? incomingBindings = null)
    {
        var endpoint = ResolveEndpoint(clause, source, incomingBindings);

        // Rent from pool - store is already Clear()'d
        var store = _pool.Rent();
        _rentedStores.Add(store);

        var query = BuildSparqlQuery(clause, source, incomingBindings);
        var results = _executor.ExecuteSelectAsync(endpoint, query)
            .AsTask().GetAwaiter().GetResult();

        LoadResultsToStore(store, results, clause, source);
        return store;
    }

    public void Dispose()
    {
        // Return all stores to pool for reuse
        foreach (var store in _rentedStores)
            _pool.Return(store);
    }
}
```

**Key differences from TempPath approach:**
- No file creation/deletion per query
- Pool handles concurrency limiting
- `Clear()` is called by pool on next `Rent()`, not on `Return()`
- Crash safety via pool ownership (pool disposes all stores on shutdown)

**Variable Endpoint Handling:** When `ServiceClause.Endpoint.IsVariable` is true, `ResolveEndpoint` must look up the endpoint URI from `incomingBindings`. This may result in different endpoints for different binding rows, requiring multiple temp stores.

**Binding Propagation:** `BuildSparqlQuery` should inject bound variables via VALUES clause for proper SPARQL 1.1 Federated Query semantics:

```sparql
# Instead of substituting directly: ?s <p> <bound-value>
# Use VALUES for cleaner semantics:
SELECT * WHERE {
  VALUES ?x { <bound-value1> <bound-value2> }
  ?s <p> ?x .
}
```

## Integration Point

In `QueryExecutor`, before execution:

```csharp
using var materializer = new ServiceMaterializer(_serviceExecutor);
var serviceStores = new Dictionary<int, ServiceStore>();

for (int i = 0; i < pattern.ServiceClauseCount; i++)
{
    var clause = pattern.GetServiceClause(i);
    serviceStores[i] = materializer.Materialize(clause, _source);
}

// Now execute - SERVICE patterns use ServicePatternScan against temp stores
```

## Why This Works

| Aspect | Benefit |
|--------|---------|
| **No behavior change in Phase 1** | Tests prove refactoring is safe |
| **SERVICE becomes "just data"** | After materialization, identical to local triples |
| **TempPath handles lifecycle** | Crash-safe, process-aware cleanup |
| **IScan unifies operators** | QueryExecutor sees uniform interface |
| **UNION integration trivial** | Each branch creates its scans via IScan |
| **Existing code unchanged** | TriplePatternScan, MultiPatternScan untouched |

## Implementation Order

### Prerequisites (from QuadStore Pooling ADR)

0. **Implement QuadStore.Clear() and QuadStorePool**
    - See [QuadStore Pooling ADR](mercury-adr-quadstore-pooling-and-clear.md)
    - Must complete before Phase 2 (temp store pattern)

### Phase 1: Interface Extraction

1. **Extract IScan interface** from TriplePatternScan
    - Run all tests → must pass unchanged

### Phase 2: Pool-Based SERVICE

2. **Add ServiceBenchmarks** to establish baseline
    - Measure current SERVICE execution overhead
    - Include: SERVICE-only, SERVICE+local join, multiple SERVICE clauses

3. **Implement ServicePatternScan** wrapping TriplePatternScan
    - Unit test: materialize mock results, scan, verify bindings

4. **Implement ServiceMaterializer** using QuadStorePool
    - Inject pool dependency
    - Integration test: HTTP mock → pooled store → query results

5. **Integrate into QueryExecutor**
    - Create shared ServiceStorePool
    - Existing SERVICE tests must pass
    - Add SERVICE + UNION tests

6. **Verify benchmark results**
    - No regression beyond 10% for SERVICE-only queries
    - Document performance improvements from pooling

7. **Remove ServiceScan** (old implementation)
    - Dead code elimination

### Optional: Legacy Fallback

8. **Keep ServiceStore** (TempPath-based) for edge cases
    - Non-pooled execution contexts
    - Single-use scenarios where pooling overhead not justified

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| ref struct can't implement interface | Duck typing with consistent method signatures; IScan as documentation contract; C# 13 `allows ref struct` for generic constraints |
| Temp store overhead | Pooling eliminates create/delete; Clear() is cheap truncate |
| Disk I/O for temp stores | Pool reuses stores; future in-memory path for small results |
| HTTP blocking in materializer | Already blocked in current ServiceScan; no regression |
| Variable endpoint multiplicity | Track per-endpoint stores; hash by resolved URI |
| Concurrent query disk exhaustion | **Solved by pool** - bounded by `maxConcurrent` |
| Pool starvation under load | Pool blocks on Rent(); queries queue rather than fail |
| Pool dependency in QueryExecutor | Optional injection; fallback to ServiceStore if needed |

## Dual-Path Implementation (Implemented 2026-01-03)

SERVICE results use threshold-based routing between in-memory and indexed paths:

### ServiceMaterializerOptions

```csharp
public sealed class ServiceMaterializerOptions
{
    /// <summary>
    /// Result count threshold for in-memory vs indexed path.
    /// Default: 500 (B+Tree indexing pays off for joins at this scale).
    /// </summary>
    public int IndexedThreshold { get; set; } = 500;
}
```

### ServiceMaterializer.Fetch()

The decision point that routes based on result count:

```csharp
public ServiceFetchResult Fetch(ServiceClause clause, ...)
{
    var results = _executor.ExecuteSelectAsync(endpoint, query)...;

    // Threshold-based routing
    if (results.Count < _options.IndexedThreshold)
    {
        // Small result set - use in-memory path
        return ServiceFetchResult.InMemory(results);
    }

    // Large result set - materialize to indexed QuadStore
    var store = _pool.Rent();
    LoadResultsToStore(store, results, clause, source);
    return ServiceFetchResult.Indexed(store, variableNames, results.Count);
}
```

### ServicePatternScan (In-Memory Path)

For small result sets (< threshold), linear scan over `List<ServiceResultRow>`:
- No disk I/O
- O(N) iteration per local result
- Best for < 500 rows

### IndexedServicePatternScan (Indexed Path)

For large result sets (>= threshold), B+Tree-backed QuadStore:
- Uses synthetic triples: `<_:row{N}> <_:var:{varName}> value`
- O(log N) lookups for join compatibility checks
- Pooled stores via `QuadStorePool` for reuse

### ServiceFetchResult

Discriminated result type returned by `Fetch()`:

```csharp
public readonly struct ServiceFetchResult
{
    public bool IsIndexed { get; }
    public List<ServiceResultRow>? Results { get; }  // In-memory path
    public QuadStore? Store { get; }                  // Indexed path
    public List<string>? VariableNames { get; }
    public int RowCount { get; }
}
```

**Threshold selection:**

| Result Count | Path | Characteristics |
|--------------|------|-----------------|
| < 500 | In-memory | No disk I/O, linear scan |
| >= 500 | Indexed | B+Tree lookup, pooled stores |

**Benchmark findings (2026-01-03):**

SERVICE-only queries always use in-memory path (indexed has no join benefit):

| Rows | Time | Allocated | Gen2 Collections |
|-----:|-----:|----------:|-----------------:|
| 1K | 184 μs | 623 KB | 0 |
| 5K | 902 μs | 3.1 MB | 0 |
| 10K | 2.2 ms | 6.3 MB | 1 |
| 50K | 11.9 ms | 31 MB | 3 |
| 100K | 23.3 ms | 62 MB | 1 |

Direct path comparison (5K rows, pure iteration):

| Path | Time | Ratio |
|------|-----:|------:|
| In-memory | 743 μs | 1x |
| Indexed | 7.5 s | 10,000x |

**Key insight:** Indexed path overhead is massive for pure iteration (includes B+Tree materialization). It only pays off when join selectivity amortizes materialization cost across multiple lookups.

**Memory considerations:**
- ~600 bytes/row for `ServiceResultRow` with bindings
- Gen2 GC starts at ~10K rows
- Significant GC pressure at 50K+ rows (31+ MB allocation)

For SERVICE-only with extreme result counts (50K+), consider:
- Streaming/pagination from remote endpoint
- Lazy materialization (only if joined later)
- Memory-based threshold for GC relief

## Success Criteria

### Phase 1: Interface Extraction
- [x] All existing tests pass after IScan extraction

### Phase 2: Pool-Based SERVICE
- [x] SERVICE-only queries work via pooled stores
- [x] SERVICE + local pattern joins work
- [x] SERVICE + UNION executes both branches (see [UNION Branch SERVICE Execution](mercury-adr-union-service-execution.md))
- [x] OPTIONAL { SERVICE } preserves outer bindings
- [ ] Stores returned to pool after query (not disposed)
- [ ] Pool limits concurrent SERVICE stores
- [x] SERVICE benchmarks show no regression > 10%
- [ ] Pooling shows improvement over TempPath baseline

### Correctness
- [x] Variable endpoint SERVICE resolves correctly
- [ ] VALUES clause injection works for binding propagation

### Lifecycle
- [ ] Pool disposes all stores on shutdown
- [ ] No file handle leaks after concurrent SERVICE queries