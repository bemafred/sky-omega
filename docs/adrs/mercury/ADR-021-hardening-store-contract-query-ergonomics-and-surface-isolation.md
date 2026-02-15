# ADR-021: Hardening the Store Contract, Query Ergonomics, and Surface Isolation

## Status

**Proposed** (2026-02-09)

## Context

The second-pass review identified several improvements that are not about `AtomStore` internals, but about **making Mercury safer to use, easier to use correctly, and more resilient at the operational surfaces** (CLI/HTTP/MCP/Solid), while preserving the existing low-level performance characteristics.

These improvements cluster around:

- Locking **contract clarity** and enforcement.
- Safer default APIs for query enumeration.
- Eliminating misleading internal comments/assumptions.
- Reducing subtle data races / unnecessary overhead in caches.
- Preventing deadlocks by codifying lock ordering.
- Isolating preview/volatile surface dependencies (MCP).
- Fixing minor doc/semantic mismatches.

Mercury’s architecture is explicitly **single-owner** and **single-writer** at the store level, but it supports multiple reader scenarios and multiple operational entrypoints. That amplifies the importance of contract and ergonomics.

## Decision

### 1) Provide a safe default for query enumeration

> **Note:** This section also covers the ReadSession ergonomics originally in ADR-020 §5, which was moved here as the natural home for query-ergonomics work.

`QuadStore.Query(...)` currently requires the caller to hold a read lock for the entire lifetime of enumeration. This is correct, but easy to misuse, and misuse can be catastrophic if a writer triggers a remap or structural change.

Add a safe default API shape so callers naturally do the right thing:

- Introduce `QuadStore.Read()` returning a `ReadSession : IDisposable` that acquires the store read lock.
- Allow `ReadSession.Query(...)` to return the same low-level enumerator.
- Optionally provide `QuadStore.QueryLocked(...)` that acquires a read lock internally and returns an enumerator that releases the lock in `Dispose()`.

The existing manual lock APIs remain available for advanced callers who want to group multiple operations under one lock, or coordinate locks across stores.

### 2) Make locking assumptions consistent across SPARQL layers

Some SPARQL internals (e.g., planner comments) currently imply that locks are held by the executor, while in practice locks are acquired at entrypoints (e.g., HTTP server). This is correct *only if all callers follow the same pattern*.

Choose one invariant and align code + comments accordingly:

- **Option A (preferred for safety):** `QueryExecutor.Execute(...)` (and update paths) acquires/releases a read lock (or holds a session) for the duration of execution/enumeration. This centralizes correctness and removes footguns for new callers.
- **Option B:** keep executor lockless, but:
  - update all remarks to say “caller must hold read lock for entire execution and enumeration”.
  - add DEBUG assertions where feasible to detect missing locks.

This ADR recommends **Option A** unless there is a demonstrated need to keep executor lockless for batching across multiple operations under one lock.

### 3) Codify lock ordering when locking multiple stores

Operations like pruning/transfer may require holding locks on two stores. Without a strict global ordering rule, future code can accidentally introduce deadlocks.

Define a canonical ordering rule, for example:

- Order locks by a stable store identity (e.g., canonical absolute path string, store GUID, or pool index).
- Acquire locks in ascending order only.

Document this in one place and enforce it in the few call sites that lock multiple stores.

### 4) Tighten QueryPlanCache semantics and reduce read-path overhead

`QueryPlanCache` uses copy-on-write snapshots for the dictionary, but mutates `plan.LastAccessed = DateTime.UtcNow` on the read path. This creates benign data races and adds overhead to every cache hit.

Change to one of:

- Use a cheaper, race-tolerant recency stamp: `Environment.TickCount64` or `Stopwatch.GetTimestamp`, and accept approximate ordering.
- Or remove read-path updates entirely and base eviction on insert/update only (often sufficient for query plan caching).

This preserves the simplicity of snapshot dictionaries while reducing overhead and eliminating misleading “immutability” semantics.

### 5) Isolate MCP preview dependency behind a thin adapter boundary

`Mercury.Mcp` currently references a preview package (`ModelContextProtocol ... preview`). This is acceptable at the surface, but the dependency must not leak types or behaviors into core libraries.

