# ADR-038: Merge-phase read-side optimization (intermediate prefix compression + user-controlled frontier cache)

## Status

**Status:** Accepted — 2026-05-07

## Context

Cycle 8 (1.7.48, 21.3 B Reference + Sorted bulk-load) measured a three-regime merge throughput structure:

- **Warmup** ~0.3 M records/sec
- **Steady-state** ~1.0–1.5 M records/sec
- **Long-tail / cold-cache** ~0.2–0.5 M records/sec

Cycle 9 (1.7.50, in flight) shows the same three-regime structure, including an observed 12× swing across regime transitions (0.5 → 6 M/s favorable, 6 → 0.16 M/s unfavorable). The structure is *not* a bug — it is the cost shape of k-way merge when intermediate volume exceeds RAM.

**The arithmetic of the problem:**

| | Size |
|---|---:|
| Intermediate occurrence chunks at 21.3 B | ~4 TB |
| Available RAM for kernel page cache | ~120 GB |
| Cache-fit ratio | **~3 %** |

At any moment, ~3 % of the intermediate is in cache. The priority queue rotates across 3,923 chunks based on alphabetical order of frontier records. Within a chunk, reads are sequential — kernel readahead works. *Across* chunks, the access pattern is effectively random — the next pop could come from any of 3,923 chunks. When the priority queue rotates back to a chunk it hasn't touched in N minutes, that chunk's current-offset page may have been evicted. Hard disk read; the long-tail regime.

ADR-037 (pipelined spill) addressed the parser side but does not touch merge. The 1.7.49 cleanup hook releases disk pressure at end-of-merge but does not affect throughput during merge.

The read-side bottleneck is the structural problem this ADR addresses.

## Hypothesis (falsifiable, two-part)

**H1 — Compressed intermediates raise effective cache-fit ratio.** Applying ADR-034 Round 2-style prefix compression to intermediate chunk records reduces intermediate volume by ~70 % (from ~4 TB to ~1 TB at 21.3 B). Cache-fit improves from 3 % to ~12 %. The long-tail regime's measured rate (0.2–0.5 M/s in cycle 8) rises proportionally — projected 0.6–1.5 M/s.

**Falsified if:** measured intermediate-volume reduction < 50 %, OR long-tail rate fails to rise meaningfully (less than 1.5×).

**H2 — User-controlled frontier cache eliminates eviction-driven stalls.** A bounded user-space readahead buffer per chunk (e.g. 4 MB) that refills via an async producer task converts the merge's read pattern from "interleaved random across 3,923 streams" (kernel sees apparent random) to "3,923 truly-sequential streams" (kernel readahead is optimal). Eviction of the frontier becomes structurally impossible because the buffer lives in our process's anonymous memory, not the kernel's LRU page cache.

**Falsified if:** the long-tail regime persists at the same magnitude with the cache enabled, OR the user-space buffer adds enough memory pressure that other phases (e.g. resolver) regress.

The two parts compose: H1 makes the working set smaller (more fits in *anywhere*); H2 ensures what we read stays in *our* control until consumed.

## Decision

### Part 1 — Prefix-compress intermediate chunk records

Today's spill chunk format (`SortedAtomStoreExternalBuilder.SpillOneChunk`):

```
[int32 length][int64 InputIdx][raw bytes]   per record
```

12-byte fixed header + variable bytes. No compression. Records within a chunk are sorted on `(bytes, InputIdx)` by the parser-side sort.

Proposed format:

```
[byte prefix_len][1-9 byte varint InputIdx][suffix bytes]   per record
```

- `prefix_len` is the byte count shared with the previous record. Anchors (every Nth record, e.g. N=64) have `prefix_len = 0` and full bytes.
- `InputIdx` encoded as varint — most values fit in 4 bytes, large ones up to 9 bytes. Replaces the fixed 8-byte field.
- `suffix bytes` is the post-prefix portion of the record's bytes (length = original_length − prefix_len).

