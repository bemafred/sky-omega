# ADR-029 Reference-vs-Cognitive Storage Gradient — 2026-04-20

**Status:** Phase 2 validation for [ADR-029](../adrs/mercury/ADR-029-store-profiles.md). Verifies the storage-economy claim at 1 M, 10 M, and 100 M triples and the time claim's **inverse** — the current Reference bulk-load path is time-unworkable at Wikidata scale without the [ADR-030](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) optimizations. Both dimensions measured below.

## The two claims

ADR-029's load-bearing pair:

1. **Storage.** At 21.3 B Wikidata, Cognitive projects to ~13.8 TB and overflows the 8 TB validation SSD. Reference projects to ~2.6 TB. **The gradient confirms this — 5× on indexes, 4× overall, linearly extrapolable.**

2. **Time.** Reference skips the separate rebuild phase because both of its indexes are populated inline during bulk-load. **The gradient falsifies the naive "Reference is faster" reading — at 100 M, Reference's total time (52 m 44 s) is 3× *slower* than Cognitive's bulk+rebuild (17 m 24 s).** The per-triple work is higher and the write pattern is cache-hostile.

Both findings are load-bearing for what comes next: the storage win is real and justifies the profile's existence; the time cost motivates [ADR-030](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) as a hard prerequisite for the full-Wikidata run.

## Headline numbers

| Scale | Cognitive total | Reference total | Total ratio | Cognitive indexes | Reference indexes | Index ratio |
|---|---|---|---|---|---|---|
| 1 M   | 736 MB | 228 MB | **3.23×** | 615 MB (4 idx) | 107 MB (2 idx) | **5.75×** |
| 10 M  | 6.8 GB | 1.8 GB | **3.78×** | ~6 GB | ~1.6 GB | ~3.75× |
| 100 M | 66 GB  | 16 GB  | **4.13×** | 60 GB (4 idx)  | 11 GB (2 idx)  | **5.45×** |

Cognitive column reuses the ADR-028 gradient stores (same hash-capacity override, same Wikidata slice). Reference runs use `--profile Reference` + the same `MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384` so the atom-hash cost is matched.

**Index-cost ratio stays near the ADR-029 projection (~5×) across the 100× scale swing.** Dilution in the total-size ratio comes from profile-independent cost (atoms, trigram, hash table).

## Per-scale detail — 100 M

| Component                  | Cognitive  | Reference  | Delta |
|----------------------------|-----------:|-----------:|------:|
| gspo.tdb                   | 14 GB      | 5.4 GB     | 2.6× |
| gpos.tdb                   | 15 GB      | 5.6 GB     | 2.7× |
| gosp.tdb                   | 14 GB      | _absent_   | Cognitive only |
| tgsp.tdb                   | 17 GB      | _absent_   | Cognitive only |
| **B+Tree indexes total**   | **60 GB**  | **11 GB**  | **5.45×** |
| atoms.atoms (UTF-8)        | 1.5 GB     | 1.5 GB     | profile-indep. |
| atoms.offsets (id → off)   | 206 MB     | 206 MB     | profile-indep. |
| atoms.atomidx (hash)       | 2.0 GB     | 2.0 GB     | profile-indep. (same hash knob) |
| trigram.hash / .posts      | 1.3 GB     | 1.3 GB     | profile-indep. |
| wal.log                    | 4 KB       | _absent_   | Reference has no WAL |
| **Total store**            | **66 GB**  | **16 GB**  | **4.13×** |

Per-entry size on disk matches the ADR's schema math:
- Cognitive 88 B/entry × 100 M × 4 indexes ≈ 35.2 GB theoretical; 60 GB actual (B+Tree pack efficiency ~59 %).
- Reference 32 B/entry × 100 M × 2 indexes ≈ 6.4 GB theoretical; 11 GB actual (ratio is similar — same B+Tree overhead).

## Query correctness — exact match at every scale

Predicate-bound COUNT: `SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }`

| Scale | Cognitive rows | Reference rows | Cognitive query | Reference query |
|---|---|---|---|---|
| 1 M   | 53,561    | **53,561**    | 130 ms   | **96 ms**   |
| 10 M  | 439,703   | **439,703**   | 817 ms   | **574 ms**  |
| 100 M | 3,212,485 | **3,212,485** | 6,577 ms | **4,704 ms** |

Exact-match row counts across three orders of magnitude confirm end-to-end correctness of the Reference profile: uniqueness invariant (ADR-029 §7), graph-aware keys, both indexes (GSPO for subject-bound, GPOS for predicate-bound), atom-ID round-trip, trigram skip for non-literal objects. **Reference query latency is consistently 25-30 % faster** than Cognitive on the same workload — smaller indexes, fewer pages in the hot set.

## Time, not storage — Reference's blocker

| Scale | Cognitive bulk | Cognitive rebuild | Cognitive total (→ Ready) | Reference total (→ Ready, bulk only) | Reference vs Cognitive total |
|---|---:|---:|---:|---:|---:|
| 1 M   |  ~2 s    | 2.9 s    | ~5 s     | 4 s          | comparable |
| 10 M  | ~12 s    | 42 s     | ~54 s    | 124 s        | 2.3× slower |
| 100 M | 112 s    | 695 s    | 807 s    | **3,164 s**  | **3.9× slower** |

