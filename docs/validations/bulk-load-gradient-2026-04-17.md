# Bulk Load Scale Gradient — 2026-04-17 / 2026-04-18

**Status (2026-04-18 14:02 local):** 100 M run in progress with 1.7.12. Five real bugs surfaced during gradient runs, four fixed. mmap-grow proper fix is pending tomorrow's session.

Phase 4.1 of [ADR-027](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md). Objective: observe Mercury's bulk-load behavior at increasing scales against `latest-all.nt` from the full Wikidata April 2026 dump.

## Headline numbers

| Scale | Throughput | Elapsed | Bytes/triple | Mercury version |
|---|---|---|---|---|
| 1 M (orphans contention) | 1,464 /sec | 11 m 23 s | 685 | 1.7.7 (broken WriteThrough) |
| 1 M (clean, fix) | **54,978 /sec** | 18.4 s | 685 | 1.7.9 (msync fix) |
| 10 M (clean, fix) | **137,780 /sec** | 1 m 12 s | 220 | 1.7.9 |
| 100 M (1.7.12, **crashed at 58.3 M**) | 77,936 /sec avg, 137,206 /sec recent | 12 m 22 s to crash | ~241 (14 GB / 58 M) | **1.7.12** — atom hash overflow |
| 1 B | not run | — | — | — |
| 21.3 B | not run | — | — | — |

## Bugs found and fixed during today's gradient

### Bug 1 — `QuadIndex.FlushPage` calls `_accessor.Flush()` per page write
- **Symptom:** 5.5 % CPU, 5,510 disk IOPS sustained (M5 Max NVMe random-write ceiling), ~1,000 triples/sec capped.
- **Root cause:** `MemoryMappedViewAccessor.Flush()` is `msync()` on macOS — flushes the **entire** mapped region per call, not a page.
- **Fix:** `FlushPage` is a no-op in bulk mode; `QuadIndex.Flush()` exposes the deferred msync; `QuadStore.FlushToDisk()` calls it on all four indexes.
- **Released:** 1.7.9
- **Impact:** 37× throughput improvement at 1 M (1,464 → 54,978 /sec)

### Bug 2 — `CheckpointIfNeeded` runs during bulk load
- **Symptom:** `AccessViolationException` in `CollectPredicateStatistics` at ~20.8 M triples (100 M run with 1.7.9).
- **Root cause:** Checkpoint scans the GPOS index, but in bulk mode GPOS is empty/uninitialized (only GSPO is written). Walking an uninitialized B+Tree page = invalid memory.
- **Fix:** Skip `CheckpointIfNeeded` entirely when `_bulkLoadMode`. Bulk-load contract defers all durability to a single `FlushToDisk()` at completion.
- **Released:** 1.7.10

### Bug 3 — `NTriplesStreamParser.Peek` typo, never refills
- **Symptom:** "Unterminated string literal" at line 27,515,974 (a 4,202-character MathML literal in Wikidata).
- **Root cause:** `return _endOfStream ? -1 : -1;` — both branches return -1, refill case missing entirely. Same class of bug as the Turtle parser fixed in 1.7.4. Any literal larger than the 8 KB buffer hit premature "EOF".
- **Fix:** Looped self-refill via new `FillBufferSync` (mirror of `FillBufferAsync` using sync `_stream.Read`), same pattern as `TurtleStreamParser.Buffer.cs`.
- **Released:** 1.7.11

### Bug 4 — `QuadIndex` mmap doesn't grow with file (workaround applied; proper fix pending)
- **Symptom:** `AccessViolationException` in `QuadIndex.SplitLeafPage` at ~27.9 M triples.
- **Root cause:** `AllocatePage` extends the underlying file via `_fileStream.SetLength`, but the `_mmapFile` and `_accessor` still cover only the original 1 GB initial size. New pages past 1 GB write to unmapped memory.
- **Workaround applied (1.7.12):** Pre-size bulk-mode mmap to 256 GB per index. macOS allocates 256 GB of virtual address space immediately; sparse-file behavior means physical pages only on touch. Trivial single-line change. Per-process VM ceiling on macOS is ~64 TB so 256 GB leaves headroom for full Wikidata at ~1.8 TB per index.
- **Proper fix (TODO, tomorrow):** Either:
  - **Option 1 (refined sparse oversize):** Bump cap to ~2 TB per index for full Wikidata. Same approach, larger initial. Disk usage tracks actual writes (sparse file).
  - **Option 2 (chunked mmap):** Multiple `MemoryMappedFile` objects per logical file, one per chunk. Page-id-to-chunk math routes lookups. Growth = append a new chunk mapping. No remapping of existing chunks; pointers remain stable.
  - **Option 3 (mmap-grow):** Unmap, recreate mmap with new size, reacquire `_basePtr`. Invasive in unsafe code; risky.
  - Recommendation: **Option 1 first, Option 2 as the strategic answer when datasets ever exceed per-process VM (~64 TB).**

### Bug 5 — `AtomStore` hash table fixed at 1 M buckets (no rehash-on-grow)
- **Symptom:** `InvalidOperationException: Hash table overflow at bucket 9415495 after 4096 probes. Load factor: 96.72%.` at 58.3 M triples on the 1.7.12 100 M run.
- **Root cause:** `AtomOffsetInitialCapacity` defaults to `1 << 20` (1 M entries). At ~58 M Wikidata triples we have ~5-10 M unique atoms, well past 1 M; load factor exceeded the 4096-probe threshold and the implementation throws rather than rehashing.
- **Status:** **NOT YET FIXED** — this is the next bug to address tomorrow.
- **Fix options:**
  - **Option A (workaround):** bump `AtomOffsetInitialCapacity` to e.g. 1 << 28 (~268 M entries) for bulk mode. Same sparse-mmap pattern as the QuadIndex 256 GB workaround. Disk usage tracks actual touched buckets.
  - **Option B (proper):** rehash-on-grow — when load factor exceeds threshold, allocate a new hash table at 2× size, walk all atoms, re-insert. Standard hash table dynamic-sizing pattern.
