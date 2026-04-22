# Limit: Bulk-load memory pressure and swap at past-RAM scale

**Status:**        Latent
**Surfaced:**      2026-04-22, Phase 6 21.3B Wikidata Reference bulk-load (~4h in)
**Last reviewed:** 2026-04-22
**Promotes to:**   ADR when (a) bulk-load wall-clock becomes binding and swap activity correlates with measurable throughput drop, OR (b) the target host has materially less RAM than the M5 Max 128 GB (compressor pressure shifts to swap pressure earlier), OR (c) a specific mitigation is characterized and shown to recover throughput

## Description

Reference bulk-load via ADR-033's external sorter has two significant resident memory components:

- **LOH sorter buffers** — `T[16_000_000]` × 2 (data + scratch) = 512 MB + 512 MB = **1 GB of anonymous memory** on the LOH for the full bulk session. These pages are written every `AddCurrentBatched` call — effectively pinned-in-use.
- **mmap'd index + atom store pages** — GSPO leaves, atom store hash table (up to 8 GB sparse at BulkMode, doubling via ADR-028 rehash), atom data file. Dirty pages resident until msync/drop.

On a 128 GB host, as the working set approaches physical RAM the kernel responds in order:

1. **Compressor**: compresses rarely-used pages in-place. Observed at 2.1-2.5 GB compressor during Phase 6 (steady state).
2. **Drop clean mmap pages**: kernel evicts clean-mapped file pages; re-reads on next fault. Invisible to the process as "free memory" but visible as iostat reads during re-fault.
3. **Swap**: writes anonymous pages (LOH, GC heap, thread stacks) to `/var/vm/swapfile*`. This is when sustained throughput drops, because the sorter buffer is anonymous and actively written.

Phase 6 observed the onset at ~4h elapsed (~4B triples loaded, ~77 GB RSS, 124 GB PhysMem used, 86 MB unused, **92 new swapouts / hour** — first non-zero delta of the run). The rate at this point stabilized at ~250 K triples/sec, down from the 351 K/sec peak earlier.

The 92/hour rate is minor (~1.4 MB/hour disk traffic) but indicates the compressor is at its headroom limit. Further RSS growth could tip it into serious swap activity with throughput consequences.

## Current state

Latent at 128 GB + Wikidata 21.3B. Phase 6 completed in the 28-30h envelope (see validation doc when available); swap activity stayed at dozens/hour rather than thousands. Not binding on the Phase 6 outcome.

The limit becomes load-bearing at either smaller RAM (64 GB host would hit swap earlier) or larger scale (3-5× Wikidata would push past 128 GB regardless of mitigation).

## Candidate mitigations (not yet characterized)

Ordered by expected leverage and effort:

1. **Smaller sorter chunk size.** Drop `chunkSize: 16_000_000` (512 MB × 2 = 1 GB LOH) to `4_000_000` (128 MB × 2 = 256 MB LOH). Saves ~750 MB of anonymous-memory residency for the bulk session. Cost: 4× more chunk files, larger k-way merge fan-out. At 21.3B scale: 5325 chunks instead of 1330. K-way merge with binary heap still O(log K) per pop; heap fits easily. **Trivial code change; measured gain unknown.**

2. **`madvise(MADV_DONTNEED)` on consumed chunk files.** In `ExternalSorter.TryDrainNext`, once a reader's file is exhausted, hint the kernel to drop its pages. Would reduce mmap residency during the merge phase without changing anonymous-memory use. Platform-specific code path (`libc.madvise` via P/Invoke) but small. **Helps the drain phase; neutral during the accumulate phase.**

3. **Smaller LOH scratch via in-place ping-pong.** The sorter currently holds `T[chunkSize]` for both data and scratch. A tighter implementation could use a single buffer with rotating regions. Complexity not obviously worth the ~512 MB savings.

4. **`mlock` the sorter buffer.** Pin pages in physical memory to guarantee they're never swap candidates. macOS requires elevated entitlements (`memlock` capability); not tenable for a consumer CLI. Relevant only for dedicated-server deployments.

5. **Out-of-process atom store.** Move atom store to a separate process. Decouples its mmap residency from the bulk-load's anonymous-memory residency. Architectural change; premature without evidence.

## Trigger condition

Promote to ADR when any of:

- A profile of a bulk-load run shows sustained swapouts > ~1 K/sec for an extended period, correlating with measured throughput drop below baseline.
- Target host with < 128 GB RAM makes swap pressure load-bearing at the 1B+ scale we care about.
- Mitigation #1 or #2 is characterized and shows ≥ 10% wall-clock recovery at 21.3B scale.
- Scale target exceeds full Wikidata (e.g., multi-language Wikidata + auxiliary datasets) such that the working set exceeds 128 GB regardless of sorter sizing.

## References

- [ADR-028](../adrs/mercury/ADR-028-atomstore-rehash-on-grow.md) — atom store hash growth, contributes to the resident footprint.
- [ADR-033](../adrs/mercury/ADR-033-bulk-load-radix-external-sort.md) — the external sorter whose LOH buffer is the primary anonymous-memory consumer characterized here.
- [ADR-032 Section 7](../adrs/mercury/ADR-032-radix-external-sort.md) — explicit rationale for the caller-owned scratch + 1 GB LOH residency during a rebuild; same pattern applies to bulk-load via ADR-033.
- [Phase 6 validation](../validations/adr-033-phase5-bulk-radix-2026-04-22.md) — 21.3B run where the pressure surfaced (doc to be extended with Phase 6 numbers on completion).
