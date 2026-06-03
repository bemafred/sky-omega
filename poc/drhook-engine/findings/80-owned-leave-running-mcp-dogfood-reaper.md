# Finding 80 — F-010-2 MCP integration: drhook_detach (Owned) wired + dogfooded; reaper closes a zombie gap dogfooding surfaced

**Date:** 2026-06-03
**Surface:** DrHook MCP (`drhook_launch` / `drhook_detach`), dogfooded live on macOS-arm64
**Status:** wired, dogfood-validated, reaper added + re-dogfooded clean.

## Wiring

`EngineSteppingSession.DetachAsync` now calls `DebugSession.DetachLeaveRunning()` for an Owned target (was an honest "not yet available" refusal), following the `KillAsync`/`Abandon` pattern: detach-leave-running first (sets `_disposed`), then `CleanupSession`'s `Dispose` is the idempotent no-op + state reset. The `drhook_detach` tool description is updated.

## Dogfood (the lost-night loop, closed on the real surface)

`drhook_launch LeaveRunning.dll` → stop at breakpoint `Program.cs:29` (Owned, `beat=1`) → `drhook_detach` → `mode=owned, status=detached` (no refusal) → target **ran to natural completion (`beat 1 → 600`)** instead of hanging at 1. The exact dogfooding loop that failed the prior session now works end-to-end through the MCP tools.

## What dogfooding surfaced that probes could not — zombie-on-exit

The launched target is a `posix_spawn` child of the long-lived `drhook-mcp` server. `DetachLeaveRunning` detaches the ICorDebug debugger but **not** the OS parent-child link; the target was acquired via `Process.GetProcessById` (not `Process.Start`), so the runtime's reaper does not track it. When the detached target exited under the live server, nothing `waitpid()`'d it → it became a zombie (`Z+ <defunct>`) under the server. Probes 62/62b structurally **could not** surface this: the probe process exits right after detach, reparenting the child to launchd (PPID=1) and reaping it. The long-lived server is the differentiator — the loop-closing value of dogfooding.

## Fix — disown-and-reap

- `PosixSignals.ReapChild(pid)` — a blocking `waitpid` reaper (Unix-only; retries `EINTR`; returns on reap or `ECHILD`).
- `DetachLeaveRunning` spawns a background thread (`drhook-reaper-<pid>`, 256 KB stack, `IsBackground`) that `waitpid()`s the pid and collects the corpse whenever the target exits (immediately if already gone). One thread per live detached target — self-limiting, dies with the process; launchd reaps the orphan if the debugger exits first.
- Cannot portably reparent a *living* child to launchd; reaping the corpse is the fix. No double-fork (ICorDebug needs the direct child for `RegisterForRuntimeStartup`).
- Corrected the reparent claims (engine doc + MCP disposition message): the target stays a child until exit, reaped then.

## Re-dogfood (post-fix) — the contrast

Same flow; target ran to natural completion (`beat 600`) and exited → **REAPED (pid GONE)**, not the `Z+` zombie seen pre-fix. Probe regression: 62/62b 2× each PASS with the reaper spawning on every detach.

## Scope

Owned leave-running now validated end-to-end (substrate probes 62/62b + MCP dogfood + reaper). Still deferred: console-pipe survival of a `Console`-writing leave-running target (D4/PTY); cross-platform Windows/Linux (Phase 9 — the reaper + spawn are POSIX; Windows has no zombie model, handle-close suffices).

## Files
- `src/DrHook.Mcp/EngineSteppingSession.cs` — `DetachAsync` wired to `DetachLeaveRunning`
- `src/DrHook.Mcp/DrHookTools.cs` — `drhook_detach` description
- `src/DrHook.Engine/Interop/PosixSignals.cs` — `ReapChild` + `waitpid`
- `src/DrHook.Engine/DebugSession.cs` — reaper thread spawn in `DetachLeaveRunning`
