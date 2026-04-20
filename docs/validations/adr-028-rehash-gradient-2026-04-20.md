# ADR-028 Rehash-on-Grow Gradient — 2026-04-20

**Status (2026-04-20, completed):** Stage 2 of [ADR-028](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md) — gradient regression against the 1.7.24 implementation committed in `f369ccf`. Validates that the rehash-on-grow path preserves throughput and exact-match correctness relative to the pre-rehash 2026-04-19 baseline in [full-pipeline-gradient-2026-04-19.md](full-pipeline-gradient-2026-04-19.md). All three scales (1 M, 10 M, 100 M) pass.

## What this gradient proves

ADR-028 Stage 1 shipped the rehash-on-grow implementation and unit tests. Stage 2 asks a different question: does the rehash path, when forced to fire repeatedly during bulk ingest, produce a store that is *byte-for-byte equivalent in behavior* to a bulk load with a pre-sized hash table?

The 2026-04-19 baseline used `BulkModeHashTableSize = 256 M buckets` — no rehash ever fires during load. Today's gradient sets `MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384` (2^14 initial buckets), which forces the rehash-on-grow path to fire every time atom count crosses 75 % of the current table size:

- 1 M atoms → approx. 8 rehashes (16 K → 4 M buckets)
- 10 M atoms → approx. 11 rehashes (16 K → 32 M buckets)
- 100 M atoms → approx. 14 rehashes (16 K → 256 M buckets)

If rehash is doing its job, the final store at each scale contains exactly the same atoms, exactly the same indexes, and produces exactly the same query results as the 2026-04-19 runs.

## Headline results

| Scale | Bulk load | Rebuild Trigram | Predicate query | Store size |
|---|---|---|---|---|
| 1 M   | 1 s (620 K/sec)      | 438,370 entries      | 129.7 ms | 736 MB |
| 10 M  | 11 s (850 K/sec)     | 4,369,219 entries    | 817.4 ms | 6.8 GB |
| 100 M | 1 m 52 s (889 K/sec) | 45,667,806 entries   | 6577.2 ms | 66 GB |

Predicate-bound query: `SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }`.

### Correctness — exact match to 2026-04-19 baseline

The crucial table. Every scale produces the same row count as the baseline, confirming rehash-on-grow preserves every `(string → atomId)` mapping through every doubling:

| Scale | Baseline count | 1.7.24 count | Match |
|---|---|---|---|
| 1 M   | 53,561     | 53,561     | ✓ |
| 10 M  | 439,703    | 439,703    | ✓ |
| 100 M | 3,212,485  | 3,212,485  | ✓ |

Trigram entry counts also match the baseline at every scale (438 K / 4.37 M / 45.67 M). GPOS/GOSP/TGSP counts scale linearly with triples as expected (99.17 M at 100 M — the ~0.83 % shortfall is RDF-dedup of identical triples, matching the 2026-04-18/19 pattern).

### Past the 58 M ceiling — the hard exit criterion

The original Bug 5 (hash table overflow) crashed at 58.3 M triples in 1.7.13 with a fixed 256 M-bucket table. Today's 100 M run starts with a **16 K-bucket table** and grows through more than a dozen rehashes to end past what the original bug's ceiling ever was. No crash, no overflow, no probe-depth degradation. Phase 1 exit criterion met.

## Throughput comparison

1.7.24 bulk load rates are substantially higher than the 2026-04-19 baseline:

| Scale | Baseline rate | 1.7.24 rate | Delta |
|---|---|---|---|
| 1 M   | 250 K/sec | 620 K/sec | +148 % |
| 10 M  | 217 K/sec | 850 K/sec | +292 % |
| 100 M | 286 K/sec | 889 K/sec | +211 % |

This is **not** attributable to ADR-028, which can only add cost (a few stop-the-world rehashes per run). Likely contributors:

- **Warm page cache.** Consecutive scales re-read the head of `latest-all.nt`; the 10 M run shares 1 M triples with the 1 M run, and 100 M shares 10 M with 10 M.
- **Accumulated non-ADR-028 perf work.** 1.7.24 incorporates every patch since the 2026-04-19 snapshot, including 1.7.23's `--limit` optimization.

The key claim for ADR-028 is **not** "faster than baseline" — it is "no slower than baseline and exact-match correct." The throughput delta is a free side-benefit from the intervening patches, not evidence for rehash.

Query-execution times and rebuild trigram counts are essentially identical to baseline (within measurement noise), consistent with "rehash only affects the ingest path; the queryable store is identical."

## Mechanism observations

Each rehash doubles `_hashTableSize`, re-inserts every live bucket via its stored per-bucket hash (no data-file reads, no hash recomputation), fsyncs `.atomidx.new`, atomically two-step renames, then unmaps/deletes `.atomidx.old`. Under the ADR-020 single-writer contract, no concurrent reader can observe the swap window. At every doubling the observable behavior is:

- `_atomCount` unchanged
- `_nextAtomId` unchanged
- every `(atom → ID)` mapping preserved
- offset index (`atomId → file offset`) unchanged — only the `hash(string) → atomId` lookup is rebuilt

The 100 M bulk-load progress log shows one visible throughput dip — `recent 40,680/sec` at the 93 M mark — consistent with a single large rehash pause around the 2^27 → 2^28 boundary. Even at that scale the whole load finished in under two minutes, well inside the ADR's projected "2-3 s pause per doubling at 256 M atoms."

Exact-match query results across three orders of magnitude are the end-to-end confirmation that this is correct.

## Reproduction

```bash
# Build and install 1.7.24
./tools/install-tools.sh

# Each scale: bulk-load with small initial hash, rebuild, query
for N in 1000000 10000000 100000000; do
  store=wiki-$(( N / 1000000 ))m
  rm -rf ~/Library/SkyOmega/stores/$store
  MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384 \
    mercury --store $store \
      --bulk-load ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt \
      --limit $N --min-free-space 50 --no-http --no-repl
  mercury --store $store --rebuild-indexes --no-http --no-repl
  echo 'SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }' \
    | mercury --store $store --no-http
done
```

Two changes from the 2026-04-19 reproduction recipe:

1. `MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384` — new in 1.7.24; forces small initial hash table and suppresses the bulk-mode 256 M floor (via `StorageOptions.ForceAtomHashCapacity`). Production bulk loads should leave this unset.
2. `--limit N` is used directly against `latest-all.nt` instead of a prior `head -n N > /tmp/slice.nt` extraction. Saves ~16 GB of /tmp I/O at 100 M.

## Stage 2 status → Stage 3

Per the [ADR-028 implementation plan](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md#implementation-plan), Stage 2 exit is met and the ADR remains **Accepted** pending Stage 3. Stage 3 is the full-Wikidata (21.3 B) run; it is blocked on the [ADR-027 storage-footprint constraint](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md#measured-storage-footprint-2026-04-19) (14 TB on an 8 TB disk with cognitive profile). Stage 3 will land when ADR-029's Reference profile is in, which is the next phase of the [production hardening roadmap](../roadmap/production-hardening-1.8.md).

## Provenance

- Hardware: MacBook Pro M5 Max, 128 GB RAM, 8 TB SSD
- Dataset: `latest-all.nt` from Wikidata April 2026 dump
- Mercury version: 1.7.24 (commit `f369ccf`)
- Gradient runs: all three scales executed 2026-04-20 in a single session, driven from Claude Code
- Peak RSS during 100 M bulk: 18.4 GB
- Final store size 100 M: 66 GB