- **Recommendation:** **Option A first**, Option B later. Option A is one-line change matching the `QuadIndex` mmap workaround pattern. Option B is the right long-term answer but more invasive (atom hash insert is on the hot path).
- **Wikidata atom estimate for sizing:** 100-200 M unique entity IRIs + 10-50 K predicate IRIs + 500 M-2 B literals → on the order of 1-2 B unique atoms total. Need a hash table sized for ~3 B buckets at < 50 % load factor → 4 GB of bucket array per atom store. Sparse-mmap tolerates this fine.

### Bug 6 (incidental) — Orphaned `dotnet test` processes
- **Symptom:** Four `testhost.dll` processes at 100 % CPU each, leftover from earlier `dotnet test` invocations I detached and didn't reap. Starved the bulk-load process of CPU during the first 1 M run, depressing the throughput baseline.
- **Fix:** Killed them. Going forward, I should not detach `dotnet test` runs without ensuring host process cleanup.
- **Not a code bug;** an operational mistake on my part. Captured here so the contaminated 1 M baseline measurement (1,464/sec) is understood as the combination of orphans + WriteThrough + msync, not a clean Mercury number.

## Architectural finding — mmap-the-whole-file is not required

In response to "is it required to map complete files?" the answer is no. Today's workaround (Option 1: huge sparse mmap upfront) is one way; chunked mmap (Option 2) is the strategic answer when files exceed the per-process VM ceiling. Mercury's zero-GC mmap-direct semantics are preserved by either approach. **This is captured as a project memory** so future sessions don't re-litigate the choice.

## Methodology validation

The gradient methodology paid off five separate times today. Each scale step surfaced exactly one new failure mode:
- 1 M: throughput ceiling (msync)
- 10 M: warmup behavior + bytes/triple amortization curve
- 100 M run #1: checkpoint AV
- 100 M run #2: parser literal-buffer overflow
- 100 M run #3: mmap-grow

Each bug's fix unlocked the next scale's failure. Without the gradient, all five would have surfaced as a single mystery crash deep into a multi-day full-load attempt. This is exactly what Phase 4.1 was designed to do.

## Resume protocol for next session

When the session resumes (after Martin's overnight OS reboot/upgrade):

1. **Confirmed status at session end:** the 100 M run with 1.7.12 made it to **58.3 M triples** (more than 2× past previous high of 27.9 M before mmap workaround). Crashed on `AtomStore` hash table overflow (Bug 5 above). The mmap-grow workaround held through the 27.9 M boundary that previously crashed; the next ceiling is atom interning capacity.
2. **Git state:** commits `719fa94` (1.7.8-1.7.12) and the prior `1c3f813` (1.7.6-1.7.7) are on origin/main. The 5th bug (atom hash overflow) is documented but **not yet fixed** in code.
3. **Next code change:** apply Option A workaround for Bug 5 — bump `AtomOffsetInitialCapacity` to ~256 M for bulk mode (matches the QuadIndex 256 GB pattern). Single-line change in `StorageOptions` or `AtomStore` constructor. Then redo 100 M, expect completion. Then 1 B.
4. **Before running 21.3 B:** Bug 4 mmap cap may need raising from 256 GB to ~2 TB per index. Bug 5 atom hash may need raising from 256 M to ~3 B buckets. Both are sparse-mmap workarounds; proper rehash and chunked-mmap fixes can come later.
5. **Re-extract the slices** (lost on reboot if /tmp cleared):
   ```bash
   head -n 1000000   ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt > /tmp/wikidata-1m-clean.nt
   head -n 10000000  ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt > /tmp/wikidata-10m-clean.nt
   head -n 100000000 ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt > /tmp/wikidata-100m-clean.nt
   ```

## Reproduction

```bash
# Slices already extracted in /tmp/ (will not survive reboot — re-extract):
head -n 1000000   /Users/bemafred/Library/SkyOmega/datasets/wikidata/full/latest-all.nt > /tmp/wikidata-1m-clean.nt
head -n 10000000  /Users/bemafred/Library/SkyOmega/datasets/wikidata/full/latest-all.nt > /tmp/wikidata-10m-clean.nt
head -n 100000000 /Users/bemafred/Library/SkyOmega/datasets/wikidata/full/latest-all.nt > /tmp/wikidata-100m-clean.nt

# Wipe + load (all gradient steps follow this pattern):
rm -rf ~/Library/SkyOmega/stores/wiki
mercury --store wiki --bulk-load /tmp/wikidata-{N}-clean.nt --metrics-out /tmp/load-{N}.jsonl --min-free-space 500 --no-http
```

## Provenance

- Sessions: 2026-04-17 evening (parser fix, convert validation, first 1 M attempt found bugs 1+2 of writer fix), 2026-04-18 morning (this session)
- Mercury versions released today: 1.7.8 (cosmetic, FileStream WriteThrough alignment), 1.7.9 (msync), 1.7.10 (checkpoint), 1.7.11 (parser refill), 1.7.12 (mmap pre-size workaround)
- Hardware: MacBook Pro M5 Max, 128 GB RAM, 8 TB SSD
- All findings stored in Mercury semantic memory (graph `urn:sky-omega:session:2026-04-18`)