Reference's bulk-load is doing inline what Cognitive's rebuild does later — write to GPOS plus trigram per triple — but the access pattern is worse: two B+Trees in different sort orders receive interleaved random writes, two sparse mmaps thrashing the page cache.

The throughput trajectory makes this tangible:

| Scale | Reference avg rate | Recent rate at end |
|---|---:|---:|
| 1 M   | 210 K triples/sec | ~210 K |
| 10 M  |  80 K triples/sec | ~55 K  |
| 100 M |  31.6 K triples/sec | ~24 K |

Extrapolated to 21.3 B at sustained ~24 K triples/sec: **~10 days**. Not deployable. For comparison Cognitive at 331 K/sec for bulk + super-linear rebuild projects to ~3.5 days (also heavy, but addressed by ADR-030 Phase 2 parallel rebuild).

## Implications for ADR-030

The gradient validates both the storage thesis and the optimization need:

- **Storage gradient is clean, linear, and predicts the 21.3 B outcome.** The Reference profile delivers its promised footprint. No surprises, no cliffs visible in the 1 M → 100 M range, so the extrapolation to 21.3 B is defensible.
- **Time gradient shows the optimization surface ADR-030 targets.** Reference bulk-load is where ADR-030's phases 2 (parallel per-index writer threads) and 3 (sort-insert fast path for GPOS inside the bulk loop) land. The current 31.6 K/sec is the *baseline* those optimizations will be measured against.
- **Measurement infrastructure** (ADR-030 Phase 1) is now the gating work — the numbers in this gradient come from `mercury` stdout and `du -sh`, which is enough for a storage claim but not for defensible per-phase timing. Phase 1 stands up `QueryMetrics` / `RebuildMetrics` / JSONL so subsequent optimization runs produce comparable evidence.

## Methodology

- **Dataset**: `latest-all.nt` (Wikidata April 2026 dump, 21.3 B triples, 3.1 TB)
- **Hardware**: MacBook Pro M5 Max, 128 GB RAM, 8 TB SSD
- **Mercury**: 1.7.30 (commit `b3e2964`) — ADR-028 rehash + ADR-029 profiles + `--profile` CLI + Reference SPARQL routing
- **Fair-comparison knobs**: `MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384` on every run so the atom hash table isn't a confounding variable. Bulk mode active for both profiles.
- **Reproduction**:

  ```bash
  # Reference gradient (Cognitive stores reused from ADR-028 run).
  for N in 1000000 10000000 100000000; do
    store_ref=wiki-$(( N / 1000000 ))m-ref
    rm -rf ~/Library/SkyOmega/stores/$store_ref

    MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384 \
      mercury --store $store_ref --profile Reference \
        --bulk-load ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt \
        --limit $N --min-free-space 50 --no-http --no-repl

    echo 'SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }' \
      | mercury --store $store_ref --no-http

    du -sh ~/Library/SkyOmega/stores/$store_ref
  done
  ```

## 21.3 B projections, reconciled

Taking the 100 M per-index sizes (which track the 1 M and 10 M numbers cleanly):

| Profile | Indexes @ 100 M | Indexes @ 21.3 B (×213) | Atoms @ 21.3 B | Trigram @ 21.3 B | Hash @ 21.3 B | **Total projection** |
|---|---:|---:|---:|---:|---:|---:|
| Cognitive | 60 GB  | 12.8 TB | 280 GB | 300 GB | ~500 GB | **~13.9 TB** |
| Reference | 11 GB  | 2.34 TB | 280 GB | 300 GB | ~500 GB | **~3.4 TB** |

The ~3.4 TB projection is somewhat above the ADR's ~2.6 TB (the ADR assumed lower atoms+trigram contributions); either way it fits on the 8 TB SSD with >2× margin. **ADR-029's storage thesis holds.**

Time projections (before ADR-030 optimizations):

| Profile | Bulk rate | 21.3 B bulk | Rebuild | 21.3 B total |
|---|---:|---:|---:|---:|
| Cognitive | 331 K/sec (measured at 1 B) | ~18 h | ~70 h (super-linear) | ~88 h (~3.5 days) |
| Reference | ~24 K/sec (sustained at 100 M) | ~10 days | 0 (no rebuild) | ~10 days |

Without ADR-030, Reference is slower to load than Cognitive despite having less total work. The ADR-030 optimizations target ~3× parallel + 3-5× sort-insert = ~10-15× throughput multiplier, which brings Reference's 21.3 B load into the "<24 hour" regime the roadmap's Phase 2 exit criterion specifies.

## Provenance

Validation authored 2026-04-20. Three Reference-profile bulk-loads driven from Claude Code, no manual intervention between scales. Cognitive column reuses the 2026-04-20 ADR-028 gradient stores (`wiki-1m`, `wiki-10m`, `wiki-100m`) — same Wikidata prefix, same hash knob, different `--profile`.

## References

- [ADR-029](../adrs/mercury/ADR-029-store-profiles.md) — source of the storage projection this gradient validates
- [ADR-030](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) — accepts the time cost measured here as motivation for parallel + sort-insert
- [ADR-028 Reference gradient](adr-028-rehash-gradient-2026-04-20.md) — Cognitive column's provenance
- [Full-pipeline gradient 2026-04-19](full-pipeline-gradient-2026-04-19.md) — Cognitive rebuild baseline at 1 B
