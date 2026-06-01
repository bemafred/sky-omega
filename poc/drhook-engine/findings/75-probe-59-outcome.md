# Finding 75 — Probe 59: own-spawn stdio isolation + RegisterForRuntimeStartup (ADR-011 D2)

**Date:** 2026-06-01 · **Platform:** macOS-arm64, .NET 10.0.0 · **Result:** PASS(0)

## Hypothesis

[ADR-011](../../../docs/adrs/drhook/ADR-011-lifecycle-console-dashboard.md) §D2: a launched (Owned) debuggee inherits the MCP server's stdin/stdout/stderr because dbgshim's `CreateProcessForLaunch` takes no stdio handles (`DbgShim.cs:237`), so a debugged `Console.WriteLine` corrupts the MCP JSON-RPC stdout channel. Proposed fix (Option B): DrHook **owns process creation** — `posix_spawn` the target SUSPENDED with stdout/stderr redirected to DrHook-owned pipes, `RegisterForRuntimeStartup`, then `SIGCONT` — replacing `CreateProcessForLaunch` + `ResumeProcess`.

The one unknown that gated Option B: **does `RegisterForRuntimeStartup` fire for a process we spawned ourselves (not via `CreateProcessForLaunch`)?** On macOS the suspend/startup-coordination mechanics could have depended on dbgshim's own process-creation path.

## Method

`59-spawn-stdio-smoke.cs` + `59-spawn-target.cs`. Two independent spawns (one unknown each — the first draft compounded them, corrected):

- **Part 1 — redirection control (no debugger):** `posix_spawn` the target with `POSIX_SPAWN_START_SUSPENDED` and `posix_spawn_file_actions_adddup2(writeFd → 1, 2)`, `SIGCONT`, let it run free; expect the target's marker IN the DrHook pipe (not the probe's stdout).
- **Part 2 — debugger:** same suspended+redirected spawn, then `RegisterForRuntimeStartup(pid, callback, …)` while still suspended, `SIGCONT`; expect the startup callback to fire with a non-null `ICorDebug` and `hr=0`. (Suspend-held checkpoint first: no target output before `SIGCONT`.)

## Result — PASS(0)

- **Redirection works.** Part-1 pipe captured `"PROBE59_TARGET_STDOUT pid=… \nPROBE59_TARGET_STDERR\nPROBE59_TARGET_DONE\n"` — both stdout and stderr isolated to the DrHook pipe, nothing leaked to the probe's own stdout.
- **`RegisterForRuntimeStartup` fires on our own suspended spawn.** Part-2 callback fired with `pCordb` non-null, `hr=0x00000000`; suspend held (no pre-`SIGCONT` output).

**Option B is viable on macOS-arm64.**

## Notes / gotchas (for the implementation)

- `POSIX_SPAWN_START_SUSPENDED` = `0x0080`, `SIGCONT` = `19` (Darwin). The process is stopped at creation; `SIGCONT` releases it after registration — so the CLR cannot initialize before `RegisterForRuntimeStartup` is armed (the property the suspend exists to guarantee).
- **`Environment.ProcessPath` is NOT the `dotnet` muxer** for a file-based app — it is the app's own apphost. `posix_spawn` does not search PATH, so launch the target by **full path**: prefer the build's native apphost (`X` for `X.dll`), else `DOTNET_HOST_PATH` + `exec`. (First draft re-spawned the probe itself — a useful confirmation that redirection worked, just on the wrong child.)
- After the callback, the existing `DebugSession.FromCordbg` flow (`DebugActiveProcess` → `SetManagedHandler` → continue-loop) is unchanged — it consumes the same `IUnknown*` the callback delivers regardless of how the process was created.
- POSIX-specific. Windows needs `CreateProcess(CREATE_SUSPENDED)` + `STARTUPINFO` redirected handles + `ResumeThread` — actually simpler there (native stdio redirection). macOS-first matches the substrate's current platform (ADR-007 Phase 9 open).

## Implication

D2 implementable: a POSIX launch path in the substrate that owns the spawn with redirected stdout/stderr, drains the child's output on a DrHook thread (discard initially; ADR-011 D3 surfaces it via the event sink later), and feeds the callback's `IUnknown*` into the unchanged `FromCordbg` flow.
