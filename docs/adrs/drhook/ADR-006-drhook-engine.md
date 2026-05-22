# ADR-006: DrHook.Engine — Native ICorDebug Replacement for netcoredbg

**Status:** Accepted — 2026-05-21

Emergence 2026-04-17 (Proposed). Scope narrowed 2026-05-19 per [ADR-009](../ADR-009-substrate-dependency-policy.md). **Moved to Accepted 2026-05-21** after the `poc/drhook-engine/` evidence pack (probes 02–06, findings 01–13) validated the entire interop approach end-to-end on macOS/ARM64, BCL-only. This ADR is rewritten from that evidence, not from design intent — every load-bearing decision below has a probe behind it.

## Context

DrHook v1 wraps **netcoredbg** (Samsung, MIT, separate-process DAP server). Its macOS/ARM64 func-eval deadlock — a netcoredbg-localized implementation gap, **not** a CoreCLR limitation (corrected 2026-05-17; vsdbg and Rider both honor conditional breakpoints on the same CoreCLR) — gutted `drhook_step_eval` and watch-mode. netcoredbg's macOS/ARM64 maintenance has been dormant since 2023. Wrapping an externally-maintained debugger means DrHook's reliability tracks theirs; that ceiling is reachable.

[ADR-009](../ADR-009-substrate-dependency-policy.md)'s four-axis admission rule resolved DrHook's two external artifacts asymmetrically:

| Dependency | Origin | Shape | Stability | Replaceability | Verdict |
|---|---|---|---|---|---|
| `Microsoft.Diagnostics.NETCore.Client` | `dotnet/diagnostics` (runtime-team-adjacent) | Extends — Diagnostic IPC + EventPipe access | `DOTNET_IPC_V1` magic + semver | Reimplementable from public spec | **Admitted** (managed dependency) |
| `libdbgshim` | `dotnet/diagnostics` (native asset) | Native shim to `ICorDebug` — part of the .NET debugging substrate | wire/ABI stable | n/a — platform | **Platform** (native runtime-substrate asset, faces no axes) |
| netcoredbg | Samsung (third-party) | Imposes DAP-server + ICorDebug-consumer architecture | dormant macOS/ARM64 | replaceable as the substrate work | **Excluded** (fails axes 1, 2, 3) |

The substrate-independence claim is paid by replacing the **excluded** dependency. The engine work is the **Layer 3 ICorDebug interop** — the layer that actually fails the policy.

## Drivers

1. **Substrate independence via netcoredbg replacement.** Own the ICorDebug surface; let the runtime-team-adjacent diagnostic client handle the wire-protocol layers it's purpose-built for.
2. **Failure ownership where it counts.** The func-eval deadlock cost a workstream. Native ICorDebug makes that class of bug ours to fix, not ours to work around.
3. **Platform reach.** netcoredbg's macOS/ARM64 bitrot generalizes to any future platform shift. Native interop tracks BCL + ICorDebug's documented ABI instead.
4. **Tractability — now proven.** The PoC demonstrated the full interop surface works BCL-only. The remaining work is bounded debugger-feature engineering, not open-ended research.

## Decision

Build **DrHook.Engine** under `src/DrHook.Engine/`, replacing netcoredbg with native ICorDebug interop. The DrHook MCP surface (tool names, schemas) is unchanged; the engine is swapped underneath.

### Dependency boundary (per ADR-009)

- **`Microsoft.Diagnostics.NETCore.Client` (managed, admitted)** — Diagnostic IPC client (`DiagnosticsClient`, `GetProcessInfo`, EventPipe session control) and EventPipe NetTrace decoding (`EventPipeSession`/`EventPipeEventSource`). Layers 1–2. Characterized at [`findings/01`](../../../poc/drhook-engine/findings/01-ipc-protocol-survey.md) for axis-4 walk-back.
- **`libdbgshim` (native, platform)** — the shim bridging to `ICorDebug`. Not in the .NET runtime install for .NET 7+ (it moved to `dotnet/diagnostics`); the engine **bundles** it from the `Microsoft.Diagnostics.DbgShim[.<rid>]` NuGet (`runtimes/<rid>/native/`). **Baseline: `Microsoft.Diagnostics.DbgShim.osx-arm64` 9.0.661903** (the 9.0.x tooling line is forward-compatible — it debugged a .NET 10 target; bump when a 10.0.x ships). [Finding 11](../../../poc/drhook-engine/findings/11-dbgshim-baseline.md).
- **netcoredbg** — removed. Zero spawns is the substrate-independence bar.

