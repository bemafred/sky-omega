# Session Summary: DrHook Inspection Surface Redesign

**Date:** 2026-04-06
**Participants:** Martin Fredriksson, Sky (Claude, claude.ai app)
**Output:** `docs/adrs/drhook/ADR-005-inspection-surface.md`

## Starting Point

On April 5th, during a production debugging session on the 903 GB Wikidata Turtle file, the Turtle parser failed after processing millions of triples. Claude Code attempted to diagnose the failure using DrHook. Stepping and variable inspection (`drhook_step_vars`) worked, but every call to `drhook_step_eval` returned a DAP error. Claude Code fell back to `step_vars` but the session was abandoned without finding the parser bug root cause.

Martin brought the problem to this session to ensure DrHook is reliable before resuming parser debugging.

## Key Discovery: The Eval Context Bug

Inspection of the codebase revealed the root cause: `SteppingSessionManager.EvaluateExpressionAsync` passes `context: "watch"` to DAP's evaluate request (as specified in the original ADR-002). This is wrong.

DAP defines two evaluate contexts with different behavior in netcoredbg:
- **`"watch"`** — restrictive, designed for IDE watch panels that silently re-evaluate after every stop
- **`"repl"`** — permissive, designed for interactive debug console use

DrHook's `step_eval` is functionally a debug console (one-shot, on-demand, interactive) but was using watch panel semantics. The blanket DAP error on every call — even for a simple variable name like `_buffer` — is consistent with a context-level rejection. Martin confirmed that Claude Code passed a single variable name, not a complex expression.

## Test Coverage Gap

All 14 existing tests in `SteppingSessionManagerTests` verify only the "no active session" guard — that methods return error JSON when called without a DAP connection. Zero tests launch netcoredbg, connect to a target, or exercise any tool against a live session. The existing `drhook-verify.cs` is a suitable non-interactive target but had never been used for automated testing.

## Design Evolution During Session

The session progressed through three rounds of design expansion, each triggered by Martin's questions:

### Round 1: Eval fix + integration tests

The initial ADR scope: change `"watch"` to `"repl"`, write integration tests against `drhook-verify.cs` covering all tool paths. Red-green methodology: write the failing eval test first with `"watch"`, confirm it fails, switch to `"repl"`, confirm it passes.

### Round 2: Watch mode (Martin asked about `"watch"` semantics)

Martin asked whether `"watch"` mode should be a separate tool — runtime inspection via persistent expressions. This surfaced a missing concept: DAP's three inspection patterns map to three DrHook tools, but only two existed:

| IDE Concept | DAP Context | DrHook Tool | Status |
|---|---|---|---|
| Variables panel | scope walk | `step_vars` | Existed, worked |
| Debug console | `"repl"` | `step_eval` | Existed, broken (wrong context) |
| Watch panel | `"watch"` | — | **Missing** |

Three new MCP tools were designed: `drhook_step_watch_add`, `drhook_step_watch_remove`, `drhook_step_watch_list`. Watched expressions are automatically evaluated and included in every step/continue/pause response — reducing tool call volume from N+1 to 1 per step when tracking N expressions.

The key insight: `"watch"` context was never wrong per se — it was assigned to the wrong tool. Now each DAP context has its correct DrHook tool, and the semantic confusion that caused the original bug is structurally impossible.

### Round 3: Process metrics dashboard (Martin asked about runtime health visibility)

Martin asked whether DrHook supports runtime inspection of system metrics — memory, GC, stack size — as a dashboard in every tool response. This surfaced a gap between DrHook's two observation layers:

- **EventPipe layer** (`drhook_snapshot`): captures GC pressure, exceptions, contention — but requires the process to be *running*. Cannot operate during stepping.
- **Stepping layer** (`drhook_step_*`): controls paused execution — but has no visibility into process health.

The solution: capture OS-level process metrics (`Process.GetProcessById` — WorkingSet64, PrivateMemorySize64, ThreadCount) and managed-layer metrics (via eval: `GC.GetTotalMemory(false)`, `GC.CollectionCount(0/1/2)`) at every stop point. Include in every step response with deltas from previous capture.

OS metrics are always available (one syscall, works on paused processes). Managed metrics depend on eval working and degrade gracefully to absent when it doesn't.

The killer combination for the parser session: **conditional breakpoint** (`"tripleCount % 100000 == 0"`) + **watches** (`"tripleCount"`, `"_position"`) + **metrics dashboard**. Every 100K triples, the process stops, and the agent sees memory trajectory, GC generation counts, and parser state — without any manual polling. The moment memory jumps non-linearly, the problematic interval is located.

Martin's key observation: the failure might not be in the parser at all — it could be the .NET runtime buckling under extreme pressure. The metrics dashboard distinguishes between these hypotheses: rising WorkingSet with flat managed heap = native memory; Gen2 GC storm = allocation pressure; both flat but parser crashes = logic bug. Different diagnoses, different fixes.

## Final ADR Structure

**ADR-005 — Inspection Surface: Eval Fix, Watch Mode, Process Metrics, and Integration Testing**

Six decisions:
1. Change eval context from `"watch"` to `"repl"`
2. Add persistent watch mode (3 new MCP tools + auto-evaluation in step results)
3. Add process metrics dashboard (OS + managed metrics with deltas in every step response)
4. Add integration tests (7 groups, 30+ tests against live `drhook-verify.cs`)
5. Extend `drhook-verify.cs` with exception and object scenarios
6. Amend ADR-002

Implementation order starts with the falsification: write the eval test, confirm it fails with `"watch"`, confirm it passes with `"repl"`. Then build outward: watches, metrics, full test suite.

## Implementation Notes for Claude Code

- The one-line fix is in `SteppingSessionManager.cs` line 793: `"watch"` → `"repl"`
- Watches use a `Dictionary<string, string>` (label → expression) on `SteppingSessionManager`, cleared in `CleanupAsync`
- Watches ARE evaluated with `"watch"` context — that's the correct DAP usage for persistent re-evaluation
- Metrics need `_targetPid` stored during session launch and `_previousMetrics` for delta computation
- The step methods (`StepNextAsync`, `StepIntoAsync`, `StepOutAsync`, `ContinueAsync`, `PauseAsync`) all need the same enrichment: add `watches` and `metrics` to the result JSON before returning
- Both watches and metrics need the top frame ID; the step methods already fetch the stack trace via `GetCurrentStateAsync` — refactor to also return the frame ID to avoid redundant DAP calls
- All integration tests should use `[Trait("Category", "Integration")]` for selective CI runs
- Conditional breakpoints are already fully implemented end-to-end — no work needed there
- The `drhook-verify.cs` target should be extended with a `ThrowAndCatch()` method and a `Person` record for object inspection tests

## Falsifiability

The hypothesis: after ADR-005 implementation, DrHook can diagnose the parser failure that defeated it on April 5th. The 903 GB Turtle file is the test. If the metrics dashboard, watches, and working eval don't surface the root cause, the hypothesis fails and we learn what's still missing.
