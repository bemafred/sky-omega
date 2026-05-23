# Finding 45: Probe 36 Outcome — PASSED: subclass-walk in member resolution (within-module)

**Status:**   **PASSED, 2/2** (clean exit 0; 47 unit tests still pass; probes 24 + 35 re-run for
regression). Object-inspection slice 2: `MetadataResolver.FindMethodInType` now walks the
`extends` chain so inherited members are findable on the runtime class. Within-module only this
slice — cross-module (`mdTypeRef` → CoreLib's `System.Exception`) is the next slice. Validates
**both** the new walking AND the scope boundary.
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/36-subclass-smoke.cs` + `36-subclass-target.cs`

## Implementation — three small pieces

```csharp
public static uint FindMethodInType(nint pModule, uint typeToken, string methodName)
{
    nint pImport = GetMetaDataImport(pModule);
    if (pImport == 0) return 0;
    try
    {
        uint currentType = typeToken;
        while (currentType != 0)
        {
            uint methodToken = FindMethodInTypeDirectly(pImport, currentType, methodName);
            if (methodToken != 0) return methodToken;
            currentType = GetBaseTypeDef(pImport, currentType);
        }
        return 0;
    }
    finally { Release(pImport); }
}

private static uint GetBaseTypeDef(nint pImport, uint typeToken)
{
    var getTypeDefProps = (delegate* unmanaged[Cdecl]<nint, uint, char*, uint, uint*, uint*, uint*, int>)Slot(pImport, GetTypeDefProps);
    uint chName = 0, flags = 0, extends = 0;
    if (getTypeDefProps(pImport, typeToken, null, 0, &chName, &flags, &extends) < 0) return 0;
    return (extends >> 24) == 0x02 ? extends : 0;   // mdTypeDef only — same module
}
```

`GetTypeDefProps` with `szTypeDef = null + cchTypeDef = 0` returns just the `ptkExtends` we need
(we don't care about the name here). The high-byte check `0x02` is the CorTokenType for
`mdtTypeDef` — same module. `mdTypeRef` (`0x01`) and `mdTypeSpec` (`0x1B`, generic instantiation)
return 0, stopping the walk; that's deliberate for this slice.

`FindMethodInTypeDirectly` is the original single-type search (the existing `EnumMethodsWithName`
logic), now extracted as a helper so the walk can reuse it per level.

## End-to-end

Target: `ProbeException : TitledException : Exception`, with `Title` declared on `TitledException`
(both classes in the target's own module).

```
plan       : at a ProbeException stop, eval ex.Title (within-module subclass-walk, expect "hello title")
             and ex.Message (cross-module, expect SetupFailed).
stopped    : Exception ProbeException @ FirstChance
phase A    : eval ex.Title -> Completed  StringValue="hello title"
phase B    : eval ex.Message -> SetupFailed (expect SetupFailed; cross-module is next slice)
PROBE 36 PASSED
```

**Phase A** proves the within-module walk: `Title` isn't on `ProbeException` directly — the
resolver had to climb to `TitledException` to find `get_Title`. The eval Completed and the
returned string content came back via the probe-35 reference-string path.

**Phase B** confirms the scope boundary: `Message` is declared on `System.Exception` in CoreLib —
cross-module. The walk correctly stops at the boundary (`mdTypeRef`), so phase B yields
`SetupFailed` — the right negative result. Without this assertion, a future change that
accidentally followed `mdTypeRef`s would silently start resolving CoreLib members and the test
would still pass; the assertion locks the boundary.

## Why this composes

- **`MemberResolver.ResolveGetter`** (probe 24) calls `FindMethodInType` — automatically benefits.
- **`TryEvalMemberCall`** (probe 25) calls `ResolveGetter` — automatically benefits.
- **`TryEvalCurrentExceptionMember`** (probe 27) calls `ResolveGetter` — automatically benefits.
- **The Roslyn walker** (probe 25, probe 29's interpolation, probe 30's `ex.`) all funnel through
  `TryEvalMemberCall` / `TryEvalCurrentExceptionMember`. Inherited members now Just Work for any
  parent in the same module.

Probes 24 + 35 re-run for regression — direct-member and reference-string paths are unchanged.

## Cross-module — the next slice (sketch, not built)

For `ex.Message` (declared on `System.Exception` in `System.Private.CoreLib`) to resolve, the
walk needs to follow `mdTypeRef`:

1. `GetTypeRefProps(refToken, out resolutionScope, name, ...)` → "System.Exception" + an
   `mdAssemblyRef`.
2. Resolve the assembly ref to a loaded module: enumerate `ICorDebugProcess` modules, match name.
3. `FindTypeDefByName(targetModule, "System.Exception")` → typedef in CoreLib (this is what
   `MetadataResolver.ResolveMethodToken` already does for `ResolveMethodToken`).
4. Continue the walk in the new module's import.

This is essentially **a second slice** of similar size; the primitives (`GetTypeRefProps`,
`FindTypeDefByName`, module enumeration) all already exist. Deferred to keep this slice tight.

## Other consumers — what subclass-walking also enables

- **Exception filter subclass matching** (finding 43's noted follow-on). The same `extends`-chain
  walk applied to the LIVE exception's runtime class, comparing type names against the filter's
  `TypeName` at each level. Same primitive (`GetBaseTypeDef` + name from `GetTypeDefProps`),
  different consumer. Within-module first, then cross-module — same two-step story.

## Scope / next

ADR-006 Phase 3 substrate gaps:

- [x] AsyncBreak (40), breakpoint registry (41), Launch (42), persistent exception filter (43)
- [~] Object inspection — **strings DONE** (44), **within-module subclass-walk DONE** (this
  finding); remaining: **cross-module walk** (unlocks `ex.Message`, generalizes filter matching),
  `ICorDebugValue2::GetExactType` for arrays + generics, `ObjectValue::GetFieldValue` for field
  walking + depth ≥ 2.
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement

## References

- Probe: `poc/drhook-engine/36-subclass-smoke.cs`, `36-subclass-target.cs`
- Fixture: `fixtures/36-subclass-osx-arm64-…`
- Engine: `Interop/MetadataResolver.FindMethodInType` (walking), `FindMethodInTypeDirectly` (new
  private helper extracted from the original body), `GetBaseTypeDef` (new private helper —
  `GetTypeDefProps`'s `ptkExtends` filtered to `mdTypeDef`)
- Findings 24 (general member resolution — direct case re-validated), 35 (reference-string
  rendering — composed automatically), 43 (subclass-aware exception type filter — same primitive
  applies, deferred), 27 (`TryEvalCurrentExceptionMember`), 25 (Roslyn walker — automatic benefit)
- Mercury session 2026-05-22 observation `probe-36-subclass-walk`
