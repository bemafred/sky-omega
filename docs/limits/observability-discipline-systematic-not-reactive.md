# Limit: observability discipline is reactive, not systematic

**Status:**        Triggered — promotes to James (Sky Omega 2.0)
**Surfaced:**      2026-05-07, via cycle 9 GSPO drain phase running silent for 40+ min after `merge_completed`. Sibling to existing [`observability-coverage-gap.md`](observability-coverage-gap.md), but the angle is different — this entry is about the *discipline pattern*, not any specific gap.
**Last reviewed:** 2026-05-16 — reframed: this is a category error, not a procedural gap. "Remember to audit periodically" asks human (or Claude Code) attention to be systematic, but attention is fundamentally reactive. The cure is a cognitive loop orchestrator (James, Sky Omega 2.0) that fires periodic substrate audits as behavior, not as discipline. Empirically observed across the matrix-completion arc: every time the discipline pattern repeated, it was caught reactively (today's drought-discrepancy = fourth instance). Promotes to a Sky Omega 2.0 architectural prerequisite, not a near-term procedural fix.

## Description

Sky Omega's observability discipline (per `docs/architecture/technical/observability-coverage.md`) states the rule: **every operation projected to take > 1 minute in production must emit periodic progress before being considered shippable**.

The rule is correctly stated. The discipline applying it is currently *reactive*: we add progress emission for a phase only after it's surfaced as a mystery during a real run. Each new gap costs an investigation cycle plus the missed signal during the run that surfaced it.

**Three recurrences of the pattern, all surfaced after a real run had no insight into a multi-hour phase:**

1. **Cycle 6 (2026-05-04, retired):** 8+ h merge phase emitted exactly one end-of-phase line. Cycle 7 added `SpillEvent` + `MergeProgressEvent` + `MergeCompletedEvent` (commit `c79e590`).
2. **Cycle 8 (2026-05-04/05):** atom-merge instrumented (cycle 7 work paid off here), but the trigram drain — also a multi-hour phase — was characterized only post-hoc via wall-clock arithmetic. No progress emission.
3. **Cycle 9 (2026-05-06/07, in flight):** GSPO drain phase silent for 40+ min and counting after `merge_completed`. We don't know whether it's stuck, slow, or normal-fast. Cycle 8 didn't isolate this phase's duration; cycle 9 won't either without instrumentation.

Each gap is fixed reactively after it surfaces. The META problem — that we discover the gap by running into it — keeps reproducing.

## Why this is a register entry (vs an ADR)

The fix is not architectural; it's procedural. There is no design decision to capture. The discipline is already documented; the gap is in *application*. Limits register is the right home: a deliberate non-decision (we know the pattern exists, we know roughly when it would matter, we have candidate fixes — but we have not committed to any one as a structural change).

## Trigger condition

This limit moves toward a procedural fix when one of:

1. **A fourth recurrence of the same pattern.** Cycle 10+ surfaces another silent phase. Reactive cost across N cycles is paid for the (N+1)th time.
2. **External characterization publication.** The Phase 6 LinkedIn writeup, the WDBench cold baseline, or any future external publication needs phase-by-phase wall-clock decomposition. Reactive instrumentation gaps mean the publication is missing data we can't recover.
3. **Sky Omega 2.0 cognitive-layer milestone.** James (the cognitive orchestrator) cannot gate on absent reporting. If a 1-minute phase has no progress emission, James has no signal to gate against. Pre-James, human review fills the gap; post-James, every gap becomes a structural blind spot.
4. **A reactive instrumentation cycle is itself a *blocking* cost.** When the next big run is queued and we realize a phase needs instrumentation before launch, the run is delayed. Cycle 9's drain instrumentation, if added now, blocks any future cycle 10 launch on the implementation.

## Current state

The discipline rule exists. Specific instrumentation has been added to:

- Parser-side spill (`SpillEvent`) — cycle 7 / commit `c79e590`
- Atom-merge progress (`MergeProgressEvent` + `MergeCompletedEvent`) — cycle 7 / commit `c79e590`
- ADR-035 Phase 7a metric channels — `LoadProgress`, `RebuildProgress`, atom-store events + samplers, process-state samplers — version 1.7.45
- Bulk-builder end (`BulkBuilderCompletedEvent`) — ADR-037 / 1.7.50
- Run configuration banner (`RunConfigurationEvent`) — cycle 7 / commit `c79e590`

Specific gaps known to exist:

- **GSPO drain** (`QuadStore.FlushToDisk` → `FinalizeSortedAtomBulkIfPresent` → drain loop). Surfaced cycle 9. No `DrainProgressEvent` emission. **Currently triggered.**
- **Trigram drain** in rebuild phase (`ExternalSorter<TrigramEntry>.Merge`). Cycle 8 measured ~8 h 24 m via wall-clock arithmetic; no progress emission within the phase. Multi-hour silent.
- **GSPO sort-insert during rebuild** (`AppendSorted` in rebuild flow). Same shape — multi-million records, single emission at end.

## Candidate mitigations

1. **Cross-reference audit (one-shot).** Take the existing `bulk-load-flow.md` stage map (already has 8 stages with measured wall-clock at 21.3B) and cross-reference against `IObservabilityListener` event coverage. Every stage projected to take > 1 min in production must have an emit point. Surface every gap as a discrete limits-register entry. **Bounded effort: 1-2 hours.** Doesn't change behavior; closes the visibility gap on what's instrumented.

2. **Pre-flight instrumentation gate (procedural).** Before any big run launch, run the audit from (1) against current code. If any stage projected > 1 min has no emission, block the launch until added. Effectively makes the discipline part of the launch checklist. **Cost: small per-launch; significant prevention value.**

3. **CI/static-analysis gate (architectural, expensive).** Detect long-running operations (file I/O loops, B+Tree traversals, external-sort merges) that don't accept an `IObservabilityListener` argument. Probably impossible to detect reliably without false positives; not worth the implementation cost for the marginal gain over (2).

4. **Observability-by-default at function level.** Refactor any internal long-loop function to mandatorily accept a listener (default no-op). Removes the "I forgot to wire it" failure mode. **Cost: ripple change across many call sites; touches API surface for `internal` methods. Mid-cost.**

The natural sequencing: ship (1) immediately as the diagnostic; adopt (2) as the per-launch discipline; (4) only if reactive cycles continue past (1) + (2).

## References

- `docs/architecture/technical/observability-coverage.md` — the existing discipline statement
- [`docs/limits/observability-coverage-gap.md`](observability-coverage-gap.md) — sibling, focuses on the rule itself; this entry focuses on systematic application
- [`docs/limits/rebuild-progress-observability.md`](rebuild-progress-observability.md) — sibling, names rebuild-side silent phases specifically
- `docs/architecture/technical/bulk-load-flow.md` — the stage map that mitigation (1) would audit against
- Cycle 6 → cycle 7 instrumentation commit `c79e590` — first reactive close
- Cycle 9 GSPO drain (in flight as of 2026-05-07 20:39 UTC) — the third recurrence; the drain phase has been silent for 40+ min with no signal whether it's stuck, slow, or normal-fast
- ADR-035 (Phase 7a metrics infrastructure) — the framework these gaps fill against
- `feedback_use_drhook.md` (memory) — sibling discipline: "actually use the substrate's observability instead of inferring from external evidence"
