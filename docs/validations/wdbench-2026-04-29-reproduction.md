# WDBench 2026-04-29 Cold Baseline — Reproduction Recipe

This note pins the substrate-defining identity of the 1.7.47 WDBench cold baseline. The disclosure artifacts are the two JSONL files in this directory:

- `wdbench-paths-21b-2026-04-29-1747.jsonl` — 660 paths queries + summary record
- `wdbench-c2rpqs-21b-2026-04-29-1747.jsonl` — 539 c2rpqs queries + summary record

The substrate (`wiki-21b-ref`) that produced these numbers is *not* archived. It is reproducible from source + Mercury commit + bulk-load command. This note documents the exact inputs.

## Substrate identity

| Property | Value |
|---|---|
| Substrate name | `wiki-21b-ref` |
| Profile | Reference (per [ADR-008](../adrs/ADR-008-workload-profiles-and-validation-attribution.md), [ADR-029](../adrs/mercury/ADR-029-store-profiles.md)) |
| Source | `latest-all.nt` (Wikidata RDF dump) |
| Source dump dateModified | `2026-03-26T...` (observed in the substrate's own dump-metadata triples; see [21b-query-validation-2026-04-26.md](21b-query-validation-2026-04-26.md) §"Step 1") |
| Source size | 3.1 TB uncompressed |
| Triples ingested | 21,316,531,403 |
| Mercury commit (build) | Phase 6 / Mercury 1.7.44 — final pre-bulk-load infrastructure landed in `aa35514` (`ReferenceQuadIndex: bump BulkMode floor 256 GB → 1 TB`); Phase 6 close-out at `3628a86` |
| Build wall-clock | 85 h end-to-end (bulk-load + GPOS rebuild + trigram rebuild) |
| Build seal | 2026-04-25 22:32 |
| On-disk footprint | 2.5 TB physical / 4.1 TB logical (sparse APFS mmap) |
| File breakdown | `gspo.tdb` 1 TB · `gpos.tdb` 1 TB · `atoms.atoms` 269 GB · `atoms.atomidx` 256 GB · `trigram.posts` 8.6 GB |
| Hardware | M5 Max MacBook Pro · 18 cores · 128 GB unified memory · internal NVMe |
| Storage path | `/Users/bemafred/Library/SkyOmega/stores/wiki-21b-ref` (local; not portable) |

## WDBench query-run identity

| Property | Value |
|---|---|
| Mercury commit (run) | `1be7a4d` — `sparql: 1.7.47 — property-path hardening (parser + walker + Case 2)` |
| Tag | `v1.7.47-wdbench-baseline` |
| WDBench corpus | `wdbench/queries/paths` (660) + `wdbench/queries/c2rpqs` (539) |
| Corpus path | `/Users/bemafred/Library/SkyOmega/datasets/wdbench/queries/{paths,c2rpqs}` |
| Cancellation cap | 60 seconds per query |
| Run window | 2026-04-29 19:23 UTC → 2026-04-30 06:54 UTC (≈11 h 30 m) |
| Output files | `wdbench-paths-21b-2026-04-29-1747.jsonl` · `wdbench-c2rpqs-21b-2026-04-29-1747.jsonl` |

Each JSONL record schema: `{schema_version, phase, record_kind, ts, file, status, elapsed_us, rows}`. The trailing record per file is a `wdbench_summary` event with corpus-level statistics.

## Headline measurements (paths + c2rpqs combined)

- 1,199 queries attempted · 0 parser failures
- 655 timeouts; every one closed between 60.000 s and 63.620 s — cancellation contract honored at scale
- p25 = 4 ms · p50 = 45 ms · p75 = 1.39 s · p90 = 12.82 s · p95 = 29.85 s · p99 = 49.50 s · max = 59.82 s

## Reproducing the substrate

Exact reproduction of v1 (same internal atom IDs, same B+Tree page layouts) requires the original `latest-all.nt` source. Equivalent reproduction (same triples, same query results) is the supported path, using the canonical bz2 source via Mercury 1.7.45+ streaming decompression:

```bash
# 1. Source: download latest-all.ttl.bz2 from a Wikidata mirror
#    matching dateModified 2026-03-26 (archived dump, not "latest").

# 2. Build Mercury at the WDBench-run commit:
git checkout 1be7a4d
dotnet build -c Release SkyOmega.sln
./tools/install-tools.sh

# 3. Reference-profile bulk-load with bz2 streaming:
mercury wiki-21b-ref --bulk-load latest-all.ttl.bz2 \
  --profile reference --no-repl --metrics-out bulk.jsonl

# 4. Verify substrate identity (smoke test from 21b-query-validation-2026-04-26.md):
printf 'SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 10\n:quit\n' | \
  mercury --store wiki-21b-ref --no-http
# First 10 triples must include the dump's own metadata
# (cc0 license, softwareVersion 1.0.0, dateModified 2026-03-26).
```

Equivalent reproduction is sufficient for any verification of the WDBench baseline distribution: query semantics depend on triple identity, not on internal atom IDs or page-layout details. A regression introduced by re-rendering would itself be a substrate-correctness finding.

## Reproducing the WDBench run

```bash
# Mercury binary as above.
# WDBench corpus available from https://github.com/MillenniumDB/WDBench
# (queries/paths/*.sparql and queries/c2rpqs/*.sparql — 660 + 539 files).

# Harness invocation (substrate present, corpus on disk):
mercury wdbench --store wiki-21b-ref \
  --queries-dir wdbench/queries/paths \
  --timeout-seconds 60 \
  --output wdbench-paths-21b-<date>-1747.jsonl

mercury wdbench --store wiki-21b-ref \
  --queries-dir wdbench/queries/c2rpqs \
  --timeout-seconds 60 \
  --output wdbench-c2rpqs-21b-<date>-1747.jsonl
```

## v1 retention

The `wiki-21b-ref` substrate is being retired from disk to free the 2.5 TB headroom needed for Round 1 (ADR-034 SortedAtomStore + parallel bz2 decompression). Retention rationale, per ADR-007 (sealed substrate immutability) and the structural reproducibility above:

- The disclosure artifacts are the JSONL files in this directory, not the substrate.
- The substrate is a deterministic render of (source dump + Mercury commit `1be7a4d` + bulk-load command + hardware). All four are pinned in this note.
- Reproducing v1 takes ~85 h of M5 Max wall-clock — significant but bounded; preferable to permanent 2.5 TB occupancy on a laptop NVMe through the Phase 7c rounds.

The git tag `v1.7.47-wdbench-baseline` (on commit `1be7a4d`) provides the stable substrate-defining identity. This note is the literal reproduction recipe.

## References

- [Phase 6 article](../articles/2026-04-26-21b-wikidata-on-a-laptop.md) — Reference-profile build context.
- [21b-query-validation-2026-04-26.md](21b-query-validation-2026-04-26.md) — substrate file breakdown and dump-metadata observation.
- [adr-035-phase7a-1b-2026-04-27.md](adr-035-phase7a-1b-2026-04-27.md) — bz2 streaming + metrics infrastructure validated end-to-end.
- [ADR-007](../adrs/ADR-007-sealed-substrate-immutability.md) — re-create-don't-modify discipline that endorses substrate retirement.
- [ADR-008](../adrs/ADR-008-workload-profiles-and-validation-attribution.md) — Reference-profile workload framing.
- [docs/limits/cancellable-executor-paths.md](../limits/cancellable-executor-paths.md) — cancellation gap closed in 1.7.46, contract honored at scale in this baseline.
- [docs/limits/property-path-grammar-gaps.md](../limits/property-path-grammar-gaps.md) — three grammar shapes closed in 1.7.47, 0 parser failures in this baseline.
