# Finding 48: Probe 39 Outcome — PASSED: object field walking (`GetFieldValue@8` + `EnumFields@20`, depth ≥ 2)

**Status:**   **PASSED, 2/2** (clean exit 0; 47 unit tests still pass; probes 18 + 28 re-run for
regression — primitive locals & policy walker reading locals both unchanged). Object inspection
slice 5, **last substrate slice before arrays**. `DebugSession.GetLocals(int depth)` and
`GetArguments(int depth)` populate object values with their instance fields when `depth > 0`;
recursive when `depth > 1`. The headline machinery behind real `drhook_step_vars` output.
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/39-fields-smoke.cs` + `39-fields-target.cs`

## End-to-end

```
plan       : at 39-fields-target.cs:27, GetLocals(depth=1) populates counter.Fields;
             GetLocals(depth=2) populates Nested.Fields too.
phase A    : counter.Fields = 4 entries
             - Count:  type=0x08 raw=42      str=(null)   nested=no
             - Label:  type=0x0E raw=(none)  str="hello"  nested=no
             - Active: type=0x02 raw=1       str=(null)   nested=no
             - Nested: type=0x12 raw=(none)  str=(null)   nested=no
phase B    : counter.Nested.Fields = 1 entries; X = 99
PROBE 39 PASSED
```

Phase A confirms the basic enumeration: a `Counter` local at the breakpoint exposes its four
declared instance fields with the correct element types (I4, STRING, BOOLEAN, CLASS) and values
(strings via the probe-35 path; bools as 0/1 raw bits). The `Nested` field is a CLASS reference
at `depth=1`, with `Fields=null` (per spec).

Phase B uses `depth=2` and confirms recursion: `counter.Nested.Fields` is populated, surfacing
`X=99` on the `Inner` object. The recursion budget decrements at each level, so cycles can't run
unbounded.

**No `System.Object` pollution.** The walk goes `Counter → System.Object` via `GetBase`, but
`EnumFields` on Object's typedef returns nothing — the runtime-internal slots (method table
pointer, syncblock, etc.) aren't exposed via metadata. So we see exactly the 4 declared fields,
no noise to filter.

## How it walks

Same GetExactType + GetBase chain as findings 46/47, with a leaf action of "enumerate fields and
read each":

```csharp
GetFields(pValue, depth):
    value2 = QI(pValue, IID_ICorDebugValue2)
    type   = Out(value2, GetExactType)
    objectValue = QI(pValue, IID_ICorDebugObjectValue)
        || (QI ReferenceValue → Dereference → QI ObjectValue on the deref'd heap value)
    while (type != 0):
        klass = Out(type, GetClass)
        (module, typeToken) = (klass.GetModule, klass.GetToken)
        import = module.GetMetaDataImport
        for each fieldToken via IMetaDataImport.EnumFields@20:
            name = GetFieldProps@57(token)
            value = objectValue.GetFieldValue@8(klass, token)
            v = Variables.ReadValue(value)             // primitives + string
            nested = (depth > 1 && IsObject(v)) ? GetFields(value, depth-1) : null
            fields.Add(FieldValue(name, ..., nested))
        type = type.GetBase                            // walks inherited fields across modules
```

**One critical fixup**: `ICorDebugObjectValue` lives on the **dereferenced heap value**, NOT on
the reference. For local/arg values (always references for object types), we Dereference first.
The dual code path (try direct QI, fall back via Dereference) lets the same function handle both
calls from `Variables.ReadActiveFrameLocals` (reference inputs) and recursive calls on field
values (which may already be heap values).

## API surface

```csharp
public readonly record struct FieldValue(
    string Name, int ElementType, long? RawValue,
    string? StringValue = null,
    IReadOnlyList<FieldValue>? Fields = null);

// ArgumentValue / LocalValue also gained the optional Fields field.

DebugSession.GetLocals(int depth = 0);      // 0 = no fields (backward-compat)
DebugSession.GetArguments(int depth = 0);   // same shape, same default
```

Default `depth = 0` keeps every existing call site behavior unchanged — probes 18, 28, and the
rest read primitives + strings exactly as before. Opt-in to fields by passing a depth.

Element-type filter for recursion: `0x12 CLASS` and `0x1C OBJECT`. **Strings (`0x0E`) are
rendered via `StringValue`, not recursed into** (finding 44). **Arrays (`0x14` / `0x1D`) are the
next slice** — they need `ICorDebugArrayValue` (`GetRank`, `GetCount`, `GetElement`).

## The stale-cache bite (again)

First probe-39 run after the engine fix still showed `counter.Fields = 0 entries` — the
file-based-app runfile cache hadn't picked up the engine edit. Cleared
`~/Library/Application Support/dotnet/runfile/39-fields-smoke-*` per
`feedback_filebased_app_stale_cache`, and the second run showed the real (correct) result.
**Same lesson, third time** in the DrHook.Engine work — the runfile cache is content-hashed on
the script, not the referenced project; engine edits don't invalidate it.

## Scope / next

ADR-006 Phase 3 substrate gaps:

- [x] AsyncBreak (40), breakpoint registry (41), Launch (42), persistent exception filter (43)
- [~] Object inspection — strings (44), within-module subclass-walk (45), cross-module
  subclass-walk (46), subclass-aware exception filter (47), **object field walking** (this)
  DONE. Remaining: **arrays + generics** (`ICorDebugArrayValue` + `ICorDebugType.EnumerateTypeParameters`).
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement.
- Polish: Launch Terminate-on-dispose, stdout/stderr capture pipes.

Field walking is the **last substrate slice that unlocks user-visible MCP value** — once arrays
land, `drhook_step_vars` has everything it needs to render an arbitrary object graph. After
that, the remaining work is host-layer (SessionManager rewrite to back the MCP tools on
DrHook.Engine) and polish.

## References

- Probe: `poc/drhook-engine/39-fields-smoke.cs`, `39-fields-target.cs`
- Fixture: `fixtures/39-fields-osx-arm64-…`
- Engine: `Interop/FieldEnumerator.cs` (new), `Interop/Variables.cs` (depth threaded through
  `ReadActiveFrameLocals` and `ReadActiveFrameArguments`; `IsObjectReference` helper),
  `DebugSession.GetLocals(int depth = 0)` / `GetArguments(int depth = 0)`,
  `ArgumentValue`/`LocalValue` (gained `Fields`), `FieldValue` (new record)
- Findings 44 (strings — composed for string fields), 46/47 (GetBase walk — same primitive),
  18 / 28 (re-validated regression-safe)
- Memory: `feedback_filebased_app_stale_cache` (this slice was the third repeat — bite the bullet
  and always clear before re-running after engine edits)
- Mercury session 2026-05-22 observation `probe-39-object-field-walking`
