# ADR-034: SortedAtomStore for Reference Profile

## Status

**Status:** Accepted — 2026-04-26 · Phase 1 Completed 2026-05-01 (commits `d832702` Phase 1B-5d, `7660bb4` production wiring, `3b49ce2` session-scoped lifecycle, `ba90595` Phase 1B-5e streaming input, `bd2f9c1` int64 InputIdx widening, `870d31b` Round 2 prefix compression). Validated end-to-end at 1B Wikidata Reference (1:00:36 wall-clock; 213M atoms; 991M triples stored). Phase 2 (BBHash MPHF) and bit-packed atom IDs both remain deferred per evidence-first ([1B trace memo](../../../memos/2026-05-01-1b-flushtodisk-trace-analysis.md)): GSPO write is 0.45% of FlushToDisk; resolver drain is 0.92%. The architectural cost is dominated by sequential file I/O, not algorithm.

## Context

Phase 6 (21.3 B Wikidata, 1.7.44) exposed atom-store hash drift as the dominant single rate contributor to Reference bulk-load wall-clock. Peak rate of ~210 K triples/sec degraded to a steady-state of ~95 K triples/sec — a ~30 % loss correlated with growing hash-table probe distance and rehash cadence (ADR-028 events) as unique atom count crossed 4 B. The hash machinery is structurally necessary for the **Cognitive / Graph / Minimal** profiles, which accept incremental `Intern(string)` writes throughout the session lifetime. It is structurally **un**necessary for the **Reference** profile, which under ADR-026 + ADR-029 is dump-sourced and whose mutations occur only via bulk-load (ADR-029 Decision 7).

QLever (Bast et al., Univ. Freiburg) addresses the same constraint with a **sorted vocabulary** built via external merge-sort during bulk-load. No hash table at any phase. String → ID lookup is `O(log N)` binary search on a compact sorted file; ID → String is direct offset lookup. The architecture exploits exactly the read-after-bulk invariant Mercury's Reference profile already declares.

This ADR extends ADR-029's profile-dispatch pattern to the atom store. Today ADR-029 dispatches on index layout:

| Profile | Index |
|---|---|
| Cognitive / Graph / Minimal | `TemporalQuadIndex` (88 B keys with valid-time) |
| Reference | `ReferenceQuadIndex` (32 B keys, no temporal) |

ADR-034 adds the same dispatch to atom-store layout:

| Profile | Atom store |
|---|---|
| Cognitive / Graph / Minimal | `HashAtomStore` (current — incremental Intern, ADR-028 rehash) |
| Reference | `SortedAtomStore` (new — external-sort built, binary-search lookup) |

The full motivation, MPHF literature survey, and space-comparison numbers are in [`docs/limits/sorted-atom-store-for-reference.md`](../../limits/sorted-atom-store-for-reference.md). This ADR is the integration decision; the limits document remains the architectural reference.

### Coupling with ADR-035

ADR-035 (Phase 7a metrics infrastructure) is the gate. Phase 7a.3 ships the atom-store metric surface (Category B: Intern rate, hash load factor, probe-distance histogram, rehash events, file growth). The before-baseline for ADR-034 is a 1 B Reference bulk-load with `HashAtomStore` + Phase 7a.3 instrumentation, captured as a sealed JSONL artifact. Without that baseline, ADR-034's claim is unfalsifiable. Sequencing: ADR-035 Phase 7a.3 ships → before-baseline captured → ADR-034 Phase 1 ships → after-baseline captured → comparison published.

### Constraint from ADR-029 Decision 7

ADR-029 Decision 7 allows incremental bulk-append to Reference: "Subsequent bulk loads append to the GSPO/GPOS indexes." A sorted vocabulary cannot honor this without a delta-plus-merge layer (LSM-tree pattern: main sorted vocab + small mutable delta + periodic merge). Phase 1 of this ADR ships the sorted vocab without the delta layer; Phase 3 (deferred to a separate future ADR) addresses appendable-Reference. **For SortedAtomStore stores, ADR-029 Decision 7's appendable-bulk semantic is suspended** — explicitly narrowed in Decision 7 below.

