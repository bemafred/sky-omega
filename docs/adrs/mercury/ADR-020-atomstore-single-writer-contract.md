# ADR-020: AtomStore Single-Writer Contract and Safe Publication

## Status

**Accepted** — clarifies the intended single-owner/single-writer model and removes ambiguous concurrency semantics (proposed 2026-02-09, implemented 2026-02-15)

## Context

`AtomStore` is an internal storage component used by `QuadStore` to intern UTF-8 terms as *atoms* (stable integer IDs) backed by memory-mapped files.

Key properties:

- `AtomStore` is **not** intended to be a reusable, independently thread-safe component.
- `AtomStore` is used only through a `QuadStore` instance.
- `AtomStore` can **remap** files during growth. Any previously obtained spans/pointers become invalid after a remap.
- Correctness therefore depends on a clear locking contract:
  - Reads must be protected from concurrent remaps.
  - Writes must be serialized.

During review, `AtomStore` contained an insertion step that used an interlocked CAS on the hash slot (claiming by writing `AtomId` first). This unintentionally implied *lock-free multi-writer support*, and it introduced a publish-order hazard:

- `AtomId` could become visible in a slot **before** `Hash`, `Length`, and `Offset` were written.
- Readers probing the hash table may observe `AtomId != 0` with default fields and incorrectly treat the slot as not matching, creating duplicates or lookup failures in rare cases.

Additionally, growth code swapped the active memory-mapped pointer **before** extending the underlying file length. While writes are serialized, reordering these operations improves cross-platform safety and maintains the invariant that the mapped region never exceeds the file size.

Mercury (and Sky Omega) is explicitly designed around a **single owner** with a **single authoritative mutation stream**. More complicated scenarios (true parallel writes, multi-owner) are considered a different architectural tier and would require sharding and additional coordination.

## Decision

### 1) AtomStore is single-writer under QuadStore write lock

- All `AtomStore` mutations MUST occur while `QuadStore` holds its **write lock**.
- `AtomStore` is considered **not thread-safe** outside that lock.
- Any future move to multi-writer would require a dedicated redesign and a new ADR.

### 2) Hash slot validity is signaled only by AtomId

A hash slot is interpreted as:

- `AtomId == 0` → empty
- `AtomId != 0` → **fully published**

Therefore:

- `AtomId` MUST be written **last** when publishing a new slot.
- Readers MAY treat `AtomId != 0` as sufficient to read other fields without additional synchronization because the write lock provides serialization and read locks prevent remaps.

### 3) Remove interlocked slot-claiming from insertion

- Interlocked CAS-based claiming of hash slots is removed from `AtomStore` insertion.
- Under the single-writer contract, normal writes under the write lock are correct and simpler.
- Interlocked operations remain acceptable for independent counters where ordering does not affect publication semantics.

### 4) Growth/remap ordering preserves file/mapping invariants

When growing mapped files:

1. Extend the underlying file length.
2. Create/refresh the mapping/accessor.
3. Swap active pointers/handles.
4. Dispose old mapping/accessor.

This ensures the mapped region never transiently exceeds the file length.

### 5) Lock contract is enforced in DEBUG

Add internal DEBUG-only assertions to verify:

- Mutating `AtomStore` methods are called with the `QuadStore` write lock held.
- Query execution paths that enumerate store data are entered with a read lock held.

## Implementation

### AtomStore insertion publication order

- Modify `AtomStore.InsertAtomUtf8(...)` to:
  1. Select an empty slot (under write lock).
  2. Write `Hash`, `Length`, `Offset`.
  3. Publish by writing `AtomId` last.

### AtomStore growth/remap ordering

- Reorder `EnsureDataCapacity(...)` and `EnsureOffsetCapacity(...)` to extend file length before swapping the active mapping.

### Documentation

- Update the relevant comments in:
  - `AtomStore.cs` (explicitly internal + single-writer)
  - `QuadStore.cs` (read lock lifetime)
  - Query execution/planner notes (caller-held lock vs executor-held lock)

## Consequences

### Benefits

- Removes misleading “maybe lock-free” semantics and aligns implementation with Mercury’s single-owner model.
- Eliminates publication-order hazards in the hash index.
- Clearer invariants reduce cognitive load and simplify future maintenance.
### Drawbacks

- AtomStore cannot be used as a multi-writer component without redesign.

### Future scaling path

If higher write throughput becomes necessary:

- Prefer **single-writer with multiple producers** (queue/actor model) as the first step.
- For true parallelism, prefer **sharding** (multiple stores / partitions), each remaining single-writer internally.
- Multi-writer AtomStore is considered a last-resort design tier due to complexity and global coordination costs.

## Alternatives Considered

### A) Make AtomStore multi-writer via two-phase publish

- Claim a slot with a sentinel value (e.g., `AtomId = -1`), write fields, then publish final `AtomId`.
- Readers must handle in-progress slots.

Rejected for now:

- Adds complexity, spin/retry behavior, and more subtle failure modes.
- Conflicts with the current single-owner architectural intent.

### B) Keep CAS-claiming but reorder field publication

- Keep interlocked claiming but publish `AtomId` last.

Rejected:

- Still implies unsupported concurrency semantics.
- Adds cost and complexity with no benefit under single-writer.

## References

- `src/Mercury/Storage/AtomStore.cs`
- `src/Mercury/Storage/QuadStore.cs`
- `src/Mercury/Storage/QuadStorePool.cs`
- `docs/adrs/mercury/ADR-005-quadstore-pooling-and-clear.md`
- `docs/adrs/mercury/ADR-008-quadstore-pool-unified.md`
- `docs/adrs/mercury/ADR-006-dual-mode-store-access.md`
