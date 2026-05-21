# ADR-006: DrHook.Engine — Native ICorDebug Replacement for netcoredbg

**Status:** Proposed — 2026-04-17 (substantially amended 2026-05-19 per [ADR-009 Substrate Dependency Policy](../ADR-009-substrate-dependency-policy.md); scope narrowed from "BCL-only across all three protocol layers" to "BCL-only Layer 3 ICorDebug only; Layers 1–2 use the substrate-admitted dependency `Microsoft.Diagnostics.NETCore.Client`"; **noted 2026-05-21** after probe 02 — the engine also relies on the native runtime-substrate asset `libdbgshim`, classified as platform per the [ADR-009 clarification](../ADR-009-substrate-dependency-policy.md), not as a managed dependency)

**Context:** Emergence during parser-fix session (2026-04-17) — raised as a substrate-independence follow-up after the netcoredbg func-eval limitation made clear that wrapping an external debugger is architecturally fragile. **Amended 2026-05-19** after [ADR-009](../ADR-009-substrate-dependency-policy.md) introduced the four-axis admission rule. Under that rule, `Microsoft.Diagnostics.NETCore.Client` passes all four axes and is admitted as a substrate-admitted dependency; netcoredbg fails on three of four axes and remains excluded. This amendment narrows the engine work to the layer that actually fails the policy — Layer 3 ICorDebug — rather than reimplementing layers that pass it. The substrate-independence claim now reads "zero spawns of netcoredbg" rather than "zero external diagnostic-protocol dependencies of any kind."

## Problem

DrHook today depends on two external artifacts:

1. **netcoredbg** — a separate-process DAP server. The func-eval deadlock on macOS/ARM64 (documented in the ADR-002 amendment, with the 2026-05-17 correction in Mercury obs `dh-010-correction` clarifying the deadlock is netcoredbg-localized rather than CoreCLR-architectural — vsdbg and Rider both honor conditional breakpoints on the same CoreCLR substrate) removed `drhook_step_eval` and watch-mode from DrHook's tool surface. netcoredbg's macOS/ARM64 maintenance has been dormant since 2023.
2. **Microsoft.Diagnostics.NETCore.Client** — a NuGet package, pulled in for EventPipe stack inspection.

Under [ADR-009](../ADR-009-substrate-dependency-policy.md)'s four-axis admission rule, these resolve asymmetrically:

| Dependency | Axis 1 (Origin) | Axis 2 (Shape) | Axis 3 (Stability) | Axis 4 (Replaceability) | Verdict |
|---|---|---|---|---|---|
| `Microsoft.Diagnostics.NETCore.Client` | `dotnet/diagnostics` — runtime-team-adjacent | Extends — Diagnostic IPC + EventPipe NetTrace access | `DOTNET_IPC_V1` wire magic + semver C# surface | Reimplementable from public spec in ~1–2 weeks | **Admitted** |
| netcoredbg | Samsung — third-party | Imposes — DAP server + ICorDebug consumer architecture | Loose semver; dormant macOS/ARM64 maintenance | Replaceable as the substrate work itself | **Excluded** (fails axes 1, 2, 3) |

The substrate-independence claim is paid for by replacing the **excluded** dependency. The **admitted** dependency is kept under axis-4 reservation — substrate independence is the destination; admission is the v1 simplification we can walk back if a substrate-driven reason surfaces later.

## Drivers

