# ADR-041: Cleanup-on-exception for bulk-tmp intermediates

## Status

**Status:** Completed — 2026-05-16 (Proposed 2026-05-11; Completed 2026-05-16 — implementation shipped 1.7.58 with 4 validation tests green)

## Context

The 1.7.49 cleanup hook (cycle 9, production-validated) reclaims intermediate sort chunks at end-of-merge under the normal success path: `MergeAndWrite` returns successfully → `SortedAtomBulkBuilder.Finalize` completes → cleanup hook fires → bulk-tmp emptied.

When `Finalize` throws — observed twice in cycle 10 Phase 3:

| Date | Failure | Bulk-tmp state | Manual cleanup required |
|---|---|---|---|
| 2026-05-10 (1.7.52) | `BBHashBuilder` int32 `OverflowException` at ~4 B atoms | ~1.2 TB retained | yes (manual rm -rf) |
| 2026-05-11 (1.7.54) | `BBHashBuilder did not converge` after 24 levels | ~1.2 TB retained | yes (manual rm -rf) |

In both cases, the parser + merge work succeeded and produced byte-correct `atoms.atoms` + `atoms.offsets`. Only the post-merge MPHF construction failed. The cleanup hook never fired because the throw propagated out of `Finalize`, bypassing the success-path cleanup.

This forces an operator to:

1. Diagnose the failure.
2. Decide whether to retry, resume, or restart.
3. Manually `rm -rf` the orphaned 1.2 TB before relaunching, OR risk the next bulk-load attempting reuse / failing on disk pressure.

The 1.7.55 `--rebuild-mphf` recovery path (ADR-040-related but separately specified) allows resuming from the sealed atoms — but it doesn't address the orphaned bulk-tmp. After successful rebuild-mphf, the operator still has to clean manually.

This ADR specifies cleanup-on-exception so that bulk-tmp is reclaimed regardless of the success or failure path through `MergeAndWrite`.

## Hypothesis (falsifiable)

**H1 — Cleanup-on-exception is correctness-preserving.** Adding `try/finally` around the bulk-tmp-using portion of `MergeAndWrite` so cleanup runs on both success and failure does not change the contents of `atoms.atoms` / `atoms.offsets` (the only outputs the cleanup hook does not touch).

**Falsified if:** a Reference profile bulk-load with the cleanup-on-exception change produces a substrate-identity-different `atoms.atoms` vs without the change, OR if a Finalize-thrown run leaves any intermediate file behind.

**H2 — Operator workflow improves measurably.** With cleanup-on-exception, the operator's recovery sequence drops from "kill → manual rm -rf 1.2 TB (~10 s on APFS) → rebuild-mphf" to "kill → rebuild-mphf" — same wall-clock for the rebuild but no operator intervention required for cleanup.

**Falsified if:** post-failure disk audit shows bulk-tmp residue OR the cleanup itself triggers a different failure mode (e.g., partial cleanup leaving the substrate in an ambiguous state).

## Decision

### Part 1 — Try/finally around bulk-tmp consumers in `MergeAndWrite`

The current cleanup-hook invocation lives at the success path of `MergeAndWrite`:

```csharp
// Today:
foreach (var path in chunkFiles) { ... use chunk path ... }
// Merge complete, build MPHF, etc.
foreach (var path in chunkFiles) { try { File.Delete(path); ... } }
// Hook end.
```

Proposed:

```csharp
try
{
    foreach (var path in chunkFiles) { ... }
    // Build MPHF, etc.
}
finally
{
    foreach (var path in chunkFiles)
    {
        try { File.Delete(path); chunksDeleted++; chunkBytesReclaimed += size; }
        catch (IOException) { /* swallow — exception path may have left files locked */ }
    }
    // Emit a "cleanup_summary" metric event regardless of outer outcome,
    // with a "cleanup_phase" field distinguishing "merge_success" from "merge_exception"
    // so per-cycle attribution can tell which path fired.
}
```

The metric event distinguishes success-path cleanup (cycle-9 measured 3.96 TB at end-of-merge) from exception-path cleanup, so post-cycle attribution preserves the signal.

### Part 2 — Don't clean if the same outputs survive

The cleanup-on-exception path must not delete `atoms.atoms`, `atoms.offsets`, `atoms.mphf`, or `atoms.idx` — those are the merge's *outputs*, not its intermediates. The hook touches only paths in `chunkFiles` (intermediate sorted-chunk files in `bulk-tmp/`), so this is satisfied by construction. Add an assertion in the cleanup loop that each `chunkFiles[i]` path contains `bulk-tmp/` as a segment, fail-fast on any path that doesn't.

### Part 3 — Idempotency

