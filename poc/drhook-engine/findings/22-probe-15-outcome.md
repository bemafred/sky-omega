# Finding 22: Probe 15 Outcome — PASSED: read argument values (inspection 5b) — Phase 2 inspection complete

**Status:**   **PASSED, 3/3.** The stop VALUES are now observable. At a breakpoint on
`Worker.Compute(int n, long total)` called with `(7, 100)`, the engine read the active frame's
arguments: `this` (Class ref), `n = 7` (I4), `total = 100` (I8). This completes the Phase 2
inspection surface (frames + variables). Two real obstacles were cleared: a wrong interface IID
(fixed from the authoritative cordebug.idl) and a stale file-based-app build cache that masked the
fix.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/15-variables-smoke.cs` + `15-vars-target.cs`
**Target:**   `Worker.Compute(int n, long total)` called with `(7, 100)`; baseline dbgshim.

## Approach: QI to derived interfaces, raw V-table on slots

`Variables.ReadActiveFrameArguments` (at a stop): `ICorDebugThread.GetActiveFrame`@15 → QI the frame
to `ICorDebugILFrame` → `GetArgument`@16 per index → `ICorDebugValue.GetType`@3 (CorElementType) → for
a generic (primitive) value, QI to `ICorDebugGenericValue` → `GetValue`@7 copies the bits. QI is used
for the two derived interfaces (it fails *gracefully* with E_NOINTERFACE on a wrong IID — unlike a raw
slot call on the wrong vtable, which would crash).

Run diagnostics confirmed the slot/IID correctness end to end:
```
QI(ILFrame) hr=0x00000000          ← S_OK with IID 03E26311
GetArgument(0) hr=0x0  → arg[0] elementType=0x12 (Class)  this, raw=(ref)
GetArgument(1) hr=0x0  → arg[1] elementType=0x08 (I4)     n=7
GetArgument(2) hr=0x0  → arg[2] elementType=0x0A (I8)     total=100
GetArgument(3) hr=0x80131304       ← CORDBG_E_IL_VAR_NOT_AVAILABLE (past the last arg) → stop
```

## Two obstacles (both instructive)

1. **Wrong IID — fixed authoritatively, not by guessing.** First attempts used
   `IID_ICorDebugILFrame = 03E26314` (actually `ICorDebugNativeFrame`) and an
   `IID_ICorDebugGenericValue` from the wrong GUID family. QI returned null → 0 args (graceful, no
   crash — the reason QI was chosen over raw slot calls for derived interfaces). Resolved by reading
   the real IIDs from dotnet/runtime `src/coreclr/inc/cordebug.idl`: `ICorDebugILFrame =
   03E26311-4F76-11D3-88C6-006097945418`, `ICorDebugGenericValue = CC7BCAF8-8A68-11D2-983C-0000F808342D`.
   The same idl also confirmed all slot numbers (GetActiveFrame@15, GetArgument@16, GetType@3, GetValue@7).
2. **Stale file-based-app build cache.** After fixing the IID the probe *still* failed identically —
   because re-running an unchanged `#:project` file-based app reuses a cached build; the engine source
   edit didn't invalidate it. Clearing `~/Library/Application Support/dotnet/runfile/15-variables-smoke-*`
   made both the fix and the diagnostics take effect immediately. (Probes 12–14 dodged this by being
   new script filenames, each built fresh once.)

## What this proves

1. **Argument values are readable** at a stop, with correct CorElementType and primitive bits.
2. **The QI-for-derived-interfaces pattern works** and fails safely on a wrong IID — validating the
   choice to QI (graceful) rather than assume a shared vtable (crash-prone).
3. **Phase 2 inspection is complete** — `DebugSession` now answers *where* (`GetStackFrames`) and
   *with what* (`GetArguments`) at any stop.

## Scope / what remains (refinements, not Phase 2 blockers)

- **Locals** need PDB local-name mapping (index-based reading works the same way via
  `GetLocalVariable`@14; names are the gap). Argument names are in metadata (a small addition).
- **Object/reference values** report their element type with a null raw value — dereferencing
  (`ICorDebugReferenceValue`/`ICorDebugObjectValue`, field reads) is a later refinement.
- **Wider primitive rendering** (R4/R8 as floats, Boolean/Char/strings) — the raw bits are captured;
  typed rendering is presentation.

These are refinements; the inspection foundation (frames + argument values) is in place. Next is
Phase 3 — switchover (default to DrHook.Engine, retire netcoredbg).

## References

- Probe: `poc/drhook-engine/15-variables-smoke.cs`, `15-vars-target.cs`
- Fixture: `fixtures/15-variables-osx-arm64-20260522T072435Z.txt`
- Engine: `src/DrHook.Engine/Interop/Variables.cs`, `ArgumentValue.cs`, `DebugSession.GetArguments`
- Findings 21 (stack frames — the "where"), 18 (metadata IID lesson), 16 (stopping model)
- Mercury session 2026-05-21 observation `probe-15-passed`