### The interop model (VALIDATED — the central design decision)

The PoC established an **asymmetry** between calling ICorDebug and being called back by it. This is the load-bearing engineering decision and it is evidence-grounded:

- **Consume direction (we call ICorDebug / ICorDebugController / ICorDebugProcess):** **source-generated COM** — `[GeneratedComInterface]` interfaces consumed as RCWs via `StrategyBasedComWrappers.GetOrCreateObjectForComInstance`. BCL (`System.Runtime.InteropServices.Marshalling`), `[PreserveSig] int` on every method for explicit HRESULT control. Validated: probes 02/03/05/06 called `Initialize`, `Terminate`, `SetManagedHandler`, `DebugActiveProcess`, `Continue`, `Detach` this way.
- **Receive direction (mscordbi calls our `ICorDebugManagedCallback`):** a **hand-rolled `[UnmanagedCallersOnly]` function-pointer vtable** — **NOT** a `[GeneratedComClass]` ComWrappers CCW. This corrects the pre-PoC assumption (the 2026-04-17/05-19 drafts said "ComWrappers is the substrate-aligned interop surface" — *that is false for the callback direction*).

  **Evidence (findings 09/10/12/13):** a `[GeneratedComClass]` CCW registered fine (`SetManagedHandler` → `S_OK`, probe 04) but the runtime **never delivered** a callback to it (probe 05 — zero callbacks across parked/live targets, 15s). A hand-rolled `[UnmanagedCallersOnly]` vtable **received `CreateProcess`** on mscordbi's event thread (probe 06). The native→managed transition on mscordbi's thread *works* (our thunk ran managed code there); the failure was specifically **ComWrappers object-CCW dispatch** (its `ComInterfaceDispatch.GetInstance` lookup + reference-tracking doesn't function in the debug-callback context). The raw thunk has no such lookup and sidesteps it.

  Construction: the engine builds the `ICorDebugManagedCallback`(+2/3/4) vtable from `[UnmanagedCallersOnly(CallConvs=[CallConvCdecl])]` static thunks (38 methods), in exact IDL order (a misordered slot crashes when the runtime calls it). A native COM object block holds the four vtable pointers; `QueryInterface` hands out the right sub-object per IID. For instance state, the thunk recovers `this` from the COM object block (generalizing probe 06's static-state approach).

### Attach flow (VALIDATED — corrected from desktop-.NET assumptions)

```
EnumerateCLRs(pid)   [RETRY: 100ms × 30; INVALID_HANDLE_VALUE = coreclr mid-load, retry]
    → pStringArray[0] is a coreclr MODULE PATH (not a version string)
CreateVersionStringFromModule(pid, modulePath, …)  → opaque version token
CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0 = 4, token, &pUnknown)
QueryInterface(IID_ICorDebug)  → typed ICorDebug
Initialize() → SetManagedHandler(vtable) → DebugActiveProcess(pid, FALSE, &pProcess)
    [process RUNS after attach — an explicit Continue here is CORDBG_E_SUPERFLOUS_CONTINUE]
… callbacks delivered on mscordbi's event thread; drive a continue-loop (CallbacksQueue pattern) …
Detach() → Terminate()
```

All strings UTF-16 (`WCHAR` = `char16_t` on the PAL). [Finding 04](../../../poc/drhook-engine/findings/04-netcoredbg-reference.md) corrected the flow; finding 02 had it partly wrong from the header alone.

**macOS attach needs no debug entitlement** — `DebugActiveProcess` from a plain `dotnet` build attached to another .NET process, no codesigning ([finding 13](../../../poc/drhook-engine/findings/13-probe-06-outcome.md); same-user targets; cross-user/SIP-protected untested). This removes the largest deployment unknown.

### Type mapping

Native ↔ .NET integer mapping is governed by the PAL's fixed-size typedefs ([finding 08](../../../poc/drhook-engine/findings/08-com-type-mapping.md)): `LONG`/`ULONG`/`DWORD`/`BOOL`/`HRESULT`/enums → `int`/`uint` (32-bit, locked by the PAL under LP64 — **never** `LONG` → C# `long`); `*_PTR`/`SIZE_T` → `nint`/`nuint`; `CORDB_ADDRESS`/`ULONG64` → `ulong`; `WCHAR` → `char`; interface/`WCHAR*`/`BYTE*` params → `nint`.

### Integration

DrHook.Core exposes stepping/observation APIs; DrHook.Engine implements the native ICorDebug interop beneath. `ProcessAttacher` delegates to `DiagnosticsClient` (admitted NuGet) for process metadata; the stepping path delegates to the engine's ICorDebug client. The DAP outward surface to MCP/IDE consumers can remain or be replaced — an integration decision separate from the engine.

## Consequences

**Gained (validated):**
- Native control over the ICorDebug surface; zero spawns of an externally-maintained debugger.
- **BCL-only callback layer confirmed viable** — no native C/C++ shim required; the `[UnmanagedCallersOnly]` vtable keeps the receive direction in managed code. Substrate independence holds for the engine's hardest layer.
- Attach with an ordinary build on macOS/ARM64 — no entitlement.
- Platform-portability tracks BCL + ICorDebug's documented ABI.

**Lost / traded:**
- Engineering investment concentrated in Layer 3 (COM interop, the 38-method vtable, lifetime management, the continue-loop).
- Functional parity with netcoredbg's mature feature set must be rebuilt on the layer we own.
- The callback vtable is hand-rolled (38 thunks in exact order) — a transcription-discipline surface; mitigated by fixture-replay testing.

**Out of scope:**
- Native reimplementation of Layers 1–2 (admitted dependencies; an ADR-009 amendment, not this ADR).
- Replacing the MCP SDK (ADR-003). Supporting non-.NET runtimes. Changing the DAP wire surface.

## Continuous Observation Surface (added 2026-04-17)

DrHook today is request/response. Activity Monitor raises the adjacent question: a deep, continuously-refreshing observation surface for .NET processes — GC heap, assemblies, JIT state, per-thread managed/native split. **In scope, .NET only.** Non-.NET processes are out of scope (others build adapters for their stack; the naming principle holds — DrHook is *the .NET substrate*).

- **Tier 1 (BCL + admitted NuGet):** per-process CPU% (`Process.TotalProcessorTime` deltas), memory breakdown, module list (`Process.Modules` + EventPipe `AssemblyLoader`), process tree (`kinfo_proc`/`/proc`), streaming/poll MCP surface (`drhook_watch`/`drhook_monitor`).
- **Tier 2 (macOS P/Invoke, shares the mach scaffolding with Layer 3):** per-thread CPU/state/native+managed stack (`task_threads`, `thread_info`), disk I/O (`proc_pidinfo`), open FDs (`proc_pidfdinfo`).
- **Tier 3 (private APIs):** per-process network, energy, GPU — out of scope, leave to the OS tool.

## Open Questions

1. ~~dbgshim API surface~~ **RESOLVED** by the PoC — the attach flow is characterized and validated (findings 02/04, probes 02–06).
2. **Func-eval design (deferred to Phase 4).** Per the dh-010 correction, the deadlock is netcoredbg-localized, not CoreCLR — vsdbg/Rider prove the eval surface is reachable. Three choices: **(A)** implement full func-eval (needs platform workarounds: W^X, Unix thread suspension, `ICorDebugEval::Abort` per `dotnet/runtime#82422`); **(B)** skip it, use code-side workarounds (`if`+unconditional-breakpoint, `Debugger.Break()` — validated in DrHook v1, more expressive than DAP conditions); **(C)** client-side conditional evaluation (read locals, evaluate in-process — likely how vsdbg/Rider do it). Decide after Phase 2 stepping primitives give empirical contact.
3. **Windows support.** ICorDebug COM interop is platform-conditional (Windows COM activation vs Unix dbgshim flat entry points). Working assumption: Unix-first; Windows in v2 once the idiom is established. (The `[UnmanagedCallersOnly]` vtable approach should port; Windows uses the same vtable model.)
4. **Continue-loop shape.** Each delivered callback synchronizes (stops) the process; the engine must `Continue` to proceed, per the netcoredbg `CallbacksQueue` pattern (finding 12). The PoC validated single-event delivery + detach; the production continue-loop (queue events, continue, handle stop-state) is Phase 2 engineering.

## Phases

### Phase 0 — PoC validation ✅ COMPLETE (2026-05-18 → 2026-05-21)
`poc/drhook-engine/`, findings 01–13. Probe 02 (dbgshim attach + QI), 03 (source-gen COM consume + lifecycle), 04 (callback V-table registration), 05 (attach + detach, no entitlement; CCW delivery blocked), 06 (`[UnmanagedCallersOnly]` vtable receives callbacks — A2). The interop model is proven.

### Phase 1 — Engine project scaffolding ✅ COMPLETE (2026-05-21)
- [x] `src/DrHook.Engine/` project, in `SkyOmega.sln`, refs admitted `Microsoft.Diagnostics.NETCore.Client`. Builds warning-clean under `TreatWarningsAsErrors`. (`libdbgshim` native-asset *bundling* deferred — `DbgShim.cs` uses a resilient resolver: `DBGSHIM_PATH` → app base → runtime dir → NuGet cache.)
- [x] dbgshim load + corrected attach flow — `Interop/DbgShim.cs`
- [x] source-gen COM RCW interfaces — `Interop/CorDebug.cs` (`ICorDebug`, `ICorDebugController`; `ICorDebugProcess` deferred to Phase 2 when its methods are needed)
- [x] `ICorDebugManagedCallback`(+2/3/4) `[UnmanagedCallersOnly]` vtable with **instance dispatch** (GCHandle recovery from the COM block) — `Interop/ManagedCallbackHost.cs`. Validated end-to-end via a `DebugSession` smoke test: attach → `CreateProcess` callback delivered → detach.
- [x] observation facade — `Observation/ProcessInspector.cs` wraps the admitted `DiagnosticsClient` (Layer-1 .NET-process discovery via `GetPublishedProcesses`) + BCL `System.Diagnostics.Process` (Tier-1 metrics: working set, threads, CPU time, native module count). 3 self-inspection tests. Managed-assembly enumeration via EventPipe (Layer 2 / TraceEvent — a separate ADR-009 admission) is a follow-up.
- [x] in-process layer tests (testability-first, primary surface per `docs/limits/drhook-testability.md`) — `tests/DrHook.Engine.Tests` drives the callback vtable *in-process*, the test playing the native caller: QI multi-interface dispatch, V-table slot layout (`CreateProcess`@9, `FunctionRemapOpportunity`@v2-3), and GCHandle instance recovery (two hosts route to their own sinks). 5 tests, deterministic, no debuggee/dbgshim. (Live-debuggee integration smoke + recorded callback-fixture replay remain as on-top follow-ups.)

`DebugSession`/`IDebugEventSink` (`DebugSession.cs`, `IDebugEventSink.cs`) compose the validated pieces into the attach/detach lifecycle.

### Phase 2 — Stepping primitives
- [x] continue-loop / CallbacksQueue (finding 12 pattern) — `CallbackPump.cs`. The callback thunks enqueue + return S_OK on mscordbi's event thread; a background worker drains, surfaces each event to the user sink, then `Continue`s for the next. `DebugSession.Attach` constructs the pump, hands it to `ManagedCallbackHost` as the sink, and calls `pump.Start(() => controller.Continue(0))` once the controller exists; `Dispose` joins the worker before Detach (it drives `Continue`). 4 in-process drain tests (backlog-before-start, post-start steady state, one-Continue-per-event, late-event-after-dispose dropped). The tests surfaced + fixed two production defects: non-idempotent `Dispose` and an `OnEvent` swallow clause too narrow for the disposed-queue shutdown state. **Live-validated by probe 07** (`07-continue-loop-smoke.cs` + `07-target.cs`, finding 14): against a target flooding managed events, the assembled engine drained **591 callbacks in 4 s** (one `Continue` per synchronized stop) — Phase 1's single-event ceiling (probe 05 saw 0 from a parked target) is gone.
- [x] **"stopping" event handling in the pump** — the keystone for breakpoints + stepping. Callbacks are classified (`CallbackKind`, host→pump via `IManagedCallbackSink`): informational events auto-continue as before; the three STOPPING callbacks (Breakpoint, StepComplete, Break) suppress the auto-Continue, leave the debuggee synchronized, and surface a `StopInfo`. `CallbackPump` parks the worker on a stop until the caller resumes; `DebugSession` exposes `WaitForStop(timeout)` / `Resume()`. 4 new in-process tests (stop suppresses Continue until Resume; stops don't leak into the informational firehose; ExitProcess wakes a waiter; no-stop returns null). **Live-validated by probe 09** (`09-stopping-model-smoke.cs` + `09-break-target.cs`, finding 16): a `Debugger.Break()` target stops on each Break, stays frozen while held (0 stops in a 400 ms window — vs ~20 if auto-continued), and advances only on `Resume` — 5 controlled stops, 3/3 runs.
- [ ] breakpoints (`ICorDebugFunctionBreakpoint`), stepping (over/into/out), stack frames + variables (direct inspection, no func-eval v1) — produce stopping events that ride the model above; needs `ICorDebugModule`/`Function`/`Code`/`FunctionBreakpoint`/`Stepper`/`Frame`/`Value` + metadata resolution. Sub-increments:
  - [x] **4a navigation** — `RuntimeNavigation` walks process→app domains→assemblies→modules via **raw V-table calls (slot numbers only, no GUIDs)**; `DebugSession.EnumerateModules()` (valid at a stop). **Probe 10** (finding 17): 8 modules listed at a Break stop incl. CoreLib + the target's own module, 2/2. Slots validated: EnumerateAppDomains@26, EnumerateAssemblies@14, EnumerateModules@5, GetName@6, Enum.Next@7.
  - [x] **4b metadata** — `MetadataResolver` via `ICorDebugModule.GetMetaDataInterface`@14 (passing `IID_IMetaDataImport`, the one unavoidable GUID — an *input*, fails cleanly if wrong) → `IMetaDataImport`; `FindTypeDefByName`@9 + `EnumMethodsWithName`@19 → `mdMethodDef`; `CloseEnum`@3. `DebugSession.ResolveMethodToken(moduleSubstr, type, method)`. **Probe 11** (finding 18): `Worker.Tick` → `0x06000003` (table 0x06), bogus name → 0, 2/2. (Probe caught a target-side race — single startup `Debugger.Break()` raced attach; fixed by waiting for `Debugger.IsAttached`.)
  - [x] **4c breakpoint** — `Breakpoints.TryCreate`: `ICorDebugModule.GetFunctionFromToken`@9 → `ICorDebugFunction.CreateBreakpoint`@8 (function entry) → `ICorDebugBreakpoint.Activate`@3. `DebugSession.SetBreakpoint(moduleSubstr, type, method)` composes 4a+4b+4c; (module, function, breakpoint) pointers kept alive, released on Dispose. **Probe 12** (finding 19): breakpoint on `Worker.Tick` hit **5×**, frozen between hits, advancing only on `Resume` — the hit rides the stopping model as `StopReason.Breakpoint`; hit path 9/9. (Caught an intermittent teardown exit-race — mscordbi's `ExitProcessWorkItem` racing detach when the target is killed; finding-14 class, mitigated by kill-first teardown; [`docs/limits/drhook-detach-exit-race.md`](../../limits/drhook-detach-exit-race.md). Deactivating breakpoints before detach was tried and falsified.)
  - [x] **stepping (over/into/out)** — `Stepping.Arm`: `ICorDebugThread.CreateStepper`@12 → `ICorDebugStepper.Step`@7 (into/over) / `StepOut`@9. The pump's resume generalized to `ResumeKind {Continue,StepInto,StepOver,StepOut}`; the resume handler arms the stepper on the captured `_stopThread` then Continues. `DebugSession.StepInto/StepOver/StepOut`. Completion rides the stopping model as `StopReason.Step` (no new callback wiring). **Probe 13** (finding 20): stop at a breakpoint, `StepOver` 3×, each a `StopReason.Step`, 3/3. v1 is IL-granularity (source-line stepping needs PDB ranges — later).
  - **Breakpoints + stepping complete.** Stack frames + variables (`ICorDebugThread`→`EnumerateFrames`→`ILFrame`→`ICorDebugValue`) is the remaining Phase 2 inspection piece — makes the stop/step *location* observable.
- [x] **clean-detach quiescence** — `DebugSession.Quiesce()` calls `ICorDebugController::Stop` before `Detach`, synchronizing the process so no callback flush is in flight when the shim is torn down. **Live-validated by probe 08** (`08-quiesce-detach-smoke.cs`, finding 15): disposing while the target is still flooding — the probe-07 crash scenario — detaches cleanly 3/3 and leaves the target running. Minimal `Stop` sufficed; the proposed `SetAllThreadsDebugState`/`HasQueuedCallbacks` escalation was not needed. Resolves [`docs/limits/drhook-clean-detach.md`](../../limits/drhook-clean-detach.md) (finding 14).
- [ ] regression: DrHook stepping tools pass without netcoredbg

### Phase 3 — Switchover
- [ ] default to DrHook.Engine; netcoredbg opt-in fallback during convergence; retire once parity verified

### Phase 4 — Func-eval (Open Question 2)
- [ ] decide A/B/C; implement or document

## Validation

This ADR moved to Accepted because the PoC validated the approach. DrHook.Engine is **complete** when:
- `DrHook.Core` has zero spawns of netcoredbg (the substrate-independence bar).
- `Microsoft.Diagnostics.NETCore.Client` (managed) + `libdbgshim` (native asset) remain the only diagnostics dependencies, both per ADR-009.
- All DrHook MCP tools pass integration tests against the engine.
- macOS/ARM64 stepping works end-to-end; func-eval decision recorded.
- Testability per [`docs/limits/drhook-testability.md`](../../limits/drhook-testability.md): in-process synthetic targets + callback-fixture replays as the primary surface.
- Engine line-count comparable to Mercury/Minerva substrate work — native interop is not a license for bloat.

## References

- [ADR-009](../ADR-009-substrate-dependency-policy.md) — Substrate Dependency Policy (admits the managed client; clarifies `libdbgshim` as native platform asset)
- PoC evidence pack — [`poc/drhook-engine/`](../../../poc/drhook-engine/) findings 01–13:
  - 01 IPC protocol · 02 dbgshim API · 03 ICorDebug contract · 04 netcoredbg attach flow · 05 COM interop decision · 06 probe-02 · 07 probe-03 · 08 type mapping · 09 probe-04 · 10 probe-05 · 11 dbgshim baseline · 12 netcoredbg event loop · 13 probe-06 (A2)
- [ADR-002](ADR-002-expression-evaluation.md)/[004](ADR-004-step-run.md)/[005](ADR-005-expression-evaluation-diagnosis-correction.md) (DrHook) — eval, stepping, the func-eval diagnosis
- [`docs/limits/drhook-testability.md`](../../limits/drhook-testability.md) — testability designed-in
- Mercury session 2026-05-21 — `probe-06-passed-a2`, `layer3-interop-proven-bcl-only`, `macos-attach-no-entitlement`
- Minerva — BCL-only, hardware via P/Invoke (the substrate reference pattern)
