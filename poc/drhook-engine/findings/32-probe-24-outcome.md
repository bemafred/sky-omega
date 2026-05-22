# Finding 32: Probe 24 Outcome — PASSED: general member resolution (member on a value's runtime type)

**Status:**   **PASSED, 2/2.** A member is resolved on a value's RUNTIME type with no hardcoded
declaring type/module. At a breakpoint where `box` (a `Box` with `Size = 42`) is in scope,
`TryEvalMemberCall("box", "Size")` derived `box`'s runtime class, found `get_Size`, func-eval'd it,
and returned `42`. Probes 21/23 hardcoded `String.get_Length`; this resolves the getter from the
value itself — the last piece the Roslyn walker needs for `box.Size`-style conditions.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/24-member-resolution-smoke.cs` + `24-member-target.cs`

## The resolution chain (all slots/IIDs from cordebug.idl)

`MemberResolver.ResolveGetter(value, "Size")`:
1. QI the value → `ICorDebugReferenceValue` (IID `CC7BCAF9…`) → `Dereference`@10 → the object value.
2. QI the object → `ICorDebugObjectValue` (IID `18AD3D6E…`) → `GetClass`@7 → `ICorDebugClass`.
3. `Class.GetModule`@3 + `Class.GetToken`@4 → the declaring module + `mdTypeDef`.
4. `MetadataResolver.FindMethodInType(module, typeToken, "get_Size")` → `mdMethodDef` (uses
   `EnumMethodsWithName` on the type token directly — no `FindTypeDefByName`).
5. `Eval.GetFunction` → `ICorDebugFunction`; func-eval `CallWithOneArg(func, this=box)` → 42.

`Box` lives in the **target's own module**, so deriving the runtime class + module from the value (not
from a hardcoded name) is exactly what was exercised.

```
stopped    : in Worker.Inspect — resolving box.Size on its runtime type …
eval status: Completed
eval result: elementType=0x08  value=42
PROBE 24 PASSED — box.Size = 42 (getter resolved on the runtime type Box, no hardcoding).
```

## What this completes

Together with finding 31 (func-eval inside a conditional predicate), a **full member-access conditional
breakpoint** (`box.Size == 42`) is now achievable end to end: parse (Roslyn) → walk → for a member
access, `ResolveGetter` on the operand's runtime type → func-eval → compare. The walker needs only the
identifier (`box`) and the member (`Size`); the engine derives everything else from the live value.

## Scope / next

- **Plain reference objects** work (`ICorDebugObjectValue.GetClass`).
- **Strings, arrays, generics** are NOT `ICorDebugObjectValue` — `ResolveGetter` returns 0 for them.
  Their type comes from `ICorDebugValue2.GetExactType`@3 → `ICorDebugType.GetClass` (IID `5E0B54E7…`
  noted). That's the follow-on that makes `s.Length` work generically (today it needs the hardcoded
  String path of probe 21/23).
- **Fields** (vs property getters) — `ICorDebugObjectValue.GetFieldValue`@8 by field token; a sibling
  of the getter path.
- **Wire into the Roslyn walker** — `MemberAccessExpressionSyntax` → `ResolveGetter` + func-eval. With
  it, `box.Size == 42` is a parsed conditional breakpoint, not a hardcoded predicate.

## References

- Probe: `poc/drhook-engine/24-member-resolution-smoke.cs`, `24-member-target.cs`
- Fixture: `fixtures/24-member-resolution-osx-arm64-20260522T154721Z.txt`
- Engine: `Interop/MemberResolver.cs`, `MetadataResolver.FindMethodInType`, `DebugSession.TryEvalMemberCall` + `ResolveLocalSlot`
- Findings 31 (func-eval in conditional predicate), 29 (instance func-eval), 21 (hardcoded String.get_Length — now generalized)
- Mercury session 2026-05-21 observation `probe-24-member-resolution`
