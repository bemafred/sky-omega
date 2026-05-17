# Sky Omega Limits Register

Known limits, scaling thresholds, and deferred decisions that have been *characterized* but not *acted on*. Each entry sits between Emergence (the unknown surfaced) and Engineering (the decision committed). They are deliberate non-decisions, not forgotten work.

## Why this directory exists

ADRs capture decisions. Validations capture measurements. Neither is the right home for "we know this exists, we know roughly when it would matter, and we have candidate fixes — but we are not acting on it now."

Such items, when buried in an ADR's Consequences or Open Questions section, become invisible after the ADR is marked Completed. This register surfaces them by design. Every limit is one short file; the index below makes the full set visible at a glance.

## Format

Each entry follows a lightweight template:

```markdown
# Limit: <title>

Status:        Latent | Monitoring | Triggered | Resolved
Surfaced:      <date>, via <link to validation, ADR, or experience>
Last reviewed: <date>
Promotes to:   <when this should become an ADR — concrete trigger condition>

## Description
## Trigger condition
## Current state
## Candidate mitigations
## References
```

Borrows from ADR but lighter — no Decision section, no alternatives-considered, no implementation plan. Those land in the ADR if and when the limit is promoted.

## Status meanings

| Status | Meaning |
|---|---|
| **Latent** | Known to exist, not currently affecting any production path. |
| **Monitoring** | Approaching a trigger condition; periodic measurement warranted. |
| **Triggered** | Now affecting a real workload. Promotion to ADR overdue. |
| **Resolved** | Addressed by an ADR or by changed circumstances. Kept in the register for paper trail. |

## Active entries

