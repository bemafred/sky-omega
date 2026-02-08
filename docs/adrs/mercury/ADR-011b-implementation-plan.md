# ADR-011b Implementation Plan: QueryResults Stack Reduction

## Executive Summary

This plan details the implementation of ADR-011 to reduce `QueryResults` from ~22KB to ~2KB, eliminating the stack overflow issue affecting 335 tests.

**Estimated Effort**: 2-3 focused implementation sessions
**Risk Level**: Medium (unsafe code, but well-contained)
**Test Coverage**: 34,000+ existing tests provide regression safety

---

## Current State Analysis

### Problem
- `QueryResults` is a ~22KB ref struct
- Embeds 7 scan types (only 1 active at a time)
- Windows 1MB stack limit causes overflow after ~40-50 tests
- 335 tests currently fail with exit code 134 (stack overflow)

### Size Breakdown (Actual Measurements 2026-01-26)

| Component | Current Size | After Phase 2 | After Phase 3 |
|-----------|-------------|---------------|---------------|
| `TriplePatternScan` | ~500B | ~500B | ~500B |
| `MultiPatternScan` | **18,080B (~18KB)** | ~18KB | ~1KB |
| `DefaultGraphUnionScan` | **33,456B (~33KB)** | ~33KB | ~3KB |
| `CrossGraphMultiPatternScan` | **15,800B (~16KB)** | ~16KB | ~1KB |
| `SubQueryScan` | **1,976B (~2KB)** | ~2KB | ~2KB |
| `QueryResults` (total) | **89,640B (~90KB!)** | ~35KB | ~5KB |

**Note**: Actual sizes are 3-4x larger than ADR-011 estimates. This makes the fix even more critical.

---

## Implementation Phases

### Phase 1: Preparation [1-2 hours]

#### 1.1 Create Stack Size Measurement Test

**File**: `tests/Mercury.Tests/Infrastructure/StackSizeTests.cs`

```csharp
using System.Runtime.CompilerServices;
using Xunit;

namespace SkyOmega.Mercury.Tests.Infrastructure;

public class StackSizeTests
{
    [Fact]
    public void QueryResults_CurrentSize_Baseline()
    {
        // Document current size for comparison
        var size = Unsafe.SizeOf<QueryResults>();

        // Current expected: ~22KB
        Assert.True(size > 20000, $"QueryResults size {size} is smaller than expected baseline");
        Assert.True(size < 25000, $"QueryResults size {size} is larger than expected baseline");
    }

    [Fact]
    public void MultiPatternScan_CurrentSize_Baseline()
    {
        var size = Unsafe.SizeOf<MultiPatternScan>();

        // Current expected: ~7.2KB
        Assert.True(size > 7000, $"MultiPatternScan size {size} is smaller than expected");
    }

    // After Phase 2, change assertion to:
    // [Fact]
    // public void QueryResults_Size_Under9KB()
    // {
    //     var size = Unsafe.SizeOf<QueryResults>();
    //     Assert.True(size < 9000, $"QueryResults size {size} exceeds 9KB target");
    // }

    // After Phase 3, change assertion to:
    // [Fact]
    // public void QueryResults_Size_Under3KB()
    // {
    //     var size = Unsafe.SizeOf<QueryResults>();
    //     Assert.True(size < 3000, $"QueryResults size {size} exceeds 3KB target");
    // }
}
```

#### 1.2 Run Full Test Suite Baseline

```bash
# Record current state
dotnet test --no-build -v minimal 2>&1 | tee baseline-tests.log

# Count passing/failing
grep -c "Passed" baseline-tests.log
grep -c "Failed" baseline-tests.log
```

#### 1.3 Create Feature Branch

```bash
git checkout -b feature/adr-011-queryresults-stack-reduction
```

**Checklist:**
- [ ] Stack size test created and passing (documents baseline)
- [ ] Test suite baseline recorded
- [ ] Feature branch created

---

### Phase 2: Discriminated Union [4-6 hours]

**Goal**: Reduce QueryResults from ~22KB to ~8.5KB by overlaying scan types.

#### 2.1 Define ScanType Enum

