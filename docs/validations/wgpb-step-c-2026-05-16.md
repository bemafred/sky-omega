# WGPB step C — 850 queries × 17 patterns on 1.7.57 substrate against 2018 reduced-truthy Wikidata

**Status (2026-05-16):** WGPB (Wikidata Graph Pattern Benchmark, MillenniumDB / Zenodo 4035223) step C of the Wikidata-publication arc complete. Substrate at `wiki-wgpb-ref-r1` built from the 593 MB compressed 2018 reduced-truthy dump in **4 m 30 s** (bulk-load 3 m 40 s + rebuild-indexes 50 s). **849 of 850 WGPB queries completed in 4 m 43 s cumulative wall-clock** across all 17 abstract pattern categories (J3, J4, P2-4, S1-4, T2-4, TI2-4, Tr1, Tr2). 1 query rejected as malformed SPARQL (source-data quality issue, not a substrate bug). 0 timeouts. 0 substrate failures. This is the apples-to-apples measurement vs published WGPB / MillenniumDB numbers for the systematic-graph-pattern story; paired with [cycle 10 r4 full](cycle10-phase3-r4-21b-2026-05-12.md) and [truthy r1](truthy-r1-2026-05-14.md), it completes the three-substrate publication record.

## Purpose

WGPB (Hogan et al., MillenniumDB, published 2020) tests **worst-case-optimal join evaluation** on complex basic graph patterns — joins, paths, stars, trees, triangles — against a reduced, filtered Wikidata-truthy graph. The benchmark was designed to isolate join-complexity performance from real-world query diversity (which WDBench captures).

This measurement complements cycle 10 r4 (full Wikidata) and truthy r1 (truthy subset) by adding the **systematic join-complexity dimension** to Sky Omega's substrate-validation record. WGPB results are published in research papers from MillenniumDB and competing systems (Virtuoso, Blazegraph, etc.), so this measurement enables direct external comparison.

## ⚠ Dataset disclosure — NOT cross-comparable with cycle 10 r4 / truthy r1

WGPB uses a **different dataset** than our other Wikidata measurements:

| run | dataset | dump date | size compressed |
|---|---|---|---:|
| cycle 10 r4 | `latest-all.ttl.bz2` (full) | 2026-04-03 | 114 GB |
| truthy r1 | `latest-truthy.nt.bz2` (current truthy) | 2026-05-08 | 40 GB |
| **WGPB step C** | **`wikidata-wcg-filtered.nt.bz2`** | **November 2018** | **593 MB** |

