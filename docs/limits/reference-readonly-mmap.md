# Limit: Reference index mmap is opened ReadWrite even when the store is sealed

**Status:**        Latent
**Surfaced:**      2026-04-26, during post-Phase-6 review of the Reference profile's lifecycle contract — opening mode does not match the "build once, query forever" semantics
**Last reviewed:** 2026-04-26
**Promotes to:**   ADR when (a) a target query workload makes Reference query-side latency or memory footprint binding, OR (b) cross-process shared query (multiple `mercury` processes against one sealed store) becomes a use case, OR (c) the SortedAtomStore work (`sorted-atom-store-for-reference.md`) ships and the seal/reopen pattern is established for the atom store anyway

## Description

`ReferenceQuadIndex` opens its B+Tree mmap as `MemoryMappedFileAccess.ReadWrite` on every code path (`src/Mercury/Storage/ReferenceQuadIndex.cs:96,100`), with `FileAccess.ReadWrite` on the underlying `FileStream` and `FileShare.None`. This is correct during the bulk-load phase. It is **not** correct for the query phase, which under [ADR-026](../adrs/mercury/ADR-026-bulk-load-path.md) and [ADR-029](../adrs/mercury/ADR-029-store-profiles.md) is structurally read-only — Reference's update model is "delete the store and re-bulk-load."

The cost of opening RW when the file will never be written:

- The kernel maintains dirty-page tracking metadata for the entire mapped region.
- Copy-on-write bookkeeping is set up on macOS even when no writes occur.
- `FileShare.None` blocks any other process from opening the file at all.
- `FileOptions.WriteThrough` (set in non-bulk mode) is a no-op for query workloads but signals write intent to the kernel scheduler.
- Explicit `_accessor.Flush()` paths exist that should not be reachable.
- Architecturally, the absence of a read-only mode means there is no compile-time guarantee that the query path can't accidentally mutate.

## What relaxes when the store is sealed

**File / mmap layer**

- `FileAccess.Read` + `FileShare.Read` instead of `ReadWrite` / `None` — multiple processes can map the same sealed file, kernel reference-counts the resident pages once across all readers.
- `MemoryMappedFileAccess.Read` on both `MemoryMappedFile.CreateFromFile` and `CreateViewAccessor` — kernel skips dirty-page tracking, no write-back pressure, no COW machinery.
- `FileOptions.WriteThrough` removed entirely; `FileOptions.RandomAccess` retained.

**Lifecycle**

- `Dispose` becomes unmap + close. No `SaveMetadata`, no `Flush`, no msync. Metadata was sealed at the build → query transition.
- `Add`, `Clear`, `FlushPage`, and the page-write paths in `PageCache` become unreachable. Make them fail-fast (`throw new InvalidOperationException("store is sealed")`) rather than silent no-ops, so a query-path bug surfaces immediately rather than corrupting state.

**Page handling**

- The in-process `PageCache` (10 000-entry LRU at `ReferenceQuadIndex.cs:114`) is partially redundant against an OS-page-cache-backed read-only mmap. For pointer-stable hot pages and predictable B+Tree walks, the L1-resident cache likely still pays for itself, but the write-back code path disappears entirely. Worth measuring whether direct pointer arithmetic into the mapped region is faster for sequential leaf walks than going through the `PageCache` lookup.
- `madvise` becomes safely tunable: `MADV_RANDOM` for query workloads (suppresses kernel readahead, which costs more than it saves on B+Tree probes), `MADV_WILLNEED` on warm-up if the workload re-queries hot subgraphs. Both are presently unsafe to set aggressively because the same code path also handles writers.

**Concurrency**

- No reader/writer coordination needed at the page level. Whatever locking exists today for the mutable case can be elided in the sealed mode.
- Cross-process query becomes possible: a `mercury` query CLI and a `mercury-mcp` server can both map the same sealed store concurrently with no coordination beyond OS page-cache sharing.

## Lifecycle: build → seal → query

The natural shape:

1. **Build phase**: open as today (`MemoryMappedFileAccess.ReadWrite`, `FileShare.None`, `_deferMsync = true` if bulk). Bulk-load runs, indexes are built.
2. **Seal**: final `SaveMetadata`, single `msync` of the whole region, close the RW mapping.
3. **Query phase**: reopen the file with `FileAccess.Read` + `FileShare.Read` and `MemoryMappedFileAccess.Read`. All the relaxations above apply for the entire query lifetime of the store.

