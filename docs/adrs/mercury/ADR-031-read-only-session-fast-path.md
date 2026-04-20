# ADR-031: Read-Only Session Fast Path

## Status

**Status:** Proposed — 2026-04-20

## Context

The full-pipeline gradient (2026-04-19) closed at 1 B triples with a predicate-bound COUNT query over GPOS that ran in **49 s of query time** — and then took **~14 min of msync on Dispose** across 648 GB of mmap'd indexes on a cold-opened cognitive-profile store. See [full-pipeline-gradient-2026-04-19.md:143](../../validations/full-pipeline-gradient-2026-04-19.md).

The validation doc flagged this as a UX wart with an obvious fix direction:

> Not a bug, but a UX wart — opening and closing a 1 B store is expensive by design when msync semantics are honored. If this becomes a problem in practice we can revisit: defer msync on read-only opens, or skip msync on Dispose entirely for read-only sessions.

That fix direction is correct, and the architectural framing is cleaner than "UX wart":

**Reads have no durability contract.** A session that only queries the store mutates nothing — no atoms created, no B+Tree pages modified, no WAL entries appended. Every piece of machinery that exists to honor the write-path ACID invariants (msync on Dispose, writer-lock acquisition, WAL recovery gate, mmap opened for read-write, posting-file growth capacity) is dead weight on such a session. The bulk-load ADRs optimized the write path by carefully relaxing parts of the ACID contract where a special durability model applied. The read path deserves the symmetric treatment — not "relax" the contract but observe that it does not apply at all.

At 1 B cognitive profile, the dominant cost is msync on Dispose (~14 min of ~14 min). Open-time costs (WAL replay, writer-lock probing, mmap access mode) are in the low-seconds range today and therefore secondary. The ordering of fixes should follow the cost.

### Two kinds of read-only — declared and inferred

Read-only is not one concept. It is two, and they deserve separate treatment:

- **Structural read-only (declared by the store).** A [Reference profile](ADR-029-store-profiles.md) store is immutable, non-temporal, dump-sourced — that's its entire purpose. The profile declaration *is* the read-only declaration. Any session opened against a Reference store is read-only because the store cannot accept writes, not because the session happened not to make any. Declaring this at the store level (via profile metadata) is not ceremony — it is load-bearing, because the store's schema, lifecycle, and intended use all hinge on it.
- **Situational read-only (inferred from the session).** A Cognitive / Graph / Minimal store accepts writes. Any given session against it may or may not mutate. Asking the caller to declare `OpenReadOnly` when `Query(...)` already tells us nothing will mutate is ceremony — it is a knob that restates what the call site already encodes. Here inference is correct: the session is read-only iff nothing was written by Dispose.

The two cases get different mechanisms, and this ADR treats them separately. Declared read-only (piece 1) needs no flag and no state machine — the store's profile says "read-only", every session opened against it starts and stays read-only, and the Dispose fast path runs unconditionally. Inferred read-only needs the flag-tracked Dispose (piece 2) and the optimistic escalation machinery (piece 3).

The Sky Omega principle that APIs must be load-bearing applies to both: the profile is the right place to declare store-level immutability; the session API is the wrong place to restate it.

### Why this is an ADR and not "just skip the msync"

Three reasons.

First, declared vs inferred read-only is itself a semantic distinction worth committing to. Conflating the two leads to bad APIs (adding `OpenReadOnly` to mutable-profile stores on top of an already-declarative profile system) or bad invariants (inferring per session for Reference stores that should reject writes structurally). The split has to be explicit, or subsequent code will drift.

Second, for mutable profiles, "was anything written in this session?" is a cross-cutting invariant. Every mutation path — `AtomStore.WriteAtom`, every `QuadIndex.Add/Remove`, WAL append, trigram entry, statistics update — has to feed a single flag reliably. Missing one path means we skip msync on a session that did mutate, which silently corrupts durability. That is load-bearing scaffolding, not a one-liner.

Third, deferred commitment (piece 3 below) reshapes how stores open. Today `QuadStore.Open` acquires the writer lock, opens mmaps for read-write, and gates on WAL state. After piece 3, the default open path on a mutable profile is cheap and read-only, with an escalation point on first mutation. That is an architectural change in the open lifecycle and cannot be rolled back without coordinating callers.

## Decision

Three pieces, in increasing order of complexity. The first is structural and falls out of [ADR-029](ADR-029-store-profiles.md) at essentially zero cost. The second and third are the inference-based machinery for mutable-profile stores.

### 1 — Reference profile stores are structurally read-only at the session API

