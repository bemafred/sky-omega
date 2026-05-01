# Limit: AtomStore does not eagerly prefix-compress

**Status:**        Resolved (shipped 2026-05-01 in ADR-034 Round 2)
**Surfaced:**      2026-04-30, via the QLever-comparison conversation captured in `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`.
**Resolved:**      2026-05-01, commit `870d31b`. Implementation: delta-encode each atom against its predecessor in sort order; anchor every 64th atom for bounded reconstruction (≤63 byte-copies per `GetAtomSpan` on a compressed atom). Anchors return zero-copy spans over the mmap'd file; compressed atoms reconstruct into a thread-local buffer. Measured 53% atoms.atoms size reduction at 1M Wikidata (14.86 MB → 6.97 MB). Projected ~75 GB memory recovery at full 21.3B Wikidata.
**Last reviewed:** 2026-05-01

## Resolution outcome

The implementation followed the cheapest of the candidate mitigations: delta-encode in `SortedAtomStoreExternalBuilder.MergeAndWrite` with an anchor every 64 atoms. No prefix dictionary, no trie, no frequency-weighted clustering — just (prefix_len, suffix_bytes) per entry, 1-byte header.

Measured against QLever's published 45% vocabulary reduction: Mercury achieves ~53% on the same Wikidata-shape data. The delta is likely from anchor-interval choice — QLever anchors more aggressively (or differently) for tighter random-access; Mercury's 64-anchor balances size vs reconstruction cost.

The format change is breaking (atoms.atoms file format header change) — existing SortedAtomStore on-disk artifacts must be rebuilt. Per ADR-007 sealed-substrate-immutability, Reference stores are re-rendered from source; this aligns with the substrate's design.

## Description

Mercury's atom store interns each unique RDF term as an opaque byte sequence indexed by a hash table (`HashAtomStore`) or, soon, a sorted alphabetical structure (`SortedAtomStore`, ADR-034 in flight). Both store the full text of every term — a 30-byte common URI prefix is stored once per term that uses it, not once per prefix family.

Wikidata is the canonical case where this matters. The full `latest-all.ttl` vocabulary at 21.3 B triples contains ~3.4 B unique terms (entities, properties, statement nodes, references, values). Sample of common prefixes:

```
http://www.wikidata.org/entity/                           (≈30 bytes, ~all entities)
http://www.wikidata.org/prop/direct/                      (≈37 bytes, ~all wdt: properties)
http://www.wikidata.org/prop/                             (≈30 bytes, ~all p: properties)
http://www.wikidata.org/prop/statement/                   (≈40 bytes, ~all statement-value paths)
http://www.wikidata.org/prop/qualifier/                   (≈40 bytes, ~all qualifiers)
http://www.wikidata.org/prop/reference/                   (≈40 bytes, ~all references)
http://www.wikidata.org/value/                            (≈29 bytes, ~all wdv: values)
http://wikiba.se/ontology#                                (≈26 bytes, schema vocabulary)
http://www.w3.org/1999/02/22-rdf-syntax-ns#               (≈42 bytes, RDF core)
http://www.w3.org/2001/XMLSchema#                         (≈30 bytes, datatypes)
```

QLever's published vocabulary handling (Bast et al., the QLever Wikidata paper) achieves a **~45% memory reduction** on Wikidata via greedy common-prefix detection. The implementation: at vocabulary-build time, identify recurring prefixes by frequency-weighted greedy clustering, assign each prefix a short ID, store each term as `(prefix_id, suffix_bytes)` instead of `full_bytes`.

On Mercury at 21.3 B Wikidata: ~3.4 B terms × ~50 bytes average = ~170 GB of vocabulary text in memory if eagerly held. A 45% reduction recovers ~75 GB. On a 128 GB machine, that is the difference between vocabulary fitting comfortably in RAM and needing partial-merge tricks (split builds, paged merges, swap pressure) to stay under the limit.

## Trigger condition

This limit moves toward an ADR when one of:

