# Limit: Observability coverage gap — instrumentation is anecdotal, not systemic

**Status:**        Latent (mitigated by post-hoc reconstruction during runs)
**Surfaced:**      2026-05-04, during the cycle 6 21.3 B run, when 8+ hours of merge phase were unobservable from outside the process. The single end-of-merge `[merge-pool]` line was the only visibility.
**Last reviewed:** 2026-05-04

## Description

Mercury has substantial Phase 7a metrics infrastructure (`JsonlMetricsListener`, `ProcessStateProducer`, `LoadProgressTick`, `RebuildProgressTick`, `AtomStoreProducers`). The infrastructure is solid; the **coverage** is not. Instrumentation was added at the public bulk-load API surface and at known scaling boundaries (atom-store probe distance, rebuild progress). It was not extended to the deep internals that became long-running at 21.3 B+ scale.

The result: a 25 h ingest run produces two screens of summary text and a JSONL with rich parser progress + sparse mid-pipeline opacity. The opaque hours hold the answers to "is the substrate working" and "where is the wall-clock cost living" — exactly the questions a characterization run is supposed to answer.

This isn't one missing instrumentation point. It's the absence of a *systemic discipline* that says "every long-running internal must emit progress." Each unobservable operation costs hours of opaque runtime when scale exposes it. Today's surfacing was the merge phase; tomorrow's will be `ExternalSorter<T>.Merge` or the GSPO drain — same shape, different name.

## Why this is a register entry

The limits-register charter is "characterized but not acted on." The characterization here is the [observability-coverage map](../architecture/technical/observability-coverage.md), which inventories what is and isn't observable across six categories: long-running internals, live state vs counters, configuration disclosure at startup, decision/invariant-approach states, failure-mode visibility, and the meta-gap (no register existed before today).

The "not acted on" piece is the mitigation work — wiring the missing emissions, building a startup banner, adding approach-warnings, exposing live-state producers. The map enumerates the work; the register entry gates promotion.

## Trigger condition

This limit moves toward an ADR / dedicated Round 2 work when one of:

1. **Any scale-defining run is planned.** A run where the answer to "did it work and how" cannot be reconstructed from existing instrumentation. The 2026-05-04 21.3 B run is the canonical example. Cycle 7 (the planned re-run with proper instrumentation) is the trigger to ship the merge-phase emissions.
2. **A new long-running operation is added to the substrate.** Round 2 prefix-compression, parallel sort, pipelined spill — each of these introduces a new internal phase that must satisfy the discipline before shipping.
3. **External characterization publication.** When publishing wall-clock or quality numbers vs comparable systems, the absence of mid-run visibility becomes a credibility gap; the publication itself becomes the trigger.
4. **Sky Omega 2.0 trajectory milestones.** James (the cognitive orchestrator, see `cognitive-orchestrator-absent.md`) cannot gate operations through substrate measurement if the substrate isn't reporting. The two limits compound — observability is upstream of orchestration.

## Current state

- **What exists:** [observability-coverage.md](../architecture/technical/observability-coverage.md) §"What is currently observable" enumerates the working surfaces — bulk-load parser, rebuild, process-level state.
- **What is missing:** same doc §"What is not currently observable" — long-running internals, live state probes, startup configuration disclosure, invariant-approach warnings.
- **What this entry adds:** the discipline (every >1-min operation emits progress) plus the entry-point that gates new long-running work on the discipline.

## Candidate mitigations

The coverage map's §"Mitigation: the systemic answer" lists the concrete sequence. Summarized:

1. **`MergeAndWrite` per-N-records emission** — closes the headline gap. Cheapest first; one method, one new event type.
2. **`ExternalSorter<T>.Merge` instrumentation** — same shape, covers two distinct uses (resolver drain + bulk GSPO).
3. **Per-spill emission in `SortedAtomBulkBuilder`** — directly serves the sibling `spill-blocks-parser` limit (lets us *measure* the parser-blocking cost rather than estimate it).
4. **Startup banner** — one-shot quality-of-life. Catches dispatch bugs in second one of the run.
5. **Approach-warnings on hard limits** — proactive signals before silent saturation (FD cap, single-bulk-load, memory budget).
6. **Live-state producers** — `MergeStateProducer` and similar, hooked into the periodic state-emission loop.

Sequence: (1) blocks cycle 7. (2) and (3) before the next large-scale validation. (4) is a same-day quality-of-life. (5) and (6) become substrate discipline.

## Why this matters beyond Mercury

Three secondary effects:

1. **The James thesis depends on it.** ADR-005's cognitive-orchestrator role is to gate operations through substrate measurement. If the substrate isn't reporting, James has nothing to gate against. Observability coverage is upstream of orchestration — see `cognitive-orchestrator-absent.md`.

2. **The cross-instance learning thesis depends on it.** Sky Omega's promise that one instance can share learning with another requires that runs produce *characterizable* records, not just success/failure. An opaque 25 h run propagates as "it worked" — that's not data, that's testimony.

3. **The "checked nothing" framing applies.** A run with no in-flight observability is untrusted absence — the operation could be hung, degraded, or progressing; the JSONL doesn't say. Observability coverage is what turns absence into checked nothing.

## References

- [observability-coverage.md](../architecture/technical/observability-coverage.md) — the inventory and discipline
- `cognitive-orchestrator-absent.md` — sibling architectural limit; this entry is upstream of that one
- `spill-blocks-parser.md` — sibling; mitigation step (3) above directly closes that limit's measurement gap
- `external-merge-intermediate-disk-pressure.md` — sibling; the merge phase that surfaces both gaps
- ADR-035 (Phase 7a metrics infrastructure) — what we built, what we missed
- 2026-05-04 21.3 B run — the surfacing observation
