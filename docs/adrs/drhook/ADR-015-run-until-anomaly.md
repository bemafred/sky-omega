# ADR-015: Run-Until-Anomaly — repeat-until-failure driving with freeze-on-catch for intermittent runtime bugs

**Status:** Proposed — 2026-07-04 (Emergence)

## Context

The [ADR-012](ADR-012-debug-state-surfaces.md) Q8 visual-capture arc surfaced a **rare intermittent** UI-thread hang in a captured Avalonia debuggee (one beachball, then unreproducible). Isolating it meant hand-writing a bash **catch-loop**: repeat the capture against a live target, health-check the UI thread with macOS `sample` after each, and hunt for a hang. It cost ~40 iterations across the poc and the real MCP path, caught nothing, and — critically — even a hang *would* only have been read post-hoc by `sample`, because the loop had no way to **freeze the target at the moment of failure** for live inspection. (Recorded: Mercury `sessions/2026-06-29-drhook-visual-capture-live` — `obs-capture-hang-is-rare-race`, `obs-capture-hang-catch-loop-negative`.)

That pattern — *drive an operation repeatedly until an intermittent failure manifests, bounded by a cap* — is a first-class feature in test runners:

- **GoogleTest** is the canonical form: `--gtest_repeat=N --gtest_break_on_failure` repeats until the first failure and, *under a debugger*, **drops into it so you can inspect variables and stacks** (`-1` = forever; `--gtest_shuffle` for order-dependent flakiness).
- **pytest**: `pytest-repeat` (`--count`) + `-x` (stop at first failure), and `pytest-flakefinder` (run each test 20× and report).
- The **inverse is an anti-pattern**: `pytest-rerunfailures` re-runs *failed* tests until they pass — it **masks** flakiness. DrHook must be the opposite: expose, never hide.

The decisive observation: GoogleTest's best form explicitly pairs *repeat-until-failure* with *hand control to a debugger on failure*. **DrHook is that debugger.** So the native DrHook form is strictly stronger than the test-runner feature — repeat until failure, then **automatically freeze the target at the failure** and hand the operator (the LLM) the live frozen state (locals, full stack, every thread) — exactly the capability the bash catch-loop lacked.

The substrate for this already exists: the typed anomaly channel (`EngineAnomaly` / `AnomalyKind` / `IDebugEventSink.OnAnomaly`), break-on-exception (`drhook_break_exception`), the stop machinery (`WaitForStop`, the hold-at-stop discipline), and the hypothesis braid ([ADR-012](ADR-012-debug-state-surfaces.md) Phase 3). What is missing is a primitive that *composes* them: **"keep driving until a failure signal fires, then hold it for inspection."**

## Decision (proposed)

Add a **run-until-anomaly** primitive to `DrHook.Engine` and expose it as an MCP tool (`drhook_run_until`): repeatedly drive the target until a failure **predicate** fires or **bounds** are hit; on fire, leave the target **stopped at the failure** and return the frozen state.

**Predicates**, in increasing difficulty (ship in this order):

1. **Exception / anomaly** *(MVP)* — a first-chance or unhandled exception, or an `EngineAnomaly`. Nearly free: break-on-exception already stops on the condition; this is *"resume, and do not stop until it fires"* + bounds + freeze. The direct `--gtest_break_on_failure` analog.
2. **Liveness / hang** *(phase 2)* — the target stops making progress: a watchdog on a progress signal (a heartbeat breakpoint whose hit-count stalls, or a stop/health timeout). **This is the class the motivating hang belongs to** — a silent deadlock raises no exception, so the MVP predicate cannot see it.
3. **Caller condition** *(phase 3)* — a breakpoint-policy condition or a hypothesis-check: fire when a predicate over target state becomes true.

**The driver** (what repeats):

- **Continue-until-fire** *(MVP)* — arm the predicate and resume; let the target run until it fires. The natural fit for "let it run until it crashes / hangs."
- **Compound driver** *(deferred)* — repeat an operation sequence (e.g. `attach → op → detach → check`, or a step sequence). More general, but expressing "the operation" over MCP is harder; not in the first cut.

