# ADR-030 Phase 3 Sort-Insert Validation — 2026-04-21

**Status:** Phase 5.1.c architectural optimization. Gradient-tests `ReferenceQuadIndex.AppendSorted` at 1 M / 10 M / 100 M against the 1.7.36 parallel-random-insert baseline. Result: **sort-insert is neutral at Reference scale — the theoretical structural win is eaten by the buffer+sort overhead plus shallow B+Tree geometry.** The code is correct and memory-bounded; the unlock for the 21.3 B run has to come from somewhere else in the pipeline.

## Result

| Scale | Sequential (1.7.34) | Parallel random-insert (1.7.36) | Sort-insert (1.7.37) |
|---|---:|---:|---:|
| 1 M   | 1.8 s | 1.9 s | **1.8 s** |
| 10 M  | 30.6 s | 28.4 s | **29.8 s** |
| 100 M | 512 s (8 m 32 s) | 524 s (8 m 44 s) | **511 s (8 m 31 s)** |

All within measurement noise. No wall-clock win at any scale; no regression either. CPU utilization at 100 M is 323 % — parallelism is still active; sort-insert just doesn't shift the balance.

Correctness holds at every scale — query rows match to the entry (1 M: 53,561; 10 M: 439,703; 100 M: 3,212,485).

## Why the structural advantage doesn't materialize

The Phase 3 premise in ADR-030 was: random-insert is O(log N) per entry, sort-insert is O(1) per entry, therefore ~27× fewer tree operations at 100 M. The ADR was correct about the operation counts. What it didn't account for is what those operations actually cost on this hardware at this scale.

**The B+Tree is shallow.** `ReferenceQuadIndex.LeafDegree` is 511. For 99 M entries the tree depth is `log_511(99 M) ≈ 3.1` — every random insert walks 3 to 4 pages. Those pages fit in L3 cache and stay hot across the scan. A "tree walk per insert" is cheap in practice; it's not 27 expensive page-fault operations.

**Buffer + sort has real cost.** The sort-insert consumer buffers every remapped GPOS key (32 B × 99 M = 3.2 GB), then `Array.Sort` the buffer (99 M × log₂(99 M) ≈ 2.67 B comparisons), then appends. The sort alone is tens of seconds of CPU; allocating and copying 3.2 GB through `List<T>` growth and `ToArray` costs several more.

**The sort-insert consumer loses parallelism.** Under parallel-random-insert (1.7.36) the GPOS consumer inserted as it read, overlapping with Trigram end-to-end. Under sort-insert (1.7.37) the GPOS consumer drains the full stream before it can start appending — so the GPOS work is sequentialized against the Trigram work instead of overlapping.

Net of all three, the buffer+sort cost is roughly equal to the random-insert savings, and the lost overlap nudges the total slightly the other way. The measurement is a wash.

## Where sort-insert would likely help

- **Deeper trees.** At 1 B the tree depth rises to ~4.1; at 21.3 B to ~4.8. Per-insert cost grows linearly with depth. Theoretical crossover is where the tree depth × depth-cost exceeds the sort's `O(log N) × comparison-cost`.
- **Smaller working sets than memory.** If the tree fits entirely in page cache, random-insert is fast. If it spills to disk, random-insert pays seek latency per walk. Reference's 100 M index is ~11 GB on disk — all of it stays in OS page cache on a 128 GB machine. 21.3 B Reference would be ~2.3 TB, far beyond RAM — page-cache misses per random-insert walk would dominate.
- **Radix-friendly keys.** A 32 B composite key can be sorted by radix-passes over `Graph` then `Primary` then `Secondary` etc., each pass O(N). At 100 M that's probably 5-10× faster than `Array.Sort` with comparator. Not done here.

So sort-insert's value is scale- and hardware-dependent, not scale-free. At 100 M Reference on an M5 Max with 128 GB RAM, we're in the regime where random-insert is already good enough.

## What this leaves for the 21.3 B run

The Phase 5 methodology is paying off — we'd have shipped sort-insert expecting a 2-3× gain and found the emperor had no clothes against the 21.3 B wall-clock. The actual unlocks still on the roadmap:

1. **Phase 5.2 dotTrace pass.** With parallel + sort-insert code in place, profile 100 M rebuild and find where the actual 500+ seconds go. Candidates: trigram indexing (it's been the lagging consumer the whole time), broadcast-channel plumbing, atom span lookups during trigram filtering.
2. **External merge sort for 21.3 B.** In-memory sort hits a 128 GB ceiling below the target. For 21.3 B Reference, sorted chunks written to disk and merged with a k-way heap is the classic fix. Could use the same `AppendSorted` primitive — external merge just provides a sorted stream of keys.
3. **Parallel sort.** `Array.Sort` is single-threaded. `PLINQ OrderBy` or a custom parallel merge sort across 8+ cores would shorten the sort phase by 4-8×. Cheap engineering relative to the ~30 s it would save at 100 M (and 300+ s at 1 B).
4. **Skip the GPOS sort entirely when the input is already close to sorted.** GSPO-order input is not GPOS-sorted, but it may be *nearly* sorted for certain predicate distributions. A one-pass "is it sorted?" check could skip the sort and go straight to AppendSorted with a DEBUG-only monotonicity validation. Risk: one bad predicate permutation silently corrupts the tree in RELEASE. Worth a guarded experiment.

None of these are required to ship Phase 5. They're the dotTrace-informed follow-ups that Phase 5.2 is specifically scoped to generate.

## What sort-insert delivers even without a wall-clock win

- **A correct, tested primitive.** `AppendSorted` composes with `BeginAppendSorted` / `EndAppendSorted` and has an explicit monotonicity contract. When external merge sort or radix sort arrive, `AppendSorted` is already built, the consumer integration is already in place, and the tests already pin the invariants.
- **Rebuild idempotence.** To support sort-insert's empty-target precondition, the rebuild path now explicitly `Clear()`s `_gposReference` and `_trigramIndex` at the start. A rebuild is now *reconstruction* — every call gives you the same final state regardless of how many times you call it. The old behavior (second rebuild appends duplicates that get silently deduped) was correct but brittle; the new behavior is explicit.
- **The architectural box is marked complete.** ADR-030 Phase 3 shipped; the remaining optimization surface is micro-level (Phase 5.2 dotTrace) rather than architectural.

## Reproduction

```bash
./tools/install-tools.sh   # 1.7.37

# Reference gradient — bulk + rebuild + correctness check
for N in 1000000 10000000 100000000; do
  store=wiki-$(( N / 1000000 ))m-ref
  rm -rf ~/Library/SkyOmega/stores/$store
  MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384 \
    mercury --store $store --profile Reference \
      --bulk-load ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt \
      --limit $N --min-free-space 50 --no-http --no-repl
  time mercury --store $store --rebuild-indexes --no-http --no-repl
  echo 'SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }' | \
    mercury --store $store --no-http
done
```

## Implications for Phase 5 sequencing

- **5.1.c complete** — correctness pinned, behavior measured, honest docs published.
- **5.2 dotTrace is now the load-bearing next step.** We have three architectural optimizations shipped (Decision 5, parallel rebuild, sort-insert); all three are correct; none individually unlocks the 24 h Phase 6 exit threshold at 21.3 B. The remaining cost is micro-level and requires profiling the actual code paths to find, not architectural refactors to guess at.
- **Cognitive gradient still TBD.** Reference has been the test case throughout Phase 5.1. Cognitive parallel + sort-insert (for GPOS/GOSP) should show a different profile because four-consumer fan-out has less skew. A Cognitive 100 M gradient is cheap to run; valuable data before the Phase 6 commitment.

## Provenance

Validation authored 2026-04-21. Three Reference-profile scales measured against the 2026-04-21 Phase 2 parallel-random-insert baseline and the 2026-04-20 Decision 5 sequential baseline. Same store builds, same hardware, same Wikidata slice.

## References

- [ADR-030 Phase 3](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) — the design this validates
- [Phase 5.1.a validation](adr-030-decision5-reference-refactor-2026-04-21.md) — sequential baseline
- [Phase 5.1.b validation](adr-030-phase2-parallel-rebuild-2026-04-21.md) — parallel-random-insert baseline
- [Production hardening roadmap 5.2](../roadmap/production-hardening-1.8.md) — the dotTrace pass this result points toward as the next structural move