**File**: `src/Mercury/Sparql/Execution/ScanType.cs` (new file)

```csharp
namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Discriminator for active scan type in QueryResults.
/// Used with StructLayout.Explicit to implement discriminated union.
/// </summary>
internal enum ScanType : byte
{
    /// <summary>No scan active (empty result or materialized only).</summary>
    None = 0,

    /// <summary>Single triple pattern scan (TriplePatternScan).</summary>
    Single = 1,

    /// <summary>Multi-pattern nested loop join (MultiPatternScan).</summary>
    Multi = 2,

    /// <summary>Subquery scan (SubQueryScan).</summary>
    SubQuery = 3,

    /// <summary>Default graph union scan for FROM semantics (DefaultGraphUnionScan).</summary>
    DefaultGraphUnion = 4,

    /// <summary>Cross-graph multi-pattern scan (CrossGraphMultiPatternScan).</summary>
    CrossGraph = 5,

    /// <summary>UNION branch single pattern (TriplePatternScan).</summary>
    UnionSingle = 6,

    /// <summary>UNION branch multi-pattern (MultiPatternScan).</summary>
    UnionMulti = 7,

    /// <summary>Pre-materialized results (iteration from List).</summary>
    Materialized = 8,

    /// <summary>Empty pattern with expressions only (BIND/SELECT without patterns).</summary>
    EmptyPattern = 9
}
```

#### 2.2 Calculate Field Offsets

Based on scan type sizes:
- Largest scan: `DefaultGraphUnionScan` ~8,000 bytes
- UNION branches cannot overlap with main scan (both may be needed)

**Layout Strategy:**
```
Offset 0:     ScanType _activeScanType (1 byte, padded to 8)
Offset 8:     Main scan union (8,000 bytes) - all 5 main scans overlaid
Offset 8008:  ScanType _unionScanType (1 byte, padded to 8)
Offset 8016:  Union scan union (7,200 bytes) - TriplePatternScan and MultiPatternScan overlaid
Offset 15216: Non-overlapping fields (~500 bytes)
```

**Total after Phase 2: ~8.5KB** (down from ~22KB)

#### 2.3 Restructure QueryResults

**File**: `src/Mercury/Sparql/Execution/QueryResults.cs`

```csharp
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Sparql.Execution;

[StructLayout(LayoutKind.Explicit)]
public ref partial struct QueryResults
{
    // === Layout Constants ===
    private const int ScanUnionOffset = 8;
    private const int ScanUnionSize = 8000;  // DefaultGraphUnionScan is largest
    private const int UnionTypeOffset = ScanUnionOffset + ScanUnionSize;  // 8008
    private const int UnionScanOffset = UnionTypeOffset + 8;  // 8016
    private const int UnionScanSize = 7200;  // MultiPatternScan is largest union branch
    private const int CommonFieldsOffset = UnionScanOffset + UnionScanSize;  // 15216

    // === Discriminator (offset 0) ===
    [FieldOffset(0)]
    private ScanType _activeScanType;

    // === Main Scan Union (offset 8, size 8000) ===
    // Only ONE of these is valid based on _activeScanType
    [FieldOffset(ScanUnionOffset)]
    private TriplePatternScan _singleScan;

    [FieldOffset(ScanUnionOffset)]
    private MultiPatternScan _multiScan;

    [FieldOffset(ScanUnionOffset)]
    private SubQueryScan _subQueryScan;

    [FieldOffset(ScanUnionOffset)]
    private DefaultGraphUnionScan _defaultGraphUnionScan;

    [FieldOffset(ScanUnionOffset)]
    private CrossGraphMultiPatternScan _crossGraphScan;

    // === UNION Branch Discriminator (offset 8008) ===
    [FieldOffset(UnionTypeOffset)]
    private ScanType _unionScanType;

    // === UNION Branch Scan Union (offset 8016, size 7200) ===
    // Cannot overlap with main scan - both may be active during UNION iteration
    [FieldOffset(UnionScanOffset)]
    private TriplePatternScan _unionSingleScan;

    [FieldOffset(UnionScanOffset)]
    private MultiPatternScan _unionMultiScan;

    // === Non-Overlapping Fields (offset 15216+) ===
    // Pattern data stored on heap via QueryBuffer
    [FieldOffset(CommonFieldsOffset)]
    private Patterns.QueryBuffer? _buffer;

    [FieldOffset(CommonFieldsOffset + 8)]
    private QuadStore? _store;

    [FieldOffset(CommonFieldsOffset + 16)]
    private Binding[]? _bindings;

    [FieldOffset(CommonFieldsOffset + 24)]
    private char[]? _stringBuffer;

    // ... continue with remaining fields, calculating offsets

    // Note: ReadOnlySpan<char> _source cannot have FieldOffset - it's a ref struct
    // Solution: Store start/length integers instead, reconstruct span when needed
    [FieldOffset(CommonFieldsOffset + 32)]
    private int _sourceStart;  // Not needed if source comes from buffer

    // ... remaining ~40 fields with calculated offsets
```