1. **Substrate independence via netcoredbg replacement.** This is what substrate independence actually means for DrHook under ADR-009: own the ICorDebug surface; let the runtime-team-adjacent diagnostic client handle the wire-protocol layers it's purpose-built for.
2. **Failure ownership where it counts.** The func-eval deadlock cost an entire workstream. With native ICorDebug, the same class of bug becomes ours to fix, not ours to work around. (The Diagnostic IPC layer doesn't have an analogous failure-ownership pressure — the protocol is stable, well-documented, and runtime-team-versioned.)
3. **Platform reach.** netcoredbg's macOS/ARM64 bitrot generalizes — any future platform shift exposes DrHook to dependency rot. Native ICorDebug decouples us from netcoredbg's timeline. The admitted NuGet's platform support tracks .NET runtime releases directly; the same pressure doesn't apply.
4. **Protocol surface is tractable.** ICorDebug is documented; the COM-via-P/Invoke pattern is well-established in .NET. Engineering effort is bounded.

## Decision (sketch)

Build **DrHook.Engine** as a new project under `src/DrHook.Engine/` that replaces netcoredbg with native ICorDebug interop. The public DrHook MCP surface (tool names, schemas) remains unchanged; the engine is swapped underneath.

### Substrate-admitted dependencies (per ADR-009)

**`Microsoft.Diagnostics.NETCore.Client`** is used as-is for two protocol layers:

- **Layer 1 (Diagnostic IPC client)** — `DiagnosticsClient` for process listing, `GetProcessInfo`, EventPipe session control.
- **Layer 2 (EventPipe NetTrace decoding)** — `EventPipeSession` + `EventPipeEventSource`.

Both layers are characterized in [`poc/drhook-engine/findings/01-ipc-protocol-survey.md`](../../../poc/drhook-engine/findings/01-ipc-protocol-survey.md) — axis-4 reference material documenting what the admitted dependency does under the hood, for v2 walk-back if a substrate-driven reason ever surfaces.

### Native runtime-substrate asset: `libdbgshim` (per ADR-009 clarification, 2026-05-21)

The engine P/Invokes `libdbgshim` — the native shim that bridges to `ICorDebug`. Per the [ADR-009 clarification (2026-05-21)](../ADR-009-substrate-dependency-policy.md), this is a **native runtime-substrate asset**, not a managed dependency: it is part of the .NET debugging substrate (the same category as `libcoreclr`), relied upon as platform and faced by none of the four axes. We have no choice but to rely on it; it is part of .NET itself.

**Deployment (probe 02 finding, 2026-05-21).** `libdbgshim` is not in the .NET runtime install for .NET 7+. The engine must obtain it via the `Microsoft.Diagnostics.DbgShim[.<rid>]` NuGet — which carries only the native binary under `runtimes/<rid>/native/` — and bundle it as a native asset, or locate a host-provided copy. Probe 02 validated the attach flow using a VS Code-bundled `libdbgshim.dylib`; the version-independent shim debugged a .NET 10 target without exact version-matching. The engine references no managed surface from that NuGet.

**Baseline adopted (2026-05-21, finding 11).** `Microsoft.Diagnostics.DbgShim` is the canonical dbgshim — current baseline `Microsoft.Diagnostics.DbgShim.osx-arm64` **9.0.661903** (the `9.0.x` tooling line is forward-compatible and independent of the runtime version it debugs; bump when a `10.0.x` ships). The borrowed VS Code copy is no longer the reference. Probes run against the official lib via `DBGSHIM_PATH`; the engine bundles the NuGet's `runtimes/<rid>/native/` payload.

### Substrate work — Layer 3: native ICorDebug interop

The substrate work concentrates on replacing netcoredbg with our own ICorDebug-via-P/Invoke implementation:

- **dbgshim discovery + attach.** `dbgshim` is the native shim that bridges to `ICorDebug`; provides the documented entry point for a target process. BCL + P/Invoke only. **Correction (probe 02, 2026-05-21):** `libdbgshim` is NOT in the .NET runtime install for .NET 7+ — it moved to `dotnet/diagnostics` and ships via the `Microsoft.Diagnostics.DbgShim[.<rid>]` native-asset NuGet. See the native runtime-substrate asset subsection below.
- **COM interop scaffolding.** `[ComImport]` interfaces for `ICorDebug` and its children; V-table thunks for the managed callback interface (`ICorDebugManagedCallback`); managed/unmanaged lifetime management. `ComWrappers` (.NET 5+) is the substrate-aligned interop surface; legacy COM RCWs are not used.
- **Process attach / launch.**
- **Breakpoint set / hit / clear** via `ICorDebugBreakpoint`.
- **Stack frames, variables** — direct field/stack inspection (no func-eval in v1; see Open Question 2).
- **Step over / into / out.**
- **Threading** — managed thread enumeration; cross-reference with mach `task_threads` for native/managed split (shared with Tier 2 continuous-observation work).

### Integration

DrHook.Core (the existing library) exposes the stepping and observation APIs. DrHook.Engine sits underneath, implementing the native ICorDebug interop. `DapClient` and `ProcessAttacher` become thin facades:

- `ProcessAttacher` delegates to `DiagnosticsClient` (the admitted NuGet) for process listing and metadata.
- `DapClient` (or its native successor) delegates to DrHook.Engine's ICorDebug client for stepping, breakpoints, variables.

The DAP outward surface — what MCP / IDE consumers see — can remain stable or be replaced; that's an integration decision separate from the engine.

## Consequences

**Gained:**
- Native control over the ICorDebug surface — the layer where netcoredbg's bugs live.
- Substrate-independence claim is paid where it matters: zero spawns of an externally-maintained debugger.
- Platform-portability of the substrate work tracks BCL + ICorDebug's documented C ABI, not netcoredbg's macOS/ARM64 maintenance cadence.
- Engine scope is ~60–70% smaller than the original (2026-04-17) framing that included native L1+L2 work. Engineering effort concentrates where it produces substrate-independence value.

**Lost / traded:**
- Significant engineering investment concentrated in Layer 3. ICorDebug via P/Invoke is not trivial — COM interop, V-table callbacks, managed/unmanaged lifetime management.
- Functional parity with netcoredbg's stepping/eval surface requires replicating a mature debugger's feature set on the layer we own.
- Risk of reproducing netcoredbg's bugs in a different form on Layer 3 before we reproduce its strengths.

**Explicitly out of scope for this ADR:**
- Native reimplementation of Layers 1 and 2 (admitted dependencies per ADR-009 — re-litigating that admission is an ADR-009 amendment, not a DrHook ADR amendment).
- Replacing the MCP SDK (ADR-003) — application-layer plumbing, separate concern.
- Supporting non-.NET runtimes.
- Changing the DAP protocol surface (the engine can still speak DAP outward if useful; the substrate-internal implementation is what changes).

## Continuous Observation Surface (added 2026-04-17; phase references updated 2026-05-19)

DrHook today is request/response: you ask for a snapshot, you get a snapshot. macOS Activity Monitor raises the adjacent question — can DrHook be a deep, continuously-refreshing observation surface for .NET processes?

**Scope decision (2026-04-17):** yes, within the .NET focus. DrHook becomes the reference .NET runtime-observation substrate, at a depth Activity Monitor can't reach (GC heap, assemblies, JIT state, per-thread managed/native split). **Non-.NET processes are explicitly out of scope** — others build adapters for their preferred stack (Python, JVM, Go, native) as they see fit. The naming principle holds: DrHook is *the .NET substrate*, and compromising that identity for generic process monitoring would weaken both it and any other stack-specific tool built in parallel.

### Capability tiers

**Tier 1 — BCL + admitted NuGet, portable, days of work.** Integrate at Phase 0 alongside scaffolding:
- Per-process CPU% via `Process.TotalProcessorTime` delta sampling
- Virtual/private/working memory breakdown
- Loaded module list (`Process.Modules` + EventPipe `AssemblyLoader` events via admitted NuGet for managed assemblies)
- Process tree (parent PID via `kinfo_proc` / `/proc`)
- Continuous-refresh surface: streaming MCP tool (or polling with configurable interval) that emits a snapshot sequence

**Tier 2 — macOS P/Invoke, documented APIs, weeks of work.** Integrate alongside Phase 1 ICorDebug work — the mach interop scaffolding is shared with native COM interop scaffolding:
- Per-thread enumeration with CPU time, state, and managed/native stack (mach `task_threads`, `thread_info`; cross-referenced with EventPipe stack samples)
- Disk I/O counters (`proc_pidinfo` PROC_PIDIOINFO or PROC_PIDTASKINFO)
- Open files / file descriptors (`proc_pidfdinfo`)

**Tier 3 — private APIs, fragile, out of scope.** Leave to the OS's own tool:
- Per-process network (macOS SystemStats.framework is undocumented and churns)
- Energy impact (IOReport)
- GPU usage per process (Metal diagnostics)

### Non-goals (explicit)

- **Non-.NET process monitoring** — by design. Adapters for other runtimes are a separate, parallel effort.
- **Competing with Activity Monitor generally** — DrHook is deep .NET, not broad OS.
- **Production telemetry / APM feature parity** — we cover observation; APM tools can consume DrHook's output if they want cross-correlation, but that's integration, not scope creep.

### Integration points

The continuous-observation surface is a new class of MCP tool alongside the existing `drhook_snapshot` — conceptually `drhook_watch` (streaming) or `drhook_monitor` (poll + snapshot series). The existing request/response tools stay as-is for point-in-time inspection.

## Open Questions

1. **dbgshim API surface and version tracking.** Original Open Question #1 ("staged vs clean-room") no longer applies — the staging is set by ADR-009 (admit L1+L2, native L3). What remains: dbgshim's documented API surface in `dotnet/runtime/docs/design/coreclr/debugging/` is partial; netcoredbg's source documents how to use it in practice. Epistemics-phase reading is required before drafting the first Layer 3 probe.

2. **Func-eval design (revised post-`dh-010-correction`).** Per [Mercury obs `dh-010-correction`](../../../poc/drhook-engine/findings/01-ipc-protocol-survey.md) (2026-05-17), the deadlock on macOS/ARM64 is netcoredbg-localized, not CoreCLR-architectural. vsdbg and Rider both honor conditional breakpoints on the same CoreCLR substrate — implying CoreCLR's ICorDebug eval surface is reachable. Three design choices remain for native Layer 3:
   - **(A) Implement full func-eval** — possible per vsdbg/Rider precedent; requires platform-specific workarounds (W^X handling, thread suspension on Unix without `SuspendThread`, ICorDebugEval::Abort handling per `dotnet/runtime#82422`).
   - **(B) Skip func-eval, rely on code-side workarounds** — `if`-statement-with-unconditional-breakpoint and `Debugger.Break()` patterns (validated in DrHook v1, 2026-04-06; more expressive than DAP conditional breakpoints).
   - **(C) Client-side conditional-breakpoint evaluation** — read locals via scopes path, evaluate condition in DrHook.Engine process. Sidesteps func-eval entirely; likely how vsdbg/Rider actually do it under the hood.

   Decision deferred to Phase 4 (after Phase 2 stepping primitives land and we have empirical contact with the ICorDebug eval surface).

3. **Windows support scope.** ICorDebug COM interop is platform-conditional (Windows uses standard COM activation; Unix uses `dbgshim`'s flat C entry points). Decide early whether Windows is a v1 target or deferred to v2. Working assumption: Unix-first; Windows in v2 once the COM interop idiom is established.

4. **Testing strategy.** Per [`docs/limits/drhook-testability.md`](../../limits/drhook-testability.md) — testability is a designed-in architectural constraint, not a deferred Open Question. ICorDebug interop is tested via (a) in-process synthetic targets — a minimal class library the test owns end-to-end — and (b) ICorDebug-callback fixture replays where the callback shape is deterministic. Live-process integration tests are smoke testing on top of layer tests, not the substrate's verification surface.

## Phases (draft, updated 2026-05-19 per ADR-009)

### Phase 0 — Project scaffolding + admitted-dependency wiring

- [ ] Create `src/DrHook.Engine/` project (BCL + `Microsoft.Diagnostics.NETCore.Client` per ADR-009)
- [ ] Wire `ProcessAttacher` to `DiagnosticsClient` via a thin facade (replaces direct NuGet usage in DrHook v1 for testability)
- [ ] Wire EventPipe stack inspection through the engine facade
- [ ] Tier 1 continuous-observation surface (no Layer 3 dependency)
- [ ] Regression: existing DrHook observation tools unchanged

### Phase 1 — ICorDebug interop scaffolding

- [ ] Epistemics-phase reading: dbgshim API surface + ICorDebug COM interface + netcoredbg's dbgshim usage as reference + modern C# COM interop patterns (`ComWrappers`, V-table thunks)
- [ ] First Emergence probe: process-attach via dbgshim, obtain `ICorDebug` pointer, release cleanly
- [ ] COM interop scaffolding for `ICorDebug` and its child interfaces
- [ ] Managed callback (`ICorDebugManagedCallback`) V-table implementation
- [ ] Process attach / detach with clean lifecycle

### Phase 2 — Stepping primitives

- [ ] Breakpoint set / hit / clear via `ICorDebugBreakpoint`
- [ ] Stack frames, variables — direct field/stack inspection (no func-eval in v1)
- [ ] Step over / into / out
- [ ] In-process synthetic test target (testability-first per `docs/limits/drhook-testability.md`)
- [ ] Tier 2 continuous-observation surface (mach interop shared with COM interop)
- [ ] Regression: stepping integration tests pass without netcoredbg

### Phase 3 — Engine switchover

- [ ] Default to DrHook.Engine; netcoredbg as opt-in fallback during convergence
- [ ] Measure failure-mode surface on macOS/ARM64 (the platform where netcoredbg breaks)
- [ ] Retire netcoredbg dependency once parity is verified

### Phase 4 — Func-eval (deferred; Open Question 2)

- [ ] Decision: (A) implement full func-eval / (B) skip / (C) client-side conditional evaluation
- [ ] Implementation if (A) or (C); documentation if (B)

## Validation

DrHook.Engine is successful when:

- `DrHook.Core` has **zero spawns of netcoredbg** — the substrate-independence claim per ADR-009.
- `Microsoft.Diagnostics.NETCore.Client` continues to ship as a substrate-admitted dependency per ADR-009's inaugural admission (renegotiation requires an ADR-009 amendment, not a DrHook ADR amendment).
- All existing DrHook MCP tools pass their integration tests against the engine.
- macOS/ARM64 stepping works; the func-eval decision (Phase 4) is recorded with rationale.
- Engine project line-count remains comparable to Mercury's and Minerva's substrate work — native ICorDebug interop is not a license for bloat.
- Testability per `docs/limits/drhook-testability.md` — in-process synthetic targets + ICorDebug-callback fixture replays as the primary verification surface.

## References

- [ADR-009 (cross-cutting)](../ADR-009-substrate-dependency-policy.md) — Substrate Dependency Policy — Four-Axis Admission Rule (Accepted 2026-05-19) — admits `Microsoft.Diagnostics.NETCore.Client`, excludes netcoredbg, sets the L1+L2 vs L3 scope boundary
- [ADR-002 (DrHook)](ADR-002-expression-evaluation.md) — Expression Evaluation (amended with netcoredbg func-eval findings)
- [ADR-003 (DrHook)](ADR-003-mcp-sdk-adoption.md) — MCP SDK Adoption
- [ADR-004 (DrHook)](ADR-004-step-run.md) — Process-Owning Stepping via DAP Launch
- [ADR-005 (DrHook)](ADR-005-expression-evaluation-diagnosis-correction.md) — Expression Evaluation Diagnosis Correction
- [`poc/drhook-engine/findings/01-ipc-protocol-survey.md`](../../../poc/drhook-engine/findings/01-ipc-protocol-survey.md) — characterization of admitted dependency's wire protocol (axis-4 reference material)
- [`docs/limits/drhook-testability.md`](../../limits/drhook-testability.md) — testability as designed-in constraint
- Mercury session 2026-04-06 obs dh-001, dh-006, dh-010 — original netcoredbg func-eval characterization
- Mercury session 2026-05-17 finding `dh-010-correction` — corrects the over-reach that framed func-eval as CoreCLR-architectural
- Mercury session 2026-05-17 decision `drhook-engine-poc-direction` — pre-policy directional commitment
- Mercury session 2026-05-19 decision `drhook-engine-scope-narrowed` — this amendment recorded
- `CLAUDE.md` — "Mercury has no external dependencies (BCL only)" — the original framing refined by ADR-009
- Minerva — BCL-only, hardware via P/Invoke (the reference pattern for substrate work)