1. **ADR-034 SortedAtomStore lands and validates.** Sorted vocabulary creates natural prefix locality — adjacent terms share long prefixes by alphabetical adjacency. The implementation of prefix compression on top of sorted storage is much cheaper than on hash storage: delta-encode each atom against the previous atom in sort order, store only the divergence point and suffix bytes. This is essentially how prefix-compressed B+Tree leaves work in classic database engines. Wait for ADR-034, then build prefix compression on top.
2. **Memory pressure on a target workload becomes binding.** Cognitive workloads with 10⁸+ triples may hit a vocabulary ceiling well below Wikidata's. The `Sample test` is `RSS > 0.6 × machine_RAM during ingest, attributable to atom-store memory`.
3. **A vocabulary-phase external benchmark publication.** When publishing comparison numbers vs QLever on Wikidata-shaped workloads, the asymmetry must be either closed (this optimization shipped) or disclosed (footnote that Mercury's vocabulary phase is uncompressed).

## Current state

Mercury 1.7.47 stores all atoms full-text. `HashAtomStore` (the current default) hashes each term and stores it in a contiguous append-only byte arena. `SortedAtomStore` (ADR-034, in flight) lays terms out in alphabetical order in `{base}.atoms` with `{base}.offsets` for binary-search lookup — also full-text, no compression.

This is the right starting point: correctness first, optimization measured. The 85h Phase 6 Wikidata run completed without prefix compression; the optimization is "future wall-clock" not "current blocker." But the gap is large enough (~75 GB at full Wikidata) and the implementation cost is low enough (especially on top of SortedAtomStore) that it is a clear next-round candidate.

## Candidate mitigations

1. **Wait for ADR-034 SortedAtomStore.** Sorted alphabetical layout provides the substrate on which prefix compression is naturally cheap. Implement after ADR-034's gradient validation lands.
2. **Delta-encode in SortedAtomStore.** During the bulk-build sort phase (after vocabulary is sorted but before the offsets file is written), compute the longest common prefix between each atom and its predecessor. Store `(prefix_length, suffix_bytes)` instead of `full_bytes`. Lookup cost: walk back to the most recent atom with `prefix_length == 0` (a "fully-stored" anchor), reconstruct forward. Anchor every N atoms (e.g. N=64) to bound reconstruction cost.
3. **Greedy frequency-weighted prefix dictionary.** QLever-style: scan the sorted vocabulary, identify recurring prefixes by frequency × length, assign each a short ID, store atoms as `(prefix_id, suffix_bytes)`. More aggressive than (2); bigger wins, more implementation cost. Reasonable second-round on top of (2) if (2)'s wins are insufficient.
4. **Trie-backed lookup.** Most aggressive form: store the vocabulary as a compressed trie (radix tree). Lookup is O(term length) with prefix sharing. Highest implementation cost; least clear cost-benefit on top of a sorted+delta-encoded structure.

## Implementation note: composes with ADR-034

The natural sequencing is: (1) ADR-034 ships SortedAtomStore with full-text storage; (2) gradient validation confirms the sorted layout's correctness and read-side performance; (3) prefix compression lands on top as a build-time and read-time delta-encoding pass. The build-time pass adds one chunk-sort phase; the read-time pass adds one anchor-walk per binary-search hit (typically 0–63 byte-copies, anchor every 64).

Without ADR-034, prefix compression on `HashAtomStore` is much harder — hash storage doesn't provide the alphabetical adjacency that makes delta-encoding work. The dependency is explicit: don't promote this limit to ADR until ADR-034 validates.

## Why this matters beyond the memory recovery

The 75 GB gap is the headline. Two secondary effects:

1. **Cache locality.** Smaller vocabulary ⇒ more terms fit in L2/L3 ⇒ faster intern() during ingest, faster lookup() during query. Hard to quantify without measurement, but the direction is unambiguous.
2. **Comparison framing.** External benchmarks of vocabulary phase against QLever currently overstate Mercury's memory cost by ~45%. Closing the gap eliminates a footnote and removes a credibility risk on published numbers.

## References

- `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md` — surfacing memo
- ADR-034 (mercury) — SortedAtomStore, the prerequisite for cheap prefix compression
- `docs/limits/sorted-atom-store-for-reference.md` — sibling entry covering ADR-034's broader scope; this entry is about a specific composable optimization on top of it
- `src/Mercury/Storage/SortedAtomStore.cs`, `SortedAtomBulkBuilder.cs` — the layer where this would land
- QLever Wikidata paper (Bast et al.) — published 45% vocabulary reduction reference