The seal step is a one-line metadata flag in the file header (e.g., `IsSealed: true`) plus the close/reopen. Re-bulk-loading would read the flag, refuse to reopen RW, and require an explicit unseal-or-recreate step — which is exactly the "delete and re-bulk-load" contract ADR-026 already specifies.

## Why this does not extend to Cognitive / Graph / Minimal

The mutable profiles accept incremental writes throughout the session lifetime. There is no "seal" point — by design. Opening Cognitive read-only-until-first-write would only buy back the relaxations until the first mutation, after which they are gone for the remainder of the session. The asymmetry of cost (one-time syscall to escalate) versus benefit (relaxations only on the few pages still untouched) makes it not worth the complexity. The clean separation is **profile-conditional**: Reference seals, Cognitive does not.

A per-page approach using `mprotect()` plus a SIGSEGV handler to lazily promote pages from PROT_READ to PROT_READ|PROT_WRITE on first touch is technically possible on Linux/macOS, and is essentially userspace COW. It is **not recommended** because:

- The .NET runtime intercepts SIGSEGV for its own use (NullReferenceException dispatch, GC stack walks). A user-installed handler conflicts with the CLR, particularly under the BCL-only / single-runtime constraints Sky Omega runs under.
- `mprotect` per first-write page is ~1 µs of syscall overhead. For a B+Tree under steady-state writes, the hot zone is a small set of leaf pages near the active region — the per-page promotion cost amortizes, but tracking which pages have been promoted (so we can msync them on dispose) is non-trivial in a zero-GC, BCL-only context.
- The OS already tracks dirty pages without our help. `msync(MS_ASYNC)` only writes back dirty pages — clean pages cost nothing. `_deferMsync` is therefore *already* effectively per-touched-page; the flag controls *when* we issue msync, not *which* pages it covers.

The answer to "should Cognitive open read-only and escalate?" is **no**, for the same architectural reason as "should Reference always be RW?" — a profile's mmap mode should match its lifecycle contract, not try to be both.

## Composability with other limits

- **[sorted-atom-store-for-reference](sorted-atom-store-for-reference.md)**: the sealed atom store has the same lifecycle. A SortedAtomStore + sealed BBHash file naturally opens read-only at query time. The two limits should likely promote together — both establish the build → seal → query pattern for the Reference profile.
- **[bit-packed-atom-ids](bit-packed-atom-ids.md)**: independent. Bit-packing is a key-layout change, orthogonal to mmap mode.
- **[btree-mmap-remap](btree-mmap-remap.md)**: relevant during bulk-load, not at query time. A sealed file has fixed size — remap is for the growing-file case.

## Trigger condition

Promote to ADR when any of:

- A target workload measures Reference query-side latency or per-process memory footprint as binding, and the relaxed read-only mode is on the critical path.
- Cross-process query becomes a real use case — e.g., a `mercury-mcp` server and a `mercury` CLI both wanting to query the same 21.3B Wikidata store concurrently. With the current `FileShare.None`, only one can have it open.
- The SortedAtomStore work is approved and ships; bundling the index seal into the same lifecycle change is cheaper than doing it separately.
- Static-analysis or invariant-checking work surfaces the absence of a read-only mode as a gap (Reference query paths can mutate the index in violation of ADR-026 with no compile-time check).

## Current state

Latent. Phase 6 21.3B Wikidata is queryable through the current `ReferenceQuadIndex.ReadWrite` mode without measured contention. The optimization is real but unmeasured — the gap is "estimated, not validated." Promoting to ADR requires a workload that exposes the cost, or the SortedAtomStore work shipping and pulling this with it.

## References

- [ADR-026](../adrs/mercury/ADR-026-bulk-load-path.md) — bulk-load contract; "delete and re-bulk-load" update model.
- [ADR-029](../adrs/mercury/ADR-029-store-profiles.md) — profile dispatch; establishes Reference vs Cognitive lifecycle separation.
- [sorted-atom-store-for-reference.md](sorted-atom-store-for-reference.md) — sibling limit, same lifecycle contract on the atom store.
- `src/Mercury/Storage/ReferenceQuadIndex.cs:60-117` — current open path, all RW.