WGPB's `wikidata-wcg-filtered.nt.bz2` is the **filtered** truthy distribution from the WGPB authors at Zenodo 4035223:
- ~7-year-old Wikidata snapshot (2018)
- English labels only
- Rare predicates (< 1,000 triples) excluded
- Very common predicates (> 1,000,000 triples) excluded
- ~100-200 M triples estimated (vs truthy's 8.17 B = ~5 %)

The filtering removes the long-tail of pathological joins, which is what makes WGPB the join-complexity-isolation benchmark it was designed to be.

**Wall-clock numbers from this WGPB run are NOT cross-comparable with cycle 10 r4 / truthy r1.** Different dataset, different scope, different shape. The comparison is vs published WGPB numbers from MillenniumDB et al., not vs our other substrates.

## Hardware + source

| | |
|---|---|
| Hardware | Apple M5 Max, 128 GB RAM |
| OS | macOS 26.4.1 (build 25E253) |
| Source dataset | `wikidata-wcg-filtered.nt.bz2` (WGPB filtered Wikidata) |
| Source path | `~/Library/SkyOmega/datasets/wgpb/wikidata-wcg-filtered.nt.bz2` |
| Source archive | https://zenodo.org/records/4035223 |
| Substrate version | mercury 1.7.57+98058a5 |
| Runtime | .NET 10, BCL-only |

## Run command

Launched 2026-05-16T05:32:41Z via `./tools/launch-wgpb-step-c.sh`. The launcher chains the full sequence:

```bash
# Step 1 — wait for filtered dump download
# Step 2 — split bgps/*.txt into per-query .sparql files
# Step 3 — bulk-load
mercury --store wiki-wgpb-ref-r1 \
        --bulk-load wikidata-wcg-filtered.nt.bz2 \
        --profile Reference \
        --min-free-space 100 \
        --metrics-out docs/validations/wgpb-bulk-2026-05-16.jsonl \
        --metrics-state-interval 30 \
        --no-http --no-repl

# Step 4 — rebuild-indexes
mercury --store wiki-wgpb-ref-r1 \
        --rebuild-indexes \
        --metrics-out docs/validations/wgpb-rebuild-indexes-2026-05-16.jsonl \
        --metrics-state-interval 30 \
        --no-http --no-repl

# Step 5 — run WGPB queries against each of 17 categories
for cat in J3 J4 P2 P3 P4 S1 S2 S3 S4 T2 T3 T4 TI2 TI3 TI4 Tr1 Tr2; do
    dotnet run --project benchmarks/Mercury.Benchmarks -c Release --no-build -- wdbench \
        --store ~/Library/SkyOmega/stores/wiki-wgpb-ref-r1 \
        --queries ~/Library/SkyOmega/datasets/wgpb/queries/$cat \
        --metrics-out docs/validations/wgpb-$cat-2026-05-16.jsonl \
        --timeout 60
done
```

## Headline numbers

### Substrate build

| metric | value |
|---|---:|
| Source size (compressed) | 593 MB |
| Bulk-load wall-clock | **3 m 40 s** |
| Rebuild-indexes wall-clock | **50 s** |
| **Total to queryable substrate** | **4 m 30 s** |
| Final store size on disk | 11 GB |

### WGPB queries — aggregate

| metric | value |
|---|---:|
| Queries attempted | 850 |
| **Completed** | **849 (99.88 %)** |
| Timed out (60 s cap) | **0** |
| **Failed** | **1** (malformed SPARQL — source-data issue, see below) |
| **Cumulative wall-clock (17 categories)** | **4 m 43 s** |

### Aggregate latency distribution (849 completed queries)

| percentile | latency |
|---|---:|
| min | 2.3 ms |
| p25 | 15.9 ms |
| **p50** | **53.3 ms** |
| p75 | 223.3 ms |
| p90 | 861.8 ms |
| p95 | 1,834.8 ms |
| p99 | 4,250.6 ms |
| max | 8,114.6 ms |

The 60 s per-query timeout cap was vastly over-provisioned for this workload — the slowest query completed in 8.1 s, and 95 % completed in under 1.8 s.

### Per-category breakdown

| category | completed | wall-clock | p50 | p95 | p99 |
|---|---:|---:|---:|---:|---:|
| J3 | 50/50 | 12.6 s | 53.3 ms | 1,250 ms | 3,788 ms |
| **J4** | **49/50** | **10.4 s** | 49.2 ms | 1,006 ms | 2,334 ms |
| P2 | 50/50 | 12.6 s | 25.7 ms | 910 ms | 6,168 ms |
| P3 | 50/50 | 14.8 s | 79.3 ms | 1,382 ms | 3,803 ms |
| P4 | 50/50 | 14.8 s | 100.8 ms | 1,636 ms | 2,128 ms |
| S1 | 50/50 | 28.5 s | 126.6 ms | 3,240 ms | 5,586 ms |
| S2 | 50/50 | 22.1 s | 113.4 ms | 1,952 ms | 4,056 ms |
| S3 | 50/50 | 27.7 s | 71.4 ms | 4,251 ms | 4,573 ms |
| S4 | 50/50 | 25.2 s | 73.2 ms | 3,031 ms | 8,115 ms |
| T2 | 50/50 | 16.6 s | 74.6 ms | 2,678 ms | 3,070 ms |
| T3 | 50/50 | 21.4 s | 66.3 ms | 1,967 ms | 3,780 ms |
| T4 | 50/50 | 16.8 s | 107.3 ms | 1,964 ms | 2,532 ms |
| TI2 | 50/50 | **1.9 s** | 14.4 ms | 193 ms | 303 ms |
| TI3 | 50/50 | **3.5 s** | 14.2 ms | 211 ms | 1,176 ms |
| TI4 | 50/50 | **2.7 s** | 18.7 ms | 201 ms | 817 ms |
| Tr1 | 50/50 | 30.4 s | 59.1 ms | 2,608 ms | 6,269 ms |
| Tr2 | 50/50 | 20.5 s | 61.5 ms | 2,488 ms | 6,515 ms |

TI patterns (transitive-inverse?) are conspicuously the fastest. S patterns (stars) and Tr patterns (triangles) are the slowest. P (paths) lands in the middle, J (joins) on the faster end.

## The J4/00038 failure

One query in the J4 category failed with a substrate-side parser error:
> `Incomplete triple pattern - expected object after predicate`

Inspecting the source query (`bgps/J4.txt` line 38, split to `queries/J4/00038.sparql`):

```sparql
SELECT * WHERE {
    ?y <http://www.wikidata.org/prop/direct/P177> ?x .
    ?z <http://www.wikidata.org/prop/direct/P1204> ?x .
    ?x <http://www.wikidata.org/prop/direct/P1997> ?u .
    ?x <http://www.wikidata.org/prop/direct-normalized/P269> ?v  LIMIT 1000}
```

The `LIMIT 1000` is **inside** the WHERE clause, before the closing `}` — invalid SPARQL syntax. The proper form would be `WHERE { ... } LIMIT 1000`.

This is a **WGPB source-data quality issue**: a malformed query made it into the published archive. Mercury's parser correctly:
- Identified the syntactic error with a precise message
- Returned a structured `"status":"failed"` record in the metric stream
- Continued processing the remaining 49 J4 queries without harness disruption
- Substrate stayed in a valid state

The substrate-discipline framing: **0 substrate failures across all 850 WGPB queries.** The 1 J4 failure was a parser rejection of malformed input — the expected and correct behavior. Mercury's SPARQL parser is identifying real defects in published benchmark data.

## Comparison-plane context — Mercury vs published WGPB systems

WGPB has been measured in multiple research papers across systems including Virtuoso, Blazegraph, Jena TDB, and MillenniumDB. Detailed cross-system comparison is beyond this validation doc's scope (Sky Omega's stance: "this is our measured WGPB result, paired with our cycle 10 r4 and truthy r1 results, available for any external comparison researchers want to make"). The data needed:

- All 17 per-category JSONLs committed alongside this doc
- Aggregate percentile distribution: p50 53 ms, p95 1.8 s, p99 4.3 s
- Substrate identity: 1.7.57 commit `98058a5` (Mercury) + 1.7.57 commit (Mercury.Benchmarks WdBench harness)
- Hardware: Apple M5 Max, 128 GB RAM
- Timeout cap: 60 s (the slowest query completed in 8.1 s, so timeout never bound)

## Substrate identity + final files

| file | size on disk |
|---|---:|
| atoms.atoms | ~6 GB (estimate; not measured directly) |
| atoms.offsets | ~2 GB |
| atoms.mphf | (small; MPHF over ~150 M atoms est.) |
| atoms.idx | ~600 MB (mphf translation table) |
| trigram.posts | small |
| gspo.tdb + gpos.tdb | bulk of the 11 GB total |
| **Total store on disk** | **11 GB** |

GPOS entries: per the bulk-load record, ~85 M triples ingested in 3 m 40 s. atom count smaller in the substrate due to filtering. Substrate file architecture identical to cycle 10 r4 / truthy r1 (Reference profile + Sorted atom store + MPHF + trigram).

## Information for external Wikidata-community registration

| Field | Value |
|---|---|
| **Benchmark name** | WGPB (Wikidata Graph Pattern Benchmark, MillenniumDB / Zenodo 4035223) |
| **Substrate version** | mercury 1.7.57+98058a5 |
| **Dataset** | `wikidata-wcg-filtered.nt.bz2`, 2018 reduced-truthy Wikidata |
| **Dump date** | November 2018 |
| **Dataset size compressed** | 593 MB (bz2) |
| **Hardware** | Apple M5 Max laptop, 128 GB RAM, macOS 26.4.1 |
| **Runtime** | .NET 10, BCL-only |
| **Substrate build wall-clock** | 4 m 30 s (bulk-load + rebuild-indexes) |
| **Final substrate size** | 11 GB |
| **Queries attempted** | 850 (17 patterns × 50 instances) |
| **Queries completed** | **849 (99.88 %)** |
| **Queries timed out** | **0** |
| **Queries failed** | **1** (malformed source query in J4 — see disclosure above) |
| **Total query wall-clock** | 4 m 43 s |
| **Per-query timeout cap** | 60 s (never bound) |
| **Aggregate p50 / p95 / p99** | 53 ms / 1.8 s / 4.3 s |
| **JSONL evidence** | 17 wgpb-{pattern}-2026-05-16.jsonl + wgpb-bulk-2026-05-16.jsonl + wgpb-rebuild-indexes-2026-05-16.jsonl |
| **Companion full measurement** | [cycle 10 r4 21.3 B](cycle10-phase3-r4-21b-2026-05-12.md) |
| **Companion truthy measurement** | [truthy r1 8.17 B](truthy-r1-2026-05-14.md) |
| **Independent reproducibility** | Repository: github.com/bemafred/sky-omega; commit `211eea2`. All metric JSONLs committed. Launcher: `tools/launch-wgpb-step-c.sh`. |