## Decision

### 1 — `SortedAtomStore` class

New class `Mercury.Storage.SortedAtomStore` implementing the same `IAtomStore` surface as `HashAtomStore`:

```csharp
internal interface IAtomStore : IDisposable
{
    long InternUtf8(ReadOnlySpan<byte> utf8);    // string → atom id
    ReadOnlySpan<byte> GetUtf8(long atomId);     // atom id → string
    long AtomCount { get; }
    void FlushToDisk();
}
```

Storage layout:

- `atoms.sorted` — concatenated length-prefixed UTF-8 strings in sort order, ~4 B atoms × ~50 B avg = ~200 GB at Wikidata scale
- `atoms.offsets` — one 8-byte offset per atom, dense sequential atom IDs 0..N-1, 32 GB at 4 B atoms
- `atoms.mphf` — Phase 2 only; ~1.5 GB BBHash structure

`HashAtomStore` is renamed from `AtomStore`. The current `AtomStore` becomes a profile-dispatching factory.

### 2 — Profile dispatch (parallel to ADR-029 indexes)

`QuadStore.Open(...)` reads the profile from `store-schema.json` (ADR-029 Decision 5) and constructs the appropriate atom-store implementation. No runtime switching; no shared state; no shared file format. Mismatch (e.g., opening a store with `SortedAtomStore` files using a build that only knows `HashAtomStore`) is a hard error on open, mirroring ADR-029 Decision 4.

### 3 — Bulk-load: two-pass via ADR-033 `ExternalSorter`

Reference bulk-load with `SortedAtomStore` becomes a two-pass operation, both passes piggybacking ADR-033's `ExternalSorter` infrastructure:

**Pass 1 — vocabulary build.** Stream triples; for each atom string, emit `(stringToken, triple-index, position)` to `ExternalSorter<StringRecord>` keyed on `stringToken`. External merge-sort. Walk the sorted stream; emit each unique string to `atoms.sorted` with sequentially assigned ID. Emit `(triple-index, position) → id` records to a second sorter keyed on `(triple-index, position)`.

**Pass 2 — triple resolution.** Walk the second sorter's output in triple-index order, reconstructing each triple as `(G, S, P, O)` with resolved IDs, and feed into ADR-033's existing GSPO sorter.

Both passes are sequential I/O. At 21.3 B scale the extra pass adds an estimated ~1–2 h; the elimination of hash drift recovers ~30 % of bulk-load wall-clock — a net win even before MPHF (Phase 2).

### 4 — Query-time lookup

**String → ID (Phase 1):** binary search on `atoms.offsets` + `memcmp` on `atoms.sorted`. `log2(N)` probes; for N ≈ 4 B, ~32 probes hitting cache-line-sized chunks at predictable addresses. Typical latency: ~500 ns warm, ~2 µs cold — comparable or better than a hash probe once the hash table exceeds L3.

**String → ID (Phase 2):** evaluate BBHash → slot N → read string at `atoms.sorted[atoms.offsets[N]]` → `memcmp` to confirm in-domain membership. ~50–100 ns MPHF + one cache-line read + memcmp. Replaces ~32 binary-search probes.

**ID → String (both phases):** direct offset lookup in `atoms.offsets`, identical to `HashAtomStore`'s ID-side lookup. `O(1)`.

### 5 — Dense sequential atom IDs (0..N-1)

Sorted vocabulary assigns IDs in sort order: 0, 1, 2, …, N-1. This is the enabling capability for [`docs/limits/bit-packed-atom-ids.md`](../../limits/bit-packed-atom-ids.md): at 21.3 B Wikidata scale, unique atom count ~4 B fits in 32 bits, allowing `ReferenceKey` to shrink from 32 B (four 8-byte IDs) to 16 B (four 4-byte IDs). That ADR is a separate follow-on; ADR-034 unblocks it but does not implement it.

