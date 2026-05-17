# Operational runbook: separate-disk metrics for production runs

**Status:** Shipped Mercury 1.7.74 ([ADR-043 Part 3](../adrs/mercury/ADR-043-metric-emission-decoupling.md))
**Audience:** Operators running Mercury bulk-loads or rebuilds projected to take > 1 hour wall-clock

## TL;DR

For production runs projected > 1 hour wall-clock, route `--metrics-out` to a disk physically separate from the store. The metric writer and the workload contend on the same SSD I/O queue when they share a disk; under high workload pressure the JSONL artefact may lag the actual phase by minutes-to-hours, defeating live observability.

```bash
# Don't (shared disk):
mercury bulk-load /Volumes/data/store source.ttl.bz2 \
  --metrics-out /Volumes/data/run.jsonl

# Do (separate disk):
mercury bulk-load /Volumes/data/store source.ttl.bz2 \
  --metrics-out /Volumes/observability/run.jsonl
```

## Why this matters

### The cycle 9 symptom

Cycle 9's trigram drain phase (2026-05-08 01:34 → 03:38 UTC) ran for ~2 hours processing `trigram.posts` at ~25 MB/sec mmap-driven SSD I/O. The substrate was healthy and progressing throughout, but `rebuild.jsonl` — the live JSONL artefact an operator tails to monitor a long run — stopped advancing for the full 2-hour phase, then flushed a delayed burst at the end.

The metric channel's bytes/sec was trivial: ~6 events/sec × ~300 bytes = ~1.8 KB/sec. The contention wasn't bandwidth — it was the SSD's I/O queue + write-back path being saturated by the workload's mmap writes. Small metric writes queued behind the large workload writes and got starved indefinitely.

### What ADR-043 fixes vs what it doesn't

ADR-043 Parts 1 + 2 (shipped in `JsonlMetricsListener` + emit-site throttles) bound the staleness on **shared-disk configurations** to ~5 seconds. That's a 1,440× improvement over the cycle 9 baseline — but it still depends on the OS page-cache flushing through to disk within the 5-second window.

ADR-043 Part 3 (this runbook) is the **operational lever**: pointing `--metrics-out` at a physically separate disk eliminates the queue contention entirely. The metric channel and the workload no longer share an I/O resource. Staleness drops to network-of-syscalls level (< 1 s).

For most runs, Parts 1+2 are sufficient. For runs where < 5 s staleness matters (live demos, paired-monitor setups, automated gating on metric flow), use Part 3 too.

## Configuration

`--metrics-out <path>` is already a CLI argument on `mercury bulk-load` and `mercury rebuild`. The change is purely *where* to point it.

### macOS

Single-host runs: any second physical disk (external SSD, second internal NVMe, attached USB-C drive). The runtime tools don't care about filesystem type — APFS, exFAT, HFS+ all work for JSONL append-write.

```bash
# Typical: workload on internal disk, metrics on USB-C SSD
mercury bulk-load ~/Library/SkyOmega/stores/wiki-21b source.ttl.bz2 \
  --metrics-out /Volumes/ObservabilityDrive/wiki-21b-run.jsonl
```

### Linux

Mount the second disk explicitly. Avoid `/tmp` if it's tmpfs — contention with the workload's mmap'd memory pages is the same problem in disguise.

```bash
# Mount a second SSD at /mnt/observability
mount /dev/nvme1n1p1 /mnt/observability
mercury bulk-load /data/store source.ttl.bz2 \
  --metrics-out /mnt/observability/run.jsonl
```

### Windows

Use a drive letter on a separate physical disk; PowerShell or cmd both work.

```powershell
mercury bulk-load D:\store source.ttl.bz2 `
  --metrics-out E:\observability\run.jsonl
```

## When this is NOT needed

- **Runs projected < 1 hour wall-clock.** Parts 1+2 5-second bound is the right tradeoff at this scale.
- **Single-disk hosts without a second physical disk available.** Parts 1+2 still give bounded staleness on shared-disk configurations. Operators monitoring a long-run on a single-disk host should expect ~5 s cadence.
- **Test or dev runs where metric flow isn't being live-monitored.** The JSONL artefact is still complete-on-disk after Dispose; only the live cadence is affected.

## How to verify the separation is working

While a long-run is in flight, in a separate terminal:

```bash
# Check the JSONL is advancing in real time (sub-second new events)
tail -f /Volumes/ObservabilityDrive/run.jsonl

# Sanity check: parallel iostat shows the metric disk's write rate
# stays small (~10 bytes/sec) regardless of the workload disk's rate
iostat -d 1
```

If the metric disk shows write activity bursting in step with workload pauses (large gaps then catch-up bursts), the metric disk is co-mounted with the workload disk or experiencing other contention. Re-check the path resolves to a physically separate disk.

## Why this runbook lives separate from the ADR

ADR-043 captures the substrate decision (Parts 1 + 2 code changes + Part 3 documentation choice). This runbook is the operator-facing surface — what command to type, when to type it, how to verify. The ADR documents *why* this matters; the runbook documents *how* to use it.

## References

- [ADR-043 — Metric Emission Decoupling](../adrs/mercury/ADR-043-metric-emission-decoupling.md) — substrate decision + cycle 9 symptom characterization
- [Limit: Metric Emission Backpressure on Shared Disk](../limits/metric-emission-backpressure-on-shared-disk.md) — the surfacing limit
- [Architecture: Observability Coverage](../architecture/technical/observability-coverage.md) — the broader observability discipline
