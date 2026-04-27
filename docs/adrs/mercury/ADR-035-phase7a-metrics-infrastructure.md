# ADR-035: Phase 7a Metrics Infrastructure

## Status

**Status:** Completed — 2026-04-27 (1.7.45; 1 B Reference end-to-end validation in [`docs/validations/adr-035-phase7a-1b-2026-04-27.md`](../../validations/adr-035-phase7a-1b-2026-04-27.md) — 22,256-record JSONL artifact spans every Phase 7a metric channel)

## Context

Phase 6 (21.3 B Wikidata, 1.7.44) closed the production-hardening arc with the substrate validated end-to-end. Phase 7 — performance rounds — picks up next, gated by a hard ground rule from `docs/roadmap/production-hardening-1.8.md`: *"no perf round merges without the relevant metric in place to demonstrate the win. Estimates are not measurements."*

Phase 7a is the metrics-infrastructure foundation that makes that rule enforceable. The seven characterized rounds in `docs/limits/` (ADR-034 SortedAtomStore being the largest projected single win) all need before/after JSONL artifacts. The cold WDBench baseline at 7c start needs the same instrumentation to produce externally-comparable distribution data. The post-Phase-6 trace pass needs richer emission to rank rounds by observed impact rather than article-time projection.

`docs/limits/metrics-coverage-review.md` catalogs eight observability gap categories (A–H). This ADR ships three eagerly (A, B, G) and defers five (C, D, E, F, H) for trigger-driven slot-in. The cross-cutting infrastructure (umbrella listener, HdrHistogram-mini, scope correlation, schema versioning, periodic-state-emission timer) lands up front in the same increment that introduces the umbrella interface — on the principle that infrastructure with named consumers is not subject to YAGNI.

### Why eager-three-only, not all-eight

Five categories (C, D, E, F, H) have no Phase 7c consumer named today. Building them speculatively is the YAGNI case `metrics-coverage-review.md` itself recommends against. The umbrella interface (Decision 1) is what makes deferred slot-in cheap: each future category lands as a new event method on `IObservabilityListener` with a default no-op, plus a `JsonlMetricsListener` override. No structural rewrites.

### Why the cross-cutting infrastructure lands up front

Three components have named consumers across the eagerly-shipped increments:

- **HdrHistogram-mini** — consumed by 7a.3 (probe-distance histogram), and the right tool for any latency distribution that lands later.
- **Scope correlation** — the only mechanism that ties enter/exit events together for nested operations. Adding it later means re-threading every emission site.
- **Periodic state-emission timer** — required by 7a.2 (RSS / GC / disk-free as periodic state, not per-operation events). Without it, 7a.2 has to invent its own timer.

Plus two zero-cost-but-hard-to-add-later pieces: the `schema_version` JSONL field, and the umbrella interface migrated **in-place** (rather than parallel-then-converge, which leaves drift to clean up).

Named consumer ⇒ build now. This ADR is the first place the principle is applied at decision granularity.

## Decision

### 1 — `IObservabilityListener` umbrella interface (in-place migration)

Introduce `IObservabilityListener` in `Mercury.Diagnostics` with default-noop methods covering every event type across the eagerly-shipped categories: `OnQueryMetrics`, `OnRebuildPhase`, `OnRebuildProgress` (new), `OnLoadProgress`, `OnGcEvent`, `OnLohDelta`, `OnRssState`, `OnDiskFreeState`, `OnAtomInternRate`, `OnAtomLoadFactor`, `OnAtomProbeDistance`, `OnAtomRehash`, `OnAtomFileGrowth`.

Existing `IQueryMetricsListener` and `IRebuildMetricsListener` are retained as derived specializations for callers wanting a subset only. `JsonlMetricsListener` migrates to implement `IObservabilityListener` directly. Existing call sites in `RdfEngine.LoadFileAsync`, `QueryExecutor`, `RebuildReferenceSecondaryIndexes` rebind to the umbrella's specialized methods. Backwards compatibility is preserved through derived-interface retention.

### 2 — HdrHistogram-mini (BCL-only, ~150 lines)

Introduce `Mercury.Diagnostics.LatencyHistogram` — BCL-only bucketed reservoir supporting p50/p95/p99/p999. Power-of-two bucket exponents, configurable max value, atomic increment via `Interlocked` for cross-thread emission. Validation test compares against scipy/numpy reference percentiles on a synthetic distribution; max bucket-aliasing error documented at 1% at p99.

Built up front because 7a.3 needs it and any future latency-distribution consumer (Category E) reaches for the same primitive.

