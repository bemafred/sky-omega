# ADR-007: Sealed Substrate Immutability — Re-create, Don't Modify

**Status:** Completed — 2026-05-02

**Context:** Sky Omega has substrates that are sealed by design — write-once, then immutable for the duration of their lifecycle. The Reference profile (Mercury, ADR-029) is the first concrete instance. Future Minerva will likely seal model weights once loaded. DrHook's attached-process inspection has sealed-snapshot semantics during the inspection window. Each substrate's seal carries different mechanics, but the architectural principle is the same.

## Problem

The Mercury Reference profile is built around a single-bulk-load contract (`QuadStore.cs:182`, ADR-034 Decision 7): the SortedAtomStore-backed Reference store is loaded once, then sealed. Decision 7 of ADR-029 rejects session-API mutations against Reference at plan time. The substrate's value proposition is "a sealed canonical snapshot you can query at scale" — the seal is a feature, not an accidental constraint.

The current `PruneEngine` (`src/Mercury.Pruning/PruneEngine.cs`) implements pruning as a dual-instance copy-and-switch: iterate primary, write surviving quads to secondary, swap. This works for Cognitive (mutable, WAL-backed). For Reference, it hits two structural blockers:

1. **Single-bulk-load constraint** — the secondary, freshly created, can be bulk-loaded once. After the first prune, the new primary (formerly secondary) is sealed; pruning *again* requires a fresh secondary, but the secondary's underlying SortedAtomStore is already past its single-bulk-load window.
2. **Session vs bulk batch mode** — `PruningTransfer` uses `BeginBatch/CommitBatch` (session mode) calling `AddCurrentBatched`. For Reference this routes to `AddReferenceBulkTriple`, which requires the in-bulk flag. Whether the session batch sets that flag is an implementation detail; either way, the path was not designed for Reference.

The deeper issue: **pruning a sealed substrate is a category error**. A seal that can be modified isn't a seal — it's just a hint. If the Reference profile claims to be canonical-snapshot-immutable, an in-place prune (even via copy-and-switch) violates the claim.

The same pattern will recur:
- **Sealed Minerva model weights**: "fine-tune" or "patch this layer" against a sealed model is the same shape — wanting in-place mutation of a substrate whose value comes from being unmutable.
- **DrHook attached-process snapshots**: tools that mutate the snapshot during inspection (e.g., function-eval with side effects) violate the snapshot's canonical-state contract.

Without an architectural rule, each substrate handles "but I want to modify it" ad-hoc. The likely outcome is exception-throwing without a clear "what should I do instead?" answer.

## Decision

**A sealed substrate exposes its data via re-creation, not in-place modification.** Operations that would mutate the seal are rejected at plan time with an error pointing to the re-creation alternative.

### The pattern

For any sealed substrate:

```
sealed_substrate.modify(filter)  → ProfileCapabilityException
  "Sealed substrates cannot be modified in place.
   To produce a filtered subset:
     <substrate-specific re-creation command>"
```

The re-creation command produces a NEW sealed substrate from the SOURCE data plus the filter. The original sealed substrate remains untouched and queryable until explicitly removed by the human.

### Concrete instances

| Substrate | Modification request | Rejection | Re-creation alternative |
|-----------|----------------------|-----------|--------------------------|
| Mercury Reference (ADR-029) | `prune --exclude-graphs ...` on sealed Reference | `ProfileCapabilityException` | `mercury --create-store wiki-subset --profile Reference --bulk-load source.bz2 --exclude-graphs ...` |
| Mercury Reference (ADR-029) | session-API `INSERT/DELETE` | (already enforced by Decision 7) | re-bulk-load with desired triples |
| Future Minerva sealed weights | "fine-tune layer N" | `ModelSealedException` | re-load adapter weights into a new sealed model instance |
| Future DrHook snapshot | mutating expression-eval during inspection | warn-and-proceed OR reject (per-substrate decision) | take a fresh snapshot post-mutation |

### Why re-creation, not "rebuild from current state"

