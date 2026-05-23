# Finding 51: SessionManager rewrite — DrHook.Mcp on DrHook.Engine (Phase 3 close)

**Status:**   **DONE.** `drhook-mcp 1.8.1` installed as global tool; backed by BCL-only
`EngineSteppingSession` over `DebugSession`. 47 unit tests still pass; all bundled per-RID
libdbgshim natives deploy correctly; `--version` reports 1.8.1. The stepping path no longer
needs netcoredbg.
**Date:**     2026-05-23
**Files:**    `src/DrHook.Mcp/EngineSteppingSession.cs` (new), `DrHookTools.cs` (rewired),
              `Program.cs` (DI + help), `DrHook.Mcp.csproj` (added DrHook.Engine ref),
              `Directory.Build.props` (1.8.0 → 1.8.1)

## What changed

`DrHook.Mcp/DrHookTools.cs` now consumes a new `EngineSteppingSession` instead of the 1173-line
DAP-via-netcoredbg `SteppingSessionManager`. Same 17-method async surface; the JSON top-level
keys (`status`, `operation`, `step`, `currentState`, `stoppedReason`, `metrics`, `hypothesis`,
`prompt`) are preserved so existing MCP consumers don't break. Inner shapes carry engine-derived
data (richer than the DAP-JSON it replaced — locals/args include `elementType`, `string`,
`fields` nested per probes 35/44/48/49).

```text
1173 LOC (DAP plumbing + netcoredbg subprocess + JSON serialization)
  → ~500 LOC (direct DebugSession calls + JSON shaping)
```

The DAP wire protocol, the `DapClient`, `NetCoreDbgLocator`, and the netcoredbg subprocess
lifecycle all leave the stepping path. They remain in `src/DrHook/` (the project) — dormant
because nothing in the MCP tool surface references them anymore — until a follow-on cleanup
extracts the still-used `ProcessAttacher` + `StackInspector` (EventPipe-based, independent of
netcoredbg) and retires the project entirely.

## 17-method mapping

| MCP tool | Engine call(s) |
|---|---|
| `drhook_step_launch` | `Attach` + `WaitForStop` (setup) + `SetBreakpointAtLine` + `Resume` + `WaitForStop` |
| `drhook_step_run` | `Launch` + `WaitForStop` + `SetBreakpointAtLine` + `Resume` + `WaitForStop`; tracks the launched `Process` for kill-on-stop |
| `drhook_step_test` | **Not yet ported** — VSTEST_HOST_DEBUG / testhost-PID-discovery dance is Phase 3 polish; surfaces a clear "use step_run or step_launch" message |
| `drhook_step_next/into/out` | `StepOver/StepInto/StepOut` + `WaitForStop` |
| `drhook_step_continue` | `Resume` (+ optional `WaitForStop`) |
| `drhook_step_pause` | `Pause` + `WaitForStop` |
| `drhook_step_breakpoint` | `SetBreakpointAtLine`, id tracked in local `file:line → id` map |
| `drhook_step_break_function` | `SetBreakpoint`, id tracked in `function → id` map |
| `drhook_step_break_exception` | `ArmExceptionFilter`; DAP `"all"` → `*` FirstChance, `"user-unhandled"` → `*` Unhandled, literal type name accepted |
| `drhook_step_breakpoint_list` | Project local id maps into a structured JSON |
| `drhook_step_breakpoint_remove` | Lookup in id map, `RemoveBreakpoint` / `RemoveExceptionFilter` |
| `drhook_step_breakpoint_clear` | Iterate maps by category, remove each |
| `drhook_step_vars` | `GetLocals(depth)` + `GetArguments(depth)` → JSON tree (primitives, strings, fields, array elements) |
| `drhook_step_stop` | Kill the launched `Process` if any (finding 42 polish), `Dispose` the session, summary JSON |

Snapshot + processes tools (EventPipe, in `DrHook.Diagnostics`) are unchanged and keep working
through the existing `DrHook` project reference.

## Deferred polish (named, not YAGNI)

These are explicit gaps in the new session, surfaced as structured errors rather than silent
no-ops. None block the substrate completeness claim:

- **`drhook_step_test`** — VSTEST_HOST_DEBUG launch + testhost-PID discovery + Attach. The
  engine has all the primitives; the orchestration just isn't ported yet.
- **Conditional breakpoints** (`condition` arg on `step_breakpoint` / `step_break_function`) —
  needs the Roslyn walker extracted from the probes into a `DrHook.Engine.Expressions` package.
  Today the walker lives in each probe (probes 22/25/29 inline).
- **`metrics` block** — currently a stub-with-shape-note; the EventPipe-based metrics path used
  by the old session manager is already served by `drhook_snapshot`. Could merge later.
- **Module disambiguation for `step_breakpoint`** — heuristic today: `moduleSubstring =
  Path.GetFileNameWithoutExtension(sourceFile)`. Works for file-based apps and single-project
  cases; multi-project projects with the same filename across assemblies need a smarter
  resolver (search loaded modules for one whose PDB references the file).

## Version bump

Per the user's instruction: `Directory.Build.props` 1.8.0 → **1.8.1** (patch — substrate-internal
change, no public-API break; `dotnet tool update` skips silently when version is unchanged per
`feedback_global_tool_version_bump`). Update verified:

```
Tool 'skyomega.drhook.mcp' was successfully updated from version '1.8.0' to version '1.8.1'.
drhook-mcp 1.8.1+ca51f033268172bd2cbdec53570129ce45b5dac1
```

## Scope / next

ADR-006 Phase 3 status:

- [x] Substrate gaps (probes 31-40, findings 40-49) — done.
- [x] libdbgshim bundling (finding 50) — done.
- [x] **SteppingSessionManager rewrite + global-tool ship** (this finding) — done.
- [ ] Per-MCP-tool regression suite — exercise the new tool end-to-end through the MCP wire.
- [ ] Netcoredbg retirement — extract `ProcessAttacher` + `StackInspector` to DrHook.Engine,
  retire `src/DrHook/` (the DAP/netcoredbg code goes with it).
- [ ] Polish: `drhook_step_test`, conditional breakpoints, real metrics merge, multi-dim arrays,
  generic-type-parameter naming, Launch stdout/stderr capture.

## References

- Old impl: `src/DrHook/Stepping/SteppingSessionManager.cs` (1173 LOC, DAP/netcoredbg) — still
  present but unused by the MCP tool surface as of this slice.
- New impl: `src/DrHook.Mcp/EngineSteppingSession.cs` (~500 LOC, BCL-only).
- Wiring: `src/DrHook.Mcp/DrHookTools.cs`, `Program.cs`, `DrHook.Mcp.csproj`,
  `Directory.Build.props`.
- Findings consumed: 40 (AsyncBreak), 41 (registry), 42 (Launch + Terminate-on-dispose),
  43 (persistent ex filter), 44/45/46/47/48/49 (object inspection), 50 (libdbgshim bundling).
- Memory: `feedback_global_tool_version_bump` (always bump before update), `feedback_no_deploy_during_long_running_process`
  (Mercury MCP was left untouched in this install — only DrHook updated, no Mercury .dll lazy-load risk).
- Mercury session 2026-05-22 observation `drhook-mcp-engine-switchover`
