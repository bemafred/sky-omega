# ADR-031: Read-Only Session Fast Path

## Status

**Status:** Proposed — 2026-04-20 (amended 2026-04-20 with Dispose-profile findings)

## Context

The full-pipeline gradient (2026-04-19) closed at 1 B triples with a predicate-bound COUNT query over GPOS that ran in **49 s of query time** — and then took **~14 min on Dispose** across 648 GB of mmap'd indexes on a cold-opened cognitive-profile store. See [full-pipeline-gradient-2026-04-19.md:143](../../validations/full-pipeline-gradient-2026-04-19.md).

The validation doc attributed that 14 min to "msync across the 648 GB of mmap'd indexes" and flagged the fix direction as "skip msync on Dispose entirely for read-only sessions." A later profile on 2026-04-20 ([dispose-profile-2026-04-20.md](../../validations/dispose-profile-2026-04-20.md)) **refuted the msync attribution**. The actual dominant cost is `CollectPredicateStatistics()`, called unconditionally from `CheckpointInternal()` at `QuadStore.Dispose()`. It scans all 1 B triples via a full GPOS enumeration and builds per-predicate `HashSet<long>` of subjects and objects — CPU-bound work, not I/O. `grep msync` on the sample output returned **0 matches**.

The amended ADR retains its original *direction* — a read-only session should not pay for write-side Dispose work — but its *mechanism* changes. This is the point of the Epistemics phase: the measurement surfaced a design error in the drafted fix before any engineering was committed to it.

