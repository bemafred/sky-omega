# Limit: metric emission backpressures on the same disk as the workload it instruments

**Status:**        Triggered (cycle 9 trigram drain, observed 2026-05-08 01:34 → 03:38 UTC: 2+ hour visible gap in `rebuild.jsonl` while the process is healthy and progressing at ~25 MB/min on `trigram.posts`)
**Surfaced:**      2026-05-08, via cycle 9 trigram drain phase
**Last reviewed:** 2026-05-08

## Description

`JsonlMetricsListener` writes events to a JSONL file via an internal bounded `Channel<T>` consumed by a single writer thread. Under normal load this is fast enough — a few hundred events/sec, one disk write per few-event batch.

Under heavy load, the writer thread competes for SSD bandwidth with the very workload it is instrumenting. Cycle 9's trigram drain phase hit this:

- **Emission rate:** trigram drain emits one `rebuild_progress` event per 1 M entries processed. At a drain rate of ~6 M entries/sec this is **~6 events/sec**, not extreme.
- **Per-event size:** ~300 bytes serialized → emission write rate ~1.8 KB/sec. Trivial in isolation.
- **Concurrent workload:** trigram drain mmaps `trigram.posts` and `gpos.tdb`, generating ~25 MB/sec of mmap-driven SSD I/O on the same NVMe.
- **Observed:** the JSONL file's tail content lags 2+ hours behind real time. The file gets touched (mtime advances) but bytes containing newer events haven't reached the file yet.

The cause is not the metric writer's CPU cost — it's that the metric writer's `fwrite` + filesystem journaling competes for the same SSD's queue with the workload's mmap-page-out flush. When the workload is the dominant bandwidth consumer, the metric writer's small writes get queued behind it and emerge in delayed bursts.

The result: **during the phase that most needs real-time observability, observability fails most**. The phase emits events; events queue; events flush minutes-to-hours later when the workload's I/O eases. By then the moment of insight has passed.

## Why this is a register entry

This is distinct from the sibling entry [`observability-discipline-systematic-not-reactive.md`](observability-discipline-systematic-not-reactive.md). That entry is about *whether* a phase is instrumented at all. This entry is about *whether the instrumentation flows during the phase it instruments*. A phase can be instrumented (event types defined, emit calls in place) and *still* invisible in real time because the metric channel is bandwidth-bound against the workload.

This is the "self-instrumentation under disk pressure has a structural limit" failure mode. Naming it as a separate entry keeps the diagnosis clean: a future "we don't see anything" symptom should check both — is there an emit point at all (sibling), and is the emit channel uncongested (this).

## Trigger condition

This limit moves toward an architectural fix when one of:

1. **A real-time decision depends on metric flow.** An operator watching cycle 9 in real time cannot tell "stuck" from "slow" from "buffered" because the metric channel is buffered. The cost is operator confusion + the temptation to take destructive action (kill the run) on incomplete information.
2. **Sky Omega 2.0 cognitive-layer milestone.** James (the cognitive orchestrator) gates on metric flow. If the metric stream is hours-stale during the phase James is supposed to gate, James is operating on past state.
3. **External characterization publication.** Phase-by-phase wall-clock decomposition written from JSONL artefacts is fine post-run (the events DO eventually reach the file). But a *live* dashboard or SaaS-style observability surface is impossible until the channel is decoupled from the workload disk.

## Current state

`rebuild.jsonl` and `bulk.jsonl` are the only metric outputs. Both written to `/tmp` by default — same APFS volume as the store directory under macOS-default `~/Library/SkyOmega`. Both metric files compete with the workload for SSD bandwidth.

Existing mitigations: none. The emission rate is throttled per-phase (`MergeProgressEmissionInterval = 100 M records` for atom-merge, lower for rebuild). That keeps event count low but does not address the bandwidth competition.

## Candidate mitigations

In rough order of cost / effectiveness:

1. **Reduce emission rate further during high-bandwidth phases.** Trigger event emission per *time elapsed* (e.g. every 30 seconds) rather than per *records processed*. Caps the metric channel's bytes-per-second load to a known small number regardless of workload throughput. Cheapest mitigation; sacrifices some granularity.

2. **Direct metrics to a separate disk.** Take the `--metrics-out` argument; route to `/Volumes/some-other-disk/metrics.jsonl`. Eliminates the bandwidth competition entirely. Operationally: requires a second disk; trivial code change (no Mercury change at all — it's a path argument).

3. **Buffered-burst flush with explicit timer.** `JsonlMetricsListener` accumulates events in memory; explicit timer flushes batches to disk every N seconds. Decouples emission cadence from disk-write cadence. Tradeoff: a crash loses up to N seconds of events, but real-time visibility is preserved during the flush window.

4. **Out-of-process tap.** Metric channel writes to a Unix socket / UDP / shared memory. A separate consumer process reads and writes to disk (or sends to a remote observer). The producing process never blocks on metric I/O. Most invasive; closest to "production observability" patterns but adds a deployment dependency.

5. **Skip metric writes during heavy-bandwidth phases entirely.** Gate emission on a "current phase is bandwidth-bound" flag; emit only periodic state samples. Worst — produces gaps exactly where you want detail — but cheapest if (1) doesn't suffice.

## How this compounds with the sibling limit

The sibling [`observability-discipline-systematic-not-reactive.md`](observability-discipline-systematic-not-reactive.md) says: *every >1-min phase must emit progress*. This entry adds: *and the channel must flow during the phase it instruments*. A phase that emits events into a channel that won't flush for hours during the phase is, for live-observability purposes, just as silent as a phase with no emit calls.

The discipline must be:

1. *Phase has an emit point.* (Sibling entry — categorical audit needed.)
2. *Phase's emit channel can flush during the phase.* (This entry — bandwidth analysis needed.)

## References

- `src/Mercury/Diagnostics/JsonlMetricsListener.cs` — current implementation; the bounded channel + writer thread pattern
- `tools/cycle9-21b/launch.sh` — current `--metrics-out` defaults (same disk)
- Cycle 9 trigram drain incident (2026-05-08 01:34 → 03:38 UTC visible-gap window) — surfacing event
- [`docs/limits/observability-discipline-systematic-not-reactive.md`](observability-discipline-systematic-not-reactive.md) — sibling, categorical instrumentation coverage
- [`docs/architecture/technical/observability-coverage.md`](../architecture/technical/observability-coverage.md) — the discipline statement; this entry adds the channel-bandwidth dimension
