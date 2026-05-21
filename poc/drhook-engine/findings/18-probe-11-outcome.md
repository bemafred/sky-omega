# Finding 18: Probe 11 Outcome — PASSED: metadata resolution (method name → mdMethodDef, breakpoint setting 4b)

**Status:**   **PASSED, 2/2.** A method name resolves to its `mdMethodDef` token through `IMetaDataImport`,
the gnarliest interop in the engine so far (a non-ICorDebug COM interface with a deep vtable). At a
stop, `DebugSession.ResolveMethodToken("11-bp-target", "Worker", "Tick")` returned **`0x06000003`**
(table `0x06` = mdMethodDef), and a non-existent method name returned `0`. Every recalled slot was
correct on the first functional run. This token feeds breakpoint creation (4c).
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/11-metadata-smoke.cs` + `11-bp-target.cs`
**Target:**   a `Worker.Tick()` method + a setup `Debugger.Break()`; baseline dbgshim.

## Approach: raw V-table, one unavoidable GUID

`MetadataResolver` reaches metadata via `ICorDebugModule.GetMetaDataInterface`(slot 14), passing
`IID_IMetaDataImport` — the **one unavoidable GUID** (it is an *input* to GetMetaDataInterface, and a
wrong value fails cleanly with E_NOINTERFACE; it is a stable, well-known metadata IID). From the
returned `IMetaDataImport` everything is raw V-table again. Validated slots:

| Call | Slot |
|---|---|
| `ICorDebugModule.GetMetaDataInterface(riid, ppObj)` | 14 |
| `IMetaDataImport.CloseEnum(hEnum)` (void) | 3 |
| `IMetaDataImport.FindTypeDefByName(szTypeDef, tkEnclosing, ptd)` | 9 |
| `IMetaDataImport.EnumMethodsWithName(phEnum, cl, szName, rMethods[], cMax, pcTokens)` | 19 |

Resolution: `FindTypeDefByName("Worker", 0, …)` → typedef token, then `EnumMethodsWithName(td, "Tick", …)`
→ the method def token. `EnumMethodsWithName` does the name match internally, so no method-signature
blob (`GetMethodProps`) is needed. Metadata is static — present whether or not `Worker` is JIT-loaded —
so the type resolves at the startup `Break`, before the loop ever instantiates it.

## A target-side race the probe caught (not an engine bug)

The first run timed out (no Break stop). Cause: this target calls `Debugger.Break()` **once** at startup,
and that single Break fired *before* attach completed (probe 09 looped Break, so one always followed
attach). Fix: the target waits for `Debugger.IsAttached` (capped at 5 s) before the setup break, so it
can't race ahead of attach. The engine's stopping model was unaffected — this was purely target timing.

## Run result (2/2)

```
stopped : Break — resolving Worker.Tick in 11-bp-target
token   : 0x06000003  (table 0x06, rid 3)
bogus   : 0x00000000
PROBE 11 PASSED — resolved Worker.Tick → mdMethodDef 0x06000003; bogus name → 0.
```

## What this proves

1. **`IID_IMetaDataImport` is correct** and `GetMetaDataInterface` hands back a working metadata import.
2. **The IMetaDataImport slots are correct** on CoreCLR 10 / macOS-arm64 — `FindTypeDefByName` and
   `EnumMethodsWithName` resolve a real method to a valid `mdMethodDef` (`0x06xxxxxx`).
3. **Resolution is specific** — a non-existent method name yields `0`, not a false positive.

## Next (4c — breakpoint hit)

`ICorDebugModule.GetFunctionFromToken`(slot 9) takes the `mdMethodDef` → `ICorDebugFunction`;
`ICorDebugFunction.CreateBreakpoint` → `ICorDebugFunctionBreakpoint.Activate(true)`. Run; each
`Worker.Tick()` call then hits the breakpoint, arriving as a `Breakpoint` callback that rides the
probe-09 stopping model (already a `StopReason.Breakpoint`). 4c keeps the module pointer (rather than
releasing it as 4b does) so it can call `GetFunctionFromToken` on it.

## References

- Probe: `poc/drhook-engine/11-metadata-smoke.cs`, `11-bp-target.cs`
- Fixture: `fixtures/11-metadata-osx-arm64-20260521T235240Z.txt`
- Engine: `src/DrHook.Engine/Interop/MetadataResolver.cs`, `RuntimeNavigation.FindModule`/`ResolveMethodToken`, `DebugSession.ResolveMethodToken`
- Finding 17 (navigation 4a — reaches the module), Finding 16 (stopping model — the synchronized window)
- Mercury session 2026-05-21 observation `probe-11-passed`
