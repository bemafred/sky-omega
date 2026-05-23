# Finding 49: Probe 40 Outcome — PASSED: array rendering (`ICorDebugArrayValue`, SZARRAY, depth ≥ 2)

**Status:**   **PASSED, 2/2** (clean exit 0; 47 unit tests still pass; probe 39 re-run for
regression — field walking through the shared dispatcher unchanged). **Object inspection is
COMPLETE** with this slice: every Phase 3 substrate gap is closed. Remaining Phase 3 work is
host-layer (`SteppingSessionManager` rewrite + regression suite + netcoredbg retirement) plus
named polish items (Launch Terminate-on-dispose, stdout/stderr capture, multi-dim arrays,
generic-type-parameter naming).
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/40-arrays-smoke.cs` + `40-arrays-target.cs`

## End-to-end

```
plan       : at 40-arrays-target.cs:32, validate int[] / string[] / Item[] rendering
             (depth=1 + depth=2 recursive through array into object).
phase A    : numbers.Fields = 5 entries
             - [0]: type=0x08 raw=1
             - [1]: type=0x08 raw=2
             - [2]: type=0x08 raw=3
             - [3]: type=0x08 raw=5
             - [4]: type=0x08 raw=8
phase B    : names.Fields = 3 entries
             - [0]: type=0x0E str="alpha"
             - [1]: type=0x0E str="beta"
             - [2]: type=0x0E str="gamma"
phase C    : items.Fields = 3 entries (depth=2)
             - [0]: type=0x12 N=10
             - [1]: type=0x12 N=20
             - [2]: type=0x12 N=30
PROBE 40 PASSED
```

Three array shapes in one probe: `int[]` (primitive elements via `ReadValue.RawValue`),
`string[]` (each element rendered via the probe-35 string path on `FieldValue.StringValue`),
and `Item[]` at `depth=2` (each element is an object, expanded into its own Fields recursively).
**Phase C is the proof-of-composition** — array elements that are objects recurse through
`FieldEnumerator` automatically via the shared dispatcher.

## Design — single dispatcher, composable inspectors

The slice's most important code is **not** in `ArrayInspector`. It's the seven-line
`Variables.GetChildren` dispatcher:

```csharp
internal static IReadOnlyList<FieldValue>? GetChildren(nint pValue, int elementType, int depth)
{
    if (pValue == 0 || depth <= 0) return null;
    if (elementType == 0x12 || elementType == 0x1C) return FieldEnumerator.GetFields(pValue, depth);
    if (elementType == 0x14 || elementType == 0x1D) return ArrayInspector.TryReadElements(pValue, depth);
    return null;
}
```

Both inspectors now recurse through this single dispatcher — `FieldEnumerator`'s previous
self-recursive `GetFields(...)` call was replaced with `Variables.GetChildren(...)`. With that
indirection, a field whose value is an array AND an array element whose value is an object both
expand correctly without either inspector needing to know about the other. The
`ReadActiveFrameLocals`/`Arguments` top-level entry calls the same dispatcher with the local's
element type.

`FieldEnumerator`'s old `IsObjectReference` helper is dead and removed.

## ArrayInspector

Slots verified from cordebug.idl: `ICorDebugArrayValue` (IID `0405B0DF-A660-11D2-BD02-0000F80849BD`)
inherits HeapValue ← Value, so own methods are at slots 9-16: `GetElementType@9`, `GetRank@10`,
`GetCount@11`, `GetDimensions@12`, `HasBaseIndicies@13`, `GetBaseIndicies@14`, `GetElement@15`,
`GetElementAtPosition@16`.

This slice uses `GetRank@10`, `GetCount@11`, `GetElementAtPosition@16` — multi-dim arrays
(rank > 1) return null for now (a future small slice via `GetDimensions` + `GetElement`).

**Element cap**: 64 entries per array with a trailing `"[…M more]"` marker if truncated. Render
truncation, not a substrate limit — keeps `drhook_step_vars` output bounded for huge collections.

**Same Dereference guard** as `FieldEnumerator` and `StringInspector`: arrays live on the
dereferenced heap value, so try direct QI first; fall back via QI ReferenceValue → Dereference
→ re-QI on the dereferenced heap value.

## Marker-in-header lesson — fourth bite

First run printed `FALSIFIED: expected Breakpoint, got null` because my target header comment
mentioned the literal `ARRAYS_HERE` token — same class as probes 17, 32, 36's first runs.
`FindMarker` matched line 8 (the comment), not the actual code line. Fixed the header to
describe the marker without using the literal token.

Pattern lesson, fourth time: **never put a marker token literally in a target's header comment**.
Worth a target-template note for future probes (kept descriptive in headers, the literal token
only appears on its code line).

## Scope / next

ADR-006 Phase 3 substrate gaps — all closed:

- [x] AsyncBreak (40), breakpoint registry (41), Launch (42), persistent exception filter (43)
- [x] Object inspection — strings (44), within-module subclass-walk (45), cross-module
  subclass-walk (46), subclass-aware exception filter (47), object fields + depth (48),
  **arrays (this finding)**.

Remaining Phase 3 work:

- **`SteppingSessionManager` rewrite** backed by `DebugSession` (~1173 lines of DAP plumbing
  → ~400 lines of engine orchestration). JSON response shapes preserved so MCP consumers don't
  break.
- **Regression suite** — per-MCP-tool integration tests; the existing probe targets are most
  of what's needed.
- **Retire netcoredbg** + drop `Microsoft.Diagnostics.NETCore.Client` if `ProcessAttacher` can
  use dbgshim's enumeration.
- **Polish**: Launch Terminate-on-dispose, stdout/stderr capture pipes, multi-dim arrays,
  generic-type-parameter naming (e.g. "List&lt;int&gt;" not just "List`1").

## References

- Probe: `poc/drhook-engine/40-arrays-smoke.cs`, `40-arrays-target.cs`
- Fixture: `fixtures/40-arrays-osx-arm64-…`
- Engine: `Interop/ArrayInspector.cs` (new), `Interop/Variables.GetChildren` (new internal
  dispatcher), `Interop/FieldEnumerator` (recursion routed through GetChildren; dead
  `IsObjectReference` removed), `Variables.ReadActiveFrameLocals`/`ReadActiveFrameArguments`
  (route through GetChildren)
- Findings 39 (field walking — composed automatically), 44 (strings), 46/47 (GetExactType +
  GetBase chain — same primitive)
- Memory: `feedback_filebased_app_stale_cache` (also relevant; probe 40 didn't hit it because
  the smoke was new — fresh runfile cache)
- Mercury session 2026-05-22 observation `probe-40-array-rendering`
