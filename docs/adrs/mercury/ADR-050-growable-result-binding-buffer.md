# ADR-050: Growable result-binding buffer — eliminate the silent overflow-drop; specify the literal-size policy

**Status:** Completed — 2026-06-22

**Validation outcome:** Implemented in `BindingTable` via `ArrayPool<char>.Shared` growth (default for every table; no per-site change). Verified: `tools/repro-literal.cs` every length 300–4000 round-trips (was a hard cliff at 1023); `LongLiteralRoundTripTests` 6/6 (500–100,000 chars); full Mercury/W3C suite 4699/0/6. **DrHook-validated live (2026-06-22):** a breakpoint at `EnsureStringCapacity` fired during a SELECT's result materialization (`BindWithHash` ← `MoveNextOrdered` ← `Query`) over a 1023-char literal — the buffer grows on the exact binding that used to silently drop. *Minor follow-up:* `ReturnGrownBuffer()` exists but is unwired at call sites; the rare final grown array is GC'd rather than pooled (steady state stays zero-GC; growth is pooled-rented). Commit `7c43ebf`.

## Context

On 2026-06-21, while dogfooding the just-fixed DrHook lazy inspection (ADR-007 inspection item, commit `6fefa8b`) against Mercury, we root-caused a silent data-loss bug: **string literals of ≥ 1023 characters are dropped from SPARQL result rows.**

### The defect

`BindingTable.Bind` writes each bound value into a shared `_stringBuffer` and records a `(StringOffset, StringLength)` slice. Every overflow site reads:

```csharp
// BindingTable.cs:44 (long), :70 (double), :94 (bool), :115 (ReadOnlySpan<char>), :137 (BindUri)
if (_stringOffset + value.Length > _stringBuffer.Length) return;   // ← silent: no bind, no error, no anomaly
```

When the value does not fit, `Bind` **silently returns without binding** — the variable simply vanishes from the row. The buffer is a 1 KB rented array: `QueryExecutor.cs:115/258` — `_stringBuffer = _bufferManager.Rent<char>(1024).Array!` (comment: *"replaces scattered new char[1024]"*). An object literal renders to its lexical form **plus two quotes**, so a 1023-char literal → 1025 chars → `> 1024` → dropped; 1022 → 1024 → fits. That matches the observed threshold to the character.

### Two distinct faults

1. **Undersized fixed buffer** — 1 KB is far below legitimate literal sizes (descriptions, base64 blobs, embedded documents). A *bigger* fixed buffer (the tree path already rents `new char[16384]`, `TreeJoinExecutor.cs:775`) only moves the cliff.
2. **Silent overflow** — the cardinal *checked-nothing / no-silent-failure* violation. Data is lost with no exception, no `EngineAnomaly`, no truncation marker. This is the more serious fault: a too-small buffer is a tuning miss; a *silent* drop is a substrate-integrity miss.

It is a **read-back** bug, not storage. The quad stores fine — the atom store caps a single atom at `MaxAtomSize` (`HashAtomStore.DefaultMaxAtomSize = 1L<<20` = 1 MB, configurable via `StorageOptions.MaxAtomSize`) and throws **loudly** above it (`HashAtomStore.cs:308/334`). Only the result-binding path fails silently.

### Confirmed live via DrHook

DrHook traced the loss path with zero bespoke instrumentation: `SELECT ?o` → `QueryExecutor.ExecuteGraphViaTree` → `TreeJoinExecutor` → `TriplePatternScan.TryBindVariable` (`:2223`) → `BindingTable.Bind` (`:115`). The result-binding path, not storage. (The same call also surfaced two DrHook gaps — value-type/ref-struct expansion and span-aware conditions — addressed in DrHook ADR-013.)

This bug has been quietly biting us: Mercury's own `ck:`/memory writes go through this path, which is why long `ck:correction` literals came back empty earlier in the session while short ones always worked.

### What the spec and the field say about literal size

- **W3C RDF 1.1** defines a literal's lexical form as *a Unicode string* (NFC) with **no maximum length**. **SPARQL 1.1** imposes **no** result- or binding-size limit. There is no spec cap to honour.
- A survey of the field (Jena, RDF4J, Virtuoso) found **no published per-literal character cap** — literals are storage-bounded, with large ones handled by the store's value/node/blob path. The *absence* of a documented small cap is itself evidence: a 1 KB cap is an outlier bug, not an industry norm.

So the only principled ceiling for Mercury is its **existing** one: `MaxAtomSize`. This ADR makes the binding path honour it.

## Decision (proposed)

### D1 — The result-binding buffer grows on demand; it never silently drops

Replace the silent `return` with **growth**. When a `Bind` would overflow, rent a larger contiguous buffer from `IBufferManager` (zero-GC, pooled), copy the live prefix (`_stringOffset` chars), return the old lease, and re-point. Growth target = `max(needed, 2 × current)` so a run of large binds amortises to O(n) copies, not O(n²).