**Important**: `ReadOnlySpan<char>` cannot be used with `FieldOffset` because it's a ref struct containing a managed reference. Options:
1. Keep `_source` without `FieldOffset` (may work if it's the last field)
2. Store reference to original query string instead
3. Access source through `_buffer` which already stores it

#### 2.4 Update Constructors

Replace boolean flag assignments with enum assignment:

**Before:**
```csharp
internal QueryResults(TriplePatternScan scan, ...)
{
    _singleScan = scan;
    _isMultiPattern = false;
    _isSubQuery = false;
    _isDefaultGraphUnion = false;
    _isCrossGraphMultiPattern = false;
    // ...
}
```

**After:**
```csharp
internal QueryResults(TriplePatternScan scan, ...)
{
    _activeScanType = ScanType.Single;
    _singleScan = scan;
    _unionScanType = ScanType.None;
    // ... non-scan fields unchanged
}
```

**Constructors to update (18 total):**
- [ ] `Empty()` → `_activeScanType = ScanType.None`
- [ ] `EmptyPattern()` → `_activeScanType = ScanType.EmptyPattern`
- [ ] `FromMaterialized()` (6 overloads) → `_activeScanType = ScanType.Materialized`
- [ ] `TriplePatternScan constructor` → `_activeScanType = ScanType.Single`
- [ ] `MultiPatternScan constructor` → `_activeScanType = ScanType.Multi`
- [ ] `SubQueryScan constructor` → `_activeScanType = ScanType.SubQuery`
- [ ] `DefaultGraphUnionScan constructor` → `_activeScanType = ScanType.DefaultGraphUnion`
- [ ] `CrossGraphMultiPatternScan constructor` → `_activeScanType = ScanType.CrossGraph`

#### 2.5 Update MoveNext Dispatch

**File**: `QueryResults.cs`, method `MoveNextUnordered()`

**Before (lines 809-836):**
```csharp
if (_isSubQuery)
    hasNext = _subQueryScan.MoveNext(ref _bindingTable);
else if (_isCrossGraphMultiPattern)
    hasNext = _crossGraphScan.MoveNext(ref _bindingTable);
else if (_isDefaultGraphUnion)
    hasNext = _defaultGraphUnionScan.MoveNext(ref _bindingTable);
else if (_unionBranchActive)
{
    if (_unionIsMultiPattern)
        hasNext = _unionMultiScan.MoveNext(ref _bindingTable);
    else
        hasNext = _unionSingleScan.MoveNext(ref _bindingTable);
}
else
{
    if (_isMultiPattern)
        hasNext = _multiScan.MoveNext(ref _bindingTable);
    else
        hasNext = _singleScan.MoveNext(ref _bindingTable);
}
```

**After:**
```csharp
hasNext = _activeScanType switch
{
    ScanType.Single => _singleScan.MoveNext(ref _bindingTable),
    ScanType.Multi => _multiScan.MoveNext(ref _bindingTable),
    ScanType.SubQuery => _subQueryScan.MoveNext(ref _bindingTable),
    ScanType.DefaultGraphUnion => _defaultGraphUnionScan.MoveNext(ref _bindingTable),
    ScanType.CrossGraph => _crossGraphScan.MoveNext(ref _bindingTable),
    ScanType.UnionSingle => _unionSingleScan.MoveNext(ref _bindingTable),
    ScanType.UnionMulti => _unionMultiScan.MoveNext(ref _bindingTable),
    ScanType.Materialized => MoveNextMaterialized(ref _bindingTable),
    _ => false
};

// Handle UNION branch switching
if (!hasNext && _hasUnion && _activeScanType != ScanType.UnionSingle && _activeScanType != ScanType.UnionMulti)
{
    if (InitializeUnionBranch())
    {
        // Switch to union branch scan type
        _activeScanType = _unionScanType;
        continue;
    }
}
```

#### 2.6 Update Dispose

**File**: `QueryResults.Patterns.cs`

**Before:**
```csharp
public void Dispose()
{
    _singleScan.Dispose();
    _multiScan.Dispose();
    _unionSingleScan.Dispose();
    _unionMultiScan.Dispose();
    _subQueryScan.Dispose();
    _defaultGraphUnionScan.Dispose();
    _crossGraphScan.Dispose();
}
```

**After:**
```csharp
public void Dispose()
{
    // Dispose only the active scan
    switch (_activeScanType)
    {
        case ScanType.Single:
            _singleScan.Dispose();
            break;
        case ScanType.Multi:
            _multiScan.Dispose();
            break;
        case ScanType.SubQuery:
            _subQueryScan.Dispose();
            break;
        case ScanType.DefaultGraphUnion:
            _defaultGraphUnionScan.Dispose();
            break;
        case ScanType.CrossGraph:
            _crossGraphScan.Dispose();
            break;
    }

    // Dispose UNION branch if active
    switch (_unionScanType)
    {
        case ScanType.UnionSingle:
            _unionSingleScan.Dispose();
            break;
        case ScanType.UnionMulti:
            _unionMultiScan.Dispose();
            break;
    }

    _activeScanType = ScanType.None;
    _unionScanType = ScanType.None;
}
```

#### 2.7 Remove Obsolete Boolean Flags

After enum conversion, remove these fields:
- [ ] `_isMultiPattern` → replaced by `_activeScanType == ScanType.Multi`
- [ ] `_isSubQuery` → replaced by `_activeScanType == ScanType.SubQuery`
- [ ] `_isDefaultGraphUnion` → replaced by `_activeScanType == ScanType.DefaultGraphUnion`
- [ ] `_isCrossGraphMultiPattern` → replaced by `_activeScanType == ScanType.CrossGraph`
- [ ] `_unionBranchActive` → replaced by `_activeScanType == ScanType.UnionSingle || ScanType.UnionMulti`
- [ ] `_unionIsMultiPattern` → replaced by `_unionScanType == ScanType.UnionMulti`

**Note**: Some of these may still be needed for compatibility. Update usages gradually.

#### 2.8 Verify Phase 2

```bash
# Run all tests
dotnet test

# Verify stack size reduction
# Update StackSizeTests to verify < 9KB

# Run previously failing tests specifically
dotnet test --filter "FullyQualifiedName~DiagnosticBagTests"
dotnet test --filter "FullyQualifiedName~AllocationTests"
```

**Phase 2 Checklist:**
- [ ] `ScanType.cs` created
- [ ] `QueryResults.cs` updated with `StructLayout.Explicit`
- [ ] All 18 constructors updated
- [ ] `MoveNextUnordered()` updated with switch expression
- [ ] `Dispose()` updated with switch dispatch
- [ ] Boolean flags removed (or marked obsolete)
- [ ] All tests passing
- [ ] Stack size verified < 9KB

---

### Phase 3: Pooled Enumerators [3-4 hours]

**Goal**: Reduce MultiPatternScan from ~7.2KB to ~200B, cascading to QueryResults ~2KB.

#### 3.1 Update MultiPatternScan

**File**: `src/Mercury/Sparql/Execution/Operators.cs`

**Before (lines 1900-1911):**
```csharp
private TemporalResultEnumerator _enum0;
private TemporalResultEnumerator _enum1;
private TemporalResultEnumerator _enum2;
// ... through _enum11
```

**After:**
```csharp
internal ref struct MultiPatternScan
{
    // Enumerator storage rented from pool (8 bytes reference)
    private TemporalResultEnumerator[]? _enumerators;
    private readonly IBufferManager _bufferManager;

    // Pattern boxed to avoid 4KB inline storage (8 bytes reference)
    private sealed class BoxedPattern
    {
        public GraphPattern Pattern;
    }
    private readonly BoxedPattern? _boxedPattern;

    // State fields remain inline (~150 bytes)
    private int _currentLevel;
    private bool _init0, _init1, _init2, _init3, _init4, _init5;
    private bool _init6, _init7, _init8, _init9, _init10, _init11;
    private int _bindingCount0, _bindingCount1, _bindingCount2, _bindingCount3;
    private int _bindingCount4, _bindingCount5, _bindingCount6, _bindingCount7;
    private int _bindingCount8, _bindingCount9, _bindingCount10, _bindingCount11;
    // ... remaining state fields

    public MultiPatternScan(QuadStore store, ReadOnlySpan<char> source,
        in GraphPattern pattern, IBufferManager? bufferManager = null)
    {
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;

        // Rent enumerator array from pool
        _enumerators = ArrayPool<TemporalResultEnumerator>.Shared.Rent(12);

        // Box pattern to avoid inline storage
        _boxedPattern = new BoxedPattern { Pattern = pattern };

        // Initialize state
        _currentLevel = 0;
        // ...
    }

    public void Dispose()
    {
        // Dispose active enumerators
        if (_enumerators != null)
        {
            for (int i = 0; i <= _currentLevel && i < 12; i++)
            {
                if (IsInitialized(i))
                    _enumerators[i].Dispose();
            }

            // Return array to pool
            ArrayPool<TemporalResultEnumerator>.Shared.Return(_enumerators, clearArray: true);
            _enumerators = null;
        }
    }

    // Helper to check if enumerator is initialized
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInitialized(int level) => level switch
    {
        0 => _init0, 1 => _init1, 2 => _init2, 3 => _init3,
        4 => _init4, 5 => _init5, 6 => _init6, 7 => _init7,
        8 => _init8, 9 => _init9, 10 => _init10, 11 => _init11,
        _ => false
    };

    // Access enumerator by index
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref TemporalResultEnumerator GetEnumerator(int level) => ref _enumerators![level];
}
```

#### 3.2 Update DefaultGraphUnionScan

Apply same pattern - it embeds MultiPatternScan, so will automatically benefit.

If it also has inline storage, apply pooling there too.

#### 3.3 Update CrossGraphMultiPatternScan

Similar pooling for its 4 enumerators:

```csharp
// Before: 4 inline enumerators (~1KB)
private TemporalResultEnumerator _enum0, _enum1, _enum2, _enum3;

// After: pooled array (~8 bytes)
private TemporalResultEnumerator[]? _enumerators;  // Rent 4 from pool
```

#### 3.4 Verify Phase 3

```bash
# Run all tests
dotnet test

# Verify stack size reduction
# Update StackSizeTests to verify < 3KB

# Run memory leak tests
dotnet test --filter "FullyQualifiedName~BufferManagerTests"
```

**Phase 3 Checklist:**
- [ ] `MultiPatternScan` updated with pooled enumerators
- [ ] `DefaultGraphUnionScan` updated (if needed)
- [ ] `CrossGraphMultiPatternScan` updated
- [ ] All constructors pass IBufferManager
- [ ] All Dispose() methods return pooled arrays
- [ ] All tests passing
- [ ] Stack size verified < 3KB
- [ ] No memory leaks (TrackingBufferManager tests)

---

### Phase 4: Validation & Cleanup [1-2 hours]

#### 4.1 Run Full W3C Conformance Suite

```bash
cd tests/Mercury.Tests
dotnet test --filter "FullyQualifiedName~W3C" -v normal
```

Verify all 1600+ W3C tests pass.

#### 4.2 Run Previously Failing Tests

```bash
# Run the specific 335 tests that were failing
dotnet test --filter "FullyQualifiedName~DiagnosticBagTests"
dotnet test --filter "FullyQualifiedName~AllocationTests"
dotnet test --filter "FullyQualifiedName~ConcurrentAccessTests"
dotnet test --filter "FullyQualifiedName~ContentNegotiationTests"
# ... etc
```

#### 4.3 Run Benchmarks

```bash
# Verify no performance regression
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*ExecutionBenchmarks*"

# Compare with baseline (should be within 5%)
```

#### 4.4 Update Documentation

- [ ] Update ADR-011 status from "Proposed" to "Accepted"
- [ ] Update CLAUDE.md with new stack characteristics
- [ ] Add migration notes for any API changes

#### 4.5 Create PR

```bash
git add -A
git commit -m "Implement ADR-011: QueryResults stack reduction via discriminated union

- Add ScanType enum for discriminated union pattern
- Apply StructLayout.Explicit to overlay scan types
- Reduce QueryResults from ~22KB to ~8.5KB (Phase 2)
- Pool enumerator storage in MultiPatternScan
- Reduce QueryResults from ~8.5KB to ~2KB (Phase 3)
- Fix 335 tests failing with stack overflow on Windows

Fixes stack overflow issue affecting test reliability.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"

git push -u origin feature/adr-011-queryresults-stack-reduction
```

---

## Risk Mitigation

### Risk 1: Incorrect Field Access
**Mitigation**: Debug assertions verify discriminator before scan access
```csharp
[Conditional("DEBUG")]
private void AssertActiveScan(ScanType expected)
{
    if (_activeScanType != expected)
        throw new InvalidOperationException($"Expected scan type {expected} but was {_activeScanType}");
}
```

### Risk 2: Memory Corruption from Overlapping Fields
**Mitigation**:
- Extensive test coverage (34,000+ tests)
- Only one scan type written per constructor
- Dispose only disposes active scan

### Risk 3: Performance Regression
**Mitigation**:
- Switch expression compiles to jump table (O(1))
- Pooled arrays avoid allocation but add indirection
- Benchmark comparison before merge

### Risk 4: Pool Exhaustion Under Load
**Mitigation**:
- `ArrayPool<T>` handles growth automatically
- TrackingBufferManager tests verify proper return
- Dispose pattern ensures cleanup

---

## Rollback Plan

If issues are discovered:

1. **Partial Rollback**: Revert Phase 3 (pooling) but keep Phase 2 (union)
2. **Full Rollback**: Revert to main branch
3. **Hotfix**: Keep changes but add `[NoInlining]` to more paths as interim

---

## Success Criteria

| Criterion | Target | Verification |
|-----------|--------|--------------|
| Stack size | < 3KB | `Unsafe.SizeOf<QueryResults>()` in test |
| Test pass rate | 100% | `dotnet test` all green |
| W3C conformance | 100% | All 1600+ W3C tests pass |
| Performance | < 5% regression | Benchmark comparison |
| Memory leaks | None | TrackingBufferManager tests |
| Windows CI | Green | CI pipeline passes |

---

## Files Modified Summary

| File | Changes |
|------|---------|
| `ScanType.cs` (new) | Discriminator enum definition |
| `QueryResults.cs` | StructLayout.Explicit, constructors, MoveNext |
| `QueryResults.Patterns.cs` | Dispose() switch dispatch |
| `QueryResults.Modifiers.cs` | Update any scan type checks |
| `Operators.cs` | Pool enumerators in MultiPatternScan |
| `StackSizeTests.cs` (new) | Verify size constraints |
| `ADR-011-*.md` | Update status to Accepted |
| `CLAUDE.md` | Update stack size documentation |

---

## Implementation Order

1. **Day 1**: Phase 1 (preparation) + Phase 2 (discriminated union)
2. **Day 2**: Phase 3 (pooling) + Phase 4 (validation)
3. **Day 3**: Buffer for issues, PR review, merge

This plan provides a clear path to eliminate the stack overflow issue while maintaining the zero-GC design for simple queries.
