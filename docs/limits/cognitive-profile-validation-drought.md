# Limit: Cognitive profile validation drought

**Status:**        Triggered
**Surfaced:**      2026-04-30, via the public-claims sweep prompted by `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`. ADR-008 makes profile-attribution mandatory for validations; the sweep revealed that all measured validations from 2026-04-19 onward exercise the Reference profile. The Cognitive profile's last measured wall-clock at 1B was 2026-04-19 (`docs/validations/full-pipeline-gradient-2026-04-19.md`), which predates ADR-031 (Dispose gate, 1.7.32), ADR-032 (radix external sort, 1.7.39+), ADR-033 (bulk-load radix, 1.7.43), ADR-034 (SortedAtomStore — Reference-only), ADR-035 Phase 7a (metrics), ADR-036 Phase 7b (bz2 streaming), and the 1.7.46/1.7.47 property-path hardening.
**Last reviewed:** 2026-04-30
**Promotes to:**   ADR for a Cognitive validation gradient run, OR a small workgroup of validation runs at 1M / 10M / 100M Cognitive scales against the current 1.7.47 substrate. Trigger: any external comparison conversation that touches Cognitive (write-heavy, bitemporal-dense workloads), OR any 2.0 cognitive-component readiness review (Lucy/James/Sky entry implies Cognitive performance must be characterized).

## Description

Sky Omega has two workload profiles per ADR-008: Reference (large mostly-static, ad-hoc-SPARQL-shaped) and Cognitive (smaller working set, write-heavy, bitemporal-dense, James-driven query patterns). The substrate work since 2026-04-19 has overwhelmingly targeted Reference: all of ADR-029 through ADR-036, plus the property-path hardening arc, plus the 21.3 B Wikidata Phase 6 run. The validation evidence in `docs/validations/` reflects this.

The most recent Cognitive validation is `full-pipeline-gradient-2026-04-19.md` — a 1B Cognitive bulk + rebuild run at version 1.7.22, before:

- **ADR-031 Dispose gate** (1.7.32) — collapsed read-only Dispose from 14 min to 0.84 s on 1 B Cognitive
- **ADR-032 radix external sort for rebuild** (1.7.39+) — 10.5× faster rebuild at 100M (Reference; Cognitive impact unmeasured)
- **ADR-033 bulk-load radix** (1.7.43) — 3.92× faster combined at 1B (Reference; Cognitive shape similar but unmeasured)
- **Property-path hardening** (1.7.46/1.7.47) — 12 cancellation token sites, 12 grammar gap fixes, 1 Case 2 binding bug, all in shared SPARQL execution path used by both profiles

Cognitive is the workload that motivates Sky Omega 2.0 (Lucy long-term memory, James orchestration, Sky language layer). Without recent Cognitive measurements, we cannot answer:

1. Did the recent substrate work regress or improve Cognitive performance?
2. What is the current Cognitive write throughput? The 1.7.22 baseline was ~50K triples/sec; that number is now stale.
3. How does Cognitive perform on the bitemporal-dense access patterns the cognitive layers will produce (AS OF, DURING, ALL VERSIONS)?
4. Where are the binding bottlenecks in Cognitive — same as Reference (write amplification, atom-store hash drift), or different (WAL contention, per-session-graph cardinality)?

The drought is a Triggered limit (not Latent) because it actively constrains decisions: any architectural choice between "ship Cognitive feature X" vs "ship Reference feature Y" is currently weighted by the implicit assumption that Cognitive is OK. We don't have the data to defend that assumption.

## Trigger condition

Already triggered. Effects:

- ADR-008 attribution discipline reveals the gap when applied to existing validation runs — every row on the STATISTICS scale-validation table from 2026-04-19 onward needs the Reference profile tag, with no Cognitive counterpart.
- External comparison conversations (the QLever discussion) cannot be scoped to Cognitive workloads with current evidence.
- Sky Omega 2.0 cognitive-component readiness reviews (when they begin) will bottleneck on this.

## Current state

The Cognitive profile is still load-bearing in the codebase — `TemporalQuadIndex`, the bitemporal `LogRecord`, valid-time + transaction-time storage in B+Tree leaves, the AS OF / DURING / ALL VERSIONS query parser branches, the predicate-statistics maintenance during Cognitive Dispose, all live and exercised by the test suite (1,586 SPARQL tests pass on Cognitive). The substrate is correct.

What is missing is performance measurement at scale against the current 1.7.47 substrate. A small set of gradient runs (1M / 10M / 100M Cognitive bulk + rebuild + query) would close the gap without committing to a full 1B Cognitive run (which is the rough analogue of Phase 6 for Cognitive).

## Candidate mitigations

In rough order of cost / payoff:

1. **Small gradient: 1M / 10M / 100M Cognitive against 1.7.47.** Mirrors the historical pattern (`docs/validations/bulk-load-gradient-2026-04-17.md`, `full-pipeline-gradient-2026-04-19.md`). ~12-24 hours wall-clock for the three scales combined. Validates current Cognitive performance, identifies regressions if any, produces an artifact for ADR-008 attribution.
2. **WDBench against Cognitive.** Run the same WDBench paths+c2rpqs queries against a Cognitive-profile store of the same Wikidata data. Direct comparison of executor cost on the two profiles. Substantial wall-clock (Cognitive bulk-load of full Wikidata is the analogue of Phase 6's 85h, almost certainly longer because Cognitive carries the bitemporal columns).
3. **Cognitive-shaped synthetic benchmark.** A workload generator producing James-style query patterns (small named graphs, AS OF queries with varying time anchors, mutation churn at session boundaries). More representative of the actual Cognitive workload than ad-hoc Wikidata SPARQL. Takes design effort upfront.
4. **Defer until Sky Omega 2.0 cognitive-component work begins.** The cognitive components (Lucy, James) will exercise Cognitive workloads naturally. Wait for that work to drive the measurement. The cost: every architectural decision between now and then is made on stale Cognitive data.

Path (1) is recommended as the immediate mitigation. Path (3) is the right long-term direction but requires substrate work in `benchmarks/Mercury.Benchmarks/`. Path (4) is the wrong choice — too much Reference-targeted substrate work has shipped to defer Cognitive measurement further.

## Why this matters beyond the metric gap

Two architectural risks are currently latent:

1. **Cognitive may have regressed unnoticed.** Property-path hardening, walker rewrite, Case 2 binding fix — all touch the shared SPARQL execution path. The Reference WDBench rerun confirms correctness on Reference. Cognitive correctness is asserted by the 1,586 SPARQL test suite, but performance-shape-changes are not caught by unit tests.
2. **The "Sky Omega" claim drifts.** Without Cognitive numbers, public-facing claims about the project cite Reference numbers exclusively. ADR-008 mandates profile-tagging; this entry surfaces the asymmetry that mandate exposes.

## References

- ADR-008 — Workload Profiles and Validation Attribution (companion ADR; this entry is the first triggered limit it surfaces)
- `docs/validations/full-pipeline-gradient-2026-04-19.md` — most recent Cognitive measurement (1.7.22)
- `docs/validations/adr-031-dispose-gate-2026-04-21.md` — most recent Cognitive-relevant validation (Dispose-only, narrow scope)
- ADR-031 (mercury) — Dispose gate; the only post-2026-04-19 ADR that touches Cognitive directly
- `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md` — the meta-pattern memo that prompted the sweep that surfaced this