Establish an explicit boundary:

- Keep all MCP-specific attributes, types, and tool wiring contained in `Mercury.Mcp`.
- Expose a stable internal “tool facade” contract from Mercury core (or a small abstractions project) that MCP binds to.
- This allows MCP package upgrades/breaking changes without rippling into the rest of the repository.

### 6) Correct minor doc/semantic mismatches

Fix comments that imply behavior different from actual code, to avoid future confusion:

- `CrossProcessStoreGate`: clarify when the gate is released (e.g., on pool dispose vs store return), matching actual behavior.
- Any similar mismatch discovered in comments around `QuadStorePool`, pruning, or execution layers should be corrected as part of this ADR's implementation.

> **Applied (2026-02-15):** The two comment fixes identified in this section have been implemented:
> - `QueryPlanner`: corrected to say the *caller* of QueryExecutor must hold locks.
> - `CrossProcessStoreGate`: corrected to describe slot-held-for-pool-lifetime behavior.

### 7) Optional: expose durability/performance modes explicitly (WAL)

`WriteAheadLog` is conservative and fsyncs per record in non-batch mode. This aligns with Mercury’s reliability stance.

If operational needs arise, introduce an explicit option such as:

- fsync per batch (already supported via BeginBatch/CommitBatch),
- or fsync every N records / time window (opt-in, clearly documented as reduced durability).

This is optional and should not undermine the default “dependability first” posture.

## Implementation

### A) Query ergonomics

- Add `ReadSession` with:
  - Acquire read lock on creation
  - Release in `Dispose()`
  - Provide `Query(...)` wrapper
- Optionally add `QueryLocked(...)` convenience for one-off foreach usage.

Update `QuadStore` docs to clearly distinguish:
- advanced/manual lock pattern
- safe default pattern

### B) SPARQL lock invariant alignment

- If adopting Option A:
  - Wrap execution in `ReadSession` inside executor paths that enumerate data.
- If adopting Option B:
  - Update planner remarks
  - Add DEBUG assertions

### C) Lock ordering

- Define the ordering rule (path/GUID/etc.)
- Apply to multi-store lock sites (pruning transfer and any future cross-store operations)

### D) Plan cache recency

- Replace `DateTime.UtcNow` with tick-based stamp, or remove read-path mutation.

### E) MCP boundary

- Introduce a small internal abstraction for “tool operations” if needed.
- Keep MCP package types entirely in `Mercury.Mcp`.

### F) Documentation fixes

- Correct `CrossProcessStoreGate` comments and any other discovered mismatches.

## Consequences

### Benefits

- Dramatically reduces accidental misuse of lock-dependent enumeration.
- Makes store/query safety robust across all entrypoints (CLI/HTTP/MCP/Solid and future integrations).
- Prevents deadlock classes in multi-store operations before they appear.
- Keeps the volatile MCP surface isolated and replaceable.
- Simplifies mental model: “safe by default, low-level when you choose it”.

### Drawbacks

- Adds small API surface (`ReadSession` / `QueryLocked`).
- Requires choosing and enforcing a single lock invariant across execution layers.
- Minor refactoring across SPARQL/entrypoints may be needed to avoid double-locking.

## Alternatives Considered

### 1) Keep manual locking only (status quo)

Rejected because it is too easy to misuse and has failure modes that are hard to diagnose.

### 2) Make enumerators always self-locking

Rejected because it removes the ability to group multiple operations under one lock and may increase lock churn.

The chosen approach provides both: safe default + advanced manual control.

### 3) Ignore lock ordering until a deadlock happens

Rejected because deadlocks are expensive to reproduce and fix once operational surfaces grow.

## References

- `src/Mercury/Storage/QuadStore.cs`
- `src/Mercury/Sparql/Execution/QueryExecutor.cs`
- `src/Mercury/Sparql/Execution/QueryPlanner.cs`
- `src/Mercury/Sparql/Execution/QueryPlanCache.cs`
- `src/Mercury.Runtime/CrossProcessStoreGate.cs`
- `src/Mercury.Mcp/*`
- `src/Mercury/Storage/WriteAheadLog.cs`
- `src/Mercury.Pruning/PruningTransfer.cs`
