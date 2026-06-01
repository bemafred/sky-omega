# Finding 76 — Probe 60: file-driven breakpoint module resolution (ADR-011 D3 follow-up)

**Date:** 2026-06-01 · **Platform:** macOS-arm64, .NET 10.0.0 · **Result:** PASS(0)

## Hypothesis

The [ADR-011](../../../docs/adrs/drhook/ADR-011-lifecycle-console-dashboard.md) §D3 live smoke surfaced a blocker: `drhook_launch` of `Launch.dll` with a breakpoint at `Program.cs:16` failed — *"Could not set breakpoint."* Cause: the MCP adapter derived the module from the **source-file stem** (`ModuleSubstrForFile` → `Path.GetFileNameWithoutExtension("Program.cs")` = "Program"), but the assembly is **`Launch`** (the csproj name). No loaded module contains "Program", so `SetBreakpointAtLine` returned 0. The stem heuristic only works when the source file name equals the assembly name (file-based apps, single-file projects); it breaks for any project where `Program.cs` → `<ProjectName>.dll`.

Fix: a file-only `DebugSession.SetBreakpointAtLine(fileHint, line, policy)` overload that resolves the owning module by **which loaded module's Portable PDB actually references the file** — iterate `EnumerateModules()`, try each module's `SymbolReader.TryFindLine(fileHint, line)`, bind on the first that succeeds. (The "richer resolution… is a polish item" the removed heuristic's comment had flagged.) The adapter's three call sites switch to this overload; the module-substring overload is kept for probes that pass an explicit module.

## Method

`60-file-driven-resolution-smoke.cs` — mirrors probe 58's logpoint+sink mechanism via the own-spawn `DebugSession.Launch` (the same path the MCP `LaunchAsync` uses), against the **built** `Launch.dll` (poc `33-launch-target`: `Program.cs` → `Launch`). After the `Debugger.Break` setup stop, arm a `Suspend.None` logpoint (`"i={i} v={v}"`) at the `PROBE_BREAK` line (`Program.cs:16`) via the **file-only** overload — passing only the `Program.cs` path + line, **no module hint** — then resume the 100-iteration loop and drain the sink. PASS requires `bpId != 0` (the fix resolved the module from the PDB) and well-formed rendered entries.

## Result — PASS(0)

```
bound      : logpoint id=1 — module resolved from PDB by file (the fix works)
resumed    : stop=ProcessExited  logs=100  first="i=0 v=0"  last="i=99 v=198"
```

- **The file-only overload bound `Program.cs:16` inside `Launch.dll` with no module hint** — exactly the case that returned 0 under the stem heuristic. Resolution walked the loaded modules and matched "Launch" by its PDB.
- The `Suspend.None` logpoint fired all **100** iterations (no surfaced `Break`), rendering `"i=0 v=0"` … `"i=99 v=198"` — every entry well-formed, `v == 2*i`, monotonic, no fault. The process then exited naturally (`ProcessExited`, 100 × 20ms ≈ 2s loop).

This also re-proves the drain_log substrate path (logpoint → `IDebugEventSink.OnLog` → 100 rendered entries) on the target that previously could not be reached through the MCP launch.

## Notes

- Both loop locals (`i`, `v`) resolved against the loop frame through the PDB-resolved module — confirming the file-driven module is the correct one for local-name lookup, not merely for the sequence-point bind.
- Backward compatible: the `SetBreakpointAtLine(moduleNameSubstring, fileHint, line, policy)` overload is unchanged; probes that pass an explicit module (17, 33, 58, …) are unaffected. Build green, 119/119 engine unit tests pass.
- Scope: this fixes the **module** resolution. A separate matching concern (file-based-app PDB document paths / leniency of `TryFindLine`'s `fileHint` contains-match) is out of scope here — Launch.csproj's PDB records the real `Program.cs` path, so contains-match succeeds once the right module is searched.

## Implication

The ADR-011 D3 drain_log live smoke is unblocked: `drhook_launch` of a normal project (source name ≠ assembly name) now binds source breakpoints / conditional breakpoints / logpoints. The remaining step is the live MCP re-run after the DrHook server reconnects onto this build.
