# Limit: Predicate-statistics memory at scale

**Status:**        Latent
**Surfaced:**      2026-04-20, via [dispose-profile-2026-04-20.md](../validations/dispose-profile-2026-04-20.md)
**Last reviewed:** 2026-04-20
**Promotes to:**   ADR when (a) a Cognitive store grows past ~5 B triples and triggers `CheckpointInternal`, OR (b) a Reference store at 21.3 B accepts session-API writes (currently rejected by ADR-029 Decision 7), OR (c) any explicit `Checkpoint()` call lands on a 21.3 B store.

## Description

`QuadStore.CollectPredicateStatistics()` (`src/Mercury/Storage/QuadStore.cs:849`) walks every quad in the GPOS index and builds, for each distinct predicate, a `Dictionary<long, (long count, HashSet<long> subjects, HashSet<long> objects)>`. The dictionary itself is small (Wikidata has ~50K predicates), but the per-predicate `HashSet<long>` of subjects and objects can grow to hundreds of millions of entries for high-diversity predicates like `rdfs:label` (one literal-object per language per entity) or `rdf:type` (one subject per entity).

At 1 B triples (Cognitive profile), peak working set during `CheckpointInternal` was measured at ~10 GB ([dispose-profile-2026-04-20.md](../validations/dispose-profile-2026-04-20.md)). At 21.3× scale, an extrapolated 200+ GB would exceed the M5 Max's 128 GB physical memory.

## Trigger condition

`CollectPredicateStatistics` runs on Dispose via `CheckpointInternal()`, but several gates currently keep it from firing on the dangerous paths:

- **Bulk-load mode short-circuits checkpoints** at `QuadStore.cs:837` — full Wikidata bulk-loads do not trigger this code.
- **ADR-031 Piece 2 (when shipped) skips it on read-only Dispose** — query-only sessions won't trigger it either.
- **ADR-029 Decision 7 rejects session-API writes on Reference stores** — the most likely 21.3 B path is locked behind this gate.

The remaining triggering paths:

1. A Cognitive store with active session-level mutations grows past ~5 B triples (rough working-set extrapolation).
2. A Reference store accepts session-API writes despite ADR-029 Decision 7 (would itself be a Decision-7 violation).
3. An explicit `Checkpoint()` call against a large store outside bulk-load mode.

## Current state

Latent. No currently-shipping path triggers it on a store large enough for memory to bind. The 1 B Cognitive case completed (slowly, via paging) on 128 GB; that is the upper bound of "observed but not OOM" — anything substantially larger is unmeasured.

## Candidate mitigations

Listed in order of suspected best fit. None implemented; pick when promoting to ADR.

1. **HyperLogLog sketches.** Standard cardinality-estimation technique. ~12 KB per (predicate, dimension) sketch, ~2 % error. Total memory: ~50K predicates × 24 KB (subjects + objects) ≈ 1.2 GB at any underlying scale. Updates incrementally on each insert (one hash + bit-set operation). For a query planner, exact distinct-counts are not needed — selectivity estimates are. **Probably the right answer.**

2. **Sorted-scan via GSPO and GOSP.** Walk GSPO sorted by (S, P, O); within each subject's subtree, increment a per-predicate "distinct subjects" counter once. Symmetric for objects via GOSP. Replaces `HashSet` with O(K) counters where K = distinct predicates. Exact counts. Bounded memory. Slower than the current implementation (two scans instead of one) but no allocation pressure.

3. **Incremental maintenance on writes.** Update sketches/counters in the write path. No periodic batch needed at all. Adds per-write overhead but eliminates the Dispose-time problem entirely. Composes well with sketches (option 1).

## References

- Source: `src/Mercury/Storage/QuadStore.cs:805-822` (`CheckpointInternal`) and `:849-896` (`CollectPredicateStatistics`)
- [dispose-profile-2026-04-20.md](../validations/dispose-profile-2026-04-20.md) — measurement that surfaced this
- [ADR-031](../adrs/mercury/ADR-031-read-only-session-fast-path.md) — Piece 2 narrows the trigger set by skipping `CheckpointInternal` on read-only Dispose
- [ADR-029 Decision 7](../adrs/mercury/ADR-029-store-profiles.md) — Reference profile session-API immutability is the other gate keeping this latent