### 3 — Scope correlation pattern

Introduce `MetricsScope` as a `readonly struct` implementing `IDisposable`, emitting enter/exit events with a 64-bit scope ID and parent reference for nested correlation:

```csharp
using var scope = MetricsScope.Begin("rebuild.gpos");
// emission within the scope carries scope.Id + scope.ParentId
// scope.Dispose() emits the exit event
```

Used by 7a.1 (rebuild sub-phase correlation) and required by any future operator-pipeline tap (Category E).

### 4 — Schema versioning on every JSONL record

Every record carries a top-level `"schema_version": "1"` field. Future schema changes increment the version with documented migration notes. Downstream consumers (`jq` filters, Grafana dashboards) adapt without breaking.

### 5 — Periodic state-emission timer

When `--metrics-out` is enabled, a background timer (default 10s, configurable) emits state-class records: current RSS, GC counts, disk-free, atom-store load factor. State events carry `"kind": "state"`; per-operation events carry `"kind": "event"`. State-record emission writes to a bounded channel; the timer reads/emits from the channel rather than blocking hot threads (e.g., the bulk-load loop).

This is the mechanism Categories G and B (load-factor) plug into. Without it, periodic-state metrics would invent their own timers or wedge into per-operation hooks.

### 6 — Throttling discipline codified

The bulk-load progress pattern becomes the single discipline across all per-record emission:

- Per-record JSONL write on every event (no throttling on JSONL output)
- Terminal display throttled to ~10s (configurable via `--metrics-display-interval`)
- Rate calculations use a sliding window of the last 10 records

Documented in a new `docs/architecture/technical/metrics-emission.md`.

### 7 — Three eagerly-shipped increments after 7a.0

After 7a.0 lands, the three eager categories ship in dependency order:

- **7a.1 — Category A (rebuild progress).** Fully specced in `rebuild-progress-observability.md`; ~50–100 LoC integration. First because the pattern is fully understood and exercises the umbrella under realistic load.
- **7a.2 — Category G (process-level inline emission).** ~50 LoC across GC/LOH/RSS/disk-free emitters. Second because it's the cheapest broad win and proves Decision 5's timer.
- **7a.3 — Category B (atom-store metrics).** Heaviest of the three (probe-distance histogram + multiple discrete event types). Last because the infrastructure has settled across two prior increments.

### 8 — Categories C, D, E, F, H deferred trigger-driven

Each of the five deferred categories slots into the umbrella as new event methods + new `JsonlMetricsListener` overrides. Promotion criteria are documented per-category in `metrics-coverage-review.md`. When a Phase 7c round or external workload makes a deferred category load-bearing, that round opens a follow-up commit — no separate ADR, the umbrella is the contract.

## Consequences

### Positive

- **Phase 7c rounds become measurable.** Every characterized round in `docs/limits/` has the metric surface to produce a before/after JSONL artifact. The roadmap rule becomes enforceable.
- **Cold WDBench baseline gets honest distribution data.** 7c-start baseline records median/p95/p99/p999 via Decision 2's histogram, not just averaged latency.
- **Single registration point for observers.** `IObservabilityListener` collapses listener-sprawl. New observers (Grafana exporters, dashboards) implement one interface.
- **Deferred categories slot in cheaply.** Default-noop pattern means C/D/E/F/H land as 50–100 LoC each when triggered, no structural rewrites.
- **Infrastructure-no-YAGNI principle codified.** Future infrastructure work has a precedent.

### Negative

- **7a.0 is the largest single PR in Phase 7a.** Umbrella migration + histogram + scope + timer + schema versioning + throttling doc lands together. ~600–900 LoC + tests. Larger than typical Mercury PRs but the alternative (split across 7a.1–7a.3) is exactly the two-pass retrofit the YAGNI principle rejects.
- **No externally-visible payoff from Phase 7a.** Ingestion and query numbers don't move. Investment is foundation for Phase 7c — measurable only after the first round (likely SortedAtomStore, ADR-034) ships.
- **Backwards-compat constraint pins the JSONL schema.** Once `schema_version=1` ships, breaking changes need migration. Worth the discipline; tightens the design.

### Risks

