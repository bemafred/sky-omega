# Dispose-Time Profile of wiki-1b — 2026-04-20

**Status:** The 14 min Dispose observed on 2026-04-19 is **not msync**. It is `CollectPredicateStatistics()` doing a full GPOS scan of all 1 B triples with per-predicate `HashSet<long>` inserts, called unconditionally from `CheckpointInternal()` on every `Dispose()`.

Move 4 of the pre-Engineering epistemic checklist (see `docs/roadmap/production-hardening-1.8.md`). Purpose: confirm or refute the attribution in the 2026-04-19 validation note — "That's msync across the 648 GB of mmap'd indexes on a cold-opened cognitive-mode store." The refutation has direct implications for [ADR-031](../adrs/mercury/ADR-031-read-only-session-fast-path.md).

## Reproduction

Mercury 1.7.23. Same query as 2026-04-19: predicate-bound COUNT via GPOS.

```bash
echo 'SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }' \
  | mercury --store wiki-1b --no-http
```

Total wall time on the first (unprofiled) run: **894 s**. Of that, the query reported 43.2 s execution and 7 ms parse. **Dispose alone therefore accounts for ~850 s ≈ 14 min 10 s** — matching the 2026-04-19 observation within timing tolerance.

## Profiling method

`sample` on the live process during a 60 s window starting ~70 s into the run (i.e., inside the Dispose phase, after query completion). Sample file at `/tmp/profile-dispose-sample.txt`, 99 KB, 51,575 main-thread samples.

After the sample completed, SIGTERM to mercury to short-circuit the remaining ~12 min of Dispose (safe — read-only session, no dirty pages).

## Headline finding

**Zero msync samples.** `grep msync` on the sample output: 0 matches.

All 51,575 main-thread samples are in JIT'd .NET code. 49,717 of them (96 %) are in one specific unresolved address (`0x10ab73cb0`), indicating a tight loop in a single managed method.

Other threads are all idle:
- Finalizer thread: parked in `WaitForFinalizerEvent`
- Dispatch queues: in `mach_msg` / `kevent`
- Diag IPC / debugger threads: blocked on `__poll_nocancel` / `psynch_cvwait`

This rules out: msync, fsync, munmap, close-time metadata sync, finalizer pressure, GC pauses, async I/O stalls, disk contention. The 14 min is single-threaded CPU in managed code, not kernel I/O.

## Source-level identification

Tracing `QuadStore.Dispose()`:

- `src/Mercury/Storage/QuadStore.cs:1166` — `Dispose()`
- → `:1173` — `CheckpointInternal()` called unconditionally before index/WAL/atoms disposal
- → `:805` — `CheckpointInternal()` calls `CollectPredicateStatistics()` then `_wal.Checkpoint()`
- → `:849` — `CollectPredicateStatistics()` does `_gposIndex.QueryAsOf(..., DateTimeOffset.UtcNow)` — a full scan of the GPOS index
- Per-quad, it updates `Dictionary<long, (long count, HashSet<long> subjects, HashSet<long> objects)>` — 1 B HashSet.Add calls across all distinct predicates

At 1 B triples, this is a CPU-bound iteration with ~2 B HashSet inserts (subjects + objects). The 14 min is plausible for that work.

The code is guarded against bulk-load mode (`_bulkLoadMode` short-circuits in `CheckpointIfNeeded` at :837) but not against read-only sessions. A query-only session that mutates nothing still recomputes statistics identical to what was already in memory, on every Dispose.

## Implications for ADR-031

The ADR-031 draft assumed msync was the dominant cost and designed around skipping it. The mechanism was wrong, but **the direction was right**: a read-only session should not pay for write-side maintenance work. The corrected fix is simpler than the drafted one:

```csharp
public void Dispose() {
    if (_disposed) return;
    _disposed = true;

    if (_sessionMutated)
        CheckpointInternal();  // was: unconditional

    _wal?.Dispose();
    _gspoIndex?.Dispose();
    // ... rest unchanged
}
```

A single `if` around one existing call. No mmap reasoning, no msync semantics, no durability contract to reason about (CheckpointInternal's durability contract applies only when mutations occurred).

Expected savings for a read-only query session at 1 B: full 14 min → essentially zero Dispose-side work.

Downstream effects:
- **ADR-031 Piece 2** — mechanism changes (gate CheckpointInternal, not msync); projected savings increase (full 14 min, not "dominant fraction").
- **ADR-031 Piece 3** (optimistic read-open with live mmap escalation) — loses its primary motivation. Open-path costs (WAL replay, writer-lock acquisition) remain real but small compared to the 14 min we now account for in Piece 2. Piece 3 moves from "desirable before 1.8" to "defer to 031b or beyond."
- **ADR-031 Piece 1** (Reference profile structural read-only) — unchanged and simpler. Reference has no WAL and no `_statistics` to update; skipping CheckpointInternal trivially falls out of the profile.

## What this does NOT say

- We did not profile the write path. CheckpointInternal's cost for an actually-mutated session is out of scope here.
- We did not measure whether CollectPredicateStatistics' result ever improves query plans enough to justify its cost. That's a separate question about query-planner value.
- We did not profile cold open — only Dispose. Open costs for a cognitive-profile store at 1 B are separately interesting but not this run.
- The 96 % concentration at `0x10ab73cb0` is JIT'd code; we did not resolve which exact line of CollectPredicateStatistics the hotspot is. Likely HashSet.Add itself or a hash-compute inner loop. The root cause is unambiguous from the code-path match; sub-method attribution doesn't change the fix.

## What this closes

- ADR-031's "Open question" — "Does `msync(MS_SYNC)` on a clean mmap actually cost 14 min, or is something else in the Dispose path dominant?" — **answered: it's CollectPredicateStatistics, not msync.** Removable from the ADR's Open Questions list.
- Move 4 of the production-hardening Epistemics checklist is done.

## References

- [ADR-031 — Read-Only Session Fast Path](../adrs/mercury/ADR-031-read-only-session-fast-path.md) — amended based on this finding
- [full-pipeline-gradient-2026-04-19.md](full-pipeline-gradient-2026-04-19.md) — original 14 min observation (line 143)
- `src/Mercury/Storage/QuadStore.cs:805-822` (CheckpointInternal) and `:849-896` (CollectPredicateStatistics)
- Roadmap Phase 4 — [production-hardening-1.8.md](../roadmap/production-hardening-1.8.md) — exit criterion updated to match