Reconstruction at read time: ChunkReader maintains a `prevBytes` buffer. On each `MoveNext`:
1. Read `prefix_len`. If 0 → full record (anchor).
2. Read varint InputIdx.
3. Compute suffix length from anchor-relative position OR from another length field. *Sub-decision: include 1–2 byte suffix-length field, or anchor every K bytes for self-synchronizing parse.*
4. Reconstruct full bytes by concatenating `prevBytes[0..prefix_len]` with suffix bytes.
5. Update `prevBytes = full_bytes`.

Anchor interval (N) trades reconstruction-cost-bound vs anchor-overhead. ADR-034 Round 2 chose N=64 for the output side; reusing 64 here.

**Projected reduction on Wikidata-shape input:**
- Avg URI length ~60 bytes, avg prefix share ~50 bytes between consecutive records (entity/property URIs share long base paths).
- Per-record cost: 1 byte prefix_len + 4 bytes varint InputIdx + 10 bytes suffix = **15 bytes**.
- Vs current: 4 bytes length + 8 bytes InputIdx + 60 bytes raw = **72 bytes**.
- **~79 % volume reduction** at the per-record level. Aggregate ~70 % accounting for anchors.

CPU cost: one prefix-compute per record on spill (parser side), one prefix-reconstruct per record on merge (read side). Both are byte-level operations on adjacent records — no random access, cache-friendly.

### Part 2 — Per-chunk user-space frontier cache

Add a `ChunkReadAheadBuffer` to each `ChunkReader`:

- Owned 4 MB byte buffer + offset cursor.
- Async task fills the buffer from the FileStream when occupancy < 1 MB threshold.
- `MoveNext` reads records from the buffer (zero syscalls in the hot path); blocks only if buffer is empty AND refill task hasn't caught up (rare under normal merge throughput).

Memory budget: 3,923 chunks × 4 MB = **~15 GB user-space buffer.** On a 128 GB host, comfortable. Trades 15 GB of kernel page cache for 15 GB of user-space anonymous memory under our control — same total memory, different allocation.