**Reads have no durability contract.** A session that only queries the store mutates nothing — no atoms created, no B+Tree pages modified, no WAL entries appended, no statistics invalidated. Every piece of machinery that exists to honor the write-path ACID invariants (CheckpointInternal's statistics collection, WAL checkpoint marker, writer-lock acquisition, WAL recovery gate) is dead weight on such a session. The bulk-load ADRs optimized the write path by carefully relaxing parts of the ACID contract where a special durability model applied. The read path deserves the symmetric treatment — not "relax" the contract but observe that it does not apply at all.

At 1 B cognitive profile, the dominant cost is **`CollectPredicateStatistics` (full 14 min out of 14 min)**. Open-time costs (WAL replay, writer-lock probing, mmap access mode) are in the low-seconds range and therefore secondary. The ordering of fixes should follow the cost.

### Two kinds of read-only — declared and inferred

Read-only is not one concept. It is two, and they deserve separate treatment:

- **Structural read-only (declared by the store).** A [Reference profile](ADR-029-store-profiles.md) store is immutable, non-temporal, dump-sourced — that's its entire purpose. The profile declaration *is* the read-only declaration. Any session opened against a Reference store is read-only because the store cannot accept writes, not because the session happened not to make any. Declaring this at the store level (via profile metadata) is not ceremony — it is load-bearing, because the store's schema, lifecycle, and intended use all hinge on it.
- **Situational read-only (inferred from the session).** A Cognitive / Graph / Minimal store accepts writes. Any given session against it may or may not mutate. Asking the caller to declare `OpenReadOnly` when `Query(...)` already tells us nothing will mutate is ceremony — it is a knob that restates what the call site already encodes. Here inference is correct: the session is read-only iff nothing was written by Dispose.

The two cases get different mechanisms, and this ADR treats them separately. Declared read-only (piece 1) needs no flag and no state machine — the store's profile says "read-only", every session opened against it starts and stays read-only, and the Dispose fast path runs unconditionally. Inferred read-only needs the flag-tracked Dispose (piece 2) and the optimistic escalation machinery (piece 3).

The Sky Omega principle that APIs must be load-bearing applies to both: the profile is the right place to declare store-level immutability; the session API is the wrong place to restate it.

### Why this is an ADR and not "just add an if-guard"

Three reasons.

First, declared vs inferred read-only is itself a semantic distinction worth committing to. Conflating the two leads to bad APIs (adding `OpenReadOnly` to mutable-profile stores on top of an already-declarative profile system) or bad invariants (inferring per session for Reference stores that should reject writes structurally). The split has to be explicit, or subsequent code will drift.

Second, for mutable profiles, "was anything written in this session?" is a cross-cutting invariant. Every mutation path — `AtomStore.WriteAtom`, every `QuadIndex.Add/Remove`, WAL append, trigram entry, statistics update — has to feed a single flag reliably. Missing one path means CheckpointInternal is skipped on a session that did mutate; the next Open sees stale `_statistics` and an un-marked WAL. That is load-bearing scaffolding, not a one-liner, even though the Dispose-side change itself is.

Third, deferred commitment (piece 3 below) reshapes how stores open. Today `QuadStore.Open` acquires the writer lock, opens mmaps for read-write, and gates on WAL state. After piece 3, the default open path on a mutable profile is cheap and read-only, with an escalation point on first mutation. That is an architectural change in the open lifecycle and cannot be rolled back without coordinating callers.

## Decision

Three pieces, in increasing order of complexity. The first is structural and falls out of [ADR-029](ADR-029-store-profiles.md) at essentially zero cost. The second and third are the inference-based machinery for mutable-profile stores.

### 1 — Reference profile stores are structurally read-only at the session API

A store whose profile metadata records `"profile": "Reference"` is opened read-only from the session-API perspective, unconditionally. This piece is the session-layer realization of [ADR-029 Decision 7](ADR-029-store-profiles.md) ("bulk-mutable, session-API immutable"). The precise contract:

- `QuadStore.Open` on a Reference-profile store skips the writer lock, opens all mmap'd files with `MemoryMappedFileAccess.Read`, skips posting-file growth capacity setup, and never runs WAL recovery (a Reference store has no WAL — ADR-029 lists `temporal: no`, `versioning: no`, which together mean no transactional session write path).
- Any mutation call through the session API (SPARQL UPDATE, triple insert, etc.) fails immediately with a clear error at the query-planning layer — same discipline as ADR-029's rejection of `AS_OF` queries against Reference stores.
- `Dispose` always skips `CheckpointInternal` for Reference stores — there is nothing session-caused to checkpoint, ever (no WAL, no `_statistics` maintenance).

**Bulk-load is a separate matter, not governed by this piece.** Per ADR-029 Decision 7, a Reference store remains bulk-appendable — the bulk-load path ([ADR-030](ADR-030-bulk-load-and-rebuild-performance.md)) has its own durability model, owns the store exclusively during load, and runs as its own process-level session with its own Dispose discipline. From the perspective of ADR-031 piece 1, bulk-load is not a "session API mutation" — it is a distinct lifecycle that sits outside the session open/Dispose path this ADR optimizes. The two coexist cleanly: session-API callers always see a read-only store; bulk-load is the sole write path and follows the rules in ADR-030.

This piece requires no new invariants and no state machine — it is a direct consequence of the profile declaration plus ADR-029 Decision 7. It belongs in this ADR only to make the read-only semantics explicit at the session-open layer, alongside ADR-029's planner-layer rejection of temporal queries.

### 2 — Mutation-tracked Dispose (inferred, mutable profiles)

Add a single `volatile bool _sessionMutated` flag to `QuadStore`. Every mutation path sets it to `true`. Only `Dispose` reads it.

On `Dispose`, gate the `CheckpointInternal()` call on the flag:

```csharp
public void Dispose() {
    if (_disposed) return;
    _disposed = true;

    if (_sessionMutated)          // was: unconditional
        CheckpointInternal();

    _wal?.Dispose();
    _gspoIndex?.Dispose();
    _gposIndex?.Dispose();
    _gospIndex?.Dispose();
    _tgspIndex?.Dispose();
    _trigramIndex.Dispose();
    _atoms?.Dispose();
    _lock?.Dispose();
}
```

`CheckpointInternal()` (at `QuadStore.cs:805`) calls `CollectPredicateStatistics()` (at `QuadStore.cs:849`), which does a full GPOS scan and builds per-predicate `HashSet<long>` of subjects and objects. At 1 B triples this is the 14 min cost established in [dispose-profile-2026-04-20.md](../../validations/dispose-profile-2026-04-20.md). For a read-only session the statistics cannot have changed — the computation is pure waste.

`CheckpointInternal()` also calls `_wal.Checkpoint()` to write a checkpoint marker. For a read-only session there is nothing to checkpoint; skipping is correct.

**Invariants:**

- The flag is write-once per session (false → true is the only transition; Dispose is the only reader). No read-modify-write. `volatile` suffices; no interlocked needed.
- Every mutation path sets the flag before the mutation commits. Tests enumerate every public-API mutation entry point and assert the flag flips. Missing a path means the session skips CheckpointInternal after mutations, which would leave stale `_statistics` at next Open until the next CheckpointInternal — degraded query-planner input, not durability loss, but still a correctness bug worth catching with an enumeration test.
- WAL state matters. If a session opened, observed a non-empty WAL from a previous run, and replayed it — that replay IS a mutation. The flag flips. CheckpointInternal runs. This is not optional.

This phase has **no API change**. It is pure behavior — callers continue to call `Open` and `Dispose` as they do today, and read-only sessions silently become dramatically faster to close.

### 3 — Deferred commitment for mutable-profile stores (optimistic read-open) — **deferred to 031b**

The 2026-04-20 Dispose profile significantly reduces the value proposition of this piece. Before the profile, "14 min Dispose" was assumed to be msync-dominated and Piece 3's escalation machinery offered parallel wins on the open side (cheaper open + avoid the msync-family at close). With the profile's finding that Piece 2 captures the entire 14 min, Piece 3's remaining value is the open-side cost: skip writer-lock acquisition, open mmap as read-only, skip WAL replay until a writer appears. At 1 B cognitive profile, those open-side costs are in the low-seconds range — not milliseconds, but not minutes either.

Relative to Piece 3's complexity (live mmap upgrade, `AcquirePointer` invalidation under escalation, concurrent read+write serialization), the remaining open-side wins do not justify Piece 3 in the 1.8.0 timeline. **This ADR defers Piece 3 to a follow-up (031b or a later query-performance ADR)** and closes at Pieces 1 and 2 for 1.8.0.

The original Piece 3 design survives in this ADR as a sketch, in case a future measurement re-opens the case:

- **Open returns in `ReadOnly` state.** No writer lock. Mmaps opened `MemoryMappedFileAccess.Read`. WAL is observed but not replayed (a non-empty WAL on open is a valid read-only state — we just cannot write). Posting files are opened at current size, no growth capacity.
- **First mutation attempt triggers escalation.** `_sessionMutated = true` also fires an escalation path: acquire writer lock, re-map each file for read-write, run WAL recovery if needed, initialize growth capacity. Then the mutation proceeds.
- **Escalation is atomic relative to reads.** Ongoing reads hold `AcquirePointer` handles on the read-only mmap regions. Escalation waits for those to drain (or uses copy-on-write mmap tricks — TBD). The invariant is that no reader sees a partially-escalated store.

The live mmap upgrade is the hard bit — on most OSes, upgrading a `MAP_PRIVATE` or read-only `MAP_SHARED` mapping to read-write requires `munmap` + `mmap` with new flags, which invalidates existing pointers. `AcquirePointer` already handles pointer invalidation for posting-file growth; extending that model to cover escalation is mechanical but non-trivial.

A signal to revisit this piece: if open-side costs measurably dominate query latency in some real workload after Piece 2 ships, 031b picks this up.

### Scope boundary — NOT in this ADR

- **Query latency improvements.** The 49 s predicate-bound COUNT at 1 B is a scan/selectivity problem. Zero overlap with this ADR. Belongs in a future query-performance ADR.
- **Cross-process read-only sharing.** Multiple reader processes attached to the same store is desirable but out of scope. The single-writer contract ([ADR-020](ADR-020-atomstore-single-writer-contract.md)) currently assumes at most one open store process. Extending it is a separate decision.
- **Opt-in hints on mutable-profile stores.** The API stays clean. No `IntentHint.ReadOnly` parameter, no `OpenMode` enum for Cognitive/Graph/Minimal profile stores. If a future caller has strong prior knowledge that a session will be read-only, piece 3's escalation machinery already gives them the performance they want without declaring it. (Reference-profile stores are already covered by piece 1 — the profile *is* the declaration.)

## Consequences

### Positive

- **14 min → essentially zero on Dispose for read-only sessions at 1 B.** The entire `CheckpointInternal()` call — including the `CollectPredicateStatistics` full-GPOS scan that accounts for all ~14 min — is skipped. Close becomes dominated by unmap + file-handle release, sub-second at any scale. Unconditionally true for Reference profile stores; true for mutable profiles when the session made no mutations.
- **Reference profile stores get the fast path for free.** No flag, no escalation, no state machine. Piece 1 is a direct consequence of ADR-029 and lands before the harder machinery. The Wikidata mirror benefits from day one.
- **No API change on the session surface.** Existing callers get the win for free. `mercury query`, SPARQL HTTP, MCP sessions — all read-dominant, all benefit without modification.
- **Correct by construction on mutable profiles.** The flag either flipped (so CheckpointInternal runs, statistics get updated, WAL checkpoint marker gets written) or it didn't (so none of that matters — statistics haven't changed, there is no checkpoint to take). No caller-declared mode that can be lied about.
- **The fix is a one-line change.** `if (_sessionMutated) CheckpointInternal();` around an existing call. No new subsystem, no durability-contract analysis, no mmap reasoning — just a guard. The mechanism is much simpler than the msync-skip approach originally drafted.
- **Composes with [ADR-029](ADR-029-store-profiles.md) (profiles) and [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) (perf).** Reference profile makes the 648 GB smaller *and* declares the fast path structurally; this ADR makes Dispose free whenever it's safe. Together they move "query a 1 B store" from a minute-scale operation to a second-scale one.

