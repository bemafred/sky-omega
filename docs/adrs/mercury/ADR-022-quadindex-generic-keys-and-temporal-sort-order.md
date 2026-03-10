# ADR-022: QuadIndex Generic Key Fields and Time-Leading Sort Order

## Status

**Proposed** — 2026-03-08 | Phase 1 (rename) complete — 2026-03-10

## Context

`QuadIndex` is an internal B+Tree index used by `QuadStore`. Four instances provide different access paths over the same quads:

| Instance | Intended sort order | Purpose |
|----------|-------------------|---------|
| GSPO | Graph → Subject → Predicate → Object → Time | Entity-first, subject-leading |
| GPOS | Graph → Predicate → Object → Subject → Time | Entity-first, predicate-leading |
| GOSP | Graph → Object → Subject → Predicate → Time | Entity-first, object-leading |
| TGSP | Time → Graph → Subject → Predicate → Object | **Time-first for temporal queries** |

### Problem 1: Misleading field names couple index to RDF

The `TemporalKey` struct uses RDF-specific field names:

```csharp
public long GraphAtom;
public long SubjectAtom;
public long PredicateAtom;
public long ObjectAtom;
```

In the GPOS index, `SubjectAtom` actually holds a predicate ID. In GOSP, it holds an object ID. The names are lies — `QuadIndex` is a generic multi-dimensional B+Tree that knows nothing about RDF. The RDF-to-dimension mapping belongs in `QuadStore`, which swaps parameters at write time:

```csharp
_gspoIndex.AddHistorical(subject, predicate, @object, ...);
_gposIndex.AddHistorical(predicate, @object, subject, ...);
_gospIndex.AddHistorical(@object, subject, predicate, ...);
```

Similarly, `QuadStore` and `QuadIndex` method signatures use `obj` as a parameter name. The idiomatic C# convention for a parameter semantically named "object" is `@object` (keyword escape), not an abbreviation.

### Problem 2: TGSP index is a byte-for-byte duplicate of GSPO

All four index instances share a single `TemporalKey.CompareTo` with hardcoded sort order:

```
GraphAtom → SubjectAtom → PredicateAtom → ObjectAtom → ValidFrom → ValidTo → TransactionTime
```

The "different" indexes work by swapping what goes into `SubjectAtom`/`PredicateAtom`/`ObjectAtom` at write time. But the TGSP index receives the **same argument order** as GSPO:

```csharp
_gspoIndex.AddHistorical(subject, predicate, @object, ...);
_tgspIndex.AddHistorical(subject, predicate, @object, ...);  // ← identical
```

Since both instances use the same `CompareTo`, the TGSP index produces **identical B+Tree content** to GSPO. It consumes 1GB of disk (64MB in testing) for zero additional query capability.

### Problem 3: Temporal range queries require full scans

`SelectOptimalIndex` routes temporal range queries (`DURING`, `ALL VERSIONS` with no entity binding) to the TGSP index. But since that index sorts entities before time, `CreateSearchKey` builds min/max keys spanning the entire entity space:

- minKey: `G=0, S=0, P=0, O=0, VF=0, VT=0, TT=0`
- maxKey: `G=MAX, S=MAX, P=MAX, O=MAX, VF=MAX, VT=MAX, TT=MAX`

Every entry is visited; `MatchesTemporalQuery` filters post-hoc. This is **O(N)** for N triples — unacceptable for a bitemporal substrate where "what changed in this time window?" is a primary query pattern.

With a proper time-leading sort order, the B+Tree could seek directly to the time window — **O(log N + k)** where k is the number of results in the window.

### How this happened

The old `TemporalIndexType` enum names reveal the intent:

```csharp
SPOT,  // Subject-Predicate-Object-Time
POST,  // Predicate-Object-Subject-Time
OSPT,  // Object-Subject-Predicate-Time
TSPO   // Time-Subject-Predicate-Object  ← time was meant to lead
```

The parameter-swapping strategy can reorder the three entity dimensions but **cannot** move time before entities — `CompareTo` always sorts the four entity fields (`GraphAtom`, `SubjectAtom`, `PredicateAtom`, `ObjectAtom`) before `ValidFrom`/`ValidTo`/`TransactionTime`. When `GraphAtom` was added as a leading field, the gap widened further.

