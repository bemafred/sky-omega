# Metrics Emission Discipline

ADR-035 Phase 7a.0 codifies a single throttling and emission pattern across every Mercury observability surface. Every event emitter — the existing `LoadProgress`, `IQueryMetricsListener`, `IRebuildMetricsListener` paths and every new event type added through the `IObservabilityListener` umbrella — follows the same rules.

## Throttling

Three rates apply to every per-operation event emission:

1. **JSONL writes are unthrottled.** Every event records one JSONL line. Downstream consumers (`jq`, Grafana, ad-hoc analysis scripts) read the full stream; throttling at write-time would discard data that has no other home. Bulk-load throughput is the worst case; at 100K triples/sec with one JSONL record per 100K-triple chunk, JSONL volume is ~1 record/sec — never the bottleneck.

2. **Terminal display is throttled to ~10s.** The CLI's interactive progress lines (the `1.2 B / 21.3 B (5.6 %), 95K t/s` line that operators watch on a live run) update every 10 seconds. Configurable via `--metrics-display-interval`. Sub-10-second updates produce flicker; longer intervals leave operators wondering whether the process is alive.

3. **Rate calculations use a sliding window.** The "recent triples per second" displayed alongside the lifetime average uses the last 10 records. This catches phase transitions (peak-to-steady-state drift, GC pauses) that lifetime averages smooth over.

The reference implementation is `LoadProgress` (in `src/Mercury/RdfEngine.cs`) — every other emitter mirrors this discipline.

## Event vs state

Records carry `record_kind: "event"` or `record_kind: "state"`:

- **Events** are one-shot records emitted by a producer when something happens — a query completes, a rebuild phase ends, a rehash event fires, a B+Tree page splits. JSONL one record per occurrence.
- **State** is a periodic snapshot: current RSS, current GC counts, current disk-free, current atom-store load factor. The `JsonlMetricsListener` background timer (Decision 5) drives state emission; producers register via `RegisterStateProducer` and are invoked on each tick.

Both share the same JSONL schema and the same writer; the distinction lets downstream consumers filter (`jq 'select(.record_kind == "state")'`).

## Schema versioning

Every record carries a top-level `"schema_version": "1"` field (Decision 4). Future schema changes increment the version with a documented migration note. Rules:

- **Additive changes** (new fields on existing records, new record types) do *not* bump schema_version. Downstream consumers are expected to ignore unknown fields.
- **Breaking changes** (renamed fields, changed semantics, type changes) bump schema_version and document the migration in this file.
- **JSONL consumers should branch on schema_version** if and only if a documented migration affects them.

## Producer obligations

Producers emitting through `IObservabilityListener`:

- Construct the event record only when at least one listener is attached. The QuadStore's fan-out helpers (`EmitQueryMetrics`, `EmitRebuildPhase`, `EmitRebuildComplete`, `HasRebuildListener`) already gate construction; new emission sites should follow the same pattern.
- Use `MetricsScope.Begin(listener, "name")` for nested operations. Scope IDs propagate via thread-local; correlation across nested events is recovered by joining on `scope_id` and `parent_scope_id`.
- Use `LatencyHistogram` for any latency or distance distribution where p50/p95/p99/p999 are the load-bearing question. The existing `record_kind: "event"` shape carries the percentiles directly (atom_probe_distance is the canonical example); raw samples are not emitted.

State producers registered via `JsonlMetricsListener.RegisterStateProducer`:

- Are invoked on the timer thread. Production work (heavy GC sampling, file I/O for free-bytes lookup) is acceptable but should remain bounded — the timer fires every 10s by default and a slow producer will delay subsequent state records.
- Catch their own exceptions. The listener swallows producer exceptions (observability must never break the producer), but unhandled errors still appear in logs as "swallowed by JsonlMetricsListener" — handle gracefully on the producer side.

## What this is *not* yet

- **Backpressure.** A slow JSONL writer (full disk, slow remote sink) blocks the producer thread on the listener lock. Phase 7a.0 does not implement backpressure. If a real workload surfaces it, the bounded-channel mechanism in `JsonlMetricsListener` is the slot-in.
- **Multi-listener fan-out.** A QuadStore today carries one umbrella listener slot plus the two legacy slots. Multiple umbrella observers (e.g. JSONL + in-process Grafana exporter simultaneously) would require either a `CompositeObservabilityListener` or a list-of-listeners contract. Defer until a workload demands it.
- **Sampling.** No sampling discipline today: every event is recorded. At Phase 7c scale (21.3B-triple load with millions of atom interns) per-event JSONL would be wasteful; a sampling scheme (1-in-N or reservoir) is a future consideration when an emitter's volume becomes the bottleneck.

## References

- [ADR-035](../../adrs/mercury/ADR-035-phase7a-metrics-infrastructure.md) — the design decisions this document codifies
- `src/Mercury/RdfEngine.cs` — `LoadProgress` reference implementation
- `src/Mercury/Diagnostics/JsonlMetricsListener.cs` — schema_version, periodic timer, throttling
- `src/Mercury/Diagnostics/LatencyHistogram.cs` — HdrHistogram-mini
- `src/Mercury/Diagnostics/MetricsScope.cs` — correlation pattern
- `docs/limits/metrics-coverage-review.md` — eight observability gap categories (A–H); A/B/G ship in 7a, others slot in trigger-driven
