# Limit: readahead-pool concurrency-contract violation

**Status:**        Resolved (introduced 2026-05-09 in 1.7.52 / Phase 2 B2; resolved 2026-05-10 in 1.7.54)
**Surfaced:**      2026-05-10, via external review of [`docs/reviews/sky-omega-latest-version-review-2026-05-10.md`](../reviews/sky-omega-latest-version-review-2026-05-10.md) §5
**Last reviewed:** 2026-05-10
**Promotes to:**   N/A — resolved by removing the violator and adding a thread-affinity contract guard

## Description

`BoundedFileStreamPool` documents a single-threaded contract:

> Pool is NOT thread-safe; callers must serialize Get calls.

The B2 implementation in `ChunkReadAheadDispatcher` (cycle 10 Phase 2, commit `0d2fca1`) violated this contract: N background workers (default `min(8, ProcessorCount/2)`) each called `_pool.Get(req.Path)` from `RunWorker` with no synchronization. `LinkedList<T>` and `Dictionary<K,V>` mutation under concurrent access can:

- Corrupt the LRU's `First`/`Last` pointers (data race on the linked list)
- Corrupt Dictionary internal bucket arrays (data race on hash buckets)
- Return a stream concurrently being torn down by an evicting worker

The hazard is silent at low pressure (no eviction → fewer concurrent mutations) but can corrupt state at scale. At 21.3 B Wikidata atoms (~3,923 chunks) the pool's hard cap of 8,192 prevents eviction, so the worst race surface is bounded — but concurrent dictionary/linked-list mutation still occurs on every refill.

## Trigger condition

External review caught the contract violation pre-merge during the cycle 10 Phase 3 production run; the run was killed during parser phase before merge could exercise the racy path.

## Current state

**Resolved structurally.** Two complementary changes:

1. **Removed the violator.** `ChunkReadAheadDispatcher.RunWorker` now opens a worker-local short-lived `FileStream` per refill (`using var fs = new FileStream(...)`). Worker-local streams have no shared mutable state. `FillBack` already seeks to its tracked file position, so opening fresh is always correct. Per-refill open() ≈ 12 µs is negligible against the 4 MB read.
2. **Added a thread-affinity contract guard** to `BoundedFileStreamPool`. First `Get`/`Drop` claims `_ownerThreadId` via `Interlocked.CompareExchange`; subsequent calls from any other managed-thread-id throw `InvalidOperationException` with a message naming the violation. Tested by `BoundedFileStreamPoolTests.Get_CrossThreadAccess_ThrowsContractViolation`.

FD pressure is unchanged: peak ≈ `poolSize + workerCount` ≈ 64 + 8 = 72, well below macOS launchd's ~10,240.

## Candidate mitigations (resolved set)

The chosen path:
- Worker-local short-lived `FileStream`s in the readahead path (reviewer §6 option 2)
- Thread-affinity contract guard in `BoundedFileStreamPool` (defense-in-depth)

Alternatives ruled out:
- ❌ Add a lock around `BoundedFileStreamPool.Get/Drop` — preserves shared mutable state and complicates a path already documented as single-threaded; merge thread takes uncontended lock cost forever for one wrong caller's benefit
- ❌ Add per-worker pool instances — leaks pool count to the dispatcher and wastes FD budget per worker
- ❌ Sidecar-based seek recovery (reviewer §6 option 3) — addresses a different concern (eviction recovery), not contract violation
- ❌ Disable readahead via `MERCURY_MERGE_READAHEAD=0` (reviewer §5 short-term workaround) — viable for production but punts the structural fix; rejected because the substrate-discipline call is "no shortcuts"

## References

- Review: [`docs/reviews/sky-omega-latest-version-review-2026-05-10.md`](../reviews/sky-omega-latest-version-review-2026-05-10.md) §5–§6
- Pre-fix: `ChunkReadAheadDispatcher` constructor accepted `BoundedFileStreamPool pool`; `RunWorker` called `_pool.Get(req.Path)` from N tasks
- Post-fix: 1.7.54 — dispatcher constructor accepts `streamBufferSize`; workers open `using var fs = new FileStream(...)` per refill
- Cross-reference: the `feedback_resource_limit_class_audit` discipline applies to *contracts* as well as resource counts — every named contract should have a runtime guard at the abstraction's boundary, not just a docstring
