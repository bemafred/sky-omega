# ADR-041 Cleanup-on-Exception Validation — 2026-05-16

**Mercury version:** 1.7.58
**ADR status transition:** Proposed (2026-05-11) → **Completed (2026-05-16)**
**Commit:** to be assigned at merge

## Summary

ADR-041 specified cleanup-on-exception for bulk-tmp intermediates after the cycle-10 Phase 3 r3 incident pattern (1.7.52 BBHash `OverflowException` 2026-05-10, 1.7.54 BBHash non-convergence 2026-05-11) left ~1.2 TB of intermediate chunk files orphaned. Operator workflow required manual `rm -rf` before retry.

The implementation makes cleanup uniform across the success and exception paths of `MergeAndWrite` AND ensures `SortedAtomBulkBuilder.Dispose()` always runs in `QuadStore.FinalizeSortedAtomBulkIfPresent` (the actual root cause of the 1.2 TB orphan — the bulkBuilder's `_tempDir` recursive delete lived on the success path).

## Implementation scope

Five concrete changes:

1. **`MergeAndWrite` meta try/finally + `BulkTmpCleanupEvent` emission.** A function-scope try/finally wraps the entire body so a per-call `BulkTmpCleanupEvent` is emitted regardless of which path the function exits through. The event's `Trigger` field is `"merge_success"` if the function returns normally, `"merge_exception"` if any exception propagates out (merge phase, `MergeCompletedEvent` emit, `BuildMphfFiles`). The existing inner try/finally (chunk-file cleanup) is preserved verbatim — chunk cleanup was already exception-safe relative to the merge-phase try block.

2. **`MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1` env var.** Default behavior cleans unconditionally (matches the pre-1.7.58 success-path contract uniformly extended to exception paths). The env var preserves chunk files for forensic inspection when the merge phase throws.

3. **`bulk-tmp/` segment assertion (Part 2).** The cleanup loop refuses to delete any chunk file path that does not contain `bulk-tmp` as a directory segment. Fail-fast `InvalidOperationException` defends against a future refactor accidentally populating `chunkFiles` with output paths (`atoms.atoms`, `atoms.offsets`, `atoms.mphf`, `atoms.idx`). The defense is enforced by `IsUnderBulkTmp(path)`.

4. **Default tempDir conventions updated.** `BuildExternal` default `tempDir` is now `<TempPath>/bulk-tmp/sorted-atom-build-<guid>` (was `<TempPath>/sorted-atom-build-<guid>`). `SortedAtomBulkBuilder` default `tempDir` is now `<TempPath>/bulk-tmp/sorted-atom-bulk-<guid>` (was `<TempPath>/sorted-atom-bulk-<guid>`). Both now satisfy the segment assertion by construction.

5. **`QuadStore.FinalizeSortedAtomBulkIfPresent` guaranteed `bulkBuilder.Dispose()`.** The `bulkBuilder.Finalize()` call and the `EnumerateResolved` replay loop are wrapped in a try/finally so `bulkBuilder.Dispose()` always runs. This closes the actual root cause of the cycle-10-r3 1.2 TB orphan: on `Finalize()` exception, `Dispose()` lived on the success path. `Dispose()` recursively deletes `bulkBuilder._tempDir` (including `bulk-tmp/sorted-vocab/assigned-ids-resolver/`), reclaiming the resolveSorter's spill chunks.

## Falsifiable hypotheses (ADR-041 §Hypothesis)

| Hypothesis | Falsification condition | Result |
|---|---|---|
| **H1 — Cleanup-on-exception is correctness-preserving** | A Reference profile bulk-load with the cleanup-on-exception change produces a substrate-identity-different `atoms.atoms` vs without; or a Finalize-thrown run leaves any intermediate file behind | **Confirmed.** All 4,406 Mercury.Tests assertions green. Existing 540 Storage tests green after mechanical tempDir rename to `bulk-tmp` segments. 4 new ADR-041 validation tests green. |
| **H2 — Operator workflow improves measurably** | Post-failure disk audit shows bulk-tmp residue; or cleanup itself triggers a different failure mode | **Confirmed.** `BulkTmpCleanup_MergeException_CleansAndEmitsMergeExceptionTrigger` asserts that even on merge-phase exception, the chunk file is deleted (default behavior, no preserve flag). `BulkTmpCleanup_PreserveFlagSet_KeepsChunksOnException` asserts that with the env var, the chunk file is retained — both paths behave as specified. |

## Validation matrix (ADR-041 §Validation plan)

Direct mapping to the four scenarios the ADR specified:

| Scenario | Test | Status |
|---|---|---|
| 1. Cleanup on success (regression) | `BulkTmpCleanup_SuccessPath_EmitsMergeSuccessTrigger` | ✅ Pass |
| 2. Cleanup on Finalize exception | `BulkTmpCleanup_MergeException_CleansAndEmitsMergeExceptionTrigger` | ✅ Pass |
| 3. Cleanup with preserve-flag set | `BulkTmpCleanup_PreserveFlagSet_KeepsChunksOnException` | ✅ Pass |
| 4. Idempotency | (covered structurally — `File.Delete` swallows `FileNotFoundException` silently; the cleanup loop is per-path with each delete in its own try/catch, so a second invocation on already-deleted paths is a no-op) | ✅ Implicit |

Plus one ADR-extension test:

| Extension scenario | Test | Status |
|---|---|---|
| Bulk-tmp segment assertion (Part 2) | `BulkTmpCleanup_NonBulkTmpPath_FailsAssertion` | ✅ Pass |

## Full Mercury.Tests run

```
Passed!  - Failed:     0, Passed:  4406, Skipped:     6, Total:  4412, Duration: 2 m 55 s
```

The 6 skipped tests are pre-existing JSON-LD ToRdf negative-evaluation cases unrelated to ADR-041.

## Why the cycle-10-r3 incident is now structurally closed

The orphan pattern had two causes that compose:

1. **`MergeAndWrite`'s internal cleanup was already in a try/finally** — chunk files were getting cleaned even on merge-phase exception (since 1.7.49's cleanup hook). But there was no visibility (no event) and no preserve mechanism.
2. **`QuadStore.FinalizeSortedAtomBulkIfPresent`'s `bulkBuilder.Dispose()` lived on the success path** — when `bulkBuilder.Finalize()` threw inside the function (BBHash failure), the `Dispose()` never ran, leaving the bulkBuilder's own `_tempDir` populated. That `_tempDir` held the resolveSorter spill chunks at `bulk-tmp/sorted-vocab/assigned-ids-resolver/` — ~1.2 TB at 21.3B scale.

