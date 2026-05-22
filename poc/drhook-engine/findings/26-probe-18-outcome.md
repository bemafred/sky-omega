# Finding 26: Probe 18 Outcome — PASSED: locals by name (symbols 6d) — symbols arc complete

**Status:**   **PASSED, 2/2.** Named local variables are now readable at a stop, completing "stopped
here (file:line), with THESE named values." At a line breakpoint inside `Worker.Step(5)`, `GetLocals`
paired PDB local names with frame values: `a` (I4) = 6, `b` (I8) = 60. This is the last symbols
sub-increment — **6a–6d are complete**.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/18-locals-smoke.cs` + `18-locals-target.cs`

## How it composes

`DebugSession.GetLocals()` joins the two halves built earlier:
- **Names** — from the top frame's method token + module path (the 6b `FrameInfo`), the cached
  `SymbolReader.GetLocalNames(token)` returns `(slot → name)` from the PDB local scopes.
- **Values** — `Variables.ReadActiveFrameLocals` reads each named slot via
  `ICorDebugILFrame.GetLocalVariable`@14 and decodes it with the same `ReadValue` used for arguments
  (5b): `ICorDebugValue.GetType`@3 + `ICorDebugGenericValue.GetValue`@7.

A local not yet in scope/assigned at the current IL offset surfaces with a null raw value, so the
name set is stable regardless of where execution stopped.

## Run result (2/2)

```
stopped at 18-locals-target.cs:32 — named locals:
  a : elementType=0x08  value=6      (I4)
  b : elementType=0x0A  value=60     (I8)
PROBE 18 PASSED — read named locals a=6 (I4), b=60 (I8) at the breakpoint.
```

## Symbols arc complete (6a–6d)

| Sub | Capability | Probe/test |
|---|---|---|
| 6a | Portable PDB reader (IL↔line, local names), BCL-only | unit tests (finding 23) |
| 6b | source lines in stack frames | probe 16 (finding 24) |
| 6c | source-line breakpoints (mid-method) | probe 17 (finding 25) |
| 6d | locals by name | probe 18 (this) |

DrHook.Engine now answers, at any stop: **where** (`Type.Method @ file:line` for every frame),
**with what** (named/typed arguments and locals), and can **stop where you point** (`file:line`
breakpoints) — all BCL + raw interop + a BCL Portable-PDB reader, no netcoredbg.

## What this unblocks: conditional breakpoints / client-side eval

Everything a conditional breakpoint needs is now in place without func-eval:
1. set the breakpoint at a `file:line` (6c),
2. at the hit, read the named locals/args (6d / 5b),
3. evaluate a predicate **client-side** (compare the read values in-process),
4. suppress or surface the stop accordingly (the stopping model, finding 16).

That is the ADR-006 Open Question 2 "option C" — and it sidesteps the func-eval deadlock that drove
the netcoredbg replacement. The decision is now empirically reachable.

## Refinements still deferred (not blockers)

- Local *values* for reference types (dereference `ICorDebugReferenceValue`/`ObjectValue`, field reads).
- Typed rendering (R4/R8 as floats, Boolean/Char/string) — raw bits captured today.
- A structured `StackFrame`/value API vs the current formatted strings + flat records.

## References

- Probe: `poc/drhook-engine/18-locals-smoke.cs`, `18-locals-target.cs`
- Fixture: `fixtures/18-locals-osx-arm64-20260522T102000Z.txt`
- Engine: `Variables.ReadActiveFrameLocals`, `DebugSession.GetLocals`, `SymbolReader.GetLocalNames`, `LocalValue`
- Findings 25 (6c line breakpoints), 24 (6b frame lines), 23 (SymbolReader), 22 (arg value reader), 16 (stopping model)
- Mercury session 2026-05-21 observation `probe-18-passed`
