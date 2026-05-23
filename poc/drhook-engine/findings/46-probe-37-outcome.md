# Finding 46: Probe 37 Outcome — PASSED: cross-module member resolution via `ICorDebugType.GetBase`

**Status:**   **PASSED, 2/2** (clean exit 0; 47 unit tests still pass; probes 24 + 35 + 30 re-run
for regression; probe 36 updated to also assert cross-module). Object-inspection slice 3:
`MemberResolver.ResolveGetter` now walks inheritance ACROSS modules via
`ICorDebugValue2.GetExactType@3` + `ICorDebugType.GetBase@7` — the runtime handles
`mdTypeRef`/`mdAssemblyRef` resolution for us. `ex.Message`-style debugging works.
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/37-crossmod-smoke.cs` + `37-crossmod-target.cs`
              (+ updated `poc/drhook-engine/36-subclass-smoke.cs` phase B)

## The cleaner refactor — let the runtime walk inheritance

Finding 45 added a metadata-level within-module walk in `MetadataResolver.FindMethodInType`
(follow `mdTypeDef` extends). The cross-module case (`mdTypeRef`) was deferred because it
required typeref resolution, assembly-ref → loaded-module matching, etc. — a substantial dance.

`ICorDebugType.GetBase` short-circuits all of that: the runtime knows the inheritance chain and
hands us the base type as an `ICorDebugType` regardless of where it lives. `GetClass` on that
type yields the `ICorDebugClass` in the parent's actual module. No manual typeref resolution.

New `ResolveGetter` shape:

```csharp
public static nint ResolveGetter(nint pThisValue, string memberName)
{
    nint value2 = QI(pThisValue, IID_ICorDebugValue2);     // 5E0B54E7-D88A-4626-9420-A691E0A78B49
    nint type   = Out(value2, Value2GetExactType);          // slot 3
    while (type != 0)
    {
        nint function = TryFindOnTypeLevel(type, getterName); // GetClass→Module/Token→FindMethodInType
        if (function != 0) return function;

        nint baseType = Out(type, TypeGetBase);               // slot 7 — crosses module boundaries
        RuntimeNavigation.Release(type);
        type = baseType;
    }
    return 0;
}
```

At each level, `MetadataResolver.FindMethodInType` (the within-module walker from finding 45)
handles same-module ancestors; `GetBase` crosses to the parent's module. The two compose
naturally — within-module walk is fast (one EnumMethodsWithName per level), GetBase is the slower
cross-module step.

## End-to-end

Target: `ProbeException : System.Exception` directly — no same-module intermediate. `ex.Message`
can ONLY resolve by crossing into CoreLib.

```
plan       : at a ProbeException stop, func-eval ex.Message via cross-module ICorDebugType.GetBase
             walk; expect StringValue == "hello message".
stopped    : Exception ProbeException @ FirstChance
eval status: Completed  StringValue="hello message"
PROBE 37 PASSED
```

## Probe 36 update — the boundary moved

Finding 45's probe 36 had a **phase B boundary lock**: `ex.Message` MUST return `SetupFailed`
(within-module-only walk shouldn't reach CoreLib). With the GetBase refactor that boundary moved
— cross-module now resolves. Probe 36's phase B was updated in this commit to assert the
**positive** outcome (Completed with the expected string). The probe now validates both:

- Phase A: within-module subclass-walk (`ex.Title` through `MetadataResolver.FindMethodInType`'s
  same-module extends chain — TitledException found inside the target's module).
- Phase B: cross-module walk (`ex.Message` through `ICorDebugType.GetBase` reaching CoreLib).

Same target, two layers of inheritance walking validated.

## What the rewrite cleaned up

The old `ResolveGetter` chain was `QI Reference → Dereference → QI ObjectValue → GetClass → …`
— which **couldn't reach strings or arrays** (not `ICorDebugObjectValue`) and couldn't cross
modules. The new chain (`GetExactType → GetBase` loop) handles strings/arrays/generics too — the
exact-type machinery is the same one the future arrays/generics slice will use. So this slice
also lays the foundation for the next object-inspection slices.

Probes that regression-pass through the new path: 24 (Box.Size), 25 (member-access walker),
27 (TryEvalCurrentExceptionMember), 30 (BreakpointPolicy at exception location), 35 (string
rendering), 36 (now both phases). No drift in any.

## Same primitive serves the exception filter follow-on

Finding 43's noted "subclass-aware exception type matching" follow-on uses the same walk: at
filter-match time, walk the live exception's `ICorDebugType` via `GetBase`, comparing each
level's class-token name against the filter's `TypeName`. **No new metadata work** — same chain
as this probe. One increment hits two consumers.

## Scope / next

ADR-006 Phase 3 substrate gaps:

- [x] AsyncBreak (40), breakpoint registry (41), Launch (42), persistent exception filter (43)
- [~] Object inspection — **strings** (44), **within-module subclass-walk** (45),
  **cross-module subclass-walk** (this finding) DONE. Remaining:
  - `ICorDebugType` + arrays/generics (the type machinery is already in use here; arrays need
    `ICorDebugArrayValue` + `GetRank`/indexed reads).
  - `ICorDebugObjectValue::GetFieldValue@8` for field walking + depth ≥ 2 inspection.
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement
- Polish: subclass-aware exception filter (free with the GetBase chain).

## References

- Probe: `poc/drhook-engine/37-crossmod-smoke.cs`, `37-crossmod-target.cs`
- Fixture: `fixtures/37-crossmod-osx-arm64-…`
- Updated: `poc/drhook-engine/36-subclass-smoke.cs` — phase B now asserts cross-module Completed
- Engine: `Interop/MemberResolver.cs` (rewritten — `GetExactType` + `GetBase` loop), reuses
  `MetadataResolver.FindMethodInType` for the same-module step
- Findings 45 (within-module walk — composed at each Type level), 35 (string rendering —
  composed for the result), 43 (subclass-aware exception filter — same primitive applies)
- Mercury session 2026-05-22 observation `probe-37-cross-module-walk`