- **HdrHistogram-mini correctness.** A from-scratch percentile estimator can drift from reference HdrHistogram on edge cases. Mitigation: validation test against scipy/numpy reference; documented bucket-aliasing bounds (1% max at p99).
- **Scope correlation overhead.** `MetricsScope.Begin` could allocate if implemented as a class. Mitigation: `readonly struct` with no boxing on the using-pattern dispatch. Bench at 7a.0 close.
- **Periodic timer thread contention with bulk-load.** A timer firing every 10s during a 50-minute bulk could interact badly with the hot path. Mitigation: bounded channel between emission sites and the timer thread; timer never blocks the producer.
- **In-place migration breaks an external consumer.** Subscribers to `IRebuildMetricsListener` / `IQueryMetricsListener` continue working (derived interfaces retained). External JSONL parsers without `schema_version` continue working (additive change). Risk minimal; flagged for completeness.

## Implementation plan

**Phase 1 — 7a.0 infrastructure**
- `IObservabilityListener` interface in `Mercury.Diagnostics`
- `LatencyHistogram` (HdrHistogram-mini) + percentile-validation test
- `MetricsScope` struct + correlation tests
- `JsonlMetricsListener` migration to umbrella + `schema_version` + periodic timer + bounded channel
- Throttling discipline doc (`docs/architecture/technical/metrics-emission.md`)
- Existing call sites (`RdfEngine`, `QueryExecutor`, `RebuildReferenceSecondaryIndexes`) rebound
- Status Proposed → Accepted after 7a.0 ships and existing listeners verified working

**Phase 2 — 7a.1 rebuild progress (Category A)**
- `OnRebuildProgress` event on the umbrella
- Sub-phase identification in `RebuildReferenceSecondaryIndexes` (emission/drain for GPOS and trigram)
- Estimated total via `_gspoReference.QuadCount`
- CLI throttled terminal lines + estimated-completion line
- Tests: 10 M Reference rebuild emits per-1M progress in JSONL

**Phase 3 — 7a.2 process-level emission (Category G)**
- GC event emitter via `GC.RegisterForFullGCNotification` / `GC.GetGCMemoryInfo()`
- LOH delta tracking via `GC.GetTotalAllocatedBytes`
- RSS / working-set periodic emission via the timer
- Disk-free monitor inline
- Tests: synthetic GC pressure produces matching event records

**Phase 4 — 7a.3 atom-store metrics (Category B)**
- Cumulative Intern count + rate via the timer
- Hash-table load factor periodic state metric
- Probe-distance histogram (consumes `LatencyHistogram`)
- Rehash event emission (timestamp, before/after size, duration)
- Atom-data-file `SetLength` event emission
- Tests: a 100K Intern run produces histogram + rate records

**Phase 5 — Phase 7a close**
- Status Accepted → Completed when all four phases land and a 1 B Reference end-to-end run produces the full JSONL artifact set
- Validation doc: `docs/validations/adr-035-phase7a-2026-XX-XX.md` showing the artifact shape

## Open questions

- **Should `LatencyHistogram` be public surface?** Currently planned `internal`. Phase 7c rounds emit through `JsonlMetricsListener`; external consumers read JSONL. If a downstream agent wants to construct histograms in-process, making it public is cheap. Defer until a consumer asks.
- **Profile-conditional emission audit.** Some Category B metrics (rehash) are common to both profiles; some Category F metrics (WAL) are Cognitive-only. The umbrella stays profile-agnostic; the listener decides what to emit. Verify on close that no Cognitive-relevant metric got accidentally Reference-gated.
- **Periodic timer thread model.** Own-thread (`System.Threading.Timer` with fresh queue) is more predictable for long bulks; ThreadPool is cheaper at idle. Default: own thread, revisit if benchmarks show cost.
- **Where does `metrics-coverage-review.md` live after 7a closes?** It remains canonical for the deferred categories. Mark A/B/G entries as "Promoted — see ADR-035" but keep the document as the master register.

## References

- [`docs/limits/metrics-coverage-review.md`](../../limits/metrics-coverage-review.md) — eight observability gap categories
- [`docs/limits/rebuild-progress-observability.md`](../../limits/rebuild-progress-observability.md) — Category A spec, mostly-final
- [`docs/limits/sorted-atom-store-for-reference.md`](../../limits/sorted-atom-store-for-reference.md) — ADR-034 candidate; consumes Category B
- [`docs/limits/bit-packed-atom-ids.md`](../../limits/bit-packed-atom-ids.md) — Round 3 candidate; consumes Category B
- [`docs/roadmap/production-hardening-1.8.md`](../../roadmap/production-hardening-1.8.md) — Phase 7a/7b/7c sequencing
- [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) — original `IRebuildMetricsListener` source
- Existing diagnostics: `src/Mercury/Diagnostics/JsonlMetricsListener.cs`, `IRebuildMetricsListener.cs`, `IQueryMetricsListener.cs`
