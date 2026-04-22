# Limit: Hash-based atom store is profile-generic; Reference could use a sorted vocabulary

**Status:**        Latent
**Surfaced:**      2026-04-22, during Phase 6 21.3B Wikidata bulk-load when rate-drift analysis pointed at atom-store hash cache pressure as the dominant contributor
**Last reviewed:** 2026-04-22
**Promotes to:**   ADR when (a) atom-store hash cache pressure becomes the binding constraint on Reference bulk-load throughput, OR (b) a concrete motivating workload wants Reference-profile performance past the 21.3B Wikidata scale, OR (c) query-time vocabulary compactness becomes load-bearing for memory footprint

## Description

Mercury today uses a single `AtomStore` implementation — an mmap-backed open-addressing hash table with rehash-on-grow (ADR-028) — across all store profiles. The hash is necessary for **Cognitive / Graph / Minimal** profiles, which accept incremental `Intern(string)` writes throughout the session lifetime.

The **Reference** profile has fundamentally different semantics: per [ADR-026](../adrs/mercury/ADR-026-bulk-load-path.md) and [ADR-029](../adrs/mercury/ADR-029-store-profiles.md), Reference is dump-sourced and read-only after bulk-load. The "update" model is "delete the store and re-bulk-load." Under that contract, the atom store is effectively read-only after the bulk phase — but the current implementation still pays the hash table's full cost during bulk (probing, rehash events) and after (bucket overhead, load factor waste).

**QLever's approach** (Hannah Bast, Univ. Freiburg) for the same structural constraint: a **sorted vocabulary** built via external merge-sort during bulk-load. No hash table. String→ID lookup is O(log N) binary search on a compact, cache-friendly sorted file. The architecture exploits the read-only invariant that Mercury's Reference profile also has.

## Parallel to ADR-029's existing profile dispatch

ADR-029 already dispatches index layout on profile:

| Profile | Index |
|---|---|
| Cognitive / Graph / Minimal | `TemporalQuadIndex` (88 B keys with valid-time) |
| Reference | `ReferenceQuadIndex` (32 B keys, no temporal) |

The same mechanism could extend to the atom store:

| Profile | Atom store |
|---|---|
| Cognitive / Graph / Minimal | `HashAtomStore` (current — incremental Intern) |
| Reference | `SortedAtomStore` (new — external-sort built, binary-search lookup) |

## What a `SortedAtomStore` looks like concretely

