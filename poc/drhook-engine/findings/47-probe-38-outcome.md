# Finding 47: Probe 38 Outcome — PASSED: subclass-aware exception filter (chain-walk via GetBase)

**Status:**   **PASSED, 2/2** (clean exit 0; 47 unit tests still pass; probes 26 + 34 re-run for
regression). Finding 43's noted follow-on, made cheap by probe 37's `ICorDebugType.GetBase` chain.
A filter on a base class (e.g. `"System.Exception"`) now matches any subclass via inheritance
walk; exact filters (`"ProbeException"`) still match only their target. **Same primitive serves
two consumers** — member resolution (finding 46) and exception type matching (this finding) both
walk the chain via `GetBase`.
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/38-subfilter-smoke.cs` + `38-subfilter-target.cs`

## Implementation — three small pieces

```csharp
// 1. ExceptionInspector.CurrentExceptionTypeChain(thread) — walks GetExactType + GetBase,
//    naming each level via MetadataResolver.TypeNameFromToken.
public static IReadOnlyList<string> CurrentExceptionTypeChain(nint pThread)
{
    nint value = Out(pThread, ThreadGetCurrentException);
    nint type  = Out(QueryInterface(value, IID_ICorDebugValue2), Value2GetExactType);
    var chain = new List<string>();
    while (type != 0)
    {
        if (NameOfType(type) is { } name) chain.Add(name);
        nint baseType = Out(type, TypeGetBase);
        Release(type);
        type = baseType;
    }
    return chain;
}

// 2. ExceptionFilterInfo.MatchesChain — accepts the chain instead of a single type name.
public bool MatchesChain(IReadOnlyList<string> actualTypeChain, ExceptionStopKind actualPhase)
{
    if (PhaseFilter != ExceptionStopKind.None && PhaseFilter != actualPhase) return false;
    if (TypeName == AnyType) return true;
    for (int i = 0; i < actualTypeChain.Count; i++)
        if (actualTypeChain[i] == TypeName) return true;
    return false;
}

// 3. DebugSession.ExceptionMatchesAnyFilter — switches to the chain.
private bool ExceptionMatchesAnyFilter(ExceptionStopKind actualPhase)
{
    var chain = ExceptionInspector.CurrentExceptionTypeChain(_pump.StopThread);
    if (chain.Count == 0) return false;
    foreach (var f in _exceptionFilters)
        if (f.MatchesChain(chain, actualPhase)) return true;
    return false;
}
```

`ExceptionInspector` was also consolidated: `CurrentExceptionTypeName` became a thin wrapper
over `CurrentExceptionTypeChain[0]`, removing the duplicate Reference → Dereference →
ObjectValue chain (the same GetExactType path that `MemberResolver` uses after finding 46).

## End-to-end

Target throws `ProbeException` + `OtherException` alternately, both deriving directly from
`System.Exception`.

```
phase A    : arm id=1 filter="System.Exception" — both user-defined exceptions should surface
   cycle 1: stop=Exception  type=ProbeException
   cycle 2: stop=Exception  type=OtherException
phase A OK : saw both ProbeException and OtherException via base-class filter

phase B    : arm id=2 filter="ProbeException" — only ProbeException should surface
   cycle 1: stop=Exception  type=ProbeException
   cycle 2: stop=Exception  type=ProbeException
   cycle 3: stop=Exception  type=ProbeException
   cycle 4: stop=Exception  type=ProbeException
   cycle 5: stop=Exception  type=ProbeException
phase B OK : observed 5 ProbeException stops (and zero non-ProbeException)
PROBE 38 PASSED
```

Phase A's first two cycles already validate the subclass match — within ~40 ms two distinct
user-exception types both bubble up under the base-class filter. Phase B's 5 cycles confirm
exact matching still gates strictly — every alternating `OtherException` was auto-resumed inside
`WaitForStop` without surfacing.

## Probe bug surfaced + fixed

The first probe-38 run printed `PROBE 38 PASSED` but phase B actually showed
`cycle 1: stop=null type=(none)` and broke out — the success message was a lie. Root cause: an
extraneous `session.Resume()` between phase A's loop (which already resumed) and phase B's wait
loop. That second Resume enqueued a `Continue` the worker consumed on the very next exception
stop, BYPASSING `WaitForStop` — so the matching `ProbeException` could go past us with no record.

Fixes:
1. Removed the redundant `session.Resume()` — phase A's last loop iteration already resumed.
2. Strengthened phase B's assertion: track `probeStops` and require ≥ 1, so "zero stops in 5
   cycles" can't masquerade as a pass.

After re-run: 5 cycles, all `ProbeException`, zero leakage — for real this time.

## Why this slice was cheap — primitive reuse

`ExceptionInspector.CurrentExceptionTypeChain` is structurally identical to `MemberResolver`'s
new GetExactType + GetBase loop (finding 46). Same IIDs, same slot constants, same walk shape.
The differences are leaf actions:
- `MemberResolver` calls `FindMethodInTypeDirectly` at each level and stops on the first hit.
- `ExceptionInspector` calls `MetadataResolver.TypeNameFromToken` and appends to a list, never
  early-exiting.

Same primitive, two consumers, one slice each — finding 43's "same axis as method walking"
prediction confirmed empirically.

## Scope / next

ADR-006 Phase 3 substrate gaps:

- [x] AsyncBreak (40), breakpoint registry (41), Launch (42), persistent exception filter (43)
- [~] Object inspection — strings (44), within-module subclass-walk (45), cross-module
  subclass-walk (46), **subclass-aware exception filter** (this finding) DONE. Remaining:
  - **Arrays + generics** — `ICorDebugArrayValue` (`GetRank`, indexed reads) + generic-parameter
    walking via `ICorDebugType.EnumerateTypeParameters` / `GetFirstTypeParameter`.
  - **Object fields + depth ≥ 2** — `ICorDebugObjectValue::GetFieldValue@8` + recursive
    rendering. Composes with the GetBase walk to enumerate inherited fields.
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement
- Polish: Launch Terminate-on-dispose, stdout/stderr capture pipes.

## References

- Probe: `poc/drhook-engine/38-subfilter-smoke.cs`, `38-subfilter-target.cs`
- Fixture: `fixtures/38-subfilter-osx-arm64-…`
- Engine: `Interop/ExceptionInspector` (rewritten — GetExactType + GetBase loop, new
  `CurrentExceptionTypeChain`; `CurrentExceptionTypeName` now `chain[0]`),
  `ExceptionFilterInfo.MatchesChain` (new), `DebugSession.ExceptionMatchesAnyFilter` (uses
  chain)
- Findings 43 (predicted this follow-on), 46 (same GetBase primitive — member resolution),
  34 (exact filter — re-validated regression-safe), 26 (basic exception stop — also re-validated)
- Mercury session 2026-05-22 observation `probe-38-subclass-aware-exception-filter`
