# Limit: Metrics coverage review — gaps in observability across the substrate

**Status:**        Latent (review document)
**Surfaced:**      2026-04-25, sweep prompted by the rebuild-progress gap surfaced during Phase 6's silent trigram phase. The rebuild-progress observability entry covered one specific gap; this entry catalogs the others.
**Last reviewed:** 2026-04-25
**Promotes to:**   This entry is itself a *review*. Individual high-priority items split out into their own limits entries (or directly into ADRs) as triggers fire. Use this as the seed catalog.

## What's covered today

| Subsystem | Metrics emission | When |
|---|---|---|
| Bulk-load (`RdfEngine.LoadFileAsync`) | `LoadProgress` callback: triples/sec, GC heap, RSS, elapsed | Per chunk-flush (every 100K triples), terminal-throttled to every 10s, JSONL per record |
| SPARQL `Query` | `IQueryMetricsListener.OnQueryMetrics`: parse/plan/exec time, rows, index path | Once per query, at completion |
| Rebuild phase | `IRebuildMetricsListener.OnRebuildPhase`: phase name, entries, elapsed | Once per phase (GPOS, Trigram), at phase end |
| Bulk-load summary | `phase=load.summary` JSONL record | Once at end of bulk |

Good coverage on **operation entry/exit boundaries** and **bulk-load steady-state progress**. Sparse coverage on **operation interiors**, **discrete events within a phase**, **state evolution over time**, and **storage-layer detail**.

## Categorized gaps

### A. Operation interior progress (high priority — already partially split out)

| Gap | Status | Cost | Notes |
|---|---|---|---|
| Rebuild phase interior (GSPO scan, chunk spills, drain) | **Already split** to [rebuild-progress-observability.md](rebuild-progress-observability.md) | Small (50-100 lines) | First triggered by Phase 6 trigram phase |
| `mercury --convert` (RDF format conversion) progress | Missing | Small | Already has parse-side progress via `LoadProgress`-like callback; output-side may not |
| Streaming `RdfEngine.LoadAsync` (single-shot, non-batched) | Missing | Small | Less common path but should mirror bulk |

### B. AtomStore metrics (high priority for atom-store work)

| Gap | What it enables | Cost |
|---|---|---|
| Cumulative `Intern` count + rate (per second, throttled) | Validates Round 2 sorted-atom-store wins | Small — single counter + listener event |
| Hash table load factor over time | Detects clustering before catastrophic regressions (1.7.16-style) | Small |
| Probe distance histogram (HdrHistogram) | Tail-latency tracking for atom interning under realistic workloads | Medium — needs HdrHistogram infrastructure |
| Rehash events (timestamp, before/after size, duration) | Quantifies ADR-028 rehash cost in production | Small — discrete event emission |
| Atom data file growth events (`SetLength` calls) | Correlates with bulk-load throughput dips | Small |

**Trigger**: when SortedAtomStore work begins (post-Phase-6) or when hash function quality is being tuned (the two limits both call for this same instrumentation).

### C. Trigram index metrics

| Gap | What it enables | Cost |
|---|---|---|
| `IndexAtom` rate during incremental writes | Compares Cognitive vs bulk path performance | Small |
| `QueryCandidates` per-query latency + result-set size | Production trigram-search workload understanding | Small |
| Posting list overflow events (hit `MaxPostingListSize` cap) | Detects "popular trigrams" data-skew issues | Small |
| Hash bucket fill rate | Correlates with `MaxProbeDistance` overflow risk | Small |

**Trigger**: when trigram FTS performance becomes a tuning target (e.g., a workload reports slow text searches).

### D. Storage-layer events

| Gap | What it enables | Cost |
|---|---|---|
| Index file growth (`SetLength` extensions on B+Tree files) | Pinpoints when/why disk fills mid-operation | Small |
| `Flush` / `msync` timing per index | ADR-031 work would have benefited from this; useful for any future durability tuning | Small |
| Page split events (B+Tree leaf splits, internal node promotions) | Validates AppendSorted's fast-path coverage; debugging tool | Small-medium |
| Page-cache hit rate | Past-RAM behavior diagnosis (currently inferred from iostat) | Medium — kernel access required, possibly via `vm_stat` parsing |

**Trigger**: any future durability/IO bug investigation; performance tuning at past-RAM scale.

### E. SPARQL execution detail

| Gap | What it enables | Cost |
|---|---|---|
| Per-operator row-count attribution | Query-planner cost-model validation | Medium — needs operator-pipeline taps |
| Filter selectivity (rows-in / rows-out per filter) | Query rewriting opportunity detection | Medium |
| Temporal predicate pushdown effectiveness (Cognitive) | Validates bitemporal query optimizations | Medium |
| SERVICE clause federation timing | Distributed-query observability | Medium |
| Latency p50/p95/p99/p999 (HdrHistogram) | SLA/throughput characterization for production deployments | Medium-large — needs histogram infrastructure |

**Trigger**: when query-planner work becomes a focus, or when production SPARQL workloads need understanding.

### F. WAL operations (Cognitive profile only)

| Gap | What it enables | Cost |
|---|---|---|
| `AppendBatch` rate | WAL throughput characterization | Small |
| Checkpoint events (start/end, entries replayed) | Already partial via `CheckpointInternal` activity; not yet emitted as a metric | Small |
| Recovery time on open | Cold-start performance for restart scenarios | Small |
| WAL file rotation events (if applicable) | Long-running session diagnostics | Small |

**Trigger**: when Cognitive-profile durability/recovery work becomes a focus.

### G. Process-level state (mostly redundant with OS but useful inline)