**Freeze-on-catch is the differentiator.** On predicate fire the target is **held at the failure** (not resumed, not detached), so `drhook_locals` / `drhook_snapshot` / `drhook_snapshot_image` / stepping inspect the *live* failure. This is what a test runner cannot give and what a manual `break_on_failure` gives only if you were already attached.

**Bounds are mandatory** (resource-limit discipline, per `reference_resource_limits_checklist`): both an **iteration cap** and a **wall-clock timeout**. On exhaustion, report *"N iterations, no catch"* — a clean, explicit negative, never a silent success.

**Braid integration.** Each run carries a `hypothesis` (as every DrHook state-changing tool does); the catch — or the clean exhaustion — is its reconciliation. The repeat count and outcome are themselves a `DebugStateDelta`, so a connected view and the Mercury capture see the hunt as it happens.

## Validation (planned — what moves Proposed → Accepted)

- **MVP, positive:** run-until-exception against (a) a deterministically-throwing path → catches on iteration 1; (b) an *intermittently*-throwing path → catches within the cap; both freeze so `drhook_locals` reads the failure state.
- **MVP, negative:** a clean target runs to the cap and reports *"no catch"* — bounds honored, no hang, no leak.
- **Phase 2:** a target that deadlocks a thread → the liveness watchdog fires and freezes it; DrHook surfaces the stuck stack. I.e. it would catch the exact class the capture-hang belongs to (even if that specific sub-2.5% instance still evades a bounded run).

## Consequences

- **Positive.** Automates intermittent-bug hunting; **freeze-on-catch** hands the LLM the live failure state — stronger than a test runner's report or a manual break-on-failure. On-identity for DrHook (runtime observation). Composes with the anomaly substrate and the hypothesis braid. A natural **James** primitive: *"keep observing until a condition fires"* is precisely the cognitive-loop-as-substrate-behavior James is meant to drive (audits fired as behavior, not reactive polling).
- **Honest limits.** It does **not** make an unreproducible bug reproducible — a sub-2.5% event may never fire inside the cap; the motivating capture-hang might still evade it. The MVP (exception/anomaly) does **not** catch silent hangs — that needs the phase-2 liveness predicate. The compound-driver is deferred, so the first cut only handles "let the target run until it fires," not "repeat a compound MCP operation."
- **Sibling, not overlap, with [ADR-007](ADR-007-teardown-concurrency-test-debug.md).** ADR-007 is about *debugging test runs*; this is about *repeating to expose intermittency*. They share the "hunt rare/flaky failures" goal; they stay distinct primitives.
- **Anti-pattern explicitly excluded:** retry-to-pass (masking flakiness). DrHook exposes, never hides.

## References

- Motivating investigation: Mercury `sessions/2026-06-29-drhook-visual-capture-live` — `obs-capture-hang-is-rare-race`, `obs-capture-hang-catch-loop-negative`, `obs-capture-hang-not-per-step`.
- [GoogleTest — Advanced topics](https://google.github.io/googletest/advanced.html) (`--gtest_repeat`, `--gtest_break_on_failure`, `--gtest_shuffle`); [issue #2645 — stop repetitions at first failure](https://github.com/google/googletest/issues/2645).
- [pytest-repeat](https://pypi.org/project/pytest-repeat/); [pytest — Flaky tests](https://docs.pytest.org/en/stable/explanation/flaky.html); [pytest-rerunfailures](https://github.com/pytest-dev/pytest-rerunfailures) (the retry-to-pass anti-pattern).
- Substrate: `EngineAnomaly` / `AnomalyKind` / `IDebugEventSink.OnAnomaly`; `drhook_break_exception`; the hypothesis braid ([ADR-012](ADR-012-debug-state-surfaces.md) Phase 3); James as the cognitive loop firing audits as substrate behavior.
