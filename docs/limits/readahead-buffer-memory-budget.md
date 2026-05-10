# Limit: readahead buffer memory budget (front + back per chunk)

Status:        Latent (acceptable on the 128 GB substrate host; characterized 2026-05-10)
Surfaced:      2026-05-10, via external review of [`docs/reviews/sky-omega-latest-version-review-2026-05-10.md`](../reviews/sky-omega-latest-version-review-2026-05-10.md) §7
Last reviewed: 2026-05-10
Promotes to:   ADR if substrate moves to a host with < 64 GB RAM; or if 21.3 B-scale measurement shows the kernel reclaiming too aggressively under buffer pressure

## Description

`ChunkReadAheadBuffer` (`src/Mercury/Storage/ChunkReadAheadBuffer.cs`) uses double-buffering: a `_front` array (consumer side) and a `_back` array (producer side), each of `DefaultBufferSize` (4 MiB). The real per-chunk-reader memory footprint is therefore **8 MiB**, not 4 MiB.

At cycle-9 production scale (21.3 B atoms ≈ 3,923 chunks):

| Quantity | Value |
|---|---:|
| Per-chunk-reader buffers | 4 MiB front + 4 MiB back = **8 MiB** |
| Active chunk readers (cycle 9) | 3,923 |
| **Peak readahead memory** | **3,923 × 8 MiB ≈ 31 GiB** |

This is user-space anonymous memory under direct allocation (not file-backed mmap, not page cache). On a 128 GB substrate host, this is acceptable; it leaves ample headroom for the kernel page cache + parser working set + Mercury's own structures.

## Trigger condition

- Substrate moved to a host with < 64 GB RAM (the 31 GiB peak would compete with kernel page cache).
- Per-chunk-reader memory accounting becomes a binding constraint on chunk count (e.g., a 64 B-atom future Wikidata exceeds 8K chunks at 8 MiB each = 64 GiB).
- Production measurement shows kernel reclaiming readahead pages aggressively under pressure (would be visible as merge-phase throughput collapse correlated with RSS climb).

## Current state

Documented and characterized; not acted on. The current docstring on `DefaultBufferSize` now states the real per-chunk footprint explicitly. Phase 3 production run on 1.7.54 will provide measured RSS evidence at 21.3 B scale to confirm or refine the 31 GiB projection.

## Candidate mitigations

If triggered:

- **Halve the buffer size to 2 MiB.** Per-chunk footprint becomes 4 MiB; peak at 21.3 B drops to 15.7 GiB. Buffer is still ~30,000× larger than any single record (URI ~60 B), so the granularity argument is preserved.
- **Lazy back-buffer allocation.** Allocate `_back` only on first refill request rather than at construction. Reduces footprint for chunks that never get read past their initial fill (rare in production but possible for very-imbalanced chunk distributions).
- **Per-chunk buffer-size scaling.** Smaller buffers for chunks that are projected to be small (< 50 MB on disk) — uses chunk-file size as a hint at construction.
- **Pool buffer arrays.** Both buffers are byte[] of identical size; an `ArrayPool<byte>` could reuse them across chunks. (Note: this conflicts with substrate-independence discipline if `ArrayPool` semantics change between BCL versions; would need careful review.)

## References

- Review: [`docs/reviews/sky-omega-latest-version-review-2026-05-10.md`](../reviews/sky-omega-latest-version-review-2026-05-10.md) §7
- Code: `src/Mercury/Storage/ChunkReadAheadBuffer.cs:46` (`DefaultBufferSize`); `:48-49` (`_front`/`_back` allocation in constructor)
- ADR-038 Part 2 (`docs/adrs/mercury/ADR-038-merge-phase-read-side-optimization.md`) — the readahead architectural decision; this entry is the operational footnote