| Limit | Status | Trigger condition |
|---|---|---|
| [Predicate-statistics memory](predicate-statistics-memory.md) | Latent | `CollectPredicateStatistics` running on a Cognitive store > ~5 B triples, or any non-bulk write path on a 21.3 B Reference store |
| [Hash function quality](hash-function-quality.md) | Latent | Need for sustained ingest throughput improvement once schema-reduction wins (ADR-029) are banked, OR adversarial-input concerns surface |
| [Bit-packed atom IDs](bit-packed-atom-ids.md) | Latent (re-affirmed 2026-05-01) | Deferral re-confirmed in Phase 7c Round 2 review: 1B trace shows GSPO write at 0.45% of FlushToDisk; storage savings real (~340-680 GB) but not binding on 8 TB target; substantial implementation cost. |
| [B+Tree index mmap remap](btree-mmap-remap.md) | Latent | Single store past 1 TB of B+Tree data (~33B ReferenceKey entries) or incremental workloads that cannot plan size at open time |
| [Bulk-load memory pressure](bulk-load-memory-pressure.md) | Latent | Bulk-load swap activity correlating with throughput drop, OR host with < 128 GB RAM, OR scale past full Wikidata 21.3B |
| [Sorted atom store for Reference](sorted-atom-store-for-reference.md) | **Resolved** | Resolved by ADR-034 Phase 1 (1.7.30, 2026-04-23) and ADR-039 Phase 2 BBHash MPHF (1.7.55, 2026-05-12). Production-validated cycle 8/9/10 r4 + truthy r1. Phase 3 delta-plus-merge remains deferred and would be its own ADR. |
| [Reference read-only mmap](reference-readonly-mmap.md) | Latent | Reference query-side latency or per-process memory footprint becomes binding, OR cross-process shared query becomes a use case, OR SortedAtomStore work ships and bundles the seal/reopen pattern |
| [Streaming source decompression](streaming-source-decompression.md) | **Resolved** | Resolved by [ADR-036](../adrs/mercury/ADR-036-bzip2-streaming-decompression.md) substrate streaming `.bz2` shipped 1.7.45 (2026-04-29). `.ttl.bz2`-as-canonical-source workflow adopted Phase 7. Production-validated cycle 8/9/10 r4 (`.ttl.bz2`) + truthy r1 (`.nt.bz2`) + WGPB step C (`.nt.bz2`). |
| [Rebuild progress observability](rebuild-progress-observability.md) | Latent | Rebuild silent phase exceeds ~1 hour, OR automation/CI needs stuck-vs-progressing detection, OR a future architectural change introduces another silent phase |
| [Metrics coverage review](metrics-coverage-review.md) | Latent (review) | Catalog of 8 categories of observability gaps. Individual categories split out into their own entries when triggered (rebuild progress already split). |
| [Per-index subdirectory layout](per-index-subdirectory-layout.md) | Latent | Two-SSD utilization (WAL + data split, or per-index placement), OR backup/replication wanting per-index granularity, OR profile-specific layout asymmetries |
| [Cancellable executor paths](cancellable-executor-paths.md) | **Resolved** | Resolved 1.7.46 (commit `527016f`, 2026-04-29). 12 cancellation-token gaps closed; every property-path inner loop and B+Tree leaf walk samples `QueryCancellation.ThrowIfCancellationRequested()` per `MoveNext()`. WDBench 1.7.47 cold baseline: every one of 655 timeouts closed 60.000–63.620 s. Re-validated cycle 9, cycle 10 r4, truthy r1, WGPB step C — 0 violations across 8,564 unique query × substrate executions. |
| [Property-path grammar gaps](property-path-grammar-gaps.md) | **Resolved** | Resolved 1.7.47 (commit `1be7a4d`, 2026-04-30) via option B compositional refactor (`ParsePathPrimary` + `ParsePathExpr`). All three failure shapes accepted; W3C SPARQL 1.1 conformance suite unaffected. WDBench 1.7.47 cold baseline: 0 parse failures across 1,199 queries. Re-validated cycle 9, cycle 10 r4, truthy r1 + extended coverage, WGPB step C — 0 parser failures across paired full+truthy WDBench matrix. |
| [BZip2 decompression single-threaded](bz2-decompression-single-threaded.md) | **Resolved** | Resolved by [ADR-036 Phase 2](../adrs/mercury/ADR-036-bzip2-streaming-decompression.md) (commit `a11f873`). Measured ceiling 2.62× (not the projected ~9×) — scanner-bound, not memory-bandwidth-bound. Workload-separated verdict: parallel bz2 is load-bearing for convert path (~57% wall-clock reduction), irrelevant for bulk-load path (parser-bound at 17.5 MB/s decompressed; single-threaded bz2 already exceeds). |
| [AtomStore prefix compression](atomstore-prefix-compression.md) | **Resolved** | Resolved 2026-05-01 by ADR-034 Round 2 (commit `870d31b`). Delta-encode each atom against predecessor in sort order; anchor every 64th atom for bounded reconstruction. Measured 53% atoms.atoms reduction at 1M Wikidata (better than QLever's published 45%). Projected ~75 GB recovered at 21.3B. |
| [Cognitive profile validation drought](cognitive-profile-validation-drought.md) | **Resolved (cycle 10 Phase 0, 2026-05-09)** | Cognitive 1M/10M/100M gradient against 1.7.50 closed the drought. 100 M ran at 256 K/sec (substantially faster than 1.7.22 baseline); atom-store probe distance max=4 (vs > 50 trigger); 0 correctness failures. Shared infrastructure (ADR-028, ADR-031, ADR-035, ADR-036, property-path refactor) verified non-regressing on Cognitive code path. See [cycle10-phase0-cognitive-gradient-2026-05-09.md](../validations/cycle10-phase0-cognitive-gradient-2026-05-09.md). |
| [External-merge intermediate disk pressure](external-merge-intermediate-disk-pressure.md) | Latent (Monitoring) | Measured peak intermediate > ~3 TB on live r1 disk-trace, OR target host with < 4 TB free during ingest, OR external benchmark publication vs QLever/Blazegraph/Virtuoso surfaces the architectural intermediate-volume gap. Round 2 mitigation candidate: apply Round 2 prefix-compression to chunk records (same algorithm, intermediate layer). |
| [Cognitive orchestrator absent](cognitive-orchestrator-absent.md) | Latent (mitigated by human-in-the-loop) | Reduced human supervision becomes operational, OR LLM-assertion rate exceeds reviewer bandwidth, OR James becomes a published deliverable, OR cross-instance Sky Omega epistemic exchange becomes a use case. Mitigation: build James as a substrate-gating layer; pre-James harness EEE-enforcement scaffolding. |
| [Single-threaded spill blocks parser](spill-blocks-parser.md) | **Resolved (production-validated cycle 9)** | ADR-037 pipelined spill: cycle 9 measured `parser_blocked = 78.9 ms / 0.000236 %` across 9 h 18 m parser wall-clock at 21.3 B (vs cycle 8's projected 5 h / 38 % sequential). Parser wall-clock −4 h 57 m. See [adr-037-cycle9-21b-2026-05-09.md](../validations/adr-037-cycle9-21b-2026-05-09.md). |
| [Observability coverage gap](observability-coverage-gap.md) | Latent (mitigated by post-hoc reconstruction) | Any scale-defining run, OR new long-running operation added, OR external characterization publication, OR Sky Omega 2.0 milestone (James cannot gate against absent reporting). Mitigation: observability-coverage map shipped (`docs/architecture/technical/observability-coverage.md`); discipline = every >1-min operation emits progress; cycle 7 needs `MergeAndWrite` instrumentation before launch. |
| [Intermediate cleanup deferred to run end](intermediate-cleanup-deferred-to-run-end.md) | **Resolved (production-validated cycle 9)** | 1.7.49 cleanup hook: cycle 9 measured `chunks_deleted: 3,923`, `chunk_bytes_reclaimed: 3.96 TB` released at end-of-merge. Manual intervention requirement structurally eliminated. See [adr-037-cycle9-21b-2026-05-09.md](../validations/adr-037-cycle9-21b-2026-05-09.md). |
| [Trigram drain cap eviction](trigram-drain-cap-eviction.md) | Latent (Monitoring — cycle 8 measured) | Round 2 substrate planning targets rebuild wall-clock, OR future scale pushes chunk count > 30K (drain dominated by misses), OR external benchmark publication. ~3.4 h overhead at 21.3 B from ~23% miss rate (10,456 chunks vs 8K cap). Mitigation: larger trigram chunks (1 GB → ~2,000 chunks ≪ cap) or hierarchical merge. |
| [ExternalSorter FD pool bypass](externalsorter-fd-pool-bypass.md) | **Retracted** (false alarm, 2026-05-16) | Speculative claim — code review confirms the pool IS engaged on the trigram-drain path via `ExternalSorter.ChunkReader.RefillBuffer → _pool.Get(_path)`. The ~8,192 concurrent FDs at cycle 10 r4 were the pool running at its documented 8K cap with LRU eviction operating as designed. Actual eviction-overhead concern is captured by [trigram-drain-cap-eviction](trigram-drain-cap-eviction.md). |
| [N-Triples parser per-triple performance](ntriples-parser-per-triple-perf.md) | **Resolved-Partially** (1.7.59, 2026-05-16) | Tier 2 profile (2026-05-16) localized the gap to two contributors: (1) shipped — `NTriplesStreamParser.Peek()` missing `AggressiveInlining` (same annotation Turtle's `Peek` had since 1.7.4); fix produced +6.0 % end-to-end / +7.4 % steady-state on 10M-triple bulk-load. (2) deferred Latent — Options B (vectorized `IndexOfAny` IRI body scan) and C (`ConsumeNonNewline` specialization). Remaining ~25 % steady-state gap is grammar-inherent (~6× more source bytes per N-Triples triple). |
| [Runtime FD detection deferred](runtime-fd-detection.md) | Latent | Linux production deployment (typical FD limit 65K — 8K cap leaves ~57K unused), OR Round 2 picks runtime-detection as the path for trigram drain, OR cross-platform substrate validation. Mitigation: `getrlimit(RLIMIT_NOFILE)` via P/Invoke at pool construction; fallback to 8K constant. |
| [Observability discipline systematic not reactive](observability-discipline-systematic-not-reactive.md) | **Triggered** (cycle 9 drain — third recurrence; mitigation queued for cycle 10 Phase 1 as DrainProgressEvent + audit) | Cycle 6 merge silent → fixed reactively in cycle 7; cycle 8 trigram drain silent → not fixed; cycle 9 GSPO drain silent during ~1 h 40 m drain phase. The discipline rule is correct; its application is reactive, not proactive. Mitigation in [cycle 10 plan](../roadmap/cycle-10-multi-fix-plan.md) Phase 1: ship DrainProgressEvent (sibling MergeProgressEvent) + cross-reference audit of `bulk-load-flow.md` stages × `IObservabilityListener` event coverage. |
| [Metric emission backpressure on shared disk](metric-emission-backpressure-on-shared-disk.md) | **Triggered** (cycle 9 trigram drain, 2026-05-08) | A phase emits progress events into the same disk being saturated by the workload's mmap I/O. JSONL writer's small writes queue behind the workload's bytes; metric file lags 2+ hours during heavy phases. Phase has an emit point (sibling-entry checkbox passes), but the channel doesn't flow during the phase. Mitigation: time-based emission throttle, or `--metrics-out` to a different disk, or in-memory burst flush with explicit timer, or out-of-process tap. |
| [GRAPH-clause parser drops property-list shorthand continuations](property-list-shorthand-projection.md) | **Resolved** (fixed 1.7.71, 2026-05-17) | The non-GRAPH `;` handler in `TryParseTriplePattern` was correct; the GRAPH-clause parser (`ParseGraph` in `SparqlParser.Clauses.cs`) was a separate code path that lacked the `;` continuation handler — second pattern silently dropped. Fix: copied the `;` handler into both `ParseGraph` pattern-parsing loops. `Select_PropertyListShorthandInGraphClause_ProducesTwoTriplePatterns` (was failing) now passes; 4,459 Mercury tests green; end-to-end CLI repro confirms both bindings render. |
| [URI atom corruption from pre-1.7.72 INSERT with `\"` literal](atom-store-corruption-from-failed-literal-insert.md) | **Triggered** (URI-specific; workaround documented) | When INSERT DATA stored a literal containing `\"` under Mercury 1.7.69/70/71 (before the `GetLexicalForm` `IndexOf`-vs-`LastIndexOf` fix), the literal was truncated and the URI's atom-store state corrupted. After 1.7.72, multi-property INSERTs to that URI silently drop all but one triple, despite `affected count` reporting the full input. Affects legacy stores only — fresh URIs and post-1.7.72 stores work correctly. Investigation candidates: span-aliasing in `UpdateExecutor.ExpandPrefixedName._expandedTerm`, tombstone interaction, or atom-store ID divergence. Workaround: use a fresh URI when multi-property INSERT silently drops. |
| [SPARQL UPDATE literal escape canonicalization](sparql-update-literal-escape-canonicalization.md) | **Promoted** to [ADR-044](../adrs/mercury/ADR-044-sparql-update-literal-canonicalization.md) (2026-05-17) | SPARQL parser stores literals verbatim (`\"` preserved as two chars); streaming parsers (Turtle, N-Triples, N-Quads, TriG) decode escapes at parse time. Same logical RDF triple ingested via the two paths produces distinct atoms with different lexical forms; cross-format queries silently disagree. The 1.7.72 `GetLexicalForm` `LastIndexOf` fix patched downstream consumers but did not close the root-cause asymmetry. Promoted to ADR-044 (Proposed) on the same day as surfacing; architectural divergence was clear from code reading. |
| [SPARQL ResolveTerm-family duplication across pattern operators](sparql-resolve-term-family-duplication.md) | Latent (characterized during ADR-044 Phase 0, 2026-05-17) | Four near-identical `ResolveTerm`-shaped methods (`ResolveSlotTerm`, `ResolveTerm`, `ResolveTermWithStorage`, `ResolveTermForQuery`) plus a fifth-cousin `ExpandPathPredicate` share the same prefix-expansion logic but diverge on five feature axes (synthetic terms, blank nodes, numeric literal expansion, typed value formatting, single vs position-specific scratch buffer). ADR-044 Phase 0 consolidation attempted; substantive feature divergence triggered the ADR's fallback to per-site canonicalization edits. Duplication held here as separate debt. Promotes to ADR when a fifth `ResolveTerm`-shaped method appears OR a bug surfaces from the duplication. |

## Adding a new entry

1. Create `docs/limits/<short-name>.md` using the template above.
2. Add a row to the table in this README under "Active entries".
3. Reference the entry from any ADR that surfaced or implies it, so the ADR's reader can follow the trail.
4. Set `Surfaced` to the date the limit was characterized, not the date you wrote the entry.

## Promoting an entry to an ADR

When a limit moves to Triggered (or pre-emptively when a project-level decision is being made):

1. Draft an ADR that captures the decision (Proposed status, normal ADR workflow).
2. Update the limit's Status to Resolved.
3. Add a "Promoted to: ADR-NNN" line.
4. Move the row from "Active entries" to a "Resolved" section (created if needed).
5. Do not delete the file — the historical record matters.
