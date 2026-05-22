# Finding 29: Probe 21 Outcome — PASSED: instance-method func-eval (s.Length) — the realistic conditional case

**Status:**   **PASSED, 2/2.** The conditional-breakpoint workhorse works: call an instance
method/property on a value read from a local. At a breakpoint inside `Worker.Inspect` (where
`string s = "hello"` is in scope), func-eval `s.Length` — `String.get_Length` with `this = s` —
returned `5`. This exercised passing a read debuggee value as `this` AND resolving the method on a
*different* module (`System.Private.CoreLib`). Func-eval breadth for conditional breakpoints is now
essentially complete.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/21-instance-eval-smoke.cs` + `21-instance-target.cs`

## How it composes

`DebugSession.TryEvalInstanceCall("s", "System.Private.CoreLib", "System.String", "get_Length", …)`:
1. **`this`** — top frame's PDB (`SymbolReader.GetLocalNames`) gives the slot for `s`;
   `Variables.GetActiveFrameLocalValue(slot)` reads its `ICorDebugValue` (kept, not released).
2. **the method** — `FindModule("System.Private.CoreLib")` → `ResolveMethodToken("System.String",
   "get_Length")` → `Eval.GetFunction` → `ICorDebugFunction`. The metadata chain works on CoreLib
   exactly as on the target's own module.
3. **the call** — `CreateEval` on the stop thread → `CallWithOneArg(func, this)` (an instance call is
   just `CallFunction` with `args[0] = this`) → `Resume` → `WaitForStop` → `GetResult` → `I4 = 5`.

```
stopped    : in Worker.Inspect — func-evaluating s.Length (String.get_Length on `s`) …
eval status: Completed
eval result: elementType=0x08  value=5
PROBE 21 PASSED — "hello".Length = 5 via String.get_Length(this=s).
```

## What this proves

1. **A read debuggee value passes as `this`** — the existing object reference (the string) is a valid
   func-eval argument; no marshaling/reconstruction needed.
2. **Cross-module method resolution works** — resolving `String.get_Length` in CoreLib uses the same
   nav+metadata as the target's own methods.
3. **The realistic conditional-breakpoint case is covered** — `s.Length`, and by the same shape
   `list.Count` (a property), `dict.ContainsKey(k)` (a method with args), etc. Static calls (probe 19),
   args (probe 20), and now instance calls + cross-module are all validated.

## Remaining (smaller now)

- **Reference-typed *results*** — `get_Length` returns a primitive; a method returning a string/object
  needs the result dereferenced to render its value (the deferred reference-read refinement). The
  *call* works; rendering the returned reference is the gap.
- **Abort under a real hang** — `Abort` is wired into the timeout path; validate it recovers a session
  from a method that genuinely blocks on a target-side lock.
- **The Roslyn front end** — parse a C# expression → drive these eval primitives (read locals, resolve
  methods, func-eval calls, combine with operators) → boolean. The interop substrate beneath it is now
  proven across the cases that matter.

## References

- Probe: `poc/drhook-engine/21-instance-eval-smoke.cs`, `21-instance-target.cs`
- Fixture: `fixtures/21-instance-eval-osx-arm64-20260522T142229Z.txt`
- Engine: `Variables.GetActiveFrameLocalValue`, `Eval.CallWithOneArg`, `DebugSession.TryEvalInstanceCall`
- Findings 27 (func-eval works), 28 (args), 26 (reading locals — the `this` source), 18 (metadata resolution)
- Mercury session 2026-05-21 observation `probe-21-instance-eval`
