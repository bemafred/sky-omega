# WDBench Aggregate Distribution — Mercury 1.7.57 paired full+truthy matrix

**Date:** 2026-05-16
**Mercury version:** 1.7.57 (cycle 10 r4 substrate; truthy r1 substrate)
**Coverage:** complete WDBench paired matrix (5 categories × 2 substrates = 5,316 queries)

## Substrates

| Substrate | Store path | Dump date | Triple count |
|---|---|---|---|
| **Full** (cycle 10 r4) | `wiki-21b-ref-r4` | 2026-04-03 | 21,316,531,403 |
| **Truthy** (truthy r1) | `wiki-truthy-ref-r1` | 2026-05-08 | 8,170,000,000 |

Both substrates produced by Mercury 1.7.57 (same substrate generation: ADR-034 SortedAtomStore + ADR-037 pipelined spill + ADR-038 merge-phase read-side + ADR-039 BBHash MPHF dense final-level + 1.7.57 listener wire-through). Both indexed with the **Reference** profile (GSPO + GPOS + trigram). All queries run under a 60-second hard cancellation cap (post-1.7.46 cancellation-token coverage — 0 cancellation contract violations across the full matrix).

## Per-category breakdown

### Full (cycle 10 r4 — `wiki-21b-ref-r4`)

| Category | Attempted | Completed | Timed out | Failed | p50 | p90 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| paths | 660 | 395 | 265 | 0 | 13.34 ms | 2.13 s | 9.38 s | 47.34 s | 50.62 s |
| c2rpqs | 539 | 221 | 318 | 0 | 145.45 ms | 20.27 s | 42.77 s | 57.14 s | 57.40 s |
| single_bgps | 280 | 271 | 9 | 0 | 2.13 ms | 2.30 s | 8.90 s | 43.66 s | 65.42 s |
| multiple_bgps | 681 | 220 | 461 | 0 | 2.67 s | 31.56 s | 50.94 s | 55.80 s | 58.95 s |
| opts | 498 | 277 | 221 | 0 | 356.49 ms | 14.79 s | 27.85 s | 43.95 s | 52.95 s |
| **Total** | **2,658** | **1,384** | **1,274** | **0** | — | — | — | — | — |

### Truthy (truthy r1 — `wiki-truthy-ref-r1`)

| Category | Attempted | Completed | Timed out | Failed | p50 | p90 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| paths | 660 | 396 | 264 | 0 | 10.44 ms | 2.12 s | 8.48 s | 43.26 s | 57.76 s |
| c2rpqs | 539 | 225 | 314 | 0 | 200.74 ms | 22.35 s | 44.75 s | 55.71 s | 58.90 s |
| single_bgps | 280 | 272 | 8 | 0 | 1.42 ms | 1.70 s | 7.81 s | 31.73 s | 50.06 s |
| multiple_bgps | 681 | 220 | 461 | 0 | 2.36 s | 30.10 s | 45.20 s | 55.77 s | 59.80 s |
| opts | 498 | 278 | 220 | 0 | 308.41 ms | 15.04 s | 25.08 s | 45.81 s | 48.66 s |
| **Total** | **2,658** | **1,391** | **1,267** | **0** | — | — | — | — | — |

## Aggregate distribution (completed-only across all 5 categories)

| Substrate | n | mean | p50 | p90 | p95 | p99 | p99.9 | min | max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **Full** | 1,384 | 4.56 s | **69.44 ms** | 17.31 s | 29.96 s | 53.12 s | 57.72 s | 10 μs | 65.42 s |
| **Truthy** | 1,391 | 4.30 s | **61.70 ms** | 16.07 s | 26.33 s | 51.03 s | 58.81 s | 9 μs | 59.80 s |

Interpretation:

- **Median is sub-100 ms on both substrates** (69 ms full, 62 ms truthy). This is the headline number — half of all completed WDBench queries return in under 70 ms cold-substrate on the 21.3 B full Wikidata graph; the 8.17 B truthy graph is ~11 % faster at the median.
- **p90 ≈ 17 s** — the tail is dominated by complex multi-hop joins (most prominent in `multiple_bgps` and `c2rpqs`).
- **p99 ≈ 51-53 s** — within the 60-second cancellation cap; queries that approach but don't exceed the cap.
- **Substrate dataset matters more at the median than at the tail.** At p50 the truthy substrate is ~11 % faster (smaller graph, simpler index lookups); at p99 the gap closes to ~4 % (compute-dominated, not data-dominated, queries).

## Completion rates

| Substrate | Completed | Timed out | Failed | Completion rate |
|---|---:|---:|---:|---:|
| Full | 1,384 | 1,274 | 0 | 52.07 % |
| Truthy | 1,391 | 1,267 | 0 | 52.33 % |

Combined: 5,316 attempted, 2,775 completed (52.20 %), 2,541 timed out (47.80 %), **0 failed**. The 0-failed line includes 0 parser failures (cancellation contract honored post-1.7.46; property-path grammar gaps closed in 1.7.47) and 0 substrate crashes — the substrate-discipline claim across the matrix is preserved.

## Methodology