`HashAtomStore` continues to assign IDs by insertion order; its IDs remain 64-bit. Profile dispatch keeps these regimes isolated.

### 6 — Phase progression

- **Phase 1 — sorted vocab + binary search.** Most of the structural win (eliminates hash drift, drops 8 GB hash table to zero, enables Decision 5's dense IDs).
- **Phase 2 — BBHash MPHF.** O(1) lookup. ~1.5 GB additional structure. Construction ~1–2 h at 21.3 B scale.
- **Phase 3 — deferred.** Delta-plus-merge layer for appendable Reference. Separate future ADR. Triggered when an appendable-Reference workload becomes load-bearing.

BBHash chosen over CHD (Belazzougui/Botelho/Dietzfelbinger 2009, ~1.6 bits/key) and RecSplit (Esposito/Graf/Vigna 2020, ~1.5 bits/key) because BBHash's stacked-bit-array structure is implementable in a few hundred lines of BCL-only C# using `System.Numerics.BitOperations.PopCount`. CHD's displacement array and RecSplit's recursive splitting are space-tighter but materially harder to ship. Space cost difference at 4 B atoms: BBHash ~1.5 GB vs RecSplit ~750 MB — not load-bearing against the ~200 GB vocabulary file.

### 7 — Single-bulk-load contract for SortedAtomStore stores

ADR-029 Decision 7 ("Subsequent bulk loads append to the GSPO/GPOS indexes") is **suspended** when the profile selects `SortedAtomStore`. Specifically:

- A SortedAtomStore-backed Reference store is **read-only** after the first bulk-load completes and `FlushToDisk()` lands.
- Subsequent `mercury --bulk-load` invocations against an existing SortedAtomStore-backed Reference store fail at plan time with a clear error: "SortedAtomStore stores are single-bulk-load. To replace, recreate the store; to add a delta layer, see ADR-034 Phase 3 (deferred)."
- The "delete and reload" model (ADR-026) is the operational update path. Wikidata's full-dump cadence accommodates this; incremental Wikidata-style updates are not supported until Phase 3 ships.

This is a deliberate narrowing. It surfaces immediately at store-open, not silently during a second bulk attempt. The Cognitive/Graph/Minimal profiles retain ADR-029 Decision 7's appendable-bulk semantic via `HashAtomStore`.

### 8 — Metrics emission via ADR-035 umbrella

`SortedAtomStore` emits to `IObservabilityListener` (ADR-035 Decision 1). New event methods:

- `OnVocabularyBuildPhase(...)` — emission/drain progress for Pass 1 (vocabulary build) and Pass 2 (triple resolution)
- `OnVocabularySize(...)` — final unique-atom count, total bytes, build wall-clock
- `OnMphfBuildEvent(...)` — Phase 2 only; level-by-level construction progress and final size

Probe-distance histogram (the Phase 7a.3 atom-store metric) becomes profile-conditional: emitted only when the active atom store is `HashAtomStore`. SortedAtomStore emits a `binary-search-depth-histogram` instead (Phase 1) and an `mphf-eval-latency-histogram` (Phase 2) — both consume `LatencyHistogram` (ADR-035 Decision 2).

The before-baseline (HashAtomStore at 1 B Reference) and after-baseline (SortedAtomStore at 1 B Reference) are sealed as JSONL artifacts under `docs/validations/` per the ADR-035 close-out pattern.

## Consequences

### Positive

- **Eliminates hash drift on Reference bulk-load.** Phase 6 measured ~30 % throughput loss peak-to-steady-state; SortedAtomStore removes the mechanism. Conservative estimate: **bulk-load drops 1.4×** at 21.3 B scale; the cold WDBench latencies improve secondarily (smaller resident footprint, better cache).
- **8 GB hash table removed from resident footprint.** Current `BulkMode` allocates a 256 M-bucket × 32 B sparse mmap. SortedAtomStore replaces this with sorted strings + offsets — no buckets, no load-factor waste. Composes with [`docs/limits/bulk-load-memory-pressure.md`](../../limits/bulk-load-memory-pressure.md) to drop swap risk on smaller hosts.
- **Enables dense 32-bit atom IDs.** Decision 5 unblocks the `bit-packed-atom-ids` limits entry. At 21.3 B Wikidata, `ReferenceKey` shrinks from 32 B to 16 B — halves GSPO storage and radix sort cost.
- **ADR-028 rehash machinery becomes profile-conditional.** No rehash events on Reference bulk-load. ADR-028 stays unchanged for Cognitive/Graph/Minimal.
- **MPHF (Phase 2) gives O(1) lookup with strcmp-based membership.** No separate Bloom filter or membership structure needed; vocabulary's sorted strings already serve as the membership oracle.
- **Architectural symmetry with ADR-033.** Bulk-load already passes through `ExternalSorter` for the GSPO write; vocabulary build adds two more passes through the same primitive. No new sort-machinery abstractions.

### Negative

- **Two external sorts vs one.** Pass 1 (vocab) + Pass 2 (triple resolve) replaces the single GSPO sort. Estimated wall-clock cost ~1–2 h at 21.3 B scale; recovered by the hash-drift elimination and then some.
- **Appendable-Reference lost (Phase 1).** ADR-029 Decision 7's incremental-bulk semantic is suspended for SortedAtomStore stores. Documented narrowing; Phase 3 restores it via delta-plus-merge if and when an appendable-Reference workload arrives.
- **Profile-conditional code paths grow.** `QuadStore.Open` already dispatches indexes on profile; now it dispatches atom store too. Each future feature touching atom-store semantics must consider both implementations. Mitigation: shared `IAtomStore` interface narrows the surface; profile dispatch happens once, at open.
- **No mid-load query visibility.** During Pass 1, the vocabulary is incomplete; during Pass 2, GSPO is being assembled. No partial-result queries against an in-progress bulk. Matches today's `--bulk-load` flow already disabling queries.
- **Phase 2 BBHash adds ~1–2 h to bulk close.** Construction runs after Pass 2 completes. Skippable via `--no-mphf` for time-sensitive loads where binary-search lookup is acceptable.

### Risks

- **BBHash correctness.** A BCL-only implementation can drift from reference BBHash on edge cases (level-overflow, hash-function quality). Mitigation: validation test against reference percentiles on a synthetic 100 M key set before Phase 2 ships at 21.3 B.
- **Vocabulary build memory pressure.** Pass 1's external sorter holds chunks of `(stringToken, triple-index, position)` records. At 21.3 B scale this is ~340 GB temp — covered by ADR-033's `--min-free-space` discipline but the floor is higher than ADR-033's 680 GB GSPO-only worst case. Document the bumped temp requirement (~1 TB total during bulk).
- **Profile-mismatch on existing stores.** A user who creates a SortedAtomStore Reference store and later tries to migrate to HashAtomStore (e.g., to enable appendable-bulk) must recreate the store — there is no in-place migration. Documented in Decision 7's error message and in `docs/architecture/technical/`.
- **Suspending ADR-029 Decision 7 surprises a user.** A user familiar with appendable-Reference behavior (Decision 7) attempts a second bulk-load and gets an error. Mitigated by the explicit error message; documented in ADR-029's amendment when ADR-034 ships.

## Implementation plan

**Phase 1 — SortedAtomStore (vocab + binary search)**
- `IAtomStore` interface extracted from current `AtomStore`; `HashAtomStore` renamed.
- `SortedAtomStore` class with two-pass bulk-build via `ExternalSorter<StringRecord>` and `ExternalSorter<TripleResolveRecord>`.
- `QuadStore.Open` profile dispatch on atom store.
- `store-schema.json` schema bumped to record atom-store implementation.
- Decision 7 enforcement: second bulk-load against SortedAtomStore fails at plan time.
- Tests: 1 M / 10 M / 100 M / 1 B Reference gradient with both atom stores; query-correctness equivalence; `IdToString` and `StringToId` round-trip; profile-mismatch error path.
- Status Proposed → Accepted after 1 B Reference equivalence + measurable hash-drift elimination on the after-baseline.

**Phase 2 — BBHash MPHF**
- `Mercury.Storage.BBHash` BCL-only implementation (~300–500 LoC + tests).
- BBHash validation harness against reference percentiles on synthetic 100 M-key distribution.
- `SortedAtomStore` Phase-2 lookup path (MPHF eval + memcmp confirmation).
- `--no-mphf` flag for skippable construction.
- Tests: 100 M / 1 B Reference gradient with MPHF; lookup-latency histogram comparison vs Phase 1 binary search.

**Phase 3 — 21.3 B Reference end-to-end**
- Combined bulk + rebuild for full Wikidata under `SortedAtomStore` Phase 1 first; then re-run with Phase 2 to measure MPHF marginal win.
- Compare against the Phase 6 HashAtomStore baseline (the 85 h reference number).
- Publish `docs/validations/adr-034-phase3-21b-2026-XX-XX.md` with the side-by-side.

**Phase 4 — ADR transitions**
- Status Accepted → Completed after Phase 3's 21.3 B run.

## Open questions

- **Vocabulary file format: simple length-prefixed vs prefix-shared (MARISA-trie style)?** Simple is BCL-trivial. Prefix-shared is 30–50 % smaller. Decision: Phase 1 ships simple; prefix-shared promotes to a separate limits entry if the vocabulary file size becomes load-bearing.
- **Should Cognitive/Graph also get a "frozen snapshot" mode that switches to SortedAtomStore?** Useful for a Cognitive store that's been written-to and is now in read-mostly mode. Out of scope for this ADR; track as a separate limits entry if a workload surfaces it.
- **BBHash level count tuning.** Default 3 levels; more levels = smaller MPHF but more bit-array lookups per `Eval`. Defer to Phase 2 measurement.
- **Atom-ID space narrowing to 32 bits.** Decision 5 makes IDs dense; the bit-packed-atom-ids ADR ships separately to actually narrow `ReferenceKey`. ADR-034 is a prerequisite, not a partial implementation.

## References

- [`docs/limits/sorted-atom-store-for-reference.md`](../../limits/sorted-atom-store-for-reference.md) — full motivation, MPHF survey, space comparison
- [`docs/limits/bit-packed-atom-ids.md`](../../limits/bit-packed-atom-ids.md) — follow-on round, depends on Decision 5
- [`docs/limits/bulk-load-memory-pressure.md`](../../limits/bulk-load-memory-pressure.md) — composes via 8 GB hash-table elimination
- [`docs/limits/hash-function-quality.md`](../../limits/hash-function-quality.md) — becomes profile-conditional (HashAtomStore-only) after this ADR
- [ADR-026](ADR-026-bulk-load-path.md) — bulk-load contract
- [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) — Wikidata-scale ingest pipeline
- [ADR-028](ADR-028-atomstore-rehash-on-grow.md) — current `HashAtomStore` rehash machinery; profile-conditional after this ADR
- [ADR-029](ADR-029-store-profiles.md) — profile-dispatch pattern; Decision 7's appendable-bulk semantic narrowed in this ADR for SortedAtomStore stores
- [ADR-032](ADR-032-radix-external-sort.md) / [ADR-033](ADR-033-bulk-load-radix-external-sort.md) — `ExternalSorter` machinery the bulk-build piggybacks
- [ADR-035](ADR-035-phase7a-metrics-infrastructure.md) — metrics infrastructure (Phase 7a.3 supplies before-baseline; this ADR's emission goes through the umbrella)
- Bast, H. et al., "QLever: A Query Engine for Efficient SPARQL+Text Search" — external reference for sorted-vocabulary approach
- Limasset, A.; Rizk, G.; Chikhi, R. (2017) — BBHash original paper