A store whose profile metadata records `"profile": "Reference"` is opened read-only from the session-API perspective, unconditionally. This piece is the session-layer realization of [ADR-029 Decision 7](ADR-029-store-profiles.md) ("bulk-mutable, session-API immutable"). The precise contract:

- `QuadStore.Open` on a Reference-profile store skips the writer lock, opens all mmap'd files with `MemoryMappedFileAccess.Read`, skips posting-file growth capacity setup, and never runs WAL recovery (a Reference store has no WAL — ADR-029 lists `temporal: no`, `versioning: no`, which together mean no transactional session write path).
- Any mutation call through the session API (SPARQL UPDATE, triple insert, etc.) fails immediately with a clear error at the query-planning layer — same discipline as ADR-029's rejection of `AS_OF` queries against Reference stores.
- `Dispose` always skips msync. There is nothing session-caused to flush, ever.

**Bulk-load is a separate matter, not governed by this piece.** Per ADR-029 Decision 7, a Reference store remains bulk-appendable — the bulk-load path ([ADR-030](ADR-030-bulk-load-and-rebuild-performance.md)) has its own durability model, owns the store exclusively during load, and runs as its own process-level session with its own Dispose discipline. From the perspective of ADR-031 piece 1, bulk-load is not a "session API mutation" — it is a distinct lifecycle that sits outside the session open/Dispose path this ADR optimizes. The two coexist cleanly: session-API callers always see a read-only store; bulk-load is the sole write path and follows the rules in ADR-030.

This piece requires no new invariants and no state machine — it is a direct consequence of the profile declaration plus ADR-029 Decision 7. It belongs in this ADR only to make the read-only semantics explicit at the session-open layer, alongside ADR-029's planner-layer rejection of temporal queries.

### 2 — Mutation-tracked Dispose (inferred, mutable profiles)

Add a single `volatile bool _sessionMutated` flag to `QuadStore` (and mirror it in any per-session context that owns mutation authority). Every mutation path sets it to `true`. No path reads the flag except `Dispose`.

On `Dispose`:

- If `_sessionMutated == false`: skip msync entirely on all index files and the atom store. Close file handles and unmap as normal. Release WAL state without flush.
- If `_sessionMutated == true`: current behavior — full msync + fsync across all mmap'd files, WAL commit, the works.

**Invariants:**

- The flag is write-once per session (false → true is the only transition; Dispose is the only reader). No read-modify-write. `volatile` suffices; no interlocked needed.
- Every mutation path sets the flag before the mutation commits. Tests enumerate every public-API mutation entry point and assert the flag flips. Missing a path is a correctness bug, caught by the enumeration test, not by production data loss.
- WAL state matters. If a session opened, observed a non-empty WAL from a previous run, and replayed it — that replay IS a mutation. The flag flips. Dispose msync runs. This is not optional.

This phase has **no API change**. It is pure behavior — callers continue to call `Open` and `Dispose` as they do today, and read-only sessions silently become dramatically faster to close.

### 3 — Deferred commitment for mutable-profile stores (optimistic read-open)

Today `QuadStore.Open`:
- Acquires the writer lock (`CrossProcessStoreGate`).
- Opens index files with `MemoryMappedFileAccess.ReadWrite`.
- Runs WAL recovery if the WAL is non-empty.
- Initializes posting-file growth capacity.

On a read-only session against a mutable-profile store, every one of those is unnecessary. Piece 3 introduces a lazy-escalation state machine:

- **Open returns in `ReadOnly` state.** No writer lock. Mmaps opened `MemoryMappedFileAccess.Read`. WAL is observed but not replayed (a non-empty WAL on open is a valid read-only state — we just cannot write). Posting files are opened at current size, no growth capacity.
- **First mutation attempt triggers escalation.** `_sessionMutated = true` also fires an escalation path: acquire writer lock, re-map each file for read-write, run WAL recovery if needed, initialize growth capacity. Then the mutation proceeds.
- **Escalation is atomic relative to reads.** Ongoing reads hold `AcquirePointer` handles on the read-only mmap regions. Escalation waits for those to drain (or uses copy-on-write mmap tricks — TBD in implementation). The invariant is that no reader sees a partially-escalated store.

Piece 3 is substantially more complex than piece 2. The live mmap upgrade is the hard bit — on most OSes, upgrading a `MAP_PRIVATE` or read-only `MAP_SHARED` mapping to read-write requires `munmap` + `mmap` with new flags, which invalidates existing pointers. `AcquirePointer` already handles pointer invalidation for posting-file growth; extending that model to cover escalation is mechanical but non-trivial.

