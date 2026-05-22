# Finding 37: Probe 27 Outcome — PASSED: func-eval works at an Exception stop

**Status:**   **PASSED, 2/2** (clean exit 0 both runs). The second probe-gated unknown from finding 35 is
resolved: func-eval composes at an **Exception** stop, not just a Breakpoint stop. At a first-chance
`ProbeException`, `get_Code` was func-eval'd on the **in-flight exception object** (its value from
`GetCurrentException`, not a local) and returned **42**. With finding 36, **conditional exception
breakpoints** (`ex.Code == 42`, `ex.Message …`) are mechanically viable end-to-end.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/27-eval-exception-smoke.cs` + `27-eval-exception-target.cs`

## The unknown it settles

Probe 23 proved func-eval composes inside a **Breakpoint** stop. An exception stop is a **different
ICorDebug controller state**, and cordebug.idl warns: *"FuncEval will clear out the exception object on setup
and restore it on completion."* Open question: does func-eval work there, and does the exception object
survive the eval's internal resume? Answer: **yes, both.**

```
plan       : at a FirstChance ProbeException, func-eval ProbeException.Code (expect 42)
stopped    : FirstChance ProbeException — func-eval ProbeException.Code on the in-flight exception …
eval status: Completed
eval result: elementType=0x08  value=42
PROBE 27 PASSED — func-eval works at an exception stop: ProbeException.Code = 42
                  (getter resolved on the runtime type, eval'd on the in-flight exception).
```

## What it composes (no new interop)

This is **pure composition** of validated parts — the only engine addition is one method:

- **Exception stop** (probe 26, finding 36): `StopReason.Exception` + `ExceptionInspector`.
- **Exception object as the eval `this`** — `ExceptionInspector.CurrentExceptionValue(thread)` returns the
  raw `ICorDebugValue` from `GetCurrentException`@10 (owned). Unlike `TryEvalMemberCall`, the `this` does
  **not** come from a named local — it comes from the in-flight exception.
- **General member resolution** (probe 24): `MemberResolver.ResolveGetter(exValue, "Code")` derives the
  getter on the exception's runtime type — no hardcoded type/module.
- **Func-eval re-entrancy** (probe 23): `CreateEval` → `CallWithOneArg(func, exValue)` → `Resume` →
  `WaitForStop(EvalComplete)`, parked at the exception stop. The eval's internal resume nests inside the
  exception stop exactly as it nested inside a breakpoint stop.

`DebugSession.TryEvalCurrentExceptionMember(memberName, timeout, out result)` is `TryEvalMemberCall` with the
`this` sourced from `CurrentExceptionValue` instead of a local slot — same eval/resolve/release skeleton.

The cordebug.idl clear-and-restore behavior is exactly why it works: the runtime saves the in-flight exception
so the thread can call a function (a thread can't func-eval while an exception is propagating), then restores
it — so the exception object value we captured stays valid, and after `EvalComplete` the subsequent real
`Resume` lets the exception continue to its catch.

## Why this matters — conditional exception breakpoints

Findings 36 + 37 together make the full exception-breakpoint model (finding 33/35) mechanically complete:

> **type filter** × **first-chance/unhandled** (finding 36) × **condition on the exception object** (this
> finding) × **action** (break | log).

`ex.Code == 42` is now achievable: stop on the exception (36) → func-eval `get_Code` (37) → compare (the same
Roslyn walker as probe 25, with the operand sourced from the exception rather than a local). The policy layer
gates *which* exceptions surface; the eval substrate evaluates the condition.

## Scope / next

- **Primitive-returning members work fully.** `ex.Message` returns a **string** (reference) — func-eval'ing
  `get_Message` works, but rendering the returned ref needs the string/`ICorDebugType` path (ADR-006 Phase 4
  "reference-typed results" gap; finding 32 scope note). So `ex.Code == 42` works today; `ex.Message.Contains`
  awaits string rendering.
- **`this`-from-exception generalizes the walker's operand** — same gap as `this`-from-arguments (finding 34):
  the Roslyn walker currently sources member-access operands from locals; an `ex.`-rooted condition needs the
  walker to know the operand is the exception. A small front-end addition once exception breakpoints get a
  policy surface.
- **Filtering / subclass matching / the `BreakpointPolicy`** — the policy-layer work (finding 33/35), now
  fully unblocked on the mechanism side.

## References

- Probe: `poc/drhook-engine/27-eval-exception-smoke.cs`, `27-eval-exception-target.cs`
- Fixture: `fixtures/27-eval-exception-osx-arm64-20260522T225410Z.txt` (+ a second clean run, exit 0)
- Engine: `DebugSession.TryEvalCurrentExceptionMember` (new), `Interop/ExceptionInspector.CurrentExceptionValue`
  (new); reuses `MemberResolver.ResolveGetter`, `Eval.*`, the probe-26 exception stop
- Findings 36 (exception callback fires), 24 (general member resolution), 23 (func-eval re-entrancy),
  35 (the two probe-gated unknowns — both now resolved), 33 (exception = a location axis)
- Mercury session 2026-05-22 observation `probe-27-eval-at-exception-stop`
