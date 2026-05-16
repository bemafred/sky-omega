# ADR-043: Metric emission decoupling — bounded staleness for live observability under shared-disk pressure

## Status

**Status:** Accepted — 2026-05-16 (Proposed 2026-05-16; Accepted 2026-05-16 — approach approved, ready for engineering; three-part decoupling (periodic FileStream.Flush + time-based emission throttle + separate-disk runbook) is the substrate-correct answer to the cycle 9 trigram-drain 2-hour staleness observation)

## Context

Cycle 9's trigram drain phase (2026-05-08 01:34 → 03:38 UTC) exhibited a 2+ hour visible-staleness gap in `rebuild.jsonl` while the substrate process was healthy and progressing at ~25 MB/min on `trigram.posts`. An operator monitoring the run in real time could not distinguish "stuck" from "slow" from "buffered" because the JSONL artefact stopped advancing — even as the underlying drain succeeded.

The symptom was the surfacing trigger for [`docs/limits/metric-emission-backpressure-on-shared-disk.md`](../../limits/metric-emission-backpressure-on-shared-disk.md). This ADR is the architectural answer.

### Measured baseline (cycle 9 trigram drain)

| Quantity | Value |
|---|---:|
| Drain throughput on `trigram.posts` | ~25 MB/sec mmap-driven SSD I/O |
| Metric emission rate (1 event per 1 M entries processed) | ~6 events/sec |
| Per-event serialized size | ~300 bytes |
| Metric write rate in isolation | ~1.8 KB/sec |
| Observed `rebuild.jsonl` staleness | **2+ hours** during the phase |
| Recovery | Events flushed in delayed bursts when workload I/O eased |

The metric channel's bytes/sec is trivial compared to the workload. The contention is on the SSD's I/O queue + write-back path, not on bandwidth. **The phase that most needs real-time observability is the phase where observability fails most.**

### Implementation surface as it exists today

`src/Mercury/Diagnostics/JsonlMetricsListener.cs` (verified 2026-05-16):

- Wraps a `Stream` (typically a `FileStream` opened to `--metrics-out` path) in a `StreamWriter` with `AutoFlush = true`.
- Per-write path: `Emit(event)` → JSON serialize → `_writer.WriteLine(json)` → StreamWriter's internal buffer auto-flushes after each call → bytes land in the underlying FileStream's buffer → eventually reach the OS page cache → eventually reach disk.
- Thread safety via a single `lock` on `_gate`. **No `Channel<T>`, no separate writer thread**, no explicit `_writer.BaseStream.Flush()` after each event.
- Optional `Timer` for periodic state-producer emission (default 30 s — orthogonal to event emission).

The limit document described a "bounded `Channel<T>` consumed by a single writer thread" — that was inaccurate to current code. The actual implementation is synchronous-under-lock with line-by-line OS handoff.

### Plausible mechanisms for the 2-hour staleness (not all verified)

1. **OS page-cache writeback deferral.** macOS APFS holds dirty pages in the unified buffer cache until kernel decides to flush. Under 25 MB/sec sustained mmap-driven writeback pressure from the workload, the kernel's writeback queue can starve small-writer flush requests. *Most likely.*
2. **StreamWriter's internal newline-buffer batching.** With `AutoFlush = true` the StreamWriter does flush its 4 KB buffer to the FileStream, but the FileStream itself has a 4 KB buffer that batches further. The chain `StreamWriter.AutoFlush → FileStream buffer → OS page cache → disk` has three buffering layers; staleness measured from any layer's perspective produces different numbers.
3. **Reader-side caching.** Tools like `tail -f` may use `inotify`/`kqueue` plus their own buffer state. If the observer's read-side caches the file at the wrong moment, mtime advances but read returns stale bytes. *Possible but not load-bearing.*

The exact mechanism is uncertain. The fix shape doesn't need to know — any fix that bounds the staleness at the *emit* side decouples from the workload-side contention regardless of which layer caches.

## Hypothesis (falsifiable, three-part)