If piece 3 proves hard enough that it derails other work, it can ship as a separate ADR (call it 031b) and this ADR closes at pieces 1 and 2 only. The dominant win is piece 2 for mutable profiles and piece 1 for Reference profiles — both unblocked without piece 3.

### Scope boundary — NOT in this ADR

- **Query latency improvements.** The 49 s predicate-bound COUNT at 1 B is a scan/selectivity problem. Zero overlap with this ADR. Belongs in a future query-performance ADR.
- **Cross-process read-only sharing.** Multiple reader processes attached to the same store is desirable but out of scope. The single-writer contract ([ADR-020](ADR-020-atomstore-single-writer-contract.md)) currently assumes at most one open store process. Extending it is a separate decision.
- **Opt-in hints on mutable-profile stores.** The API stays clean. No `IntentHint.ReadOnly` parameter, no `OpenMode` enum for Cognitive/Graph/Minimal profile stores. If a future caller has strong prior knowledge that a session will be read-only, piece 3's escalation machinery already gives them the performance they want without declaring it. (Reference-profile stores are already covered by piece 1 — the profile *is* the declaration.)

## Consequences

### Positive

- **14 min → seconds on Dispose for read-only sessions at 1 B.** The entire msync pass is skipped. Close becomes dominated by unmap + file-handle release, both microsecond-scale. Unconditionally true for Reference profile stores; true for mutable profiles when the session made no mutations.
- **Reference profile stores get the fast path for free.** No flag, no escalation, no state machine. Piece 1 is a direct consequence of ADR-029 and lands before the harder machinery. The Wikidata mirror benefits from day one.
- **No API change on the session surface.** Existing callers get the win for free. `mercury query`, SPARQL HTTP, MCP sessions — all read-dominant, all benefit without modification.
- **Correct by construction on mutable profiles.** The flag either flipped (so we do full durability work) or it didn't (so there's nothing to flush). No caller-declared mode that can be lied about.
- **Composes with [ADR-029](ADR-029-store-profiles.md) (profiles) and [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) (perf).** Reference profile makes the 648 GB smaller *and* declares the fast path structurally; piece 3 makes the open path cheaper for mutable stores too; the whole ADR makes the close path free whenever it's safe. Together they move "query a 1 B store" from a minute-scale operation to a second-scale one.
- **Piece 3 enables cheaper read-heavy workloads on mutable profiles.** SPARQL HTTP serving many concurrent reads against a Cognitive store, MCP sessions that open+query+close repeatedly, ad-hoc CLI queries — all pay reduced open-time cost.

### Negative

- **New correctness-load-bearing invariant on mutable profiles.** Every mutation path must set the flag. Missing one is a silent durability bug. Reference profile stores are immune — no mutation paths exist for them. Mitigation in the Risks section.
- **Piece 3's escalation state machine is non-trivial.** Live mmap upgrade is the hard part. If it proves too hard, piece 3 can be deferred to a follow-up ADR without blocking pieces 1 and 2.
- **Observability story shifts slightly.** Today "Dispose took N seconds" is a reliable proxy for "the store was this big". After piece 2, Dispose time depends on whether writes happened. Metrics dashboards that conflate the two will need adjustment. Minor — flagged for honesty.

### Risks