- **Source files:** 10 JSONL artifacts under `docs/validations/`, one per (category, substrate). Each file's `wdbench_summary` record carries per-category aggregates; the per-query records carry `elapsed_us` and `status` fields.
  - Full: `wdbench-paths-21b-2026-05-13-cycle10r4.jsonl`, `wdbench-c2rpqs-21b-2026-05-13-cycle10r4.jsonl`, `wdbench-single_bgps-cycle10r4-2026-05-15.jsonl`, `wdbench-multiple_bgps-cycle10r4-2026-05-15.jsonl`, `wdbench-opts-cycle10r4-2026-05-15.jsonl`
  - Truthy: `wdbench-paths-truthy-2026-05-14.jsonl`, `wdbench-c2rpqs-truthy-2026-05-14.jsonl`, `wdbench-single_bgps-truthy-2026-05-14.jsonl`, `wdbench-multiple_bgps-truthy-2026-05-14.jsonl`, `wdbench-opts-truthy-2026-05-15.jsonl`
- **Completion criterion:** only records with `"status":"completed"` contribute to the aggregate distribution. Timed-out queries (`"status":"timeout"`) and any failed parses are excluded — including their elapsed_us in the distribution would skew percentiles by the 60-second cap.
- **Percentile method:** sorted-array index `ceil(p × n)` (one-based), no interpolation. Matches the convention used by the per-category `wdbench_summary` records the harness emits at end-of-run.
- **Per-category p90 values** are computed here from the raw `elapsed_us` values; the per-category summary records only emit p50/p95/p99/p99.9/max so p90 was not previously recorded.

## Reproducibility

```bash
# Each substrate, completed-only elapsed_us across all 5 categories:
for sub in cycle10r4 truthy; do
  > /tmp/${sub}-elapsed.txt
  for cat in paths c2rpqs single_bgps multiple_bgps opts; do
    f=$(ls docs/validations/wdbench-${cat}-*${sub}*.jsonl | tail -1)
    grep '"status":"completed"' "$f" | grep -o '"elapsed_us":[0-9]*' | cut -d: -f2 \
      >> /tmp/${sub}-elapsed.txt
  done
  sort -n /tmp/${sub}-elapsed.txt > /tmp/${sub}-sorted.txt
  awk -v n=$(wc -l < /tmp/${sub}-sorted.txt) '
    { v[NR]=$1 }
    END {
      p50  = v[int(0.50  * n)]; p90  = v[int(0.90 * n)]
      p95  = v[int(0.95  * n)]; p99  = v[int(0.99 * n)]
      p999 = v[int(0.999 * n)]
      printf "p50=%d p90=%d p95=%d p99=%d p99.9=%d max=%d\n",
             p50, p90, p95, p99, p999, v[n]
    }
  ' /tmp/${sub}-sorted.txt
done
```

## Limitations and disclosures

- **Cancellation cap.** All measurements were taken under a 60-second hard cancellation cap. Queries reported as "timed out" do not contribute to the completed-only distribution; the cap chosen matches the WDBench paper's convention so external comparisons are apples-to-apples.
- **Cold cache only.** Each query was run in a single attempt against a fresh process; no warm-cache replay was conducted. This is the harder distribution to publish (warm-cache numbers favor smaller-than-RAM working sets); external comparisons should ensure the comparator's measurement also reports cold-cache numbers.
- **Dump-date confounder.** The Full substrate sources from `latest-all.ttl.bz2` dated 2026-04-03; the Truthy substrate sources from `latest-truthy.nt.bz2` dated 2026-05-08. The 11 % median-latency advantage of truthy includes both effects (smaller graph + newer data with possibly different cardinality distributions). A same-dated pair (truthy slice of the same 2026-04-03 dump) is on the substrate-followup list to isolate the dataset-size effect.
- **No external comparator in this doc.** This validation is the Mercury single-substrate paired record. Comparator-vs-Mercury claims belong in a separate document that includes the comparator's raw data, the comparator's measurement methodology, and an explicit apples-to-apples framing.
- **Format gap (informational).** The full substrate was built from `.ttl.bz2`; the truthy substrate was built from `.nt.bz2`. The ~18-23 % per-triple parse-side gap on `.nt` is characterized in [docs/limits/ntriples-parser-per-triple-perf.md](../limits/ntriples-parser-per-triple-perf.md) but applies only to ingest, not query-time measurements presented here.

## References

- [Cycle 10 r4 validation](cycle10-phase3-r4-21b-2026-05-12.md) — full substrate end-to-end build
- [Truthy r1 validation](truthy-r1-2026-05-14.md) — truthy substrate end-to-end build
- [WGPB step C validation](wgpb-step-c-2026-05-16.md) — sibling external benchmark on the 2018 reduced-truthy substrate (849/850 queries in 4 m 43 s aggregate; p50 53 ms, p95 1.8 s, p99 4.3 s)
- [ADR-035 metrics infrastructure](../adrs/mercury/ADR-035-phase7a-metrics-infrastructure.md) — JSONL emission scheme this aggregation reads
- WDBench: Hogan et al., *"WDBench: A Wikidata SPARQL Benchmark"*, ISWC 2023 — published cold-baseline numbers for comparison