`File.Delete` is idempotent (`FileNotFoundException` is silently swallowed). The cleanup hook can run twice — once on a successful Finalize, once if the operator re-invokes the same substrate manually — without ambiguity.

### Part 4 — Visibility

A `BulkTmpCleanupEvent` structured event:

```csharp
public sealed class BulkTmpCleanupEvent : MetricEvent
{
    public string Phase => "bulk_tmp_cleanup";
    public string Trigger;           // "merge_success" | "merge_exception" | "manual_rebuild"
    public int ChunksDeleted;
    public long ChunkBytesReclaimed;
    public double ElapsedSeconds;
    public bool AnyDeleteFailures;
    public string? FirstFailureMessage; // diagnostic if any chunk couldn't be deleted
}
```

Emitted exactly once per `MergeAndWrite` invocation, in the `finally` block.

## Consequences

### Positive

- **Disk pressure resolved regardless of success path.** A failed Finalize no longer holds 1.2 TB hostage on the substrate disk; the next operator action (e.g., `--rebuild-mphf`) starts from a clean slate.
- **Operator workflow simplifies.** No manual `rm -rf` step in the recovery runbook.
- **Substrate behavior becomes uniform.** Cleanup is a property of `MergeAndWrite`, not of "the run completed successfully" — easier to reason about.
- **Attribution is preserved.** The `Trigger` field on `BulkTmpCleanupEvent` distinguishes success vs exception paths, so cycle attribution remains accurate.

### Negative / risks

- **Diagnostics lost on failure.** Today, the orphaned bulk-tmp serves as forensic evidence — the operator can inspect intermediates to understand what happened. With cleanup-on-exception, those intermediates are gone the moment the exception surfaces. Mitigation: an `MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1` env var preserves the existing behavior for diagnostic sessions; default behavior cleans up.
- **Cleanup itself could throw.** If a chunk file is locked (e.g., another process is reading it), `File.Delete` raises. The proposed catch swallows `IOException` and records the failure in the metric event; the substrate doesn't propagate cleanup-time exceptions over the original exception. Could mask a second-order failure mode.
- **Race with `--rebuild-mphf`.** If the operator launches `--rebuild-mphf` concurrently with the failing process's cleanup, file conflict possible. Mitigation: `--rebuild-mphf` doesn't touch bulk-tmp; it only reads `atoms.atoms` / `atoms.offsets`. Documented in the command's help text.

### Neutral

- **Cleanup adds end-of-merge time when it fires.** Cycle 9 measured 3.96 TB reclaimed at end-of-merge — APFS unlink is fast (~9 s for 1.4 TB measured during cycle-10 cleanup); negligible vs the multi-hour merge phase.

## Validation plan

A standalone integration test in `Mercury.Tests`:

1. **Cleanup on success** (regression — existing behavior preserved). Build a small store via bulk-load, verify bulk-tmp is empty post-`Finalize`.
2. **Cleanup on Finalize exception.** Wrap a synthetic builder that throws during MPHF construction (e.g., `MaxLevels=1` over many keys forcing dense-final overflow), verify bulk-tmp is empty post-throw.
3. **Cleanup with preserve-flag set.** Set `MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1`, repeat (2), verify bulk-tmp is NOT cleaned.
4. **Idempotency.** Call `MergeAndWrite` twice on the same substrate (second call obviously fails for a different reason); verify no error from cleanup on the second call.

Validation document: `docs/validations/adr-041-cleanup-on-exception-{date}.md`.

## Alternatives considered

- **Atexit hook on the process.** Mercury could register a process-exit handler that walks all stores and cleans bulk-tmp. Rejected: operator may explicitly want to inspect intermediates after a crash; process-exit cleanup is too aggressive.
- **Periodic sweep at store-open.** When opening a store, find any bulk-tmp directory that doesn't belong to the active substore (per pool.json) and delete it. Useful complementary mechanism but doesn't address the within-substore case that motivated this ADR.
- **Just document the manual rm -rf.** Punts the operator burden. Inconsistent with substrate-discipline ("no shortcuts"). Rejected.

## References

- 1.7.49 cleanup hook (cycle 9, production-validated): [`docs/validations/adr-037-cycle9-21b-2026-05-09.md`](../../validations/adr-037-cycle9-21b-2026-05-09.md)
- Cycle 10 Phase 3 failure 1 (1.7.52, int32 overflow): see `CHANGELOG.md` 1.7.53 release notes
- Cycle 10 Phase 3 failure 2 (1.7.54, MPHF non-convergence): see `CHANGELOG.md` 1.7.55 release notes
- ADR-040 readahead-memory adaptive sizing — complementary substrate-discipline piece around merge-phase resource correctness