The buffer is **owned and grown by the query executor** (which holds the `BufferLease`); `BindingTable` is handed the current span and a growth callback (or is restructured to hold the lease + manager). Either way the silent `return` is gone: a value that does not fit triggers growth, not loss.

### D2 — Storage stays contiguous (so we do *not* adopt the chunked collection)

Bindings are `(StringOffset, StringLength)` slices into a **single contiguous** `char[]`; readers `Slice(offset, length)`. Growth preserves this — a larger contiguous array, prefix copied.

We considered **`SkyOmega.Bcl.ChunkedList<T>`** (fixed-size data chunks + a doubling top-level pointer array, `long`-indexed — grows past `int.MaxValue` *without bulk-recopying the data*). It is the right tool for a **persistent, > int-max-element** collection (the atom store at 4 B+ atoms, which is why it exists). It is the **wrong** tool here: (a) bindings require *contiguous* slices and a value could straddle a chunk boundary, breaking sliceability; (b) the binding buffer is **transient** — per-query, no longevity, and a single query's bindings are bounded by `MaxAtomSize` × working-set, nowhere near `int.MaxValue` chars. *(Martin, 2026-06-21: "read buffers won't need longevity.")* It inspired the *grow-without-bulk-recopy* framing but does not fit; a contiguous grow-to-fit is correct.

### D3 — The literal-size policy is `MaxAtomSize` (now specified)

A binding may be as large as its atom. Since RDF/SPARQL are unbounded and there is no industry small-cap norm, Mercury's literal-size policy is its existing per-atom ceiling: **`MaxAtomSize` (default 1 MB, configurable)** — the single, already-loud knob. The binding buffer grows to honour it. If a binding would somehow exceed a sane absolute ceiling derived from `MaxAtomSize`, surface an `EngineAnomaly`-class signal — **never** a silent return. This was an unstated gap; this ADR records the decision.

### D4 — Fix every overflow site, and audit for siblings

Apply D1 to all `BindingTable` overflow sites (`:44/:70/:94/:115/:137` and any further `Bind*`), and audit the codebase for the same `if (... > buffer.Length) return;` silent-drop shape elsewhere (the anti-pattern, not just this instance).

### Alternatives rejected

- **Bigger fixed buffer (e.g. 16 KB):** moves the cliff; still silently drops above it. Rejected.
- **Bind long values by reference (source offset / atom id) instead of copying:** more architectural — changes the binding-value model. Deferred; the grow-to-fit contiguous buffer is the smaller correct fix that fully resolves the incident. Revisit if profiling shows copy cost dominates.

## Validation

**Proposed → Accepted** when the growth approach + the `MaxAtomSize` policy are reviewed and the `BindingTable` ownership change (D1) is agreed.

**Accepted → Completed** when:
- Every overflow site grows; the silent `return` is gone (a value that doesn't fit is bound after growth, or — at the absolute ceiling — raises an anomaly).
- A literal of 1023, 1 MB-minus, and `MaxAtomSize` chars round-trips through INSERT → SELECT (the regression oracle `tools/repro-literal.cs`, promoted to a unit test).
- The literal-size policy (`MaxAtomSize`) is documented (production-hardening notes + `StorageOptions`).
- Full W3C suite green; zero-GC preserved (growth uses the pooled manager; the allocation gate covers the grow path — growth is amortised, not per-bind).
- **DrHook validates it** (the finale): under the DrHook ADR-013 fix, re-run the literal hunt and observe `_stringBuffer.Length` *grow* and the `?o` binding *succeed*.

## Consequences

- Long literals (up to `MaxAtomSize`) become queryable; the silent-drop class is eliminated at this site and audited elsewhere.
- Mercury's literal-size policy is specified for the first time (was an unstated gap the incident exposed).
- Mercury's own `ck:`/memory writes of long content become robust.
- Small amortised cost when growth fires (rare; most bindings are tiny and never grow the buffer past its initial rent).

## References

- `src/Mercury/Sparql/Types/BindingTable.cs:44/70/94/115/137` — the silent-`return` overflow sites.
- `src/Mercury/Sparql/Execution/QueryExecutor.cs:57/115/258` — the 1 KB `_stringBuffer` rent; `TreeJoinExecutor.cs:775` — the 16 KB tree buffer.
- `src/Mercury/Storage/HashAtomStore.cs:72/308/334`, `StorageOptions.cs:36` — `MaxAtomSize` (the real, loud ceiling).
- `src/SkyOmega.Bcl/Collections/ChunkedList.cs` — the chunked collection considered and rejected for D2.
- `tools/repro-literal.cs` — isolation sweep / regression oracle (threshold = 1023 chars, content-independent).
- `ck:obs-mercury-update-long-literal-empty` (design-knowledge graph) — root-cause record.
- [W3C RDF 1.1 Concepts](https://www.w3.org/TR/rdf11-concepts/) (literal = unbounded Unicode lexical form); [SPARQL 1.1 Query](https://www.w3.org/TR/sparql11-query/) (no binding-size limit).
- DrHook ADR-013 — the value-type/ref-struct inspection gaps this hunt surfaced; the joint DrHook-validates-both finale.
