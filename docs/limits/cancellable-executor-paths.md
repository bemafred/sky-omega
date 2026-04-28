# Limit: SPARQL executor has code paths that block past the cancellation token

**Status:**        Triggered
**Surfaced:**      2026-04-28, observed twice during the WDBench cold-baseline run against `wiki-21b-ref`. First in the `paths` category (silent event-loss for ~547 of 660 queries; 3h 25m gap between final logged query and process exit). Second in `c2rpqs` query `00137.sparql` — `status: "timeout"` was emitted but with `elapsed_us = 17,495,488,600` (4 hours 51 minutes) for a 60 s cancellation cap.
**Last reviewed:** 2026-04-28
**Promotes to:**   ADR before any further timed benchmark run. The bug actively corrupts measurements as long as it exists; the cold-baseline writeup must disclose this until it's fixed. ADR draft recommended within the post-baseline window (next 1-3 days).

## Description

The Mercury SPARQL executor accepts a `CancellationToken` and the WDBench harness sets it to fire at 60 seconds per query. For most query shapes the executor honors the token cleanly — the result-enumerator yields control to the cancellation check on every `MoveNext`, the query terminates promptly, and the harness emits a `timeout` record at ~60,002 ms.

For at least one class of queries — observed in property-path-heavy categories — the token *fires* but the execution does not unwind for hours. The harness's `OperationCanceledException` reclassification (`WdBenchRunner.cs`, the `else if (cts.IsCancellationRequested)` branch) sees `IsCancellationRequested = true` and writes a `timeout` record, but only **after** the underlying executor thread eventually returns control. Until then, the harness blocks waiting on the executor.

Two concrete observations, both during the 2026-04-27/28 cold baseline run (`docs/validations/wdbench-cold-baseline-21b-2026-04-27.jsonl`):

1. **`paths` category — silent event loss.** First 113 queries (files `00001.sparql`–`00113.sparql`) processed normally in a 2-minute window starting 03:15:53 UTC. Then the JSONL went silent for 3 h 25 m. The harness shell loop reported `=== Done: paths ===` at 06:42 UTC and continued to the next category cleanly, but **547 of 660 events were never written**. No paths-category summary record was emitted. This is the more pathological mode: the executor doesn't merely block past 60 s, it blocks indefinitely (multi-hour) and the metrics writer never gets a chance to flush the per-query event.
2. **`c2rpqs` query `00137.sparql` — measurable hang magnitude.** Same shape, but the executor *eventually* unwound and the harness *did* emit a metrics record. `elapsed_us = 17,495,488,600` ≈ 4 h 51 m for a 60 s cap. We don't know whether `paths`'s silent gap was 547 separate hangs or one extreme one; this c2rpqs measurement is the first data point where we have an actual elapsed value for the bug.

Combined, these account for ~5 of 9 h of the c2rpqs wall-clock so far and made the `paths` baseline unusable.

The bug is **distinct from** the `SequencePath` planner crash fixed in commit `0c2f88b` (2026-04-27). That fix was at *parse + plan* time. This bug is at *execute* time, and it manifests on queries that successfully parse and plan.

## Trigger condition

Already triggered:
- The WDBench cold-baseline run (in flight as of 2026-04-28) is producing data with a partial `paths` category and a `c2rpqs` outlier so large it dominates the category's wall-clock.
- Any future timed benchmark run on a non-trivial store will hit the same class of queries.

The token-honor contract is not optional for any timeout-driven workload. ADR-level fix is overdue rather than latent.

## Likely root cause

The executor uses cooperative cancellation. Most paths check the token in their inner enumeration loop. Specific paths that don't:

- **Property-path enumeration with high fan-out.** RPQ-style queries (`(<P>/<P>)+`, `^(<P>)*` over Wikidata `wdt:P31` or similar predicates with millions of pivots) build large intermediate result sets. If the property-path enumerator iterates a transitive-closure or fixed-point computation without yielding to a `ThrowIfCancellationRequested()`, a single query can run for hours regardless of the timeout.
- **Tight B+Tree iteration loops.** Some scan operators in `Storage/` walk leaf chains without periodic cancellation checks. Token doesn't propagate into the unsafe pointer-arithmetic hot loop. Honored only at row-yielding boundaries, which are infrequent for fan-out-heavy queries.
- **`SequencePath` and `RegularPath` evaluators.** The planner-level fix (`0c2f88b`) confirms these are areas where cooperative cancellation is patchily wired. If `ComputeVariableHash` had `Term.Start` issues, the runtime evaluators may have analogous gaps in their `MoveNext` implementations.

Without a focused trace (`dotnet-trace` against a stuck query) we don't know exactly which call frame is blocking. That's the first concrete next step when the ADR is drafted.

## Current state

- The cold baseline has at least two affected queries (`paths` partial + `c2rpqs` 00137). More may emerge as `c2rpqs` continues.
- Mercury 1.7.45 ships with the planner-level `SequencePath` synthetic-term fix (commit `0c2f88b`) but no executor-level cancellation audit.
- The `WdBenchRunner` correctly classifies cancelled queries as `timeout` once they unwind. The runner is *not* responsible for the hang; the executor is.
- No regression test today exercises long-running query cancellation. The W3C SPARQL test suite tests correctness, not cancellation behavior.
- This is one of the few places where `dotnet-trace` (per the Sky Omega DrHook substrate) is genuinely necessary — static reasoning about call sites won't identify all the blind spots.

## Candidate mitigations

In rough order of cost / payoff:

1. **Audit and instrument the property-path evaluators.** Add `cancellationToken.ThrowIfCancellationRequested()` at the head of every loop body in `SparqlEngine.Execution.PropertyPath*` and `Operators/PropertyPathScan.cs`. Cheap, mechanical, almost certainly closes 80% of the gap.
2. **Plumb the token into B+Tree iterator hot loops.** The `Storage/` enumerators currently honor cancellation only at row-yield boundaries. A periodic check (every N nodes) inside the leaf-walk loop bounds the worst case to N × leaf-cost regardless of upstream caller.
3. **Add a `dotnet-trace`-driven test that reproduces the hang.** Record the c2rpqs query body and run it with a 5 s cap; assert termination within 10 s. Without a regression test, future fixes can re-break this.
4. **Document the token-honor contract.** A short ADR (or expanded comment in the executor base class) stating "every code path that takes longer than ~50 ms must sample the cancellation token at least once" would make this part of the codebase's discipline rather than a recurring surprise.
5. **Hard kill as a safety net.** If a query exceeds N × the soft timeout (say, 5× = 300 s for a 60 s cap), wrap the executor thread in something that can be aborted. This is harder than it sounds in .NET — `Thread.Abort` is gone — but it's the difference between a 4.86 h hang and a 300 s hang. May not be worth the engineering complexity if (1) and (2) close the gap; revisit only if some query class proves immune to cooperative cancellation.

## Why this matters beyond the baseline

WDBench is a benchmark, but the underlying issue affects any workload with a query timeout — production HTTP endpoints, multi-user services, anything that needs to bound query latency. A 60 s SPARQL endpoint that occasionally takes 5 hours to return is not a 60 s endpoint. Honoring the cancellation contract is the difference between Mercury being deployable behind a customer-facing surface and being a development-only tool.

Once `paths` is the only place this fires (and the ADR maps the affected operators), the win is large: the WDBench scores currently being recorded as "timeout" for queries that *would* complete in 70-90 s become accurately classified, and the long-tail hangs disappear from production deployments.

## References

- `docs/validations/wdbench-cold-baseline-21b-2026-04-27.jsonl` — the data showing both observed instances
- Commit `0c2f88b` — the related but distinct planner-level `SequencePath` fix (parse-time, not execute-time)
- `benchmarks/Mercury.Benchmarks/WdBenchRunner.cs` — the harness whose cancellation classification surfaced the issue cleanly
- `src/Mercury/Sparql/Execution/Operators/` — the directory most likely to contain the offending paths
- ADR-035 Phase 7a metrics infrastructure — the JSONL pipeline that captured `elapsed_us` accurately even when the query violated the token contract