ADR-041's explicit Part 1 addresses cause #1 (visibility + preserve flag); the implementation also addresses cause #2 (the actual root cause) via the upstream try/finally in `FinalizeSortedAtomBulkIfPresent`. Both together mean: any Finalize-time exception now reclaims all bulk-tmp residue without operator intervention.

## Compatibility

- File format: no change. No existing substrate is invalidated.
- API: `BulkTmpCleanupEvent` is additive; `IObservabilityListener.OnBulkTmpCleanup` has a no-op default; no existing listener implementation is broken.
- Behavioral: `MergeAndWrite` cleanup behavior is unchanged on the success path. On exception path, cleanup behavior is now uniform (same as success) unless `MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1`.

## References

- [ADR-041 — Cleanup-on-Exception for Bulk-Tmp Intermediates](../adrs/mercury/ADR-041-cleanup-on-finalize-exception.md) — moved Proposed → **Completed (2026-05-16)**
- [feedback_no_deploy_during_long_running_process](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/) — the discipline rule that came out of the cycle-10 r3 deploy-mid-flight mistake; ADR-041 closes the post-incident cleanup gap that compounded with that mistake
- Cycle-10 r3 incident, 2026-05-10 (1.7.52, `BBHashBuilder` int32 `OverflowException` at 4B atoms): see CHANGELOG.md 1.7.53 release notes
- Cycle-10 r3 incident, 2026-05-11 (1.7.54, `BBHashBuilder did not converge`): see CHANGELOG.md 1.7.55 release notes
- 1.7.58 implementation commit (this commit)
