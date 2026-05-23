# Finding 52: Netcoredbg retirement — `src/DrHook/` retired (substrate-independence bar reached)

**Status:**   **DONE.** `src/DrHook/` is gone — DAP client, netcoredbg locator, and the old DAP-
backed `SteppingSessionManager` deleted along with the project itself. The two still-used
EventPipe files (`ProcessAttacher` + `StackInspector`) moved to `src/DrHook.Engine/Diagnostics/`.
`DrHook.Mcp.csproj` now references only `DrHook.Engine`. `drhook-mcp 1.8.2` installed. 47 unit
tests still pass. **No managed dependency on netcoredbg remains in any shipping code** — the
substrate-independence bar from ADR-006 is reached.
**Date:**     2026-05-23
**Files moved:**   `src/DrHook/Diagnostics/{ProcessAttacher,StackInspector}.cs` →
                   `src/DrHook.Engine/Diagnostics/` (namespace `SkyOmega.DrHook.Diagnostics` →
                   `SkyOmega.DrHook.Engine.Diagnostics`)
**Files deleted:** `src/DrHook/Stepping/DapClient.cs`,
                   `src/DrHook/Stepping/NetCoreDbgLocator.cs`,
                   `src/DrHook/Stepping/SteppingSessionManager.cs`, `src/DrHook/DrHook.csproj`,
                   entire `src/DrHook/` directory.

## What this closes

Per ADR-006: *"DrHook.Engine is complete when DrHook.Core has zero spawns of netcoredbg (the
substrate-independence bar)."* The stepping path stopped using netcoredbg in finding 51 (the
SessionManager rewrite); this finding **removes the code itself**, so there is no longer any
managed reference to netcoredbg in the build.

The DrHook MCP tool surface is unchanged. Process listing and EventPipe snapshots still work —
`ProcessAttacher` and `StackInspector` just live in the engine project now.

## Surviving deps

After this slice, `DrHook.Engine` references:
- `Microsoft.Diagnostics.NETCore.Client` — Diagnostic IPC / EventPipe client (admitted per ADR-009).
- `Microsoft.Diagnostics.Tracing.TraceEvent` — EventPipe trace parsing for `StackInspector`
  (moved here from the retired `DrHook` project; admitted alongside the EventPipe machinery).
- `Microsoft.Diagnostics.DbgShim.<rid>` — per-RID native asset for `libdbgshim` (finding 50).
- (Conditional, only the dev's RID locally; all RIDs unconditionally in `DrHook.Mcp` so the
  global tool ships with every platform's shim.)

`DrHook.Mcp` now has a single project reference (`DrHook.Engine`) plus its MCP-server packages.

## Solution layout after retirement

```
src/
  DrHook.Engine/              ← BCL-only ICorDebug substrate + EventPipe diagnostics
    Diagnostics/
      ProcessAttacher.cs      ← moved from src/DrHook/Diagnostics/
      StackInspector.cs       ← moved from src/DrHook/Diagnostics/
    Interop/...
    BreakpointPolicy.cs
    DebugSession.cs
    …
  DrHook.Mcp/                 ← global tool, references only DrHook.Engine
    DrHookTools.cs
    EngineSteppingSession.cs  ← finding 51
    Program.cs
  (src/DrHook/ gone)
```

The `DrHook` solution folder remains in `SkyOmega.sln` as a virtual grouping for the two
surviving DrHook projects (`DrHook.Engine`, `DrHook.Mcp`) — only the third child, the retired
`DrHook` csproj, is removed.

## Version + install

`Directory.Build.props` 1.8.1 → **1.8.2** (patch — internal restructure, no public-API change).

```
Tool 'skyomega.drhook.mcp' was successfully updated from version '1.8.1' to version '1.8.2'.
drhook-mcp 1.8.2+ba9d8ca43eede91b82f673e82d77e2f66bf4a8dc
```

## Scope / next

ADR-006 Phase 3 — substrate, bundling, rewrite, retirement all DONE. Remaining items are
quality and polish, not gating:

- Per-MCP-tool regression suite — exercise each `drhook_step_*` end-to-end through the MCP
  wire. The substrate is validated by 40+ probes; this is the integration-layer validation.
- Polish: `drhook_step_test` (VSTEST_HOST_DEBUG dance), conditional breakpoints (Roslyn walker
  extraction to `DrHook.Engine.Expressions`), `drhook_step_run` stdout/stderr capture, multi-dim
  arrays, generic-type-parameter naming, smarter module disambiguation for `step_breakpoint`.

`drhook-mcp` is now a real consumer of every piece of substrate that's been built across
findings 40-51 — the dbgshim natives shipped in 1.8.1 stop being dead weight as of 1.8.2
(both stepping AND diagnostics go through the engine).

## References

- Old impl now gone: `src/DrHook/Stepping/SteppingSessionManager.cs` (1173 LOC, DAP/netcoredbg),
  `DapClient.cs`, `NetCoreDbgLocator.cs`.
- Moved (history-preserving git mv): `Diagnostics/ProcessAttacher.cs`, `Diagnostics/StackInspector.cs`.
- Touched: `DrHook.Engine.csproj` (+ TraceEvent), `DrHook.Mcp.csproj` (dropped DrHook ref),
  `DrHookTools.cs` (using ns change), `Directory.Build.props` (1.8.1 → 1.8.2), `SkyOmega.sln`
  (dropped DrHook project).
- ADR-006 Phase 3: substrate done (probes 31-40, findings 40-49), bundling done (50),
  SessionManager rewrite done (51), netcoredbg retirement done (this — 52).
- Mercury session 2026-05-22 observation `netcoredbg-retirement`
