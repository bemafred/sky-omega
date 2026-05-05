# Limit: Intermediate cleanup deferred to run end (chunks held longer than needed)

**Status:**        Latent (Triggered on cycle 8 — manually mitigated)
**Surfaced:**      2026-05-05 16:25 CEST, during cycle 8 drain phase. After `MergeAndWrite` consumed all 3,923 occurrence chunks at 15:09 CEST, the 3.6 TB of `bulk-tmp/sorted-vocab/occurrences/chunk-*.bin` files remained on disk untouched. Free disk dropped from initial 6.5 TB to 699 GB during drain — within ~600 GB of the 100 GB min-free-space abort threshold — before manual cleanup recovered 3.5 TB.
**Last reviewed:** 2026-05-05

## Description

`SortedAtomBulkBuilder` writes occurrence chunks to a `tempDir` provided by `QuadStore`. After `MergeAndWrite` consumes those chunks (one full pass through the priority queue), they are no longer referenced by any active code path: the file pool is disposed at end of merge, no `ChunkReader` instances exist, the merge has emitted its final `merge_completed` event.

But the files **remain on disk**. They are deleted only when the entire bulk-load run completes — parser + merge + drain + GSPO bulk sort + rebuild — and `QuadStore`'s end-of-run cleanup fires.

The architectural cause: `SortedAtomBulkBuilder._ownsTempDir = false` when the caller (`QuadStore`) provides an explicit `tempDir`. The bulk builder defers cleanup to the caller. The caller cleans only at run end. **No phase-transition cleanup hook fires** when one phase finishes consuming an artifact that the next phase doesn't need.

## Why this is a register entry

The orphaned-intermediate problem is **not the same as peak-intermediate-volume** (sibling entry: `external-merge-intermediate-disk-pressure.md`). That entry covers how much disk is needed *during* the merge phase. This entry covers how *long* dead intermediate is held *after* its phase ended.

At 21.3B Wikidata scale on a 7.3 TB host, manual cleanup recovered **3.6 TB** that was sitting unused. On a smaller host (e.g., 6 TB total disk), the same orphaned-intermediate would have triggered the `MinimumFreeDiskSpace` abort during GSPO bulk sort, killing a 26+ hour run mid-flight.

This is *characterized but not acted on*: the bug is identified, the architectural cause is named, the fix shape is clear (incremental cleanup hooks at phase transitions), the workaround exists (manual `rm -rf` after `merge_completed`), and the trigger threshold is concrete.

## Trigger condition

This limit moves toward an ADR / fix when one of:

1. **A future bulk-load run aborts on `MinimumFreeDiskSpace` during drain or GSPO sort** with disk exhausted by orphaned occurrence chunks. Cycle 8 came within ~600 GB of this on a 7.3 TB host; smaller hosts hit it sooner.
2. **Sky Omega 2.0 deployment** to a host where total disk < (peak_intermediate × 1.5). The cleanup gap forces the disk requirement higher than the architectural minimum.
3. **A subsequent run with new long-running phases** (Round 2 prefix-compression of intermediate chunks, parallel GSPO writers, etc.) extends the period orphaned chunks are held.

## Current state

After cycle 8's manual cleanup intervention, the run is healthy. The bug remains in code:

- `src/Mercury/Storage/SortedAtomBulkBuilder.cs` — `_ownsTempDir` semantics
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `MergeAndWrite` consumes chunks but does not delete them
- `src/Mercury/Storage/QuadStore.cs` — end-of-run cleanup (`bulk-tmp/` deletion) is the only place chunks get removed

Future runs at 21.3B scale on hosts with > ~7 TB free disk will succeed without intervention. Smaller hosts will require manual cleanup or will fail.

## Candidate mitigations

1. **Phase-transition cleanup hook in `MergeAndWrite`.** After the merge loop completes (and after `dataFs.Flush(flushToDisk: true)` confirms the output is persisted), explicitly delete the consumed chunk files. Either via `File.Delete` per-chunk in the readers' Dispose path, or a final `Directory.Delete(occurrencesDir, recursive: true)`. Lowest-impact fix; targets the specific path that caused cycle 8.

2. **Incremental cleanup as chunks exhaust.** When a `ChunkReader.MoveNext()` returns false (chunk fully consumed), delete the file immediately rather than waiting for end-of-merge. Streams the disk-pressure relief through the merge phase. More invasive; touches the reader lifecycle.

3. **Generalized phase-transition discipline.** Define explicit phase boundaries in the bulk-load pipeline (parser-end, merge-end, drain-end, gspo-sort-end, rebuild-end) and run cleanup hooks at each. The other long-running internals (resolver chunks during drain, GSPO sorter chunks during rebuild) likely have similar end-of-phase cleanup gaps that this run hasn't surfaced because cycle 8 didn't hit min-free-space at those transitions.

The natural sequencing: ship (1) immediately as the fix for the surfaced incident; design (3) as the structural answer; (2) only if (1) is insufficient at scales beyond 21.3B.

## Why this matters beyond disk

Two secondary effects:

1. **Run-success vs host-class coupling.** Without the fix, "can my host run a 21.3B Reference bulk-load" depends on having ~30% more disk than the architecture requires. That's a hidden host-class requirement — not visible from the architectural docs, only surfaced by hitting the abort. The `bulk-load-flow.md` "Disk artifacts and lifetime" table presumes correct cleanup; the actual behavior diverges.

2. **Compounds with peak-intermediate.** The sibling limit (`external-merge-intermediate-disk-pressure.md`) projected ~5 TB peak. The actual peak observed at cycle 8 was ~5.6 TB (1.4 TB resolver + 0.6 TB GSPO + 3.6 TB orphaned occurrences) — the orphaned tail extends the peak window indefinitely from end-of-merge to end-of-run.

## References

- `src/Mercury/Storage/SortedAtomBulkBuilder.cs` — `_ownsTempDir` field and constructor logic
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `MergeAndWrite` consumes chunks without delete
- `src/Mercury/Storage/QuadStore.cs` — end-of-run cleanup
- `urn:sky-omega:bug:orphaned-chunks-after-merge-2026-05-05` (Mercury) — the surfacing incident
- `urn:sky-omega:incident:cycle8-21b-instrumented-2026-05-04` (Mercury) — the cycle 8 run that surfaced this
- 2026-05-05 cycle 8 drain phase — observation 16:25 CEST; manual cleanup 16:32 CEST
- `docs/limits/external-merge-intermediate-disk-pressure.md` — sibling limit, focuses on peak volume vs this entry's hold duration
- `docs/limits/observability-coverage-gap.md` — sibling; this bug was caught only because cycle 7+8 instrumentation made disk-trace data observable
