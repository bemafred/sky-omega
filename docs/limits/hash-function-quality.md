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

1. **Adopt `System.IO.Hashing.XxHash3` (.NET 10 BCL, hardware-accelerated).** This is now the strongest candidate and Sky Omega's BCL-only constraint permits it — it's in BCL. On Apple Silicon the implementation emits NEON vectorized code (10-15 GB/s single-threaded); on x64 it uses AVX2/AVX-512 where available. Zero implementation work. MSFT-maintained correctness. By far the cleanest path once the regression harness exists.
2. **`System.IO.Hashing.XxHash64` (.NET 6+ BCL).** Same provenance, slightly older algorithm, same hardware acceleration. Marginally slower than XxHash3 but well-validated.
3. **`System.IO.Hashing.Crc32C` (BCL, uses ARMv8 CRC32 instruction / Intel CRC32 instruction).** ~20+ GB/s. But only 32-bit output and weaker avalanche — viable for a secondary "quick-check" hash layer but not as the primary 64-bit hash for a multi-billion-atom table.
4. **Hand-rolled wyhash (Wang Yi, 2019).** If we want source-visible per Sky Omega's transparency ethos, wyhash is ~100-200 lines of BCL C#, competitive with XxHash3 per recent SMHasher runs, and simpler than porting xxHash. Lower priority than #1 unless transparency trumps "just use BCL."
5. **SIMD-vectorized FNV variant.** Maintains FNV's known-good distribution properties on adjacent strings while processing multiple bytes per cycle via NEON / AVX intrinsics. Requires per-platform paths but no algorithmic change. Historical interest; #1 dominates.

All five need the regression harness before adoption (see below).

## Hardware acceleration on target platforms

| Hardware feature | Benefit | .NET 10 access |
|---|---|---|
| ARMv8 NEON SIMD (128-bit) | Parallel byte ops in `XxHash3` round function | `System.Runtime.Intrinsics.Vector128<T>`; used implicitly by `System.IO.Hashing.XxHash3` |
| ARMv8 CRC32 instructions | 20+ GB/s throughput | `System.IO.Hashing.Crc32C` or `System.Runtime.Intrinsics.Arm.Crc32` |
| ARMv8 AES instructions | AES-round-based hashing ("meow hash" pattern), 15-20 GB/s with good quality | `System.Runtime.Intrinsics.Arm.Aes` — direct intrinsic use |
| ARMv8 POPCNT | Fast bit counting (useful for perfect-hashing rank/select) | `System.Numerics.BitOperations.PopCount` |

**NVIDIA / Apple GPU: not applicable for atom interning.** Kernel dispatch cost + memory marshaling dwarfs hash computation for 4 small strings per triple. GPU hashing shines for bulk cryptographic operations, not fine-grained per-triple work. Minerva's LLM inference work is the right GPU target in Sky Omega.

## The bucket-probe ceiling

**Hash computation is not the dominant cost of `Intern`.** At 250 K triples/sec × 4 atoms/triple = 1 M hashes/sec on ~50-byte URIs — 50 MB/sec hashed. Any modern 64-bit hash handles this at a small fraction of its throughput ceiling.

The dominant cost is the **bucket probe**: at 4 B atoms in a 32 GB hash table, each probe misses L3 and hits main memory (~100 ns). Four probes per triple → 400 ns/triple memory latency floor, equivalent to ~2.5 M triples/sec ceiling from memory alone.

Consequence: a free 100 GB/s hash saves maybe 10-20% of the total `Intern` cost. The remaining 80% is the bucket memory access.

Larger wins require addressing the probe pattern, not the hash function:

- **Software prefetching.** Issue `System.Runtime.Intrinsics.Arm.ArmBase.Prefetch1` at the bucket address several hundred ns before reading. Overlaps memory fetch with other work. Well-applied in a batched Intern loop, plausibly recovers 30-50% of cache-miss cost.
- **Pipelined batch intern.** For bulk-load, process N atoms in a shifted pipeline: hash + prefetch atom k+1 while comparing atom k while writing atom k-1. Hides memory latency via ILP. More invasive refactor but 2-3× throughput is plausible.
- **Eliminate the hash probe entirely for Reference.** See [sorted-atom-store-for-reference](sorted-atom-store-for-reference.md) — sorted vocabulary + perfect hashing moves to O(1) deterministic lookup with no chain traversal.

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
