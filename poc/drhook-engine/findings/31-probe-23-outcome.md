# Finding 31: Probe 23 Outcome — PASSED: func-eval works INSIDE a conditional predicate (re-entrancy was a false alarm)

**Status:**   **PASSED, 2/2.** A member-access condition (`s.Length > 3`) that requires func-eval to
evaluate works inside `WaitForConditionalStop`: at each breakpoint hit the predicate func-evals
`s.Length` and the breakpoint surfaces only at the first iteration where it exceeds 3 (length 4). The
feared re-entrancy between the conditional-stop loop's `Resume`/`WaitForStop` and the func-eval's was
**not** a problem — func-eval composes cleanly inside the predicate.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/23-condeval-smoke.cs` + `23-condeval-target.cs`

## The misdiagnosis, and the discipline lesson

The first run **hung** (looped forever, no verdict). I concluded "re-entrancy deadlock" — and was
wrong. Instrumenting the predicate to log each eval's status showed the truth in one run:

```
[diag] eval #1..8: status=SetupFailed raw=null
```

**`SetupFailed`, not a deadlock or timeout.** The "hang" was an infinite *fast* loop:
setup-fail → predicate false → resume → next hit → setup-fail → … The root cause was trivial: the
target declared `Step(string s)`, so `s` is a method **parameter**, but `TryEvalInstanceCall` resolves
the `this` value via `GetLocalNames` (PDB **locals** only). It never found `s`, so every eval setup
failed. Making `s` a real local (`string s = word;`) fixed it immediately.

Lesson (the EEE one, again): a symptom ("it hangs") is not a diagnosis. One instrumented observation
beat a confident wrong inference — exactly the "observe, don't assume" discipline. I should have
diagnosed before concluding re-entrancy.

## What this proves

1. **Func-eval inside a conditional predicate works.** `WaitForConditionalStop` calls the predicate at
   each hit; the predicate's `TryEvalInstanceCall` (`Resume` + `WaitForStop(EvalComplete)`) nests inside
   the loop's own `WaitForStop`/`Resume` without corruption — because func-eval returns the thread to
   the same stop, the loop's accounting stays consistent.
2. **Member-access conditions are viable.** `s.Length > 3` evaluated correctly and stopped at exactly
   the right iteration. The substrate for full standard-C# conditions (including method/property calls)
   is proven.

## Surfaced limitation (small)

`TryEvalInstanceCall` resolves the `this`-holder from **locals** only. Arguments (very common as the
object a condition references) are not yet resolvable as `this`. A `GetActiveFrameArgumentValue`
companion (using `GetArgument`@16) closes that — a minor enhancement, noted for the general path.

## Next: general member resolution

The probe **hardcoded** `String.get_Length` (declaring module + type + method). The Roslyn walker must
instead resolve `.Length` on `s`'s **runtime type** generically: `ICorDebugValue` → dereference →
`ICorDebugObjectValue.GetClass` → `ICorDebugClass` → module + type token → metadata member lookup →
`ICorDebugFunction`. That general member resolution is the next increment; with it, the walker handles
`s.Length`, `list.Count`, `obj.Field` from a parsed expression without per-call hardcoding.

## References

- Probe: `poc/drhook-engine/23-condeval-smoke.cs`, `23-condeval-target.cs`
- Fixture: `fixtures/23-condeval-osx-arm64-20260522T153008Z.txt`
- Engine (unchanged — pure probe over existing primitives): `DebugSession.WaitForConditionalStop` + `TryEvalInstanceCall`
- Findings 30 (primitive-local conditions), 29 (instance func-eval), 27 (func-eval works)
- Mercury session 2026-05-21 observation `probe-23-condeval`
