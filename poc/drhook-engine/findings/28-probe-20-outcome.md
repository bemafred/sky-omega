# Finding 28: Probe 20 Outcome — PASSED: func-eval with arguments (breadth)

**Status:**   **PASSED, 2/2.** Func-eval passes arguments. At a stop, `Probe.Doubled(21)` was
func-eval'd — the argument `21` built as an eval value via `CreateValue`/`SetValue` — and returned
`42`. This retires the "func-eval with arguments" unknown; instance methods are now a composition
(arg 0 = `this`, a read value), and the deadlock-safety net (`Abort`) is wired.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/20-eval-args-smoke.cs` + `20-eval-args-target.cs`

## What was added

`Eval.cs`:
- `CreateInt32(eval, value)` — `ICorDebugEval.CreateValue`@12 (ELEMENT_TYPE_I4) → QI `ICorDebugGenericValue` → `SetValue`@8. Returns an owned arg value.
- `CallStaticOneArg(eval, function, arg)` — `ICorDebugEval.CallFunction`@3 with `nArgs=1` and a one-element `ppArgs`.
- `Abort(eval)` — `ICorDebugEval.Abort`@9, the safety net.

`DebugSession.TryEvalStaticCallInt(...)` resolves the method, creates the eval, builds the arg, calls,
`Resume`s, and `WaitForStop(timeout)`. **On a timeout it `Abort`s the eval** before returning `TimedOut`
— so a hung eval is torn down rather than leaked. (`Doubled` never hangs, so the abort path isn't
exercised here; that's the dedicated Abort-safety validation.)

```
stopped    : Break — func-evaluating Probe.Doubled(21) …
eval status: Completed
eval result: elementType=0x08  value=42
PROBE 20 PASSED — func-eval with arguments works: Probe.Doubled(21) = 42.
```

## What this proves

1. **`CallFunction` with arguments works** — the args array marshals correctly and the call returns
   the right value.
2. **Eval values are constructible** — `CreateValue` + `SetValue` build a primitive arg in the
   debuggee that the call consumes.
3. **The breadth gap narrows to composition** — an instance call is `CallFunction` with `args[0] = this`
   (a value we already read from a local, finding 26); a multi-arg call is a longer `ppArgs`. Neither
   is a new platform unknown now.

## Next (remaining breadth + safety)

- **Instance methods / properties** — pass a read local/object value as `args[0]` (`this`), e.g.
  `s.Length` on a string local; resolve the method on its declaring module (CoreLib for `String`).
- **Reference-typed results** — dereference the returned value (strings, objects).
- **Abort safety under a real hang** — a target method that blocks on a lock; confirm `Abort` + the
  timeout recover the session cleanly (the netcoredbg failure mode, handled rather than fatal).
- Then the **Roslyn front end**: C# expression → these eval primitives → boolean result.

## References

- Probe: `poc/drhook-engine/20-eval-args-smoke.cs`, `20-eval-args-target.cs`
- Fixture: `fixtures/20-eval-args-osx-arm64-20260522T134229Z.txt`
- Engine: `Interop/Eval.cs` (`CreateInt32`, `CallStaticOneArg`, `Abort`), `DebugSession.TryEvalStaticCallInt`
- Finding 27 (func-eval works — the foundation), 26 (reading values — the source for `this`/operands)
- Mercury session 2026-05-21 observation `probe-20-eval-args`