| Gap | What it enables | Cost |
|---|---|---|
| GC events (Gen 0/1/2 collections, pause times) | Correlates managed-heap pressure with operation slowdown | Small — `GC.RegisterForFullGCNotification` or `GC.GetGCMemoryInfo()` |
| LOH allocation events | Validates zero-GC discipline; spots regressions | Small — `GC.GetTotalAllocatedBytes` deltas |
| Working set / RSS over time | Already available via `Environment.WorkingSet`; emit periodically | Small — already in `LoadProgress`, extend to other paths |
| Disk free monitoring inline | Mercury already has `--min-free-space`; emit free-bytes per operation | Small |

**Trigger**: when production-scale deployments need first-class observability without out-of-band OS tooling.

### H. Health and sanity events

| Gap | What it enables | Cost |
|---|---|---|
| `StoreIndexState` transitions (PrimaryOnly → BuildingGPOS → Ready) | Correlates external observation with internal state machine | Small — already emitted to `index-state` file but not to metrics |
| Index integrity check results (when run) | Ops/automation visibility | Medium — needs an integrity check to exist |
| Cross-process gate acquire/release timing | Multi-process workload diagnostics | Small |
| MCP request handling (Mercury.Mcp / DrHook.Mcp) | Long-running MCP server observability | Medium — separate from core Mercury |

**Trigger**: production deployments where ops tooling is required.

## Priority ranking (post-Phase-6)

For the limits-register-driven progression after Phase 6, this is the order each block becomes useful:

1. **A — Rebuild progress** (already split to its own entry; first to ship in Round 2)
2. **B — AtomStore metrics** (composes with SortedAtomStore work, also Round 2)
3. **G — Process-level inline emission** (tiny effort, broad coverage; could ship at any time)
4. **D — Storage-layer events** (when next durability/IO debugging happens)
5. **C — Trigram detail** (when trigram performance becomes a tuning target)
6. **E — SPARQL operator detail** (when query-planner work begins)
7. **F — WAL detail** (when Cognitive durability work resumes)
8. **H — Health events** (when production deployments arrive)

## Cross-cutting concerns

These apply across most of the gaps:

- **HdrHistogram infrastructure.** ADR-030 originally proposed it as part of Phase 1 measurement. Wasn't built then. Many of the priority-B/C/E gaps want it (probe distance, query latency, filter selectivity). Worth a small standalone effort once any one of those ships, then reused across the rest.
- **Throttling discipline.** Match bulk-load's pattern: per-record JSONL on every event; terminal display throttled to ~10s. Keeps log volume sane during multi-hour runs.
- **Event-vs-state-vs-rate distinction.** Three different emission patterns:
  - **Events** (one-time, like rehash): emit a discrete record with timestamp + before/after fields
  - **State** (periodic snapshots, like RSS): emit every N seconds
  - **Rates** (cumulative + delta, like Intern/sec): track total + emit derived rate periodically
  Different listener-event signatures may be appropriate.
- **Profile-conditional emission.** Some metrics only meaningful for one profile (WAL only for Cognitive). The listener interface should pass enough context for consumers to filter.
- **Backwards compatibility.** New events on existing listeners: default no-op implementations. Existing JSONL consumers must keep working.

## Trigger condition for promoting any specific category

When that category's metrics would be load-bearing for an in-flight decision:

- Operation/throughput tuning where you need rate stability data → A, B, F
- Latency-sensitive workload SLA characterization → E, B (probe distance), F
- Failure post-mortem where you need to localize within a phase → A, D, G
- Production deployment requiring first-class observability without OS tooling → G, H
- Architectural change validation (Round 2 SortedAtomStore, Round 3 bit-packed IDs) → B + A

## Current state

Latent. Bulk-load + per-query metrics have been adequate for measuring the work to date, but each new validation arc has surfaced one or two specific gaps. This review consolidates them so future work has a clear menu rather than ad-hoc additions.

The natural moment to act is the post-Phase-6 trace pass: that pass identifies which categories are most binding for Round 2, and adding the relevant metrics emission lands alongside the optimizations they're measuring.

## Candidate mitigations (general patterns, applicable to many gaps)

1. **Add `IObservabilityListener` umbrella interface** with all the new event types as default-noop methods. `IRebuildMetricsListener` becomes a specialization. JsonlMetricsListener implements all of them.
2. **HdrHistogram BCL-only mini-implementation** (~100-200 lines). Sufficient for p50/p95/p99 with bucketed reservoir sampling. Not as feature-rich as the open-source HdrHistogram library but no dependency.
3. **`metrics::scope("name")` pattern** for nested operations. Like a using block that emits enter/exit events with a unique scope id; supports correlation across events.
4. **Periodic state emission via timer**. When `--metrics-out` is enabled, a background timer emits state records every N seconds. Doesn't need per-operation hooks.
5. **Documented metrics schema versioning.** JSONL records carry a `schema_version` field; consumers can adapt. Avoids breaking downstream tooling when adding fields.

## References

- ADR-030 § Scope boundary (`docs/adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md`) — originally proposed HdrHistogram and richer rebuild metrics; partially shipped in 1.7.31, partially deferred
- `IQueryMetricsListener` source: `src/Mercury/Diagnostics/IQueryMetricsListener.cs`
- `IRebuildMetricsListener` source: `src/Mercury/Diagnostics/IRebuildMetricsListener.cs`
- `LoadProgress` source: `src/Mercury/Diagnostics/LoadProgress.cs`
- `JsonlMetricsListener` source: `src/Mercury/Diagnostics/JsonlMetricsListener.cs`
- Sister limits entries:
  - [rebuild-progress-observability](rebuild-progress-observability.md) — the first split-out from this review (priority A)
  - [hash-function-quality](hash-function-quality.md) — would consume metrics from category B
  - [sorted-atom-store-for-reference](sorted-atom-store-for-reference.md) — Round 2 work that will need category B emission to validate
