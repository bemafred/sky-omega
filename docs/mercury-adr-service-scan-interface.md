# ADR: SERVICE Execution via IScan Interface and Temp Stores

## Status

Proposed

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

Implementation:

```csharp
internal ref struct TriplePatternScan : IScan
{
    // Existing implementation unchanged
    public bool MoveNext(ref BindingTable bindings) { ... }
    public void Dispose() { ... }
}
```

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

### ServiceStore

Temporary `QuadStore` with `TempPath` lifecycle management:

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

Orchestrates SERVICE materialization before query execution:

```csharp
public sealed class ServiceMaterializer : IDisposable
{
    private readonly ISparqlServiceExecutor _executor;
    private readonly List<ServiceStore> _stores = new();

    static ServiceMaterializer()
    {
        // Clean orphaned stores from crashes
        TempPath.CleanupStale("service");
    }

    public ServiceStore Materialize(ServiceClause clause, ReadOnlySpan<char> source)
    {
        var endpoint = ResolveEndpoint(clause, source);
        var store = new ServiceStore(ComputeHash(endpoint));
        _stores.Add(store);

        var query = BuildSparqlQuery(clause, source);
        var results = _executor.ExecuteSelectAsync(endpoint, query)
            .AsTask().GetAwaiter().GetResult();

        LoadResultsToStore(store.Store, results, clause, source);
        return store;
    }

    public void Dispose()
    {
        foreach (var store in _stores)
            store.Dispose();
    }
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

1. **Extract IScan interface** from TriplePatternScan
    - Run all tests → must pass unchanged

2. **Implement ServiceStore** using TempPath
    - Unit test: create, write triples, query, dispose, verify cleanup

3. **Implement ServicePatternScan** wrapping TriplePatternScan
    - Unit test: materialize mock results, scan, verify bindings

4. **Implement ServiceMaterializer**
    - Integration test: HTTP mock → temp store → query results

5. **Integrate into QueryExecutor**
    - Existing SERVICE tests must pass
    - Add SERVICE + UNION tests

6. **Remove ServiceScan** (old implementation)
    - Dead code elimination

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| ref struct can't implement interface | Use duck typing or wrapper; IScan as documentation contract |
| Temp store overhead | Only for SERVICE queries; simple queries unchanged |
| Disk I/O for temp stores | Consider in-memory option for small result sets |
| HTTP blocking in materializer | Already blocked in current ServiceScan; no regression |

## Success Criteria

- [ ] All existing tests pass after Phase 1
- [ ] SERVICE-only queries work via temp stores
- [ ] SERVICE + local pattern joins work
- [ ] SERVICE + UNION executes both branches
- [ ] OPTIONAL { SERVICE } preserves outer bindings
- [ ] Temp stores cleaned up after query
- [ ] Orphan cleanup works after crash simulation