## Decision

### 1) Rename TemporalKey fields to generic dimension names

```csharp
// Before (lies about content)
public long GraphAtom;
public long SubjectAtom;
public long PredicateAtom;
public long ObjectAtom;

// After (honest, generic)
public long Graph;
public long Primary;
public long Secondary;
public long Tertiary;
```

`QuadIndex` is a generic multi-dimensional index. The field names must reflect that. The RDF-to-dimension mapping lives exclusively in `QuadStore`.

### 2) Rename method parameters: `obj` → `@object`, entity params in QuadIndex to `primary`/`secondary`/`tertiary`

In `QuadIndex`:
```csharp
public void Add(ReadOnlySpan<char> primary, ReadOnlySpan<char> secondary,
                ReadOnlySpan<char> tertiary, ...)
```

In `QuadStore` (public API, RDF-aware):
```csharp
public void Add(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
                ReadOnlySpan<char> @object, ...)
```

### 3) Make sort order configurable via enum

Introduce a `KeySortOrder` enum:

```csharp
internal enum KeySortOrder
{
    /// Graph → Primary → Secondary → Tertiary → ValidFrom → ValidTo → TransactionTime
    EntityFirst,

    /// ValidFrom → Graph → Primary → Secondary → Tertiary → ValidTo → TransactionTime
    TimeFirst
}
```

`QuadIndex` accepts this at construction time and stores a comparison delegate selected once:

```csharp
internal delegate int KeyComparer(in TemporalKey a, in TemporalKey b);
```

Two static methods provide the two orderings:

```csharp
static int CompareEntityFirst(in TemporalKey a, in TemporalKey b)
    // Graph → Primary → Secondary → Tertiary → ValidFrom → ValidTo → TransactionTime

static int CompareTimeFirst(in TemporalKey a, in TemporalKey b)
    // ValidFrom → Graph → Primary → Secondary → Tertiary → ValidTo → TransactionTime
```

`QuadIndex` stores the delegate at construction and calls it everywhere the B+Tree compares keys — insertion, search, and enumeration. The JIT will optimize the monomorphic delegate call site to a direct call with no overhead. This is cleaner than branching on an enum in the comparison method, and avoids the code duplication of a separate key struct.

### 4) Construct TGSP index with TimeFirst sort order

```csharp
_gspoIndex = new QuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, KeySortOrder.EntityFirst);
_gposIndex = new QuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, KeySortOrder.EntityFirst);
_gospIndex = new QuadIndex(gospPath, _atoms, options.IndexInitialSizeBytes, KeySortOrder.EntityFirst);
_tgspIndex = new QuadIndex(tgspPath, _atoms, options.IndexInitialSizeBytes, KeySortOrder.TimeFirst);
```

### 5) Update CreateSearchKey for time-leading queries

When querying the TGSP index, the time bounds must become the **leading** search bounds:

```csharp
// Time-first: seek by ValidFrom range, entities are secondary
minKey: VF=rangeStart, G=0, P=0, S=0, T=0, VT=0, TT=0
maxKey: VF=rangeEnd,   G=MAX, P=MAX, S=MAX, T=MAX, VT=MAX, TT=MAX
```

This transforms temporal range queries from O(N) full scans to O(log N + k) seeks.

### 6) Update TemporalIndexType enum names

```csharp
internal enum TemporalIndexType
{
    GSPO,  // Graph-Subject-Predicate-Object (maps S→Primary, P→Secondary, O→Tertiary)
    GPOS,  // Graph-Predicate-Object-Subject (maps P→Primary, O→Secondary, S→Tertiary)
    GOSP,  // Graph-Object-Subject-Predicate (maps O→Primary, S→Secondary, P→Tertiary)
    TGSP   // Time-Graph-Subject-Predicate-Object (TimeFirst sort, maps S→Primary, P→Secondary, O→Tertiary)
}
```

### 7) No disk migration needed

Mercury has not shipped production stores. All existing stores are development/test artifacts. This ADR assumes a clean slate — no backward compatibility with existing index files.

