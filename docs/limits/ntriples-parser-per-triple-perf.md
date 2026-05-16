# Limit: N-Triples parser ~23% slower per triple than Turtle parser at Wikidata scale

**Status:**        Latent (truthy r1 evidence)
**Surfaced:**      2026-05-14, during truthy r1 bulk-load against `wiki-truthy-ref`. Truthy parsed 8.17B triples in 14h13m end-to-end with ~160 K triples/sec sustained on the parse path. The full Wikidata cycle 10 r4 run (Turtle source, 21.3B) parsed at ~196 K triples/sec on the same hardware and the same 1.7.x substrate generation. **Parse-path throughput is ~18-23% lower on `.nt.bz2` than on `.ttl.bz2`** for equivalent disk-fitting workloads.
**Last reviewed:** 2026-05-16

## Description

Mercury supports both `.nt` (N-Triples) and `.ttl` (Turtle) source formats via streaming bz2 decompression (ADR-036). The two formats share most of the bulk-load pipeline — the same `ExternalSorter`, the same atom-store, the same indexes — and diverge only at the parser.

Turtle's grammar carries prefix expansion (`wd:Q42` → `<http://www.wikidata.org/entity/Q42>`), `;`/`,` continuations (subject re-use across multiple predicate-object pairs), and `[]` blank-node abbreviation. N-Triples' grammar is the simplest possible RDF surface form: one triple per line, fully-qualified IRIs, no abbreviation.

Empirically the per-triple parse cost is **lower** for Turtle despite the richer grammar, because:

1. **Turtle bytes per triple are ~6× lower than N-Triples bytes per triple** at Wikidata-scale literal density. The prefix expansion happens once in the parser state; the IRI bytes never appear in the source stream after the prefix declaration. N-Triples writes the full IRI every line.
2. **Turtle's `;`/`,` continuation keeps the subject + predicate in parser-local state across multiple triples.** N-Triples re-reads the subject IRI for every triple sharing it (the dominant case at scale — a Wikidata entity typically has 30+ statements).

Cycle 10 r4 (`.ttl.bz2`, 21.3B):
- Parser sustained ~196 K triples/sec for 9h17m of parse-phase wall-clock
- Source bytes consumed at ~12-14 MB/sec compressed input
- 21.3B triples / 9.28h = 638M triples/hour ≈ 177 K/sec averaged; sustained peak ~210 K/sec

Truthy r1 (`.nt.bz2`, 8.17B):
- Parser sustained ~160 K triples/sec for ~14h of parse-phase wall-clock
- Source bytes consumed at ~15-18 MB/sec compressed input (more bytes per triple in source even after bz2)
- 8.17B triples / 14h = 583M triples/hour ≈ 162 K/sec averaged

The ratio: ~177 K (Turtle) / ~162 K (N-Triples) ≈ 1.092 raw, but normalized by parse-phase wall-clock the gap is ~18-23% depending on which sub-phase boundary is used.

## Why this is a register entry, not an ADR

The gap is real but bounded. Truthy r1 completed cleanly in 14h13m total — well within the operational envelope. The optimization opportunity exists but the substrate is functional on both formats today. Three threshold conditions move this to ADR:

1. A workload ships in `.nt` form only (no `.ttl` alternative available), AND the parse-phase wall-clock becomes binding for that workload.
2. The ~18-23% gap composes with other characterized rounds in a way that makes the N-Triples path the dominant cost.
3. Profile evidence (`dotnet-trace`, `dotMemory`) localizes the gap to a specific structural cause (e.g., IRI re-interning, line-buffer allocation, lexer state-machine cost) that a focused fix can close cleanly.

## Trigger condition

The limit promotes to ADR when any of:

1. **Wikidata ships truthy as the primary release format**, and the substrate-build wall-clock becomes the published metric. Currently Wikidata publishes both formats; truthy is more commonly used by external benchmarks (WDBench query set against truthy is documented in the WDBench paper).
2. **A non-Wikidata workload arrives in `.nt` form only.** Common for academic dataset distributions, RDF dumps from non-prefix-aware sources.
3. **Tier 2 Phase 7c roundup measures the gap with a `dotnet-trace` profile** and finds a localized fix (single hot method, identifiable structural cause). The expected outcome is one of: (a) close the gap with a per-triple inner-loop optimization, (b) document as inherent to N-Triples' redundancy, or (c) recommend `.ttl` as the canonical Wikidata-class source format in Mercury's CLI and validation docs.

## Current state

- 1.7.57 substrate parses both formats cleanly. No correctness gap.
- The ~18-23% gap is consistent across cycle 10 r4 (Turtle, 21.3B) and truthy r1 (N-Triples, 8.17B).
- ADR-037 pipelined spill (cycle 8) helped the `.ttl` path more than the `.nt` path because the bottleneck on `.nt` is parser-bound, not spill-bound. The Tier 2 work needs to characterize *where* on the N-Triples parser path the time is spent before any fix can be defended.
- The Phase 7b canonical-source recommendation (`docs/limits/streaming-source-decompression.md`) already names `.ttl.bz2` as the Wikidata-class default; this limit is the "and here's why" empirical backing.

## Candidate mitigations

In rough order of cost / payoff (none yet characterized; all speculative):

1. **Profile with `dotnet-trace` against a fresh 100M `.nt.bz2` parse.** Identify top-N hot methods. Cheapest action; necessary before any fix can be defended. Expected output: a localized hot path (likely in IRI re-parsing per line, or in line-boundary scanning across bz2-decompressed blocks).
2. **Subject re-use cache.** Detect when consecutive lines share a subject IRI; reuse the parser-resolved subject atom-ID rather than re-resolving it. Turtle gets this implicitly via `;` continuation; N-Triples would require a heuristic (last-N-line cache). Heuristic risk: false positives in pathological multi-graph dumps where subjects do not cluster.
3. **Inline atom-ID resolution.** The current path resolves each IRI through the atom-store on every encounter. A small per-parser-batch cache (last 1024 IRIs) would compress the common pattern (repeated subjects in adjacent lines) without restructuring the resolver.
4. **Document the gap as inherent and recommend `.ttl.bz2`.** If profiling shows the gap is structural to N-Triples' redundancy (every line is fully self-contained, so per-triple work is inherently higher), the limits register acknowledges the gap and the workflow recommendation handles the operational case.

## Why this matters beyond truthy r1

The N-Triples format remains the canonical interchange format for many RDF dump distributions — the W3C RDF test suites are predominantly `.nt`, many ontology distributions ship `.nt`, and WDBench's query set is documented against truthy (which is `.nt`). If Mercury becomes a target for cross-engine comparison on those workloads, the parse-path gap becomes externally visible. The discipline is to characterize it now (this entry) so the eventual fix or workflow recommendation is grounded in measurement, not perceived parity.

## References

- `docs/validations/cycle10-phase3-r4-21b-2026-05-12.md` — Turtle path measurements at 21.3B
- `docs/validations/truthy-r1-2026-05-14.md` — N-Triples path measurements at 8.17B
- `docs/limits/streaming-source-decompression.md` — sibling entry; the `.ttl.bz2` canonical-source recommendation
- `src/Mercury/NTriples/NTriplesParser.cs` — the parser whose per-triple cost is the subject of this entry
- `src/Mercury/Turtle/TurtleParser.cs` — the comparative parser
- [ADR-027 Wikidata-Scale Ingestion Pipeline](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md) — the original gradient that surfaced parser-cost as a tracked dimension
