# Limit: External-merge intermediate disk pressure

**Status:**        Latent (Monitoring — disk-trace running on 21.3 B Round 1 r1 build)
**Surfaced:**      2026-05-02, during the Round 1 21.3 B run reflection on the QLever ingest gap (6-10 h vs Mercury's projected 30-35 h).
**Last reviewed:** 2026-05-02

## Description

`SortedAtomStoreExternalBuilder.MergeAndWrite` (ADR-034) does external-merge sort over every atom occurrence in the input. The intermediate format spills to chunk files under `bulk-tmp/` as `(int32 length, int64 inputIdx, raw_bytes)` records — a 12-byte header per occurrence plus the full atom bytes (no compression). This converts atom-store assignment into a sequential-I/O workload — good for SSD throughput, the architectural reason Mercury hit 2.4-2.8× wall-clock improvement over the Phase 6 v1 substrate.

But the win on I/O *pattern* is paid for in I/O *volume*. Back-of-envelope at 21.3 B Wikidata:

```
21.3 B triples × ~4 atoms/triple   ≈ 85 B atom occurrences
85 B × ~62 bytes (~50 atom + 12 header) ≈ 5.3 TB raw intermediate
```

The retired `wiki-21b-ref-r1` first attempt observed 3.9 TB peak before the FD-limit crash (incident `urn:sky-omega:incident:21b-fd-crash-2026-05-01`); the 5.3 TB extrapolation is the ceiling if no chunks are merged-and-deleted before the peak.

This is the **disk-volume axis** of the perf budget. QLever's published 6-10 h ingest does not pay this cost: their external sort operates on `(int_id, int_id, int_id)` tuples (~24 bytes) *after* vocabulary assignment. Mercury's atom-store merge sort *is* the assignment phase, so the intermediate carries full atom bytes. Different architectural choice — not necessarily a worse one (Mercury wins prefix compression and binary-search lookup on the read side via the same sorted layout) — but it sets a hard floor on intermediate disk volume.

## Trigger condition

This limit moves toward an ADR / Round 2 mitigation when one of:

1. **Measured peak intermediate exceeds ~3 TB on the live r1 run.** The disk-trace started 2026-05-02 (`/tmp/round1-gradient/21b-disk-trace.jsonl`, polled every 60 s) gives the real number. Above 3 TB, the disk-volume axis is binding on commodity 8 TB SSD setups.
2. **A target deployment surfaces under 4 TB free disk on the Mercury data volume.** The host-class assumption underneath Round 1 is "≥ 5 TB free during ingest." Drop below this and intermediate compression becomes load-bearing.
3. **External benchmark publication vs QLever / similar.** When publishing comparison numbers, the architectural intermediate-volume gap must be either closed (a mitigation shipped) or disclosed (footnote).

## Current state

Mercury 1.7.47 (commit `b2d4e97`) writes uncompressed chunk records. The `SortedAtomBulkBuilder.SpillOneChunk` path serializes each `(bytes, inputIdx)` directly. Read-side reconstruction in `ChunkReader.MoveNext` does the same.

The output `atoms.atoms` *is* prefix-compressed (Round 2, commit `870d31b`, 53% reduction at 1 M Wikidata). The intermediate is not. This asymmetry is the cleanest mitigation target: same algorithm, different layer.

## Candidate mitigations

Multiple axes (analysis 2026-05-02). Listed roughly cheapest-first.

1. **Prefix-compress chunk records (same algorithm, intermediate layer).** Apply Round 2's delta-encoding-with-anchors to `SpillOneChunk` and `ChunkReader.MoveNext`. Within a sorted chunk adjacency is denser than across the global vocabulary (sorts are smaller, fewer prefix breaks), so the win likely *exceeds* the output's 53%. Estimate: 5.3 TB → ~2.5 TB peak (50-55% reduction). CPU cost: trivial — the prefix-compute is the same tight byte-compare loop already shipped. Read cost: very slightly higher (per-record reconstruct) but linear and pages well during sequential merge.

2. **Byte-level chunk compression (zstd).** Wrap chunk-file IO in a zstd-1 stream. Generic; ~2-3× on text. Adds CPU (~500 MB/s decompress on M-series), still cheaper than disk write at SSD throughput. Composes with (1) — would be a follow-on if (1)'s win is insufficient.

3. **Reduce intermediate volume via hash-then-string sort.** Intermediate stores `(hash64, len, inputIdx, bytes)`; merge compares hashes first, only byte-compares hash collisions. Possibly 10-20% reduction (fewer bytes in priority-queue keys). More implementation cost; smaller win than (1). Probably not worth it on top of (1).

4. **Skip the intermediate entirely (architectural).** Hash-based ingest into a disk-paged hash table; single sort pass at end produces the sorted output. Decouples intermediate format from final layout. Big change; ~200 GB hash table or careful disk-paging needed. Round 3+ candidate, not Round 2.

The natural sequencing is **(1) only**, measured against the r1 baseline. (2) is a fallback if (1) underdelivers; (3) and (4) are larger architectural shifts that wait on (1)'s evidence.

## Why this matters beyond disk

Three secondary effects of compressing the intermediate:

1. **Wall-clock.** Disk write is one of two parser-phase bottlenecks (the other is BZip2 source decompression). Halving intermediate bytes halves chunk-write time. Not the dominant axis but real — possibly 1-2 h saved on the 21.3 B run.
2. **Headroom for smaller hosts.** Below 5 TB free disk, the current intermediate volume disqualifies the host. Compression broadens the host class.
3. **Comparison framing.** Every Wikidata-scale ingest comparison (QLever, Blazegraph, Virtuoso) eventually surfaces the intermediate-volume question. Closing the gap removes a footnote; not closing it requires explicit disclosure.

## References

- ADR-034 (mercury) — SortedAtomStore + external-merge architecture
- `docs/limits/atomstore-prefix-compression.md` — sibling, output-layer compression already shipped (the algorithm being reused here)
- `docs/limits/bit-packed-atom-ids.md` — sibling, atom-ID storage compression (a different layer of the same trade-off)
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `SpillOneChunk` + `ChunkReader.MoveNext` are the implementation surfaces
- `urn:sky-omega:pattern:third-path-dimension-shift` (Mercury) — the meta-pattern that surfaced this entry: the win is "apply the same compression on a different axis," not a new algorithm
- `/tmp/round1-gradient/21b-disk-trace.jsonl` — live disk-pressure measurement (active during 21.3 B r1 run); will inform whether (1) is promoted to Round 2 work