**H1 — Explicit periodic FileStream flush bounds staleness.** A periodic timer that calls `_writer.BaseStream.Flush()` (push StreamWriter's internal buffer + FileStream buffer through to the OS page cache) every N seconds bounds the worst-case observable staleness to ~N + page-cache writeback latency. The current `AutoFlush = true` only handles the StreamWriter layer.

**Falsified if:** under cycle 9 replay conditions, the JSONL artefact's tail-to-real-time staleness exceeds 2 × N seconds during the high-bandwidth phase.

**H2 — Time-based emission throttling caps the metric channel's bytes-per-second.** Switching `MergeProgressEvent` / `RebuildProgressEvent` emission from "every M records" (records-per-event) to "every T seconds elapsed" (seconds-per-event) gives a fixed bytes/sec ceiling regardless of workload throughput. At 1 event / 30 s × 300 bytes = 10 bytes/sec — three orders of magnitude below any plausible contention.

**Falsified if:** the cumulative event count over a representative phase changes by more than ±20 % vs the records-based regime at typical throughput. (Too few events = lost insight; too many = back to the original problem.)

**H3 — A separate-disk `--metrics-out` configuration option eliminates contention entirely as an operational lever.** This is already supported in code (the path is a CLI arg). The ADR-level change is documentation + a runbook entry stating "for production runs > 1 hour expected, point `--metrics-out` at a different physical disk than the store."

**Falsified if:** routing `--metrics-out` to a second disk does NOT eliminate the 2-hour staleness in cycle-9-style replay. (This would falsify the "shared-disk contention" diagnosis itself, escalating the investigation.)

The three hypotheses are independent and additive: H1 is the substrate change, H2 is the emission-shape change, H3 is the operational lever. H1 + H2 ship in code; H3 is documentation against capability that already exists.

## Decision

### Part 1 — Periodic explicit FileStream flush

Add a flush-tick `Timer` to `JsonlMetricsListener` analogous to the existing `_stateTimer`, default 5 seconds (configurable via constructor). The tick fires `_writer.BaseStream.Flush()` under `_gate`. This forces the StreamWriter's internal buffer AND the FileStream's buffer to the OS page cache, on a known cadence, independent of event emission.

**Why 5 seconds:** small enough that operator gets sub-coffee-sip cadence; large enough that a million-events-per-second emission spike doesn't cause per-event flush overhead (since AutoFlush already handles per-event StreamWriter→FileStream handoff; the periodic tick only does FileStream→OS).

**Note:** This does NOT call `fsync()` / `FlushTrueAsync()`. Crash recovery is not the goal — live observability is. The OS page cache is "the file" from the perspective of `tail -f` and any in-flight reader. Adding fsync would *increase* contention with the workload, the opposite of what we want.

Implementation surface: ~25 LOC in `JsonlMetricsListener` + 3 LOC at the constructor sites that opt in.

### Part 2 — Time-based emission throttling for high-bandwidth phases

Add a `MetricEmissionThrottle` helper:

```csharp
internal sealed class MetricEmissionThrottle
{
    private readonly TimeSpan _minInterval;
    private long _lastEmitTicks;

    public MetricEmissionThrottle(TimeSpan minInterval) { _minInterval = minInterval; }

    public bool ShouldEmit()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastEmitTicks);
        if (TimeSpan.FromTicks(now - last) < _minInterval) return false;
        return Interlocked.CompareExchange(ref _lastEmitTicks, now, last) == last;
    }
}
```

Apply at the emit points in `SortedAtomStoreExternalBuilder.MergeAndWrite` (atom-merge progress), `QuadStore.RebuildSecondaryIndexes` callbacks (rebuild progress), and the trigram-drain progress loop. Default throttle: 5 seconds.

The existing records-based throttle (`MergeProgressEmissionInterval = 100M records` etc.) becomes a *floor*: emit at least every N records OR every T seconds, whichever fires first. At high throughput this means "every T seconds" wins; at low throughput "every N records" wins (so a slow phase still emits eventually).

Implementation surface: ~50 LOC for the throttle + emit-site updates.

### Part 3 — Runbook entry for separate-disk operational pattern

`docs/operations/runbook-metrics-disk-separation.md` (new file). One page:

> For production runs projected > 1 hour wall-clock, route `--metrics-out` to a disk physically separate from the store. The metric writer and the workload contend on the same SSD I/O queue when they share a disk; under high workload pressure the JSONL artefact may lag the actual phase by minutes-to-hours, defeating live observability. Cycle 9 trigram drain measured 2+ hours of staleness on a shared APFS volume.
>
> Recommended pattern: `--metrics-out /Volumes/observability/run-{date}.jsonl` where `/Volumes/observability` is a different physical disk than the store directory.
>
> Implementation note: this is a purely operational pattern. ADR-043 Parts 1+2 bound staleness on shared-disk configurations to ~5 seconds; separate-disk eliminates contention entirely. Pick the discipline that fits the run.

No code change for Part 3.

## Consequences

### Positive

- **Live observability is bounded.** Under any workload pressure, JSONL artefact lag is ~5 seconds (Part 1) or fewer events with proportionally bounded staleness (Part 2). The 2-hour symptom becomes a 5-second cadence.
- **Operational lever is documented.** The `--metrics-out` path is already a CLI arg; Part 3 makes its load-bearing role visible.
- **Bytes/sec ceiling on metric channel.** Part 2's time-throttled emission means the metric channel never amplifies under high-throughput phases. A 100× throughput jump produces the same metric event count, not 100× more events.
- **Sky Omega 2.0 unblocked on this axis.** James (orchestration) gates on metric flow; with Parts 1+2 the gate has < 5 s lookback regardless of substrate pressure.

### Negative / risks

- **Periodic timer is one more managed resource per `JsonlMetricsListener`.** Trivial cost — there's already a state-emission timer; this is the same pattern. No new disposal concerns.
- **Time-throttled emission produces fewer events at high throughput.** Operator switching between cycle 9's records-based JSONL and a future cycle's time-based JSONL will see different event count + spacing. Anyone scripting against the JSONL needs to know event spacing is now time-bounded, not record-bounded.
- **Part 1's 5-second cadence is a magic number.** Could be wrong for a future workload. Constructor-configurable mitigates, but a wrong default ships if 5 s turns out to be too coarse for some phase.
- **H3's mechanism diagnosis is unverified.** If cycle-9-style staleness persists on a separate disk, this ADR's framing is wrong. Validation must explicitly test the separate-disk config.

### Neutral

- File format unchanged. JSONL emitted under ADR-043 must be valid input to existing post-run analysis tools (the `wdbench-aggregate-distribution-*.md` flow, the cycle-N validation flow). No schema change.

## Validation plan

1. **Cycle-9-style replay on shared disk, before ADR-043.** Reproduce the trigram-drain conditions (or a downscaled analogue at 100 M atoms instead of 4 B) on a current-`main` substrate. Capture `rebuild.jsonl` tail-to-real-time staleness across the phase. Expected: > 60 s staleness under load.
2. **Same replay, after Part 1 + Part 2.** Same hardware, same workload, ADR-043 implementation present. Expected: < 10 s tail-to-real-time staleness across the phase.
3. **Same replay, after Part 3 (separate disk).** Same workload, `--metrics-out /Volumes/different-disk/...`. Expected: < 1 s staleness (network-of-syscalls level, not contention-bound).
4. **Event-count parity.** Total event count over a fixed-duration phase, comparing records-based throttle (baseline) and time-based throttle (Part 2). Expected: within ±20 % at typical throughput; at extreme throughput, time-based emits proportionally fewer (which is the design intent).
5. **Crash recovery sanity.** Kill -9 a running process mid-phase. Verify the JSONL on disk is well-formed (no torn lines, no truncated JSON objects) up to the last `_writer.BaseStream.Flush()` tick. Expected: tail line is complete, no parse errors.
6. **Long-run measurement.** Replay against a cycle-10-scale 1 B atom Reference build with ADR-043 active and `--metrics-out` on the workload disk. Expected: staleness stays < 10 s across the multi-hour run.

Validation document: `docs/validations/adr-043-metric-decoupling-{date}.md`.

## Alternatives considered

- **Out-of-process metric tap (Unix socket / UDP / shared memory).** Most invasive; closest to "production observability" patterns; adds a deployment dependency. Right answer if the metric channel ever needs to fan out across processes, but premature for current Sky Omega — the substrate is single-process by design. Deferred to a future ADR if the SaaS-style observability surface becomes a requirement.
- **Skip metric writes during heavy-bandwidth phases entirely.** Worst alternative — produces gaps exactly where you want detail. Rejected.
- **Increase emission rate, hope page-cache pressure eases.** Backwards — emit *more* events means *more* bytes competing on the same queue. Rejected.
- **Use `FileOptions.WriteThrough` + per-event `fsync()`.** Forces durable disk writes per event. Increases contention with workload (more competing fsync calls), the opposite of the goal. Rejected — durability is not the problem; visibility is.
- **Move metrics emission entirely to a different thread on a different priority.** Doesn't fix anything — the SSD queue doesn't care which thread issued the write. Rejected as misframing.
- **Wait for cycle 11 to see if it recurs.** Pre-mortem applies: cycle 9's 2-hour gap was sufficient evidence; cycle 10's r4 didn't replicate because the phase shapes differ. A future cycle's "we don't see anything" symptom is a known failure mode — fix it before the next time it surfaces. Rejected as reactive.

## References

- [`docs/limits/metric-emission-backpressure-on-shared-disk.md`](../../limits/metric-emission-backpressure-on-shared-disk.md) — the surfacing limit; this ADR is its architectural answer
- [`docs/limits/observability-discipline-systematic-not-reactive.md`](../../limits/observability-discipline-systematic-not-reactive.md) — sibling; the META pattern that "we discover gaps by hitting them" applies to this gap's surfacing as well
- [`docs/architecture/technical/observability-coverage.md`](../../architecture/technical/observability-coverage.md) — the discipline statement; this ADR adds the "emit channel must flow during the phase it instruments" dimension
- `src/Mercury/Diagnostics/JsonlMetricsListener.cs` — current implementation; the lock + AutoFlush pattern Part 1 augments
- Cycle 9 trigram drain incident: `docs/validations/adr-037-cycle9-21b-2026-05-09.md` (surfacing run) + `rebuild.jsonl` artefact (raw)
- [feedback_resource_limit_class_audit](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_resource_limit_class_audit.md) — "every interaction with an OS-enforced resource must be characterized, bounded — at introduction time, not when scale exposes it." The metric channel's interaction with the SSD I/O queue is the resource interaction this ADR characterizes.
