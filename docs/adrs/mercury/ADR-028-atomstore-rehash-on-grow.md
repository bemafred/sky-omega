# ADR-028: AtomStore Rehash-on-Grow

## Status

**Status:** Completed — 2026-04-26 (shipped 1.7.24, validated end-to-end through 21.3 B Wikidata in Phase 6; see [`adr-028-rehash-gradient-2026-04-20.md`](../../validations/adr-028-rehash-gradient-2026-04-20.md) and [`21b-query-validation-2026-04-26.md`](../../validations/21b-query-validation-2026-04-26.md))

## Context

ADR-027 validated bulk ingest through the 1 B-triple scale with a fixed 256 M-bucket hash table (introduced in 1.7.13 to fix Bug 5). The 1 B run finished with **213 M atoms at 83 % load factor** — approaching the ceiling. Full Wikidata projects to ~4.58 B atoms, which overflows the current cap by an order of magnitude.

See [ADR-027 § Measured Storage Footprint](ADR-027-wikidata-scale-streaming-pipeline.md#measured-storage-footprint-2026-04-19) for the full per-index size breakdown through 1 B. The atom-hash ceiling at 256 M buckets is the *first* ceiling a Wikidata-scale load hits — even a reduced-index configuration (Option A in ADR-027) crosses it around 4-5 B triples. Rehash-on-grow is therefore unavoidable for any run past ~1.2 B triples, regardless of which storage path ADR-027 chooses.

The previous bulk-mode fix (`BulkModeHashTableSize = 1L << 28`, 8 GB sparse mmap) relies on POSIX sparse-file semantics: `SetLength(8GB)` on APFS/ext4/xfs creates a virtual-only file whose physical footprint tracks touched pages. On Windows/NTFS, the same call allocates physical clusters immediately — an **8 GB file really uses 8 GB of disk**, before a single atom is interned.

So the current approach has two problems:

1. **Capacity ceiling at 256 M buckets.** Full Wikidata needs more.
2. **Cross-platform correctness.** The sparse-mmap trick is POSIX-specific. Users on Windows would pay full physical disk cost for a hash table sized "in case."

Enlarging the cap further (`1L << 33` = 8 B buckets, 256 GB sparse) buys us one more scale step and makes the Windows problem worse, not better.

The right answer: **grow the hash table dynamically.** This removes both the ceiling and the POSIX assumption.

### Constraints in effect

- **ADR-020** single-writer contract: `AtomStore` mutations happen under the QuadStore write lock. No concurrent writers.
- **ADR-026 / ADR-027** bulk-mode contract: durability deferred to one `FlushToDisk()` at load completion. No crash-consistency guarantee *during* a bulk load — a crash means "delete and reload."
- **BCL-only**: implementation uses only `System.IO`, `System.IO.MemoryMappedFiles`, standard `FileStream.SetLength` / `File.Move`. No P/Invoke beyond what's already there.
- **Atom IDs are stable**: `_nextAtomId` is monotonic and durable. The offset-index (`atomId → offset`) is unaffected by rehash — only the `string → atomId` lookup structure (the hash table) is rebuilt.

## Decision

### 1 — Implement rehash-on-grow as the primary mechanism

`AtomStore` grows its hash table in-place when load factor exceeds a configurable threshold. The fixed 256 M-bucket cap is removed; `AtomHashTableInitialCapacity` becomes a hint, not a ceiling.

Same code path on every platform. On macOS/Linux with a large initial size (say 256 M), the rehash may never trigger during a Wikidata-scale load — same performance as today. On Windows or with small initial sizes, the rehash keeps things correct and the physical disk footprint matches actual atom count.

### 2 — Location

Implementation lives in `src/Mercury/Storage/AtomStore.cs`, alongside the existing `EnsureDataCapacity` and `EnsureOffsetCapacity` methods. Those already embody the "grow an mmap'd file" pattern; rehash is the same shape with a different inner loop.

No new files, no partial classes. The rehash method is the third member of the same family:

```
EnsureDataCapacity    — grow the .atoms data file when a new atom doesn't fit
EnsureOffsetCapacity  — grow the .offsets index when atomId exceeds cap
EnsureHashCapacity    — grow the .atomidx hash table when load factor exceeds threshold
```

### 3 — Trigger

Checked at the top of `InsertAtomUtf8`, which is only called when an atom is *new* (the lookup path in `InternUtf8` already confirmed the atom isn't present). The check is:

```csharp
if ((_atomCount + 1) * 100 > _hashTableSize * MaxLoadFactorPercent)
    EnsureHashCapacity(_hashTableSize * 2);
```

`MaxLoadFactorPercent` is a compile-time const, default **75**. Two considerations:

- **75 % is quadratic-probing territory.** Empirically from the 1 B run, we saw the byte-wise FNV hash maintain decent probe depth up through 83 % load. 75 % leaves headroom before probe degradation becomes visible.
- **Load factor is a proactive trigger, not a reactive one.** We do *not* try to rehash in response to probe-depth overflow. If probe depth hits the 4096 cap, either the hash function is broken (1.7.16 word-wise FNV clustering) or something else is wrong — growing the table in that state would just preserve the clustering. Rehash fixes capacity, not distribution.

### 4 — Safety

Four properties must hold:

**4a. Single-writer correctness.** Rehash runs inline in `InsertAtomUtf8`, which already runs under the QuadStore writer lock per ADR-020. No new locking needed. Readers hold the QuadStore read lock, which blocks any rehash; the rehash runs exclusively.

**4b. Memory barrier at pointer swap.** The field swap follows the pattern already used in `EnsureDataCapacity`:

```csharp
_hashTable = newHashTable;       // publish new pointer
Thread.MemoryBarrier();          // ensure visibility
_indexAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
_indexAccessor.Dispose();        // unmap old
_indexMap.Dispose();
```

After the barrier, any future read-lock acquisition sees the new pointer. Existing reads under read lock are impossible because the rehash holds the writer lock.

**4c. Crash safety via two-file swap.** During rehash:

```
atoms.atomidx        ← current live file, writer lock held
atoms.atomidx.new    ← allocated at 2× size, populated from old entries
```

After the new file is fully populated and fsync'd:

```
atoms.atomidx        → renamed to atoms.atomidx.old
atoms.atomidx.new    → renamed to atoms.atomidx
atoms.atomidx.old    → unmapped, deleted
```

POSIX `rename(src, dst)` is atomic on the same filesystem. Windows needs `File.Move(src, dst, overwrite: true)` which is durable but not strictly atomic — acceptable because bulk mode already disclaims crash consistency.

On reopen, if `atoms.atomidx.new` exists but `atoms.atomidx` does not: a rehash was interrupted mid-swap. Delete the orphan `.new`, the live file is still `.old` — promote it back to `atoms.atomidx`. If both `.new` and `.old` exist with neither promoted: ambiguous, prefer the `.old` (pre-rehash state) and discard `.new`. Startup validation does this cleanup before any inserts.

**4d. Atom IDs preserved.** Rehash iterates old hash-table entries and re-inserts each into the new table. Each entry already stores `AtomId`, `Hash`, `Offset`, `Length` — the hash is *stored*, not recomputed. So:

```csharp
for (long oldBucket = 0; oldBucket < oldSize; oldBucket++)
{
    ref var entry = ref oldTable[oldBucket];
    if (entry.AtomId == 0) continue;     // empty slot

    var newBucket = (long)((ulong)entry.Hash % (ulong)newSize);
    // quadratic-probe in newTable, write entry to first empty slot
    ProbeAndPlace(newTable, newSize, newBucket, entry);
}
```

`_nextAtomId` and the offset index (`_offsetIndex`) are untouched — the atom → offset mapping is independent of hash bucket position.

### 5 — Interaction with bulk-mode sparse-mmap default

`AtomHashTableInitialCapacity` stays in `StorageOptions`. The bulk-mode override (`Math.Max(configured, 1L << 28)`) also stays.

On POSIX, a bulk load that fits in 256 M buckets still pays no rehash cost — sparse mmap handles it, and the rehash trigger never fires. On Windows with a Wikidata-scale load, rehash grows the table organically and the physical disk footprint stays reasonable. Same code, different tuning per deployment.

### 6 — Out of scope

- **Lazy/incremental rehash** (two-table-during-transition, split-ordered hash tables). Rehash is stop-the-world because we already are single-writer; additional complexity is not justified.
- **Rehash the offset index.** `EnsureOffsetCapacity` already grows via doubled SetLength and re-map. No redesign needed.
- **Rehash-on-shrink.** Not useful for bulk workloads; cognitive stores rarely shrink enough atoms to matter.
- **Hash function replacement.** The distribution-quality work around replacing byte-wise FNV with a SIMD-friendly alternative (xxHash64-style) is separate. An ADR would cover it once we have a regression harness.

## Consequences

### Positive

- **Correctness across filesystems.** Windows users stop paying full physical-disk cost for empty hash tables.
- **No capacity ceiling.** 21.3 B-triple Wikidata load can complete without running out of buckets. The current 256 M-bucket cap would overflow around 4-5 B triples even under the reduced-index configuration in [ADR-027 § Measured Storage Footprint](ADR-027-wikidata-scale-streaming-pipeline.md#measured-storage-footprint-2026-04-19) — rehash-on-grow is unavoidable for any ingest past ~1.2 B triples.
- **Smaller default footprint for cognitive stores.** Starting at, say, 1 M buckets instead of 16 M means a 32 MB hash table for a quiet semantic-memory store that only ever interns a few thousand atoms.
- **Single code path.** One implementation, one behavior, auditable uniformly.

### Negative

- **Stop-the-world pause per doubling.** At 256 M atoms, rehashing into 512 M buckets = memcpy + re-probe of 256 M × 32-byte entries = ~8 GB of data movement. At ~3-4 GB/s on typical hardware: **2-3 second pause**. Under bulk load (single-writer, multi-hour job) this is negligible. Under cognitive load (interactive REPL, MCP) it's a pause that users may notice.
  - Mitigation: start with a reasonable initial capacity so rehash triggers at most 2-3 times over a store's lifetime. For a cognitive store that grows slowly, each pause is infrequent.
- **Peak memory doubles during rehash.** Old + new table both exist until the swap. Not a problem at reasonable sizes; an 8 GB old + 16 GB new = 24 GB peak for a 256 M→512 M rehash.
- **More complex than a fixed-size table.** Reopen path must handle orphaned `.new`/`.old` files. Documented and tested once; then invisible.

### Risks

- **Hash distribution at high load factors.** The 1.7.16 incident (word-wise FNV clustering) showed that a bad hash at 12 % load can overflow probe limits. Rehash doesn't fix a broken hash — it just delays the symptom. The 75 % load-factor trigger assumes the hash's distribution is reasonable. Byte-wise FNV-1a has known-good behavior; future hash-function work must include a distribution regression test before shipping.
- **Rename semantics on Windows.** `File.Move(..., overwrite: true)` is not strictly atomic under power loss. Bulk mode already disclaims crash consistency mid-load, so this matches the contract, but it should be documented.

## Implementation plan

Phased rollout:

1. **Stage 1 — Implementation & unit tests.**
   - Add `EnsureHashCapacity(long requiredSize)` to `AtomStore`.
   - Wire the load-factor check at top of `InsertAtomUtf8`.
   - Add startup cleanup for `.new` / `.old` orphans in the `AtomStore` constructor.
   - Unit tests covering: trigger threshold, correctness after rehash (lookup existing atoms still works), crash-safety (simulated interruption between fsync and rename; orphan cleanup).

2. **Stage 2 — Storage gradient regression.**
   - Replay the 1 M / 10 M / 100 M gradient. Start with `AtomHashTableInitialCapacity = 1L << 14` (16 K buckets) to force several rehashes during the 100 M run.
   - Confirm throughput, atom count, and query correctness match the pre-rehash baselines in [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md) — those numbers are the reproducible reference for "does rehash change anything it shouldn't."

3. **Stage 3 — Full Wikidata run.**
   - Remove the `BulkModeHashTableSize` ceiling (or leave it as a performance hint, not a cap).
   - Run full Wikidata ingest. Expected rehash events: ~10 doublings from 256 M to ~4 B buckets.
   - Storage constraints from [ADR-027 § Measured Storage Footprint](ADR-027-wikidata-scale-streaming-pipeline.md#measured-storage-footprint-2026-04-19) — the full four-index 21.3 B footprint (~13.8 TB) exceeds the 8 TB validation disk. Stage 3 therefore runs one of the three ADR-027 paths (drop GOSP+TGSP, external NVMe, or partial 12 B load). The rehash behavior being validated is independent of which B+Tree indexes run — they use separate files and separate code paths from `AtomStore`.
   - Validate: load completes, atom count ≈ expected, pause duration per rehash documented.

4. **Stage 4 — ADR update.**
   - Move status Proposed → Accepted after Stage 2, → Completed after Stage 3.
   - Record measured pause durations and any emergent issues.

## Open questions

- **Trigger at exactly 75 %?** Empirically 83 % held up on 1 B with byte-wise FNV — the 1.7.19 run completed 991.8 M triples at 213 M atoms in 256 M buckets without probe degradation (see [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md)). But the rehash cost is amortized anyway; picking 75 % is a conservative default, not a tight optimum.
- **Should the initial capacity for cognitive stores shrink?** `AtomHashTableInitialCapacity` default is 16 M today. A typical cognitive store has a few thousand atoms at most. 16 K initial + rehash on growth would be enough for 99 % of cognitive workloads. Defer until Stage 2 validates the rehash implementation.