- **Missed mutation path on mutable profiles.** If a mutation code path forgets to set `_sessionMutated`, Dispose skips msync and durability is silently violated. This is the critical correctness risk for piece 2. Mitigation: (1) a regression test that enumerates every public mutation API and asserts the flag flips; (2) the flag is set by `AtomStore.WriteAtom` / `QuadIndex.Add` / WAL append at the lowest levels, so higher-level wrappers can't bypass it; (3) audit grep for all call sites that mutate mmap'd memory as part of ADR-031 review.
- **Piece 3 mmap escalation race.** A reader holding `AcquirePointer` on a read-only mmap, concurrent with a writer triggering escalation. Mitigation: leverage the existing `AcquirePointer` invalidation model already proven for posting-file growth; extensive concurrency tests before piece 3 merges.
- **Profile misclassification.** If metadata incorrectly records a store as Reference when the original intent was Cognitive, piece 1 would open it read-only and reject writes. Symmetric concern for the other direction. Mitigation: profile is written exactly once at store creation and verified on every open (ADR-029's "hard error on open" on schema mismatch); this risk lives in ADR-029, not here.
- **False sense of free.** "Read is free now" may invite callers to open/close per-query more aggressively, which still incurs mmap + page-cache-warm costs. Not a correctness risk but a performance-expectation one.

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
4. `Dispose` branches: Reference profile unconditionally skips msync/fsync.
5. Test: open a Reference-profile Wikidata store, run a predicate-bound COUNT, measure open time and Dispose time.

**Piece 2 — Mutation-tracked Dispose (mutable profiles)**

1. Add `private volatile bool _sessionMutated` to `QuadStore`.
2. Audit every mutation path:
   - `AtomStore.WriteAtom`
   - `QuadIndex.Add` / `Remove` / `AddRaw`
   - WAL append paths
   - Trigram entry updates
   - Statistics / metadata updates
   - Posting file growth (itself a mutation, even if the user didn't add a triple — it's a file-level change)
3. Set the flag in each path at the earliest point before the mutation commits to disk.
4. Branch `Dispose`: if flag is false, skip msync/fsync; close handles as normal.
5. Test: enumerate every public mutation API, assert flag flips; assert flag stays false after pure-query sessions on a Cognitive store.
6. Test: 1 B cognitive store, predicate-bound COUNT, measure Dispose time before/after. Target: < 5 s (down from ~14 min).

**Piece 3 — Deferred commitment (mutable profiles)**

1. Add `QuadStoreState` enum: `ReadOnly`, `ReadWrite`. Applies only to mutable-profile stores (Reference stores are unconditionally `ReadOnly`, no state).
2. `Open` on a mutable profile starts in `ReadOnly` state. Skip writer lock, skip WAL replay, mmap as `MemoryMappedFileAccess.Read`.
3. Add `EscalateToReadWrite()` internal method. Called from any mutation path before the mutation proceeds.
4. Implement mmap upgrade — munmap + mmap with new flags, atomically invalidate and re-issue pointers via existing `AcquirePointer` model.
5. WAL replay deferred to escalation point.
6. Test: open read-only, observe non-empty WAL, query succeeds without replay; escalate on write, WAL replays, write succeeds.
7. Test: concurrent read + write escalation with stall injection.
8. Baseline: measure open time on 1 B cognitive store, read-only vs current. Expected win is smaller than piece 2 but non-trivial on cold open.

**Piece 4 — ADR update and retrospective**

- Move status Proposed → Accepted after piece 2 passes correctness tests at 1 B (piece 1 lands with ADR-029).
- Status → Completed after piece 3 ships or is explicitly deferred to a follow-up ADR.
- Update [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md) with post-fix Dispose numbers.

## Open questions

- **Does `msync(MS_SYNC)` on a clean mmap actually cost 14 min, or is something else in the Dispose path dominant?** The validation doc attributes it to msync, but a proper dtrace/`Instruments` profile on macOS would confirm. If the cost is actually `close()`-time metadata sync or APFS fsync ceremony, the piece-2 win might come from a different syscall. The fix shape is unchanged either way — if nothing was written, skip the expensive syscall — but the specifics inform implementation.
- **Does WAL state count as mutation if no user write occurred?** If a previous session crashed mid-write and left a non-empty WAL, the current session that observes it has two choices: (a) replay-on-open (flag flips, full Dispose cost), or (b) defer replay until a writer appears. Piece 2 can take (a) for simplicity; piece 3 must take (b). This distinction is worth calling out during piece 3 design. Not applicable to Reference stores (no WAL).
- **How do we test "flag correctly flipped" without writing a meta-test that every mutation API owner must remember to update?** Candidate: reflection-based enumeration of all public mutation entry points on `QuadStore` + `AtomStore`, assert each flips the flag. Requires a naming convention or attribute on mutation methods. Worth proposing as part of piece 2.
- **What about sessions that open read-only but observe shared state changing under them?** Not relevant today (single-process contract) but will matter if cross-process read sharing ever ships — particularly for Reference stores, which are the natural candidates for multi-reader sharing. Flagged, out of scope.

## References

- [ADR-020](ADR-020-atomstore-single-writer-contract.md) — single-writer contract preserved; read-only sessions take no writer lock in piece 3
- [ADR-023](ADR-023-transactional-integrity.md) — WAL and transaction-time semantics that read-only sessions bypass
- [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) — the pipeline whose query-phase Dispose cost motivated this ADR
- [ADR-029](ADR-029-store-profiles.md) — prerequisite for piece 1; Reference profile is the structural read-only declaration this ADR wires into the open path
- [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) — sibling perf ADR on the write side
- [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md) — original observation (49 s query + 14 min Dispose at 1 B)