## Three-substrate substrate-discipline summary

Cumulative across all 1.7.57 measurements on the three substrates:

- **cycle 10 r4 full Wikidata 2026-04-03** (sealed 21.3 B) + WDBench paths/c2rpqs/single_bgps/multiple_bgps/opts = 2,658 queries
- **truthy r1 truthy 2026-05-08** (sealed 8.17 B) + WDBench paths/c2rpqs/single_bgps/multiple_bgps/opts = 2,658 queries
- **wgpb step C 2018 reduced-truthy** (sealed ~150 M) + WGPB 17 categories = 850 queries

| measurement | substrate failures |
|---|---:|
| WDBench × cycle 10 r4 | 0 |
| WDBench × truthy r1 | 0 |
| WGPB × wgpb-r1 | 0 (1 query rejected as malformed source data) |
| Cumulative across all 6,166 unique-substrate queries | **0** |

Including the prior cycle 8 + cycle 9 WDBench runs (paths + c2rpqs only, 1,199 each = 2,398 prior): **0 substrate failures across 8,564 unique query × substrate executions on the cumulative 1.7.x substrate line** (5,316 paired matrix + 2,398 cycle 8+9 prior + 850 WGPB step C).

## References

- [Cycle 10 Phase 3 r4 21.3 B production validation](cycle10-phase3-r4-21b-2026-05-12.md) — the full-Wikidata companion measurement
- [Truthy r1 8.17 B validation](truthy-r1-2026-05-14.md) — the truthy-subset companion measurement
- [QLever comparison-plane memo](../memos/2026-04-30-latent-assumptions-from-qlever-comparison.md) — full vs truthy distinction
- WGPB upstream: https://zenodo.org/records/4035223
- WGPB paper: Hogan et al., MillenniumDB / Zenodo 2020
- `urn:sky-omega:finding:wgpb-step-c-complete-2026-05-16` (Mercury) — TODO: record
- WGPB JSONL evidence (committed `211eea2`): docs/validations/wgpb-{J3,J4,P2,P3,P4,S1,S2,S3,S4,T2,T3,T4,TI2,TI3,TI4,Tr1,Tr2}-2026-05-16.jsonl