### Negative

- **New correctness-load-bearing invariant on mutable profiles.** Every mutation path must set the flag. Missing one means CheckpointInternal is skipped after mutations, leaving `_statistics` stale until the next mutating session triggers a checkpoint. Degraded query-planner input, not durability loss — but still a correctness bug worth catching. Reference profile stores are immune (no mutation paths exist for them). Mitigation in the Risks section.
- **Observability story shifts slightly.** Today "Dispose took N seconds" is a reliable proxy for "the store was this big". After Piece 2, Dispose time depends on whether writes happened. Metrics dashboards that conflate the two will need adjustment. Minor — flagged for honesty.
- **Piece 3 is deferred.** The remaining open-side wins (writer-lock skip, read-only mmap, deferred WAL replay) are real but small once Piece 2 has banked the 14 min. Acknowledged as a future ADR (031b) if a concrete workload surfaces the need.

### Risks

- **Missed mutation path on mutable profiles.** If a mutation code path forgets to set `_sessionMutated`, CheckpointInternal is skipped on a session that actually mutated. Result: `_statistics` is not updated in memory for the next Open; the WAL checkpoint marker is not written. `_statistics` recovers on the next mutating session that does reach CheckpointInternal. No durability loss to atoms or indexes (those are their own Flush paths, independent of CheckpointInternal). Mitigation: (1) a regression test that enumerates every public mutation API and asserts the flag flips; (2) the flag is set by `AtomStore.WriteAtom` / `QuadIndex.Add` / WAL append at the lowest levels, so higher-level wrappers can't bypass it; (3) audit grep for all call sites that mutate atom or index state as part of ADR-031 review.
- **Profile misclassification.** If metadata incorrectly records a store as Reference when the original intent was Cognitive, Piece 1 would open it read-only and reject writes. Symmetric concern for the other direction. Mitigation: profile is written exactly once at store creation and verified on every open (ADR-029's "hard error on open" on schema mismatch); this risk lives in ADR-029, not here.
- **False sense of free.** "Read is free now" may invite callers to open/close per-query more aggressively, which still incurs mmap + page-cache-warm costs on each open. Not a correctness risk but a performance-expectation one.

## Implementation plan

Shipped in this order — earlier pieces do not depend on later pieces.

**Piece 1 — Reference profile declared read-only (depends on ADR-029)**

1. Wait for [ADR-029](ADR-029-store-profiles.md) Phase 1 (profile metadata + `ReferenceQuadIndex`) to land. Piece 1 here cannot precede it.
2. In `QuadStore.Open`, branch on the stored profile. If `Reference`:
   - Skip writer-lock acquisition.
   - Open all mmap'd files with `MemoryMappedFileAccess.Read`.
   - Skip WAL recovery (Reference stores have no WAL per ADR-029).
   - Skip posting-file growth capacity setup.
3. Add mutation-rejection at the session API boundary: SPARQL UPDATE and any mutation entry point fails at plan time with a clear error when the open store's profile is `Reference`. Same mechanism ADR-029 already uses for temporal-query rejection.
4. `Dispose` branches: Reference profile unconditionally skips `CheckpointInternal` (nothing to checkpoint — no WAL, no `_statistics` to update).
5. Test: open a Reference-profile Wikidata store, run a predicate-bound COUNT, measure open time and Dispose time.

**Piece 2 — Mutation-tracked Dispose (mutable profiles)**

1. Add `private volatile bool _sessionMutated` to `QuadStore`.
2. Audit every mutation path that invalidates `_statistics` or needs a WAL checkpoint marker:
   - `AtomStore.WriteAtom`
   - `QuadIndex.Add` / `Remove` / `AddRaw`
   - WAL append paths (including replay on Open if the WAL was non-empty)
   - Trigram entry updates
   - Explicit `Checkpoint()` call (flag not strictly needed here since the caller already intends durability, but setting it is harmless)
3. Set the flag in each path at the earliest point before the mutation commits.
4. Gate `CheckpointInternal()` in `Dispose()` on the flag — one-line change at `QuadStore.cs:1173`:
   ```csharp
   if (_sessionMutated) CheckpointInternal();
   ```
5. Test: enumerate every public mutation API, assert flag flips; assert flag stays false after pure-query sessions on a Cognitive store.
6. Test: 1 B cognitive store, predicate-bound COUNT, measure Dispose time before/after. Target: under 60 s total for open+query+close (down from ~15 min), with Dispose sub-second.
7. Test: mutate-then-Dispose behavior unchanged — `_statistics` still updated on the next Open, WAL checkpoint marker still written when mutations occurred.

**Piece 3 — Deferred to 031b**

Not part of the 1.8.0 timeline. If a future workload measurement shows open-side costs dominating after Piece 2, a follow-up ADR (031b) picks up the optimistic read-open design sketched in the Decision section above.

**Piece 4 — ADR update and retrospective**

- Move status Proposed → Accepted after Piece 2 passes correctness tests at 1 B (Piece 1 lands with ADR-029).
- Status → Completed after Piece 1 and Piece 2 ship and are validated against the 1 B store. Piece 3 is explicitly deferred to 031b as part of this ADR's scope decision — no further work required to close 031.
- Update [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md) with post-fix Dispose numbers.

## Open questions

- **Does WAL state count as mutation if no user write occurred?** If a previous session crashed mid-write and left a non-empty WAL, the current session that observes it on Open and replays it has mutated the on-disk indexes. The flag must flip in that case; CheckpointInternal runs on Dispose. Not applicable to Reference stores (no WAL).
- **How do we test "flag correctly flipped" without writing a meta-test that every mutation API owner must remember to update?** Candidate: reflection-based enumeration of all public mutation entry points on `QuadStore` + `AtomStore`, assert each flips the flag. Requires a naming convention or attribute on mutation methods. Worth proposing as part of Piece 2.
- **What about sessions that open read-only but observe shared state changing under them?** Not relevant today (single-process contract) but will matter if cross-process read sharing ever ships — particularly for Reference stores, which are the natural candidates for multi-reader sharing. Flagged, out of scope.

## Closed questions

- ~~**Does `msync(MS_SYNC)` on a clean mmap actually cost 14 min, or is something else in the Dispose path dominant?**~~ **Answered 2026-04-20 by the Dispose profile: `CollectPredicateStatistics` is the cost, not msync.** See [dispose-profile-2026-04-20.md](../../validations/dispose-profile-2026-04-20.md). Piece 2's mechanism rewritten accordingly.

## References

- [ADR-020](ADR-020-atomstore-single-writer-contract.md) — single-writer contract preserved
- [ADR-023](ADR-023-transactional-integrity.md) — WAL and transaction-time semantics; `_wal.Checkpoint()` in `CheckpointInternal()` is the WAL side of what Piece 2 skips
- [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) — the pipeline whose query-phase Dispose cost motivated this ADR
- [ADR-029](ADR-029-store-profiles.md) — prerequisite for Piece 1; Reference profile is the structural read-only declaration this ADR wires into the open path
- [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) — sibling perf ADR on the write side
- [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md) — original observation (49 s query + 14 min Dispose at 1 B)
- [dispose-profile-2026-04-20.md](../../validations/dispose-profile-2026-04-20.md) — the profile that identified `CollectPredicateStatistics` as the true cost
- Source: `src/Mercury/Storage/QuadStore.cs:805-822` (`CheckpointInternal`) and `:849-896` (`CollectPredicateStatistics`)
