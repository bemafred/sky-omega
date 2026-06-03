# Finding 81 — Launch env override: silent MCP→substrate drop closed (Owned-only; POSIX implemented, Windows Phase 9)

**Date:** 2026-06-03
**Probe:** `63-launch-env-smoke.cs` + `63-env-target/`
**Platform:** macOS-arm64, .NET 10.0.0
**Status:** PASS. 62b regression PASS.

## The gap

`drhook_launch` advertised an `env` parameter (KEY=VALUE), parsed it into a dictionary, and passed it to `EngineSteppingSession.LaunchAsync` — which then **silently dropped it**: it never reached `DebugSession.Launch` (which had no `env` parameter), per the in-code comment *"env override is not yet plumbed through DebugSession.Launch."* The launched child inherited the MCP server's environment; a per-launch override had **no effect, with no signal** on the tool surface. A surface/substrate mismatch (advertised > delivered) — the conformance-vs-dogfood class.

## Owned vs Borrowed

Env is **Owned-only by nature** — a process's environment is fixed at its own spawn, so a Borrowed (attached) target cannot take an override. The MCP correctly reflects this: `drhook_launch` has `env`, `drhook_attach` does not.

## Implementation

Threaded `env` (`IReadOnlyDictionary<string,string>?`) through `DebugSession.Launch` → `DbgShim.LaunchWithDebuggerPosix` → `PosixSpawn.SpawnSuspendedRedirected`. The spawn already built the child's `envp` explicitly (`CurrentEnv()`); replaced with `BuildChildEnv(overrides)` = the parent environment with overrides applied on top (**inherit-plus-override**, ordinal keys since POSIX env is case-sensitive). `posix_spawn`'s `envp` delivers it. `EngineSteppingSession` now passes `env`; the tool description is sharpened (merge semantics, Owned-only).

**Windows:** an explicit `PlatformNotSupportedException` if env overrides are requested — the `CreateProcessForLaunch` env block lands with the Phase 9 Windows launch hardening. Explicit failure, **not** a new silent drop.

## Validation

Probe 63: launch `EnvTarget` with `DRHOOK_PROBE_ENV=override-<pid>`; the child wrote `DRHOOK_PROBE_ENV=override-<pid>` (override applied) **and** `HOME_PRESENT=True` (inherited survived) — inherit-plus-override confirmed. 62b regression PASS (shared spawn path unaffected).

## Files
- `src/DrHook.Engine/Interop/PosixSpawn.cs` — `BuildChildEnv` + `env` param
- `src/DrHook.Engine/Interop/DbgShim.cs` — `LaunchWithDebuggerPosix` `env` param
- `src/DrHook.Engine/DebugSession.cs` — `Launch` `env` param + Windows explicit-defer
- `src/DrHook.Mcp/EngineSteppingSession.cs` — pass `env`
- `src/DrHook.Mcp/DrHookTools.cs` — description
- `poc/drhook-engine/63-launch-env-smoke.cs` + `63-env-target/`
