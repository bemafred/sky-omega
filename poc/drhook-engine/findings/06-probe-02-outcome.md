# Finding 06: Probe 02 Outcome — PASSED (Engineering)

**Status:**   Probe 02 PASSED. First Engineering-phase falsification of the Layer 3 Epistemics arc — the dbgshim attach flow and the IUnknown→ICorDebug QI are validated against a live .NET 10 process on macOS/ARM64.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/02-dbgshim-attach-probe.cs`
**Target:**   disposable .NET 10 sleeper (`Thread.Sleep(Timeout.Infinite)`), one CLR.

## Run result

```
runtime    : .NET 10.0.0
os-arch    : osx-arm64
target pid : 44107
dbgshim    : .../ms-dotnettools.csharp-2.130.5-darwin-arm64/.debugger/arm64/libdbgshim.dylib
clr-count  : 1  (retries=0)
coreclr    : /usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.dylib
version    : 00000004;0000ac4b;0000000107FF0000
IUnknown   : 0x1053ECB98
ICorDebug  : 0x1053ECB98  <-- CONFIRMED (QI returned S_OK)
vtable[0..3]: 0x143044AD0 0x143048890 0x1430488DC 0x143044D70
released   : ICorDebug->1, IUnknown->0
PROBE 02 PASSED
```

Every step of the corrected attach flow (finding 04) executed: `EnumerateCLRs` → `CreateVersionStringFromModule` → `CreateDebuggingInterfaceFromVersionEx(4, …)` → `QueryInterface(IID_ICorDebug)` → clean release.

## Build fix (the one compile error)

`Marshal.QueryInterface` in .NET 10 takes `in Guid`, not `ref Guid` — `ref iid` produced **CS9191** (promoted to error). Changed to `in iid`. This was review risk #3, settled by the build. The `IntPtr`→function-pointer casts (review risk #2) compiled cleanly — no `(void*)` intermediate needed. ImplicitUsings is on for file-based apps (review risk #1 was a non-issue; the defensive `using` lines are harmless).

## Falsification ladder — both branches exercised

- **As-is (no `DBGSHIM_PATH`)**: correctly falsified at the discovery step (exit 3), reporting the searched path + hint. Validated the discovery logic and the ladder.
- **With `DBGSHIM_PATH`**: full flow passed (exit 0).

## Findings the run produced

### 1. dbgshim is NOT in the .NET runtime anymore (post-.NET 6) — substrate-deployment-relevant

`libdbgshim.dylib` is absent from `Microsoft.NETCore.App/10.0.0/`. On this host it exists only in old `.NET 6.0.x` runtime dirs and bundled inside the VS Code C# extension. dbgshim was moved out of `dotnet/runtime` into `dotnet/diagnostics` after .NET 6 (corroborated by netcoredbg's CMake note: "After move of dbgshim from runtime to diagnostics…"). It now ships as the native-asset NuGet `Microsoft.Diagnostics.DbgShim[.<rid>]`.

**Engine implication:** DrHook.Engine has a **native-asset dependency** — `libdbgshim.{dylib,so,dll}` — that must be bundled (via the `Microsoft.Diagnostics.DbgShim` NuGet's `runtimes/<rid>/native/` payload) or located on the host. This is distinct from the *managed* `Microsoft.Diagnostics.NETCore.Client` admitted in ADR-009.

**ADR-009 angle (flag for Martin):** dbgshim is a *native shim*, not a managed framework. The cleanest framing is that it sits in the same category as `libcoreclr` itself — part of the .NET runtime/debugging substrate accessed via P/Invoke, the way Minerva P/Invokes hardware/OS libraries. The `Microsoft.Diagnostics.DbgShim` NuGet is just the redistribution vehicle for that native binary. Likely admissible by the same reasoning as the managed client (origin `dotnet/diagnostics`, extends a runtime primitive, versioned, and we redistribute rather than reimplement) — but axis 4 (replaceability) is weak: reimplementing dbgshim natively would be substantial (process inspection + version detection + mscordbi loading). Worth a short ADR-006 note that the engine carries a bundled native asset, and possibly an ADR-009 clarification that native runtime-substrate assets (libcoreclr-class) are a distinct admission category from managed NuGets.

### 2. A VS Code-bundled arm64 dbgshim debugged a .NET 10 target — version-independent shim confirmed

The dbgshim used (`csharp-2.130.5`) is not matched to .NET 10, yet it enumerated the .NET 10 CLR and created the debugging interface correctly. This is dbgshim working as designed: it inspects the target, detects the target's runtime, and loads the target's own `mscordbi`. Practical consequence: the engine can use a reasonably-current dbgshim against a range of target runtimes; exact version-matching isn't required for the attach flow.

### 3. The dbgshim "version string" is an opaque token, not a semver

`00000004;0000ac4b;0000000107FF0000` — semicolon-delimited hex, not `10.0.0`. First field `00000004` is the `CorDebugInterfaceVersion` (4 = `CorDebugVersion_4_0`); the third field is the coreclr module base address (`0x107FF0000`). It is a dbgshim-internal token that round-trips into `CreateDebuggingInterfaceFromVersionEx`. **Do not parse or display it as a human version.** It is produced by `CreateVersionStringFromModule` and consumed by `CreateDebuggingInterfaceFromVersionEx`; treat it as an opaque handle.

### 4. No macOS entitlement wall for the enumerate + create-interface flow

A plain `dotnet run` probe process inspected another process's diagnostic surface and created an `ICorDebug` interface with **no special codesigning / `task_for_pid` entitlement**. This was an open risk (macOS restricts cross-process inspection). It does NOT apply to the enumerate + create-interface flow. **Caveat:** `DebugActiveProcess` (probe 05) actually attaches and may hit entitlement/`ptrace`-class restrictions that this flow didn't — re-test at probe 05.

### 5. `EnumerateCLRs` succeeded first try (retries=0) for a settled process

The retry loop (finding 04) didn't fire — a long-running, fully-started target enumerates immediately. The retry is reserved for the freshly-started / mid-load race. Both the retry code and the happy path are now exercised (happy path here; the race path remains validated-by-reading until a probe catches a process mid-launch).

### 6. QI for ICorDebug returns the same pointer as IUnknown

`IUnknown` and `ICorDebug` came back as the same address (`0x1053ECB98`) — `ICorDebug` is the object's primary interface, so QI returns the same pointer AddRef'd. The two `Release` calls balanced to refcount 0. Confirms the review's refcount analysis.

## What probe 02 validates for the substrate

- The BCL + P/Invoke approach works: `NativeLibrary.Load`/`GetExport` + `delegate* unmanaged[Cdecl]` function pointers + `Marshal.QueryInterface`/`Release` on raw `IntPtr`. No netcoredbg, no `Microsoft.Diagnostics.NETCore.Client`.
- The corrected attach flow (finding 04) is correct end-to-end.
- UTF-16 marshalling is correct (module path and version token both round-tripped intact).
- Source-gen COM was NOT needed for this layer — raw `Marshal` sufficed, as the EEE scoping intended.

## Next — probe 03

`ICorDebug.Initialize()` + `Terminate()` on a typed wrapper. This is where source-generated COM (`[GeneratedComInterface] ICorDebug` + `StrategyBasedComWrappers.GetOrCreateObjectForComInstance`) gets its first validation, alongside the lifecycle. Probe 03 reuses probe 02's attach flow, then wraps the `IUnknown*` as a typed `ICorDebug` and calls `Initialize` → `Terminate`. Falsification surface: does source-gen COM accept dbgshim's pointer, and does the lifecycle behave per finding 03's contract.

## References

- Probe: `poc/drhook-engine/02-dbgshim-attach-probe.cs`
- Fixture: `poc/drhook-engine/fixtures/02-dbgshim-attach-osx-arm64-20260521T012027Z.txt`
- Findings 02 (dbgshim API), 03 (ICorDebug contract), 04 (corrected attach flow), 05 (COM interop decision)
- Mercury session 2026-05-21 finding `probe-02-passed`
