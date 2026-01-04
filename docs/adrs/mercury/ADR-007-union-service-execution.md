# ADR-007: UNION Branch SERVICE Execution

## Status

Accepted (Implemented 2026-01-03)

## Related ADRs

- [SERVICE Execution via IScan Interface](mercury-adr-service-scan-interface.md) - Foundation for SERVICE execution

## Problem

SERVICE clauses inside UNION branches are parsed but not executed:

```sparql
SELECT ?item ?source WHERE {
    { ?item <http://ex.org/type> <http://ex.org/Widget> }
    UNION
    { SERVICE <http://remote.example.org/sparql> { ?item <http://ex.org/source> ?source } }
}
```

The query parses correctly but the SERVICE branch is silently dropped during execution.

### Root Cause

1. **No branch affiliation**: `ServiceClause` is stored flat in `GraphPattern._svc0/_svc1` without tracking which UNION branch it belongs to

2. **InitializeUnionBranch ignores SERVICE**: The method only looks for `PatternKind.Triple`:
   ```csharp
   for (int i = unionStart; i < _buffer.PatternCount; i++)
   {
       if (patterns[i].Kind == PatternKind.Triple)  // Never sees SERVICE
       {
           // ...
       }
   }
   ```

3. **Empty branch returns false**: If UNION branch has only SERVICE (no local patterns), `InitializeUnionBranch` returns false, skipping the branch entirely

## Solution

### Phase 1: Track Branch Affiliation

Add `UnionBranch` field to `ServiceClause`:

```csharp
public struct ServiceClause
{
    public bool Silent;
    public bool IsOptional;
    public int UnionBranch;    // 0 = first/no UNION, 1 = second branch
    public Term Endpoint;
    // ...
}
```

Add `_hasUnion` flag to `GraphPattern` (separate from `_unionStartIndex`) to properly detect UNION even when first branch has no triple patterns:

```csharp
private bool _hasUnion;       // True if UNION keyword was encountered
private bool _inUnionBranch;  // True when parsing second UNION branch

public void StartUnionBranch()
{
    _hasUnion = true;
    _unionStartIndex = _patternCount;
    _inUnionBranch = true;
}
```

### Phase 2: Execute SERVICE in UNION Branch

Route UNION+SERVICE queries through `ExecuteWithServiceMaterialized()` which handles all combinations:

1. **SERVICE in first branch only**: Execute local patterns, join with SERVICE
2. **SERVICE in second branch only**: Execute first branch, then SERVICE-only second branch
3. **SERVICE in both branches**: Execute both SERVICE clauses separately
4. **Local + SERVICE in same branch**: Join local patterns with SERVICE results

```csharp
private List<MaterializedRow> ExecuteUnionWithServiceMaterialized(...)
{
    // Separate SERVICE clauses by branch
    ServiceClause? firstBranchService = null;
    ServiceClause? secondBranchService = null;
    for (int i = 0; i < pattern.ServiceClauseCount; i++)
    {
        var svc = pattern.GetServiceClause(i);
        if (svc.UnionBranch == 0)
            firstBranchService = svc;
        else
            secondBranchService = svc;
    }

    // Execute first branch
    if (firstBranchPatternCount > 0 && firstBranchService.HasValue)
    {
        // Both local patterns AND SERVICE - join them
        var localResults = ExecuteFirstBranchPatterns(...);
        foreach (var localRow in localResults)
        {
            var serviceResults = FetchServiceResults(firstBranchService.Value, bindingTable);
            // Join and add to results
        }
    }
    else if (firstBranchPatternCount > 0)
    {
        // Local patterns only
        results.AddRange(ExecuteFirstBranchPatterns(...));
    }
    else if (firstBranchService.HasValue)
    {
        // SERVICE-only first branch
        var serviceResults = FetchServiceResults(...);
        // Iterate and add to results
    }

    // Execute second branch (similar logic)
    // ...
}
```

### Phase 3: Variable Endpoint Support

Updated `ExecuteServiceJoinPhase` to handle variable endpoints:

```csharp
var isVariableEndpoint = serviceClause.IsVariable;

// For fixed endpoints, fetch SERVICE results ONCE (key optimization)
List<ServiceResultRow>? cachedServiceResults = null;
if (!isVariableEndpoint)
{
    cachedServiceResults = FetchServiceResults(serviceClause, emptyBindingTable);
}

foreach (var localRow in localResults)
{
    // For variable endpoints, fetch per local result (endpoint may differ)
    var serviceResults = isVariableEndpoint
        ? FetchServiceResults(serviceClause, bindingTable)  // Has endpoint binding
        : cachedServiceResults!;
    // ...
}
```

## Success Criteria

- [x] `{ local } UNION { SERVICE ... }` executes both branches
- [x] `{ SERVICE ... } UNION { local }` executes both branches
- [x] `{ SERVICE ... } UNION { SERVICE ... }` executes both SERVICE clauses
- [x] `{ local + SERVICE } UNION { local }` joins first branch correctly
- [x] Variable endpoint SERVICE executes when endpoint is bound
- [x] All existing SERVICE tests continue to pass (1834 total tests pass)

## Implementation Notes

- `ServicePatternScan` from `ServiceMaterializer.cs` handles iteration
- `FetchServiceResults` from `QueryExecutor.Service.cs` handles fetching
- UNION+SERVICE routes through `QueryExecutor.ExecuteWithServiceMaterialized()` path
- Variable endpoint optimization: fixed endpoints still fetch once, variable endpoints fetch per local result

## Tests Added

- `Parse_UnionWithService_TracksUnionBranch` - Verifies `UnionBranch = 1` for SERVICE in second branch
- `Parse_ServiceInFirstUnionBranch_TracksUnionBranch` - Verifies `UnionBranch = 0` for SERVICE in first branch
- `Execute_UnionLocalAndService_ExecutesBothBranches` - `{ local } UNION { SERVICE }`
- `Execute_UnionServiceAndLocal_ExecutesBothBranches` - `{ SERVICE } UNION { local }`
- `Execute_UnionServiceAndService_ExecutesBothServices` - `{ SERVICE } UNION { SERVICE }`
- `Execute_UnionLocalWithServiceAndLocal_ExecutesBothBranches` - `{ local + SERVICE } UNION { local }`
- `Execute_ServiceWithVariableEndpoint_ResolvesFromBindings` - `SERVICE ?endpoint { ... }`