A "rebuild from current state" pattern (read sealed → write filtered → swap) has the same architectural shape as in-place modification. It violates the seal in spirit: the user can no longer point to "the canonical snapshot from <date>" because that snapshot's identity is now ambiguous (was it pre-rebuild or post-rebuild?). Re-creation from the SOURCE preserves the canonical-snapshot contract: each sealed instance has a clear provenance — "loaded from source X with filter Y at time T."

This shifts the storage cost calculation: keeping the original sealed substrate around alongside the new one consumes more disk. That's the right tradeoff. Sealed substrates are large by nature (Reference at 21.3 B Wikidata triples is ~3 TB); a user who creates a filtered subset typically wants both: the canonical full snapshot AND the working subset. If the user wants to free the original, they delete it explicitly — the human is in the loop for that destructive action.

### Why this is top-level, not Mercury-specific

The principle applies to any substrate that claims canonical-snapshot semantics. Mercury Reference is the first concrete instance; sealed Minerva model weights will be the second. The architectural rule "sealed substrates are re-created, not modified" is part of how Sky Omega defines what "sealed" means at the platform level. Pushing it into a per-substrate ADR would invite each substrate to redefine the term.

## Implementation

1. **Mercury (immediate):**
   - Add a `ProfileCapabilityException` check at the top of `PruneEngine.Execute(...)` that rejects `Reference` profile primary stores. Error message points to `mercury --create-store ... --bulk-load source --exclude-graphs ...`.
   - Update `mercury prune` CLI help and `MERCURY.md` to document the rejection and the re-creation alternative.
   - Test: attempt to prune a Reference store, assert the exception and message.
2. **Future Minerva:** when sealed-model semantics are introduced, the same pattern — `ModelSealedException` with a re-load-adapter alternative.
3. **DrHook:** audit MCP surface and CLI for snapshot-mutation operations against this ADR; classify per tool.

## Consequences

**Positive:**
- "Sealed" is now a precise architectural term across substrates, not a per-substrate hint.
- Users get clear guidance ("here's the command you actually want") rather than cryptic exceptions.
- Canonical-snapshot provenance is preserved — an analyst can always point to "the wiki-21b-ref store loaded 2026-04-25 from latest-all.ttl.bz2" without wondering whether it's been silently modified.
- Aligns with the governed-automation thesis: destructive sealed-substrate modifications are pushed to explicit human re-creation, not hidden behind an in-place pruning verb.

**Negative:**
- Disk cost: keeping the original sealed substrate alongside a filtered subset doubles storage during the lifetime where both exist.
- Loss of "modify the seal" as an option for advanced users who understand the implication. They can still modify via re-creation; the cost is wall-clock for the re-bulk-load (~85 h for full Wikidata Reference profile), which is real friction.

**Neutral:**
- The `mercury prune` CLI behavior changes per profile: works on Cognitive, rejects on Reference. This is consistent with Decision 7's plan-time profile-capability rejections.

## Status transitions

- **Proposed** — 2026-04-30. Pending review.
- **Accepted** — when the implementation lands for Mercury Reference.
- **Completed** — when (1) Reference rejection ships in PruneEngine, (2) MERCURY.md and CLI help document the alternative, (3) DrHook surface has been audited against this ADR.

## References

- ADR-006 — MCP Surface Discipline (companion ADR; `mercury_prune` removal from MCP is the parallel concern)
- ADR-029 (mercury) — Store Profiles, Decision 7 (Reference rejects session-API mutations) — this ADR generalizes the seal beyond mutations to include in-place rebuilds
- ADR-034 (mercury) — SortedAtomStore single-bulk-load constraint that makes Reference pruning structurally impossible
- `src/Mercury.Pruning/PruneEngine.cs` — implementation site
- `src/Mercury/Storage/QuadStore.cs:182` — the existing Decision 7 enforcement point
- Memory: `project_governed_automation_thesis` — the broader philosophy this ADR operationalizes for sealed substrates