Refill task scheduling: bounded thread pool of N readahead workers (default `min(8, ProcessorCount/2)`). Each task services a queue of "needs-refill" chunks. Priority: chunks whose frontier is closest to current emit point (computed from priority queue's top entry).

The architectural insight is that this transforms the access pattern *from the kernel's perspective*. Today: 3,923 file streams accessed in priority-queue-driven order — kernel sees apparently random switches. After: each chunk has its own constantly-draining sequential stream — kernel readahead is optimal for each. We move from "thrash kernel page cache" to "many parallel sequential reads."

### Part 3 — Composition

Compression first (it's a chunk file format change; small implementation, big leverage on cache-fit). Frontier cache second (it's a `ChunkReader` behavioral change; depends on chunk format being stable). Both ship in sequence, gradient-validated independently before compose-validation.

### Interaction with existing ADR-034 Round 2 output compression

The two compressions operate on independent sort orders and do not interfere. ADR-034 Round 2 prefix-compresses the output `atoms.atoms` based on *global* sort order across all chunks (post-dedup). ADR-038 Part 1 prefix-compresses each intermediate chunk based on *chunk-local* sort order. Both work because Wikidata URIs share long base-path prefixes either way, but the prefix-share contexts are independent.

The merge loop receives full reconstructed bytes from `ChunkReader.MoveNext` — same interface as today. The output compression's per-record `prefix_len` calculation is unchanged, and its measured ratio (53 % on `atoms.atoms` at cycle 8) should be preserved.

### Chunk format and pool eviction (the new concern)

Today's chunk format is *self-synchronizing per record*: `[int32 length][int64 InputIdx][raw bytes]`. On `BoundedFileStreamPool` eviction-and-re-acquire, `ChunkReader` simply seeks to the saved `_offset` and the next `Read` decodes correctly without prior context.

The compressed format introduces stateful decoding: `prevBytes` (the previous record's reconstructed bytes) is needed to compute the next record's full bytes from a non-anchor `[prefix_len][suffix]`. State must survive across `MoveNext` calls, and crucially, across pool eviction-and-re-acquire.

**Mitigation: per-chunk anchor offset table (sidecar).**

When a chunk is sealed (parser-side spill complete), write a small sidecar `chunk-NNNNNN.idx` containing `[anchor_record_index, file_offset]` pairs — one entry per anchor (every 64 records).

- Storage: at 21.3 B with 3,923 chunks averaging ~17 M records each, anchors-per-chunk ≈ 266 K, sidecar size ≈ 4 MB per chunk → ~16 GB total. Cleaned up by the same end-of-merge cleanup hook as the chunk files.
- Recovery: on re-acquire after eviction, binary-search the sidecar for the largest anchor offset ≤ saved `_offset`. Seek to anchor, reset `prevBytes`, replay forward at most 63 records to reach `_offset`. O(log) lookup, O(1) bounded reconstruction.
- Discipline: sidecar is written atomically with the chunk file — partial sidecar means partial chunk; the cleanup hook treats them as a unit.

**Alternatives considered and rejected:**

- *Magic-byte synchronization markers* — every anchor begins with a fixed 4-byte sentinel, scan-backward to recover. Cheaper to write but requires linear scan on recovery, and false positives if record bytes happen to contain the magic. Sidecar is strictly better for the cost.
- *Reset `prevBytes` on every record* — kills compression ratio. Not viable.
- *No sidecar, scan from chunk start on re-acquire* — re-acquire becomes O(N) in chunk size; pathological under any pool-eviction pressure.

**Scope discipline — atom-merge vs trigram drain:**

For the atom-merge path specifically, cycle 8 + cycle 9 measurements show **zero evictions in practice** (3,923 chunks << 8K pool cap). The eviction recovery path is structurally exercised only at scales beyond 21.3 B Wikidata. We *could* ship ADR-038 Part 1 for atom-merge without the sidecar and accept that re-acquire-after-eviction would re-scan from chunk start (rare path; performance bug, not correctness bug).

For the trigram drain (a follow-on ADR if ADR-038 extends there), cycle 8 measured 10,456 chunks > 8K cap → 23 % miss rate, ~3.4 h overhead. There the sidecar is mandatory; without it, the eviction recovery cost would dominate.

**Decision:** ship the sidecar for atom-merge from the start, even though it's not load-bearing at current scale. Reasons: (a) avoids a future-scale performance cliff that's invisible until 50 B+ runs hit it; (b) makes the chunk format identical for any future application of the same compression to trigram or other ExternalSorter-backed paths — a single format spec, not two; (c) ~16 GB sidecar cost on a host that already commits 4 TB to chunks is negligible. The "ship without sidecar" alternative trades clarity for marginal storage savings; not worth it.

## Validation protocol

### Unit tests

- Round-trip: compressed chunk written by parser, read by `ChunkReader`, byte-identical to original after reconstruction. At every record, including anchor and non-anchor.
- Frontier cache concurrency: stress test with high contention between parser-fill and reader-drain. Buffer underflow surfaces correctly (block, refill, continue).
- Memory bound: 4 MB ring buffer never exceeds its allocated size under any input pattern.

### Gradient

Same protocol as ADR-037: 1 M / 10 M / 100 M Wikidata Reference + Sorted, A/B against 1.7.50 (current `main`).

**Phase 1: compression alone** (no frontier cache). Measure:
- Intermediate volume (sum of chunk file sizes) — direct test of H1's compression-ratio claim.
- Merge phase wall-clock vs 1.7.50 baseline. Long-tail regime should shorten or shift; per-window rate distribution should narrow.
- CPU: prefix-compute on parser side, prefix-reconstruct on merge side. Should not regress parser throughput (the spill is async; small extra CPU).

**Phase 2: frontier cache added** (compressed intermediates from Phase 1). Measure:
- Merge phase wall-clock vs Phase 1.
- Long-tail regime rate floor — should rise from cycle-8 0.2–0.5 M/s into steady-state 1.0+ M/s if H2 is right.
- Kernel page-cache thrash indicators (vm_stat pageins, page_outs during merge).
- Memory: 15 GB anonymous user-space allocation should appear; kernel cache should shrink correspondingly.

**Phase 3: 21.3 B production validation** (cycle 10). Same hypothesis, same falsifiability conditions, but at the scale where the long-tail regime is most pronounced.

### Falsification triggers

If Phase 1 shows < 50 % intermediate-volume reduction → revisit compression scheme (varint may be too aggressive; suffix-length field may be needed). H1 false.

If Phase 2 shows merge wall-clock UNCHANGED from Phase 1 → frontier cache isn't the right fix; the bottleneck is elsewhere (CPU? offsets file? resolver?). Need to use `dotnet-trace` against the live merge to find actual top-N hotspots. H2 false; pivot.

If memory pressure causes resolver or output-side regression → buffer size needs tuning down. The 4 MB-per-chunk default was projected; gradient validates it.

## Consequences

### Positive

- Long-tail regime mitigation: projected merge wall-clock ~30–50 % reduction at 21.3 B (combining both parts).
- Compression also reduces disk pressure during merge (4 TB → ~1 TB intermediate). Compounds with the 1.7.49 cleanup hook.
- Frontier cache architecture composes well with future merge-axis work (cascading merge would inherit the per-chunk-buffer abstraction).
- Architectural pattern: "user-controlled cache where kernel LRU eviction is wrong for our access pattern" is a design idiom that may apply to other mid-substrate paths (rebuild trigram drain, GSPO sort-insert).

### Negative / risks

- Chunk file format change. Cycle 8 + cycle 9 chunks (currently on disk) cannot be read by the new format. Acceptable: chunks are intermediate, deleted at end of run by the 1.7.49 cleanup hook. No persistent data migration needed.
- Frontier cache adds concurrency: a readahead worker pool per merge. Same architectural shape as ADR-037 (worker thread + bounded queue), and the same disciplines apply — exception propagation, dispose contracts, listener thread-safety. Less risky than ADR-037 (read-only access vs ownership-transfer), but still concurrency.
- Chunk format becomes self-relative: a corrupt chunk midway breaks reconstruction from that anchor onward. Since chunks are ephemeral and rewritten on every run, the failure mode is "abort + restart," not "data loss." Documented but not load-bearing.
- 15 GB user-space allocation is a real cost on smaller hosts. The 4 MB buffer should be tunable via env var (similar to `MERCURY_MERGE_POOL_SIZE`) so smaller hosts can scale down.

### What this does NOT do

- Does not address the cache-fit boundary structurally. Even with 70 % compression, intermediate volume is still > RAM (1 TB > 120 GB). The merge will still cross some boundary; just at a larger fraction of work done.
- Cascading merge (a deeper architectural change) remains the answer if even the compressed-intermediate cache-fit ratio (12 %) proves insufficient. ADR-038 is the cheaper / simpler step before the cascade.
- Does not measure the actual bottleneck breakdown today. We *infer* it's read-side cache pressure from access pattern reasoning + cycle 8/9 regime structure. A `dotnet-trace` against a live cycle 9 merge in the long-tail regime would *prove* it. Worth doing during cycle 9's slow window if feasible without perturbing the run.

### Limits register impact

- **Resolves** [`docs/limits/external-merge-intermediate-disk-pressure.md`](../../limits/external-merge-intermediate-disk-pressure.md) — Round 2 mitigation candidate explicitly listed there.
- **Touches** the merge-phase regime structure surfaced in `project_three_regime_merge` (memory) and `urn:sky-omega:obs:merge-cache-fit-boundary-2026-05-05` (Mercury). The cache-fit boundary doesn't go away, but the working-set side of the inequality shrinks.
- **Does not yet retire** the cycle 8-projected ~3.6 TB peak — that's a different limit covered by the 1.7.49 cleanup hook.

## References

- [`docs/limits/external-merge-intermediate-disk-pressure.md`](../../limits/external-merge-intermediate-disk-pressure.md) — surfacing limit
- ADR-034 Round 2 — output-side prefix compression precedent (commit `870d31b`)
- ADR-037 — concurrency pattern precedent (BlockingCollection + worker)
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `SpillOneChunk`, `MergeAndWrite`, `ChunkReader`
- `src/Mercury/Storage/BoundedFileStreamPool.cs` — existing FD-pool architecture
- Cycle 8 + cycle 9 measurements: three-regime structure, 12× swing across transitions, 100 % FD-pool hit rate masking kernel page-cache thrash
- `urn:sky-omega:pattern:merge-three-regimes` (Mercury) — pattern entry