**Bulk-load path** (piggybacks ADR-033's `ExternalSorter`):

1. Stream triples. For each atom string, emit `(stringToken, triple-index, position)` to an external sorter keyed on `stringToken`.
2. External merge-sort. Walk the sorted stream; emit each unique string to a sorted-strings file with sequentially assigned ID (0, 1, 2, ...).
3. Re-sort the `(triple-index, position) → id` records back into triple order.
4. Emit final `(G, S, P, O)` triples with resolved IDs into ADR-033's existing GSPO sorter.

This is two external sorts instead of one, but both are sequential I/O. At 21.3B scale the extra sort adds ~1-2h; in exchange, the atom-store hash drift (currently ~30% throughput loss from peak-to-steady-state during Phase 6) disappears entirely.

**Query-time lookup:**

- String → ID: binary search on the sorted strings file. `log2(N)` probes. For N ≈ 4B unique atoms at Wikidata scale, that's ~32 probes, each hitting cache-line-sized chunks with predictable addresses. Typical latency: ~500 ns warm, ~2 µs cold — comparable or better than a hash probe once the hash table exceeds L3.
- ID → String: direct offset lookup in a paired offsets file. O(1), same as current.

**Storage compactness:**

- Sorted strings file is prefix-compressible. A MARISA trie (or simpler prefix-shared layout) typically achieves 30-50% space reduction vs the current AtomStore data file.
- No hash table overhead: current BulkMode allocates 8 GB sparse hash table (256M buckets × 32 B). `SortedAtomStore` replaces this with just sorted strings + offsets — no buckets, no load-factor waste.

## Composability with other limits

Three existing register entries cross-reference into this one:

- **[hash-function-quality](hash-function-quality.md)**: becomes profile-conditional. Only Cognitive / Graph / Minimal need a better hash; Reference moves off hashes entirely.
- **[bit-packed-atom-ids](bit-packed-atom-ids.md)**: sorted vocabulary assigns dense sequential IDs 0..N-1. At 21.3B scale, unique atom count is ~4B, fits in 32 bits. A ReferenceKey could shrink from 32 B (four 8-byte IDs) to 16 B (four 4-byte IDs) — halving GSPO storage and radix sort cost.
- **[bulk-load-memory-pressure](bulk-load-memory-pressure.md)**: eliminates the 8 GB sparse hash table from the resident footprint during Reference bulk-load. Compressor pressure and swap risk drop accordingly.

## Diff ingestion and the read-only trade-off

Sorted vocabulary is fundamentally bulk-oriented. Adding a single string after the vocabulary is built would shift every subsequent ID and invalidate every triple that used a shifted ID. The standard workaround — which QLever uses — is **delta-plus-merge** (LSM-tree pattern):

- Main: sorted vocabulary, IDs 0..N-1, read-only
- Delta: small mutable structure (hash or small sorted-append), IDs N+1..
- Lookup: binary search main, fall back to delta
- Periodic merge: sort delta into main, rewrite triples with ID remap

Mercury's Reference profile currently has no diff-ingest capability (ADR-026 "delete and retry" is the update model). Moving to `SortedAtomStore` therefore **loses no existing capability**. Future diff-ingest for Reference would be a separate ADR; it'd build the delta layer on top of the sorted vocabulary rather than reverting to the hash approach.

Cognitive stays hash-based because the delta-plus-merge pattern is overhead the incremental-writes use case doesn't want to pay.

## Trigger condition

Promote to ADR when any of:

- Atom-store hash cache pressure is the binding constraint on Reference bulk-load wall-clock after ADR-032/033 have shipped (Phase 6 shows this is now the dominant contributor to rate drift).
- A target workload exceeds full Wikidata 21.3B and the combined 8 GB hash + ADR-028 rehash events become materially expensive.
- Query-time memory footprint for a Reference store becomes load-bearing — sorted + prefix-compressed vocabulary can be 30-50% smaller than the current hash-backed layout.
- The Mercury.Compression package work proceeds to where a MARISA trie implementation is a small marginal cost.

## Current state

Latent. Phase 6 21.3B Wikidata is running against the hash-based atom store and will complete in the ~32h envelope without the sorted vocabulary. The drift is characterized but not binding. The ADR-033 validation arc just established the pattern ("external sort + sequential append + profile dispatch") that a `SortedAtomStore` would follow — the groundwork is laid but the decision to ship is not made.

## Candidate implementations (not yet characterized)

1. **Sorted strings + sparse offsets index**. Simplest: one file of concatenated length-prefixed UTF-8 strings in sort order, one file of offsets (one 8-byte offset per string). Binary search on offsets + memcmp on string bytes. No dependencies beyond BCL.
2. **MARISA-trie-style prefix-shared structure**. More compact (30-50% smaller) but BCL-only implementation non-trivial. Could live in `Mercury.Compression` if added.
3. **ART (Adaptive Radix Tree)**. Balanced performance/size. Faster than binary search for common prefixes. Again, BCL-only implementation non-trivial.

## Phase 2 refinement: minimal perfect hashing (MPHF) on the finalized vocabulary

The sorted-vocabulary approach (Phase 1) gives O(log N) lookup via binary search — cache-friendly but still log(N) probes. Once the vocabulary is finalized and immutable, we can do strictly better: build a **minimal perfect hash function** over the known N strings. O(1) deterministic lookup with zero collisions, ever.

### What an MPHF is

Given N known keys, an MPHF `h(key) → {0, 1, …, N-1}` maps each key to a unique slot. No collisions. Space overhead is **1.5 – 3 bits per key** depending on the algorithm (vs 32 B/bucket for open-addressing hash tables). The catch: the MPHF can only be constructed when the complete key set is known. You cannot add a key without rebuilding the entire MPHF (or layering a delta — see "Diff ingestion" section above).

**This constraint is precisely why MPHF fits the Reference profile's read-only-after-bulk contract.** Once the external merge sort finalizes the vocabulary, the set is closed.

### Algorithms

| Algorithm | Authors, year | Bits/key | Implementation complexity |
|---|---|---:|---|
| **CHD** (Compress, Hash, Displace) | Belazzougui, Botelho, Dietzfelbinger, 2009 | ~1.6 | Two-level with displacement array. Denser, harder. |
| **BBHash** (BooHash) | Limasset, Rizk, Chikhi, 2017 | ~3.0 | Stacked bit arrays. Simplest. Best fit for BCL-only C#. |
| **RecSplit** | Esposito, Graf, Vigna, 2020 | ~1.5 | Current state-of-the-art space-wise. More complex. |

**BBHash** is the recommended starting point: conceptually simple (level-by-level bitmap marking), implementable in a few hundred lines, uses popcount for rank/select (native ARM/x64 instruction via `System.Numerics.BitOperations.PopCount`).

### Compose with Phase 1

Phase 1 alone → sorted vocabulary file + offsets, binary-search lookup.
Phase 2 adds → `atoms.mphf` file, ~1 GB at Wikidata 4B-atom scale.

At query time:
- `StringToId(key)`: evaluate MPHF → get slot N → read string at sorted-vocab[N] → strcmp to confirm key membership (MPHF maps *in-domain* keys correctly; out-of-domain keys return *some* slot, hence the membership check).
- `IdToString(id)`: direct offset lookup, unchanged from Phase 1.

One MPHF evaluation (a handful of hash calls and bit-array lookups, ~50-100 ns) + one cache-line read at the vocab slot + strcmp. Replaces ~32 binary-search probes (~500 ns warm, ~2 µs cold).

### Non-member validation

MPHF alone does NOT answer "is this string in the vocabulary?" — it maps *any* bit pattern to *some* slot. Validation requires comparing against the string stored at that slot. Mercury's SortedAtomStore stores strings anyway (to support `IdToString`), so this check is zero additional storage and adds only the strcmp cost. No separate membership filter (Bloom, etc.) needed.

### Space comparison at Wikidata 4B atoms

| Structure | Size |
|---|---:|
| Current hash table (BulkMode, 256M buckets × 32 B, sparse) | 8 GB |
| Phase 1 sorted-vocab offsets (4B × 8 B) | 32 GB |
| Phase 2 BBHash MPHF on top of Phase 1 | **~1.5 GB additional** |

The MPHF is essentially free compared to the vocabulary itself. The "10× smaller than the current hash table" headline comes from dropping bucket overhead, not from the MPHF specifically.

### References for the MPHF literature

- **Fredman, Komlós, Szemerédi** (1984): *"Storing a Sparse Table with O(1) Worst Case Access Time"*. The foundational theorem — O(1) lookups on a static set with linear space.
- **Belazzougui, Botelho, Dietzfelbinger** (2009): CHD.
- **Limasset, Rizk, Chikhi** (2017): BBHash.
- **Esposito, Graf, Vigna** (2020): RecSplit.
- Mehlhorn, K. (1984): *"Data Structures and Algorithms"* Vol. 1 — textbook context.

Knuth TAOCP Vol 3 §6.4 mentions perfect hashing briefly but predates the modern MPHF algorithms — the craft lives in the 1984 → 2020 papers above.

### Phase progression

The SortedAtomStore limit becomes ADR-034 (or similar) when triggered, with internal phases:

1. **Phase 1**: Sorted vocabulary + binary-search lookup. Most of the structural win (eliminate hash drift, 10× smaller, enable bit-packed IDs).
2. **Phase 2**: BBHash MPHF on top of Phase 1. Tightens query latency from O(log N) to O(1). Adds ~1.5 GB to the structure.
3. **Phase 3 (speculative)**: Delta-plus-merge layer for incremental writes, if future workloads need it. Would be its own ADR.

Phase 1 alone is probably sufficient for Wikidata 21.3B query workloads (32 binary-search probes at 500 ns warm = ~16 µs per lookup is not the bottleneck anywhere). Phase 2 becomes relevant when query-time atom lookup latency becomes load-bearing — unusual but possible for tight query loops.

## References

- [ADR-026](../adrs/mercury/ADR-026-bulk-load-path.md) — bulk-load "delete and retry" contract.
- [ADR-028](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md) — current atom store's rehash machinery, which a sorted vocabulary obsoletes for Reference.
- [ADR-029](../adrs/mercury/ADR-029-store-profiles.md) — establishes the profile-dispatch pattern that this limit extends to the atom store.
- [ADR-032](../adrs/mercury/ADR-032-radix-external-sort.md) / [ADR-033](../adrs/mercury/ADR-033-bulk-load-radix-external-sort.md) — the external-sort architecture that `SortedAtomStore`'s bulk-build path would piggyback on.
- Bast, H. et al., "QLever: A Query Engine for Efficient SPARQL+Text Search" and subsequent papers — external reference for the sorted-vocabulary approach.
