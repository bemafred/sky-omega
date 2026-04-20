# Limit: Bit-packed atom IDs (48-bit packing)

**Status:**        Latent
**Surfaced:**      2026-04-19, via [ADR-029 Decision 5](../adrs/mercury/ADR-029-store-profiles.md) ("Offset and ID widths stay at 64-bit signed (`long`) for now")
**Last reviewed:** 2026-04-20
**Promotes to:**   ADR when storage becomes binding even after Reference profile lands — i.e., when a 21.3 B Reference store at projected 2.6 TB still doesn't fit on the target hardware, OR when a credible deployment scenario emerges where ~680 GB additional storage savings would be load-bearing.

## Description

Atom IDs and file offsets in Mercury are 64-bit signed integers (`long`). At 21.3 B Wikidata atoms, addressable space is well below 2⁴⁸ ≈ 281 trillion entries — roughly 13,000× headroom. The 16 high bits of every atom ID are always zero.

ADR-029 Decision 5 quantifies the projected savings from packing atom IDs into 48-bit fields:

> The savings from moving atom IDs to packed 48-bit integers (~680 GB at full Wikidata) are real but much smaller than the schema-reduction win (~7-10 TB). Bit-packing adds subtle serialization complexity with no upside until the schema-reduction win is banked. Defer to a future ADR if needed.

The deferral logic is sound: the Reference profile schema reduction (ADR-029) wins ~7–10 TB by removing temporal/versioning fields. Bit-packing wins another ~680 GB on top. Without the schema reduction first, the absolute savings are smaller and the implementation cost is the same.

## Trigger condition

Two distinct triggers, either of which warrants promotion:

1. **Hard storage binding after Reference profile lands.** If a 21.3 B Reference store at the projected 2.6 TB still doesn't fit on the deployment target (e.g., a 2 TB SSD scenario), bit-packing's ~680 GB closes 26 % of the gap. Promote to recover headroom.
2. **Credible deployment scenario where storage savings are load-bearing.** E.g., a multi-store host where total disk pressure across multiple 21.3 B Reference mirrors becomes binding. Or a price-sensitive deployment where 680 GB at scale translates to material cost savings.

Neither is currently expected — the M5 Max 8 TB target with Reference profile leaves ~5.4 TB headroom on a single Wikidata mirror.

## Current state

Latent. ADR-029 explicitly chose to defer this. The deferral remains correct as long as Reference profile fits on target hardware with margin.

## Candidate mitigations

When promoted, the canonical approach:

1. **48-bit packed atom IDs** in B+Tree entries and atom-store index files. Read/write via bit-shift unpacking. Backwards-incompatible with current 64-bit format, so a `keyLayoutVersion` bump in `store-schema.json` (already designed in ADR-029 Decision 2) is the migration mechanism.
2. **48-bit packed file offsets** for the atom-store offset index. Same packing, same layout-version bump.
3. **Mixed mode considered and rejected** — partial packing (atom IDs only, not offsets) gives most of the savings but adds dual code paths. Either fully packed or unpacked; no halfway.

Implementation cost: per ADR-029 Decision 5's framing, "subtle serialization complexity." The hot-path read/write routines for B+Tree pages and atom-offset entries each need a packed-aware variant. Estimated medium effort — not trivial, not large. Comparable in scope to ADR-028's rehash-on-grow.

## Compose-with notes

- **ADR-029 Reference profile** is the prerequisite — bit-packing on top of 88 B Cognitive entries is much smaller win than on top of 32 B Reference entries (the entry already loses the packed-could-help temporal fields).
- **ADR-030's measurement infrastructure** would let us quantify the actual storage savings post-fact rather than from projection alone.
- **ADR-028's hash-table layout** is independent — atom-store hash entries are already a separate format and not affected by this packing.

## References

- [ADR-029 Decision 5](../adrs/mercury/ADR-029-store-profiles.md) — original deferral statement
- [ADR-027 § Measured Storage Footprint](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md) — the storage projections this builds on
- [full-pipeline-gradient-2026-04-19.md](../validations/full-pipeline-gradient-2026-04-19.md) — measured storage at 1 B which the projections extrapolate from
