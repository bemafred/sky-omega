# Finding 86 — A DrHook debugger process is locked to ONE runtime version; mixing net10/net11 targets fails with 0x80131C3C

**Date:** 2026-06-24 (root-caused 2026-06-25)
**Status:** Root cause **PROVEN**; **near-term fix IMPLEMENTED + validated** (the runtime-lock guard — see
below); long-term per-version isolation remains. Operator workaround documented.
(Filed first as "single-file launch/attach fails" — that was the *symptom*, corrected here.)

## How it first looked (the misdiagnosis)

Through the long-lived `drhook-mcp` server, launching *and* attaching to a managed single-file
(net10.0) apphost failed with `0x80131C3C` (= **`CORDBG_E_DEBUG_COMPONENT_MISSING`**), while the same
`DebugSession.Launch` worked from a fresh `dotnet run` probe. It looked single-file-specific. **It is
not** — single-file was just a net10.0 target debugged *after* net11.0 targets.

## Root cause (proven)

**A debugger process can host the debug components (`mscordbi` + the DAC `libmscordaccore`) of only ONE
CLR runtime version.** dbgshim loads the `mscordbi` matching the *target's* runtime version into the
debugger process; once loaded it is **process-global and persists across `DebugSession.Dispose`**. A
later target on a *different* runtime version needs a different `mscordbi`, which cannot be loaded
alongside the first → `CORDBG_E_DEBUG_COMPONENT_MISSING`.

**Evidence:**
- `lsof` on the `drhook-mcp` process: it runs on its own **10.0.9** runtime, but has the
  **11.0-preview** `libmscordbi` + `libmscordaccore` + `libcoreclr` resident (pulled in when it debugged
  net11 file-based apps earlier this session) and **no 10.0 `mscordbi`**. The 11.0 lock **persists**
  after those sessions `Dispose`d and after every failed net10 attempt.
- **Controlled reproduction** (one process, two versions in sequence — `scratch/mixver.cs`):
  `Launch(net11)` → **OK** (first stop `EntryModuleLoaded` via the hold-gate, *no* `Debugger.Break`);
  then `Launch(net10)` → **FAILED 0x80131C3C**. One version per process always works.
- **Not** same-version-host/target (a tempting but wrong hypothesis): the net11 host debugged a net11
  target fine as step 1. It is **mixing** versions in one debugger process that fails.

## Why it bit this repo

The substrate targets **net10.0**, but a file-based app (`dotnet run x.cs`) under the **SDK 11 preview**
builds for **net11.0**. So a session that debugs a file-based app (net11) locks the debugger to net11;
any net10 target afterward (a single-file app, a net10 project) fails — and vice versa. The engine
probes always worked because each is a *fresh* process debugging *one* version.

## Operator workaround

- Keep every target in one debugging session on **one** runtime version.
- To debug **net10** targets, pin file-based probe apps to net10.0 (`#:property TargetFramework=net10.0`,
  or use a net10 project) so nothing pulls in net11.
- **Reconnect (restart) the MCP whenever you switch the target runtime version.**

## Substrate fix

1. **Near-term — fail clearly, not cryptically. ✅ DONE (2026-06-25).** DrHook now detects a target's
   runtime major from its `runtimeconfig.json` *before* dbgshim spawns/attaches (`RuntimeConfig`,
   unit-tested), records the first-debugged major process-globally (`DebugSession._processRuntimeMajor`,
   set after the first successful attach), and `GuardRuntimeLock` refuses a mismatched launch/attach up
   front with an actionable `InvalidOperationException` ("locked to .NET X … reconnect, or pin to netX.0")
   — **no opaque `0x80131C3C`, no leaked suspended spawn**. The locked runtime is surfaced in the MCP
   launch/attach response (`lockedRuntime`) and via `DebugSession.ProcessRuntimeMajor`. Validated: 5
   `RuntimeConfigTests` + a two-version probe (Launch net11 → Launch net10 now yields the clear refusal,
   no leak); full `DrHook.Engine.Tests` **135/135**.
2. **Longer-term — per-version isolation (still open).** The `mscordbi`/DAC process-global constraint is
   an ICorDebug platform reality; truly debugging mixed versions from one server needs a helper process
   per runtime version (or reliable unload+reload of the debug components — note `dlclose` of `mscordbi`
   did **not** reset the state here). The guard turns this from a cryptic failure into a clear "reconnect".

## Living with the .NET 11 preview (the trigger)

The mix happens because the substrate targets **net10.0** but `dotnet run x.cs` under the SDK 11 preview
builds **net11.0**. Pin the SDK with a repo-root `global.json` (`{ "sdk": { "version": "10.0.100",
"rollForward": "latestMinor" } }`) so file-based apps default to net10 again; the preview stays installed
for deliberate net11 testing (override/remove the `global.json`). Per-target control: `#:property
TargetFramework=net10.0` for a single file. (Verified: both pin file-based apps back to `"tfm":"net10.0"`.)

## References

- The fix: `src/DrHook.Engine/RuntimeConfig.cs` (pre-flight detection),
  `src/DrHook.Engine/DebugSession.cs` (`_processRuntimeMajor`, `GuardRuntimeLock`, `ProcessRuntimeMajor`),
  `src/DrHook.Mcp/EngineSteppingSession.cs` (`lockedRuntime`), `tests/DrHook.Engine.Tests/RuntimeConfigTests.cs`.
- `src/DrHook.Engine/Interop/DbgShim.cs` (`CreateCordbForProcess`, `LaunchWithDebuggerPosix`, `Resolve`).
- Findings 82–85 (single-file *symbol* work — engine-validated, unaffected; "single-file" was a symptom).
