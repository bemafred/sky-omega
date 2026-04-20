# Limit: AtomStore hash function quality

**Status:**        Latent
**Surfaced:**      2026-04-19, via the 1.7.16 word-wise FNV regression on the 1 B Wikidata slice (`AtomStore: revert word-wise FNV — hash clustering on 1B Wikidata`, commit `2f7ea80`)
**Last reviewed:** 2026-04-20
**Promotes to:**   ADR when (a) ingest throughput becomes binding even after ADR-029 + ADR-030 ship, OR (b) a distribution-quality regression harness is built (currently absent — see ADR-030 § Scope boundary), OR (c) any adversarial-input concern surfaces

## Description

`AtomStore.ComputeHash` (`src/Mercury/Storage/AtomStore.cs:514`) currently uses byte-at-a-time FNV-1a 64-bit. The implementation is documented in source:

> Byte-at-a-time is slower than a word-wise variant but has known-good avalanche on adjacent strings (e.g., `<…entity/Q1000001>`, `<…entity/Q1000002>`) where the varying bytes fall inside a shared 8-byte word. An earlier word-wise FNV (1.7.16) caused hash clustering on the 1B-triple slice: 4096-probe overflow at 11.93 % load factor. Reverted. A faster hash with proper per-word mixing (xxHash64-style rounds) would need inline BCL-only implementation; deferred until we have a distribution-quality regression harness to stress-test it.

Two distinct concerns sit underneath:

1. **Throughput.** Byte-at-a-time hashing is measurably slower than word-wise. The 1.7.16 attempt won 12 % on 10 M bulk-load before clustering at 1 B. A SIMD-friendly hash (xxHash64-style rounds with proper per-word mixing) could plausibly recover that throughput without the clustering pathology.
2. **Distribution quality under realistic input.** The 1.7.16 disaster proved that hash quality on adjacent IRI strings (Wikidata's `wd:Q1`, `wd:Q2`, … `wd:Q138816518`) is the binding constraint, not the textbook avalanche tests. Any replacement must be tested against this exact distribution.

## Trigger condition

Multiple potential triggers, any one of which warrants promotion:

- **Throughput-driven**: Sustained ingest workloads where the byte-at-a-time hashing measurably bottlenecks write performance after ADR-028 (rehash) and ADR-030 (parallel rebuild + sort-insert) have removed the upstream constraints. Likely visible in profiles via time spent in `ComputeHash` exceeding some threshold (10 %? 15 %?) of bulk-load time.
- **Adversarial-input concern**: External input where IRI distributions are not guaranteed to have the friendly properties Wikidata's do. E.g., user-controlled IRIs in a multi-tenant context.
- **Regression-harness available**: ADR-030's Scope boundary explicitly defers this "until the regression-harness scaffolding exists." Once that scaffolding lands (likely as part of ADR-030 Phase 1 measurement infrastructure), this constraint is removed.

## Current state

Latent. The current FNV-1a byte-at-a-time hash works correctly at all measured scales (1 B Cognitive validated 2026-04-19; 100 M Turtle 2026-04-20). Throughput is acceptable at 331 K triples/sec bulk-load (1 B run) and 292 K triples/sec for Turtle (100 M run). No production workload has surfaced ComputeHash as a binding constraint.

The 1.7.16 disaster proved the danger of changing the hash without rigorous testing. The current cautious choice is the right one *until* we have the testing infrastructure to support a change.

## Candidate mitigations

When promoted, candidates to consider:

1. **xxHash64-style word-wise hash with proper per-word mixing.** Designed to avoid exactly the clustering pathology that broke 1.7.16. BCL-only implementation feasible (no dependencies). Word-aligned reads with per-word avalanche rounds; should give comparable throughput to the failed 1.7.16 attempt with markedly better distribution.
2. **SIMD-vectorized FNV variant.** Maintains FNV's known-good distribution properties on adjacent strings while processing multiple bytes per cycle via NEON / AVX intrinsics. Requires per-platform paths but no algorithmic change.
3. **Adopt `System.IO.Hashing.XxHash64`.** Available in BCL since .NET 6. Sky Omega's BCL-only constraint *permits* this (it is BCL). Loses the in-source visibility of the algorithm but gains MSFT-maintained correctness.

All three need the regression harness before adoption.

## Required prerequisite — regression harness

Per ADR-030 § Scope boundary: "A proper SIMD-friendly hash (xxHash64-style) requires [a regression harness]. That's a separate ADR once the regression-harness scaffolding exists."

The harness must cover:

- Adjacent-IRI distributions matching Wikidata's pattern (`wd:Q<sequential-integer>`).
- High-cardinality literal distributions (Wikidata's labels in 100+ languages per entity).
- Adversarial worst-case inputs designed to provoke clustering.
- Load-factor measurement at 1 B+ scale, not synthetic.

The harness is a prerequisite, not part of the hash-replacement work itself. It belongs in ADR-030 Phase 1 (measurement infrastructure) or as a separate small ADR if Phase 1 doesn't naturally absorb it.

## References

- Source: `src/Mercury/Storage/AtomStore.cs:502-525` (current FNV-1a + the explanatory comment block)
- `git log` commits: `dd041e9` (word-wise FNV introduction, 1.7.16), `2f7ea80` (revert, 1.7.18 series)
- [ADR-030 § Scope boundary](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) — explicit deferral statement
- [bulk-load-gradient-2026-04-17.md](../validations/bulk-load-gradient-2026-04-17.md) — the gradient run that surfaced Bug 5 (the original AtomStore overflow that motivated rehash work, related but distinct)