## Implementation

### Phase 1: Rename fields and parameters (mechanical, all tests must pass)

1. Rename `TemporalKey` fields: `SubjectAtom` → `Primary`, `PredicateAtom` → `Secondary`, `ObjectAtom` → `Tertiary`, `GraphAtom` → `Graph`
2. Rename `QuadIndex` method parameters: `subject` → `primary`, `predicate` → `secondary`, `obj` → `tertiary`
3. Rename `QuadStore` method parameters: `obj` → `@object`
4. Rename `TemporalQuad` fields to match (or add RDF-named accessors that map to generic storage)
5. Update `TemporalIndexType` enum values: `SPOT` → `GSPO`, `POST` → `GPOS`, `OSPT` → `GOSP`, `TSPO` → `TGSP`
6. Update all internal references (enumerators, `TemporalResultEnumerator`, `SelectOptimalIndex`, etc.)

### Phase 2: Add configurable sort order

1. Add `KeySortOrder` enum
2. Add `CompareTimeFirst` static method to `TemporalKey`
3. Store sort order in `QuadIndex`, use it in B+Tree insertion and search
4. Construct TGSP with `KeySortOrder.TimeFirst`

### Phase 3: Fix temporal query routing

1. Update `CreateSearchKey` to produce time-leading bounds when sort order is `TimeFirst`
2. Verify `SelectOptimalIndex` correctly routes temporal queries to TGSP
3. Update `TemporalQuadEnumerator` if needed (the `minKey`/`maxKey` bounds do the heavy lifting)

### Phase 4: Verify

1. All existing tests pass (rename is mechanical, sort order fix changes only TGSP behavior)
2. Add targeted tests for temporal range queries with no entity binding
3. Verify O(log N + k) behavior via page-access counting or benchmarks

## Consequences

### Benefits

- **Field names are honest**: `Primary`/`Secondary`/`Tertiary` reflect the generic nature of the index. RDF semantics live in `QuadStore` where they belong.
- **TGSP index actually works**: Temporal range queries go from O(N) full scan to O(log N + k) B+Tree seek.
- **1GB disk recovered** (was wasted on a duplicate index): TGSP now contains differently-sorted data that serves a distinct purpose.
- **`@object` follows C# convention**: Clearer than abbreviation, uses the language's keyword-escape feature as intended.

### Risks

- Sort order divergence means TGSP cannot be used as a fallback for entity queries (it never was — `SelectOptimalIndex` already only routes temporal queries there).
- Two comparison paths add a small amount of code to maintain.

## Alternatives Considered

### A) Separate TemporalFirstKey struct

A distinct struct with `ValidFrom` as the first field and its own `CompareTo`. Avoids any runtime dispatch.

Rejected: Doubles the key-related code (two structs, two B+Tree entry types, two page layouts). The delegate approach achieves the same zero-overhead via JIT optimization of monomorphic call sites, with far less code.

### B) Branch on enum in comparison method

Store a `KeySortOrder` enum and branch inside a single comparison method.

Rejected: A branch on every comparison is noisier than a delegate selected once at construction. The delegate approach is both cleaner and equally performant — the JIT devirtualizes monomorphic delegate calls to direct calls.

### C) Remove TGSP index entirely

If temporal range queries are rare, save the disk and code.

Rejected: Mercury is a **bitemporal** substrate. Temporal queries are a first-class operation, not an edge case. The fourth index exists for a reason — it just needs to work correctly.

### D) Keep SubjectAtom/PredicateAtom/ObjectAtom names

"Everyone knows what they mean."

Rejected: They mean different things in different indexes — that's the problem. Generic names eliminate a class of reasoning errors and make the parameter-swapping strategy self-evident.

## References

- `src/Mercury/Storage/QuadIndex.cs` — B+Tree index with `TemporalKey`
- `src/Mercury/Storage/QuadStore.cs` — Multi-index quad store, parameter remapping
- `src/Mercury/Storage/StorageOptions.cs` — Index configuration
- `docs/adrs/mercury/ADR-003-buffer-pattern.md` — Related zero-GC patterns
