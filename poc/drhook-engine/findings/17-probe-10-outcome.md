# Finding 17: Probe 10 Outcome — PASSED: process→module navigation (breakpoint setting 4a)

**Status:**   **PASSED, 2/2.** The CONSUME-direction walk down the live object graph —
process → app domains → assemblies → modules — works. At a stop, `DebugSession.EnumerateModules()`
returned all 8 of the target's loaded modules with full paths, including `System.Private.CoreLib.dll`
and the target's own `09-break-target.dll`. This is the first leg of breakpoint setting: the next
legs resolve a method token in a module (4b) and create the breakpoint (4c).
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/10-module-navigation-smoke.cs` + `09-break-target.cs`
**Target:**   `Debugger.Break()` loop (reused for a clean synchronized stop); baseline dbgshim.

## Approach: raw V-table, no GUIDs

Setting a breakpoint needs an `ICorDebugFunction`, reached only by walking the object graph. The
walk was implemented with **raw V-table calls (slot numbers only — no QueryInterface, no IIDs)**
in `RuntimeNavigation`, so it depends solely on the documented cordebug.idl slot layout (finding
03), not on interface GUIDs that would otherwise have to be transcribed and risk being wrong.
Every slot used was validated empirically here:

| Call | Interface | Slot |
|---|---|---|
| `EnumerateAppDomains` | ICorDebugProcess (Controller 3–12, own 13–) | 26 |
| `EnumerateAssemblies` | ICorDebugAppDomain (Controller 3–12, own 13–) | 14 |
| `EnumerateModules` | ICorDebugAssembly (IUnknown 0–2, own 3–) | 5 |
| `GetName` | ICorDebugModule (IUnknown 0–2, own 3–) | 6 |
| `Next` | ICorDebug\*Enum : ICorDebugEnum (Skip3/Reset4/Clone5/GetCount6) | 7 |

`Next` is drained one element at a time; every returned pointer is an owned reference and is
`Release`d (slot 2). `GetName` uses the two-call buffer pattern (size, then fill; the count
includes the trailing NUL). Inspection runs only while the debuggee is SYNCHRONIZED (at a stop) —
the probe reaches `EnumerateModules` after `WaitForStop` returns a `Break`, with the pump worker
parked, which is exactly the synchronized window ICorDebug enumeration requires.

## Run result (2/2)

```
stopped    : Break — enumerating modules (debuggee synchronized)
modules    : 8
  - …/runfile/09-break-target-…/bin/debug/09-break-target.dll   <-- the target's own module
  - …/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll    <-- always-loaded sentinel
  - …/System.Console.dll, System.Threading.dll, System.Runtime.dll, …
PROBE 10 PASSED — 8 modules, CoreLib present.
EXIT=0
```

## What this proves

1. **The raw-V-table navigation slots are correct** on CoreCLR 10 / macOS-arm64 — the walk
   reaches every module and reads its name. No GUIDs were needed; slot numbers from finding 03 held.
2. **The target's own module is reachable** (`09-break-target.dll`) — that is where 4b/4c will
   resolve a method and set a breakpoint.
3. **Inspection composes with the stopping model** — enumeration runs cleanly at a `Break` stop
   (worker parked, process synchronized) and the session resumes + detaches cleanly afterward.

## Next (breakpoint setting continues)

- **4b — metadata.** `ICorDebugModule.GetMetaDataInterface`(slot 14) → `IMetaDataImport`; enumerate
  type defs + methods (`EnumTypeDefs`/`GetTypeDefProps`, `EnumMethods`/`GetMethodProps`) and match a
  method by name → `mdMethodDef` token. This is the gnarliest interop (buffer + enum patterns on a
  non-ICorDebug COM interface) and gets its own probe.
- **4c — breakpoint.** `ICorDebugModule.GetFunctionFromToken`(slot 9) → `ICorDebugFunction.CreateBreakpoint`
  → `ICorDebugFunctionBreakpoint.Activate`. A hit then arrives as a `Breakpoint` callback and rides the
  probe-09 stopping model (already a `StopReason.Breakpoint`).

## References

- Probe: `poc/drhook-engine/10-module-navigation-smoke.cs`
- Fixture: `fixtures/10-module-navigation-osx-arm64-20260521T232209Z.txt`
- Engine: `src/DrHook.Engine/Interop/RuntimeNavigation.cs`, `DebugSession.EnumerateModules()`
- Finding 03 (ICorDebug contract / slot layout), Finding 16 (stopping model — provides the synchronized window)
- Mercury session 2026-05-21 observation `probe-10-passed`
