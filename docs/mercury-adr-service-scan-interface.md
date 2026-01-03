# ADR: SERVICE Execution via IScan Interface and Temp Stores

## Status

Proposed (Reviewed 2026-01-03)

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

    public ServiceStore Materialize(ServiceClause clause, ReadOnlySpan<char> source,
        BindingTable? incomingBindings = null)
    {
        var endpoint = ResolveEndpoint(clause, source, incomingBindings);
        var store = new ServiceStore(ComputeHash(endpoint));
        _stores.Add(store);

        var query = BuildSparqlQuery(clause, source, incomingBindings);
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

1. **Extract IScan interface** from TriplePatternScan
    - Run all tests → must pass unchanged

2. **Implement ServiceStore** using TempPath
    - Unit test: create, write triples, query, dispose, verify cleanup

3. **Add ServiceBenchmarks** to establish baseline
    - Measure current SERVICE execution overhead
    - Include: SERVICE-only, SERVICE+local join, multiple SERVICE clauses

4. **Implement ServicePatternScan** wrapping TriplePatternScan
    - Unit test: materialize mock results, scan, verify bindings

5. **Implement ServiceMaterializer**
    - Integration test: HTTP mock → temp store → query results

6. **Integrate into QueryExecutor**
    - Existing SERVICE tests must pass
    - Add SERVICE + UNION tests

7. **Verify benchmark results**
    - No regression beyond 10% for SERVICE-only queries
    - Document any performance changes

8. **Remove ServiceScan** (old implementation)
    - Dead code elimination

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| ref struct can't implement interface | Duck typing with consistent method signatures; IScan as documentation contract; C# 13 `allows ref struct` for generic constraints |
| Temp store overhead | Only for SERVICE queries; simple queries unchanged |
| Disk I/O for temp stores | Future optimization: in-memory path for small result sets (see below) |
| HTTP blocking in materializer | Already blocked in current ServiceScan; no regression |
| Variable endpoint multiplicity | Track per-endpoint stores; hash by resolved URI |
| Memory pressure under concurrent queries | TempPath ownership tracking prevents leaks; SafeCleanup handles stragglers |

## Future Optimization: In-Memory Store

Once the `IScan` abstraction is in place, adding an in-memory path for small result sets is **trivial** (~50 lines):

```csharp
internal ref struct InMemoryServiceScan
{
    private readonly List<MaterializedRow> _rows;
    private int _index;

    public bool MoveNext(ref BindingTable bindings)
    {
        while (_index < _rows.Count)
        {
            if (TryBindRow(_rows[_index++], ref bindings))
                return true;
        }
        return false;
    }

    public void Dispose() { } // GC handles List<T>
}
```

**Threshold selection:**

| Result Count | Recommended Path |
|--------------|-----------------|
| < 100 | In-memory (linear scan acceptable) |
| 100-500 | Configurable threshold |
| > 500 | Temp QuadStore (B+Tree index pays off for joins) |

The `ServiceMaterializer` becomes the decision point:

```csharp
if (results.Count < options.InMemoryThreshold)
    return new InMemoryServiceScan(results);
else
    return new ServicePatternScan(CreateTempStore(results), ...);
```

**Why defer this?** The temp store approach is correct for all sizes; in-memory is a performance optimization. Implementing temp stores first ensures correctness, then in-memory can be added without architectural changes.

## Success Criteria

- [ ] All existing tests pass after Phase 1
- [ ] SERVICE-only queries work via temp stores
- [ ] SERVICE + local pattern joins work
- [ ] SERVICE + UNION executes both branches
- [ ] OPTIONAL { SERVICE } preserves outer bindings
- [ ] Temp stores cleaned up after query
- [ ] Orphan cleanup works after crash simulation
- [ ] SERVICE benchmarks show no regression > 10%
- [ ] Variable endpoint SERVICE resolves correctly
- [ ] VALUES clause injection works for binding propagation