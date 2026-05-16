# Limit: ExternalSorter chunk-stream pool bypassed in cycle 10 trigram drain path

**Status:**        Retracted — false alarm (2026-05-16)
**Surfaced:**      2026-05-13, during cycle 10 r4 trigram drain phase against `wiki-21b-ref`. With ~8,192 concurrent open trigram chunk streams and the launchd-effective FD limit of 10,240, the run sustained only 17% headroom on the FD ceiling for ~6 hours of drain wall-clock.
**Last reviewed:** 2026-05-16
**Retraction:**    The "pool bypass" claim was speculative and incorrect. Code review against `src/Mercury/Storage/ExternalSorter.cs` confirms the pool IS engaged on every `ExternalSorter` consumer, including the trigram-drain path at `QuadStore.cs:1571`. `ChunkReader.RefillBuffer()` at `src/Mercury/Storage/ExternalSorter.cs:315` calls `_pool.Get(_path)` on every read. The ~8,192 observed concurrent FDs at cycle 10 r4 was the pool running at its documented `MergeFileStreamPoolHardCap = 8 * 1024` cap (`src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs:97`) with LRU eviction operating as designed. The 17% headroom on the 10,240 launchd FD limit is the cap's intended behavior — leaving headroom for resolver/atom-store/parser/OS handles. The actual eviction-overhead concern is captured by the sibling limit [trigram-drain-cap-eviction](trigram-drain-cap-eviction.md), which proposes structural mitigations (larger trigram chunks → fewer total chunks → no eviction at all; hierarchical merge; runtime FD-limit detection on Linux). Those remain the right framing.

## Description

Cycle 8 (2026-05-03) introduced the FD-aware pool (`dddfda3`) into `ExternalSorter.Merge` and lowered the cap from 32K to 8K (`880bfe1`) to prevent EMFILE crashes against the launchd-effective 10,240 FD limit on macOS children. The pool's job: keep at most `cap` chunk streams open at any moment, evict LRU when the cap is reached.

Cycle 10 r4 (2026-05-13) measured the trigram drain at `wiki-21b-ref` and observed:

- ~8,192 trigram chunk streams open concurrently during the drain peak
- FD count sustained at ~8,500 (combining trigram chunks + atom-merge residual + parser file handle + miscellaneous OS handles)
- 17% headroom on the 10,240 launchd-effective limit — within crash-distance, but not crashing
- Drain throughput stable; **no eviction observed** (zero LRU evict events emitted by the pool listener)
- The pool exists in `ExternalSorter<TrigramEntry>.Merge` but the trigram drain code path opens chunk streams via the **older** factory (pre-pool), bypassing the pool entirely

The pool bypass is structural: the trigram drain wires a chunk-stream factory that doesn't route through `ChunkStreamPool`. The pool wraps the *atom-merge* path correctly (cycle 8 evidence: 23% eviction at 10,456 chunks fully contained at 8K cap, sustained 8,356 FDs). On the trigram-drain path it's effectively a no-op — chunks open directly, the LRU cap is not enforced, and the crash mode the pool was supposed to prevent is still latent at higher chunk counts.

## Why this is a register entry, not an ADR

The cycle 10 r4 evidence is one substrate generation; the pool integration on the trigram drain path is a known-shape engineering task, not an architectural decision. Promotion to ADR is warranted only if:

1. A future scale pushes the trigram chunk count materially above the current ~8K (would force eviction once the pool is wired in, requiring the trigram path to handle eviction-during-drain correctness).
2. The fix requires non-trivial restructuring of `ExternalSorter<TrigramEntry>.Merge` rather than a factory replacement at the call site.

## Trigger condition

Already triggered in part — the limit moves to Engineering when:

1. **Tier 2 substrate close-out (2026-05-16 plan).** "ExternalSorter FD pool integration" is the named Tier 2 task. The integration is the engineering fix; this limits doc is the characterization that justifies it.
2. **Cycle 11+ targets 30-100 B substrate validation.** At that scale, trigram chunk counts grow past the 8K cap and the pool becomes load-bearing for crash avoidance.
3. **Linux/server validation moves to first-class status.** On hosts with FD limits in the 65K–1M range, the trigram drain bypass is not yet visible — but the engineering correctness gap is the same.

## Current state

- Cycle 10 r4 (1.7.55): bypass measured, run completed cleanly. No eviction events. 17% headroom on 10,240 FD limit.
- Truthy r1 (1.7.57, 2026-05-14): trigram drain at 8.17B has ~3K chunks — well under cap, bypass not visible on this run.
- WGPB step C (1.7.57, 2026-05-16): trigram drain at ~150M has ~50 chunks — trivial, bypass not visible.
- The bypass is silent on the happy path. The discipline is to wire the pool before the chunk count grows past it, not after.

## Candidate mitigations

In rough order of cost / payoff:

1. **Replace the trigram-drain chunk-stream factory call site with `ChunkStreamPool.Open`.** Single-call-site change; matches the atom-merge wiring already in place. Estimated effort: 1-2 hours including a regression test that asserts pool-open invocations on the trigram path. Pairs cleanly with the [trigram-drain-cap-eviction](trigram-drain-cap-eviction.md) limits entry — both describe the same architectural area but at different chunk-count regimes.
2. **Emit a startup assertion: if chunkCount > capHeadroom, fail-fast with diagnostic.** Defends against future regressions of the bypass. Trivial.
3. **Per-pool-instance metrics line in the JSONL listener.** `pool_opens / pool_evicts / pool_hits` per drain phase. Surfaces the gap as data, not as inference from FD count.

## Why this matters beyond cycle 10 r4

The pool exists to prevent a specific crash mode (EMFILE under launchd's 10,240-effective limit on macOS). The cycle 8 incident demonstrated the crash; the cycle 10 r4 measurement demonstrated the bypass. The discipline is to assume any new external-sort consumer needs the pool until proven otherwise, not the reverse. The same shape will recur in any future ExternalSorter consumer (a column-store path, a sort-merge join, etc.) — getting the factory wiring right at the API boundary is upstream of every future consumer adding the same bug.

This is the [resource-limit-class audit](../../memory/feedback_resource_limit_class_audit.md) feedback applied at substrate scale: **every** interaction with FDs must be accounted, characterized, bounded — at introduction time, not when scale exposes it.

## References

- `docs/validations/cycle10-phase3-r4-21b-2026-05-12.md` — cycle 10 r4 validation; observed FD count at trigram drain peak
- `docs/limits/trigram-drain-cap-eviction.md` — sibling entry; the cycle 8 path where the pool *is* engaged
- Commit `dddfda3` — pool wired into `ExternalSorter.Merge` (cycle 8, 1.7.47)
- Commit `880bfe1` — pool cap 32K → 8K (cycle 8, 1.7.47)
- `src/Mercury/Storage/Sort/ExternalSorter.cs` — pool integration site
- `src/Mercury/Storage/Sort/ChunkStreamPool.cs` — the pool itself
- `src/Mercury/Storage/TrigramIndex.cs` — the trigram-drain consumer that bypasses the pool
- [Memory entry `feedback_resource_limit_class_audit`](../../memory/) — discipline rule: every FD interaction must be accounted at introduction time
