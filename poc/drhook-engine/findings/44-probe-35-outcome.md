# Finding 44: Probe 35 Outcome — PASSED: reference-string rendering (`ICorDebugStringValue` via Dereference)

**Status:**   **PASSED, 2/2** (clean exit 0; 47 unit tests still pass; probe 22 regression
re-validated — `Variables.ReadValue` unchanged for primitives). First slice of object inspection
(the longest pole of Phase 3 substrate): a func-eval'd string result now comes back with its
rendered content. The recurring "reference-typed results" gap (findings 32 / 37 / 39) is closed
for **strings** — arrays, generics, and reference-typed object fields remain.
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/35-string-smoke.cs` + `35-string-target.cs`

## The chain — direct QI, fallback via Dereference

`ICorDebugStringValue` inherits `ICorDebugHeapValue` inherits `ICorDebugValue`, so its vtable is
`IUnknown(0–2)` + `ICorDebugValue(3–6)` + `ICorDebugHeapValue(7–8)` + own `(9 GetLength, 10 GetString)`.
IID `CC7BCAFD-8A68-11D2-983C-0000F808342D` (verified from cordebug.idl — close to but distinct from
the other CC7BCA-family IIDs, **never guess these**).

```csharp
public static bool TryRead(nint pValue, out string? text)
{
    // 1. Direct QI: works if pValue is already a dereferenced heap value (e.g. inside ResolveGetter).
    nint stringValue = QueryInterface(pValue, IID_ICorDebugStringValue);
    if (stringValue == 0)
    {
        // 2. Fallback: pValue is a ReferenceValue → Dereference → QI StringValue on the result.
        nint reference = QueryInterface(pValue, IID_ICorDebugReferenceValue);
        if (reference == 0) return false;
        nint dereferenced = Dereference(reference);
        stringValue = QueryInterface(dereferenced, IID_ICorDebugStringValue);
        if (stringValue == 0) return false;
    }

    // 3. GetLength@9 then GetString@10 — capacity = length+1, truncate to returned count.
    uint length = GetLength(stringValue);
    char[] buffer = new char[length + 1];
    uint actual = GetString(stringValue, buffer);
    text = new string(buffer, 0, Math.Min(actual, length));
    return true;
}
```

Cheap on misses (one or two QIs return E_NOINTERFACE), so it's safe to call on every value read.

## Where it hooks — `Variables.ReadValue`, once

```csharp
internal static ArgumentValue ReadValue(nint pValue)
{
    int elementType = OutInt(pValue, ValueGetType);
    long? raw = ReadPrimitiveBits(pValue);
    string? stringValue = StringInspector.TryRead(pValue, out string? text) ? text : null;
    return new ArgumentValue(elementType, raw, stringValue);
}
```

One change reaches **every** consumer:
- `GetArguments` / `GetLocals` (locals & args of any frame), and
- `Eval.GetResultValue` (func-eval results — `TryEvalCurrentExceptionMember`, `TryEvalMemberCall`, etc.).

`ArgumentValue` and `LocalValue` both gained an optional `string? StringValue = null` field — default
null for non-strings keeps every existing call site behavior unchanged. The probe-22 regression
re-run confirms primitive paths are untouched.

## End-to-end

```
plan       : arm filter on ProbeException; at the exception stop, func-eval Description; expect StringValue == "hello string".
stopped    : Exception ProbeException @ FirstChance — func-eval Description …
eval status: Completed
eval result: elementType=0x0E  raw=(none)  StringValue="hello string"
PROBE 35 PASSED
```

`elementType=0x0E` is `CorElementType.ELEMENT_TYPE_STRING` — the runtime confirms the result kind;
`raw=(none)` because strings aren't primitive; `StringValue="hello string"` is the actually-read
content from the heap object.

## Scope notes — what this DOESN'T cover yet

- **Subclass-aware method resolution.** The target's `ProbeException.Description` is declared
  directly on `ProbeException` so `MetadataResolver.FindMethodInType` finds `get_Description` on
  the runtime class. `ex.Message` (inherited from `System.Exception`) would NOT be found by the
  current resolver — that's the `extends`-chain walk (via `GetTypeDefProps`'s `ptkExtends` parameter)
  flagged in finding 43. A separate slice.
- **Arrays, generics, non-string reference objects.** This probe covers strings only. The general
  reference-result rendering uses `ICorDebugValue2::GetExactType@3` (IID `5E0B54E7…`) to get the
  exact `ICorDebugType`; from there the type's kind discriminates array (`GetRank`, indexed
  reads), generic instantiation (`EnumerateTypeParameters`), or plain object (walk fields via
  `ICorDebugObjectValue::GetFieldValue@8`). The next object-inspection slice.
- **Deeper rendering (object → fields → values).** Today the engine renders one level (the value
  itself). For `drhook_step_vars depth=2+` the engine needs to walk fields. Composes with the
  `GetFieldValue` work above. Future slice.
- **Mutating values.** `ICorDebugGenericValue::SetValue` is wired (for func-eval arg construction);
  `ICorDebugReferenceValue::SetValue` for mutating an object's reference is not yet exposed at the
  engine API surface. Not in the assessment's punch list.

## Scope / next

Phase 3 substrate gaps:

- [x] AsyncBreak (40), breakpoint registry (41), Launch (42), persistent exception filter (43)
- [~] **Object inspection — strings DONE** (this finding). Remaining: subclass-aware resolution,
  `GetExactType` + arrays/generics, field walking (depth ≥ 2), and (possibly) reference mutation.
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement

## References

- Probe: `poc/drhook-engine/35-string-smoke.cs`, `35-string-target.cs`
- Fixture: `fixtures/35-string-osx-arm64-…`
- Engine: `Interop/StringInspector.cs` (new — TryRead via QI + Dereference + GetLength/GetString),
  `Interop/Variables.ReadValue` (hooked StringInspector), `ArgumentValue`/`LocalValue`
  (gained `StringValue?`), `Interop/Variables.ReadActiveFrameLocals` (passes `v.StringValue` through)
- Findings 32 (member resolution — strings noted as not covered), 37 (func-eval at exception stop —
  reference results gap), 39 (rendering gap in BoundedLogSink + walker), 43 (subclass-walking
  follow-on — same axis)
- Mercury session 2026-05-22 observation `probe-35-string-rendering`
