# DrHook.Engine PoC

A proof-of-concept for **native ICorDebug interop** — the substrate work that replaces netcoredbg in DrHook. Scope set by [ADR-009 Substrate Dependency Policy](../../docs/adrs/ADR-009-substrate-dependency-policy.md) (the four-axis admission rule) and [ADR-006 DrHook.Engine](../../docs/adrs/drhook/ADR-006-drhook-engine.md) (substantially amended 2026-05-19 to narrow scope to Layer 3).

This PoC validates substrate hypotheses for Layer 3 (ICorDebug). Layers 1 and 2 (Diagnostic IPC, EventPipe NetTrace) are substrate-admitted dependencies per ADR-009 — **out of PoC scope**. No engineering integrates back into sky-omega until each Emergence probe falsifies or confirms its hypothesis.

## Why this exists

Three substrates, one principle. Mercury and Minerva are BCL-only with platform P/Invoke; their substrate-independence claim is sharp. DrHook v1 wraps netcoredbg (Samsung, third-party, dormant macOS/ARM64 maintenance since 2023) plus uses `Microsoft.Diagnostics.NETCore.Client` (Microsoft, runtime-team-adjacent, runtime-versioned).

ADR-009 — the four-axis dependency admission rule — resolves these asymmetrically:

- `Microsoft.Diagnostics.NETCore.Client` passes all four axes (origin: `dotnet/diagnostics`, runtime-team-adjacent; shape: extends a runtime primitive; stability: wire-protocol versioned via `DOTNET_IPC_V1` magic + semver C# surface; replaceability: reimplementable from public spec in ~1–2 weeks). **Admitted.**
- netcoredbg fails three of four (origin: Samsung; shape: imposes a DAP-server-plus-ICorDebug-consumer architecture; stability: dormant maintenance, no protocol versioning beyond ad-hoc). **Excluded.**

The substrate work concentrates on what fails the policy: native ICorDebug interop, replacing netcoredbg's consumer pattern with one DrHook owns end-to-end.

## EEE staging

EEE methodology applied strictly to the Layer 3 substrate work:

- **Emergence** — surface unknown unknowns about the ICorDebug interop surface. Smallest falsifiable probes, single-file where possible.
- **Epistemics** — survey what dbgshim and ICorDebug document against what they actually do. Cross-check Microsoft specs and netcoredbg's usage (as reference, not template) against captured behavior.
- **Engineering** — only after Emergence and Epistemics produce evidence-grounded design grammar. Layer 3 becomes a substrate component in `src/DrHook.Engine/` only when the COM interop pattern, attach/detach lifecycle, and stepping primitives have been characterized end-to-end.

Premature jumps from Emergence to Engineering produce vibe-coded substrate work. The PoC's discipline is to refuse that path even when a layer looks easy.

## Scope (per ADR-006 amended 2026-05-19)

**Substrate work — in PoC scope:**

- **Layer 3 — ICorDebug via P/Invoke.** dbgshim discovery + attach, COM interop scaffolding (modern `ComWrappers`-based), managed callback (V-tables) for `ICorDebugManagedCallback`, process attach/detach lifecycle, breakpoint primitives, stack frames + variables (no func-eval in v1), stepping over/into/out, managed-thread enumeration.

**Admitted dependencies — out of PoC scope (per ADR-009):**

- **Layer 1 — Diagnostic IPC** — `DiagnosticsClient` from `Microsoft.Diagnostics.NETCore.Client`. Characterized at [`findings/01-ipc-protocol-survey.md`](findings/01-ipc-protocol-survey.md) as axis-4 reference material — what's under the hood, for v2 walk-back if a substrate-driven reason ever surfaces.
- **Layer 2 — EventPipe NetTrace** — `EventPipeSession` + `EventPipeEventSource` from the same NuGet.

**Explicitly out of scope:**
- Native reimplementation of Layers 1+2 (admitted by ADR-009; re-litigating that admission is an ADR-009 amendment, not PoC work)
- Replacing DrHook v1's MCP surface
- Eval recovery, conditional breakpoints, watch mode — ADR-006 Open Question 2; decision deferred to Phase 4
- Production substrate concerns (configuration surface, error contracts, logging, telemetry)
- Windows-specific implementations (Unix-first; Windows added once the COM interop idiom is established)
- Cross-runtime support (.NET Framework, Mono) — not on the trajectory

**Explicitly rejected as PoC scope:**
- "Fix netcoredbg's func-eval deadlock" — fixing someone else's external dependency, not substrate work
- "Implement client-side conditional-breakpoint evaluation in current DrHook" — working around the gap inside the wrapper, still not substrate work
- "Build native Layer 1 / Layer 2" — they're admitted dependencies per ADR-009
- Any path that ends with DrHook v1 staying as the production substrate

## First Emergence probe (Layer 3) — pending Epistemics

Layer 3's first probe target requires Epistemics-phase reading first. The working hypothesis (to be confirmed by the readings below):

> **Working hypothesis:** A BCL + P/Invoke client can use `dbgshim`'s documented entry points to attach to a running .NET process, obtain a valid `ICorDebug` interface pointer, and release cleanly — without netcoredbg, and without using `Microsoft.Diagnostics.NETCore.Client` for this specific surface (the NuGet handles a different observation channel; ICorDebug is a separate substrate).

Required Epistemics-phase reading before drafting the probe:

1. **dbgshim API surface** — `dotnet/runtime/docs/design/coreclr/debugging/`. Exact entry points (`CreateProcessForLaunch`, `RegisterForRuntimeStartup`, `EnumerateCLRs`, etc.), calling conventions, lifecycle, error semantics.
2. **ICorDebug COM interface** — `dotnet/runtime/src/coreclr/debug/ee/`. V-table layout, threading model, the callback contract (`ICorDebugManagedCallback`), managed/unmanaged lifetime expectations.
3. **netcoredbg's dbgshim usage** — reference material only. We read it to learn what they do at the protocol layer; we build it ourselves under our own substrate discipline. Per ADR-006: "netcoredbg's source is reference material for understanding what the runtime expects on each protocol. It is not a template for what we build."
4. **Modern C# COM interop patterns** — `ComWrappers` (.NET 5+) as the substrate-aligned interop surface; V-table thunks for the managed callback; lifetime management for native pointers. Legacy `[ComImport]` RCWs may show up in netcoredbg's source but are not the substrate-aligned approach for our v1.

Probe 02 (the first Layer 3 probe) is drafted only after these readings — not before.

## Testability is a designed-in architectural constraint

DrHook v1's integration tests drove a live netcoredbg subprocess and were deleted from the solution on 2026-04-06 — the live-external-tool dependency made them unmaintainable. See [`docs/limits/drhook-testability.md`](../../docs/limits/drhook-testability.md).

The PoC and the Layer 3 substrate work are built under the inverse discipline:

1. **Callback-sequence fixtures.** Where ICorDebug callbacks have deterministic shapes — and many do, since the V-table contract is well-defined — capture them once and replay forever. Captures are version-tagged (`runtime: 10.0.0`, `os: darwin-arm64`).
2. **In-process synthetic targets.** A minimal class library that the test starts in-process, observes via DrHook.Engine's Layer 3 surface, and stops. No external subprocess. No platform variance baked into the test surface. This is the inverse of DrHook v1's "spawn netcoredbg against a pre-built target DLL" pattern.
3. **Layer falsifiability.** Layer 3 is testable as a pure-data transformation against fixtures wherever the ICorDebug callback shape allows. Live-process integration is smoke testing on top of layer tests, not the substrate's primary verification surface.

A substrate that observes other .NET processes deterministically must itself be observable deterministically. That's the bar.

## dbgshim baseline

The probes need `libdbgshim` (the native shim to `ICorDebug`), which left the .NET runtime install at .NET 7+. The adopted baseline is the official **`Microsoft.Diagnostics.DbgShim.<rid>` 9.0.661903** NuGet native asset (see [`findings/11-dbgshim-baseline.md`](findings/11-dbgshim-baseline.md) for the obtain command + provenance).

Discovery is automatic. Both `DrHook.Engine.DbgShim.Resolve` (used by probes 07+) and the local `ResolveDbgShim` in probes 02–06 walk the NuGet cache (`~/.nuget/packages/microsoft.diagnostics.dbgshim.<rid>/*/runtimes/<rid>/native/`) and pick the newest version. Building `DrHook.Engine` once populates the cache; from then on the probes run without `DBGSHIM_PATH`. The env var remains as an explicit override for testing a custom build.

The engine bundles this NuGet's `runtimes/<rid>/native/` payload at build time; it is a native runtime-substrate asset per [ADR-009](../../docs/adrs/ADR-009-substrate-dependency-policy.md), not a managed dependency.

## How to run a probe

Each probe is a file-based `.cs` script per [CLAUDE.md File-Based Apps](../../CLAUDE.md#file-based-apps-net-10). Shebang line, no `.csproj`, not part of `SkyOmega.sln`. Run with `./<probe>.cs` or `dotnet <probe>.cs`.

Probes are numbered to make the Emergence sequence visible. The original numbering anchored on Layer 1 (Diagnostic IPC); under the amended scope, the visible sequence is:

- **01 (retained as reference):** Diagnostic IPC protocol survey at [`findings/01-ipc-protocol-survey.md`](findings/01-ipc-protocol-survey.md) — characterizes the substrate-admitted dependency, not implementation prep
- **02+ (Layer 3):** ICorDebug Epistemics readings → dbgshim attach probe → COM interop scaffolding → stepping primitives. Numbered as the sequence develops.

## Layout

```
poc/drhook-engine/
├── README.md                              # this file
├── 02-<icordebug-probe>.cs                # (pending Epistemics) first Layer 3 emergence probe
├── fixtures/                              # captured wire bytes / callback sequences, version-tagged
└── findings/
    ├── 01-ipc-protocol-survey.md          # characterization of admitted dependency (reference material)
    └── 02-<icordebug-readings>.md         # Layer 3 Epistemics-phase notes (pending)
```

## References

- [ADR-009: Substrate Dependency Policy](../../docs/adrs/ADR-009-substrate-dependency-policy.md) — sets the L1/L2 vs L3 scope boundary via the four-axis rule (Accepted 2026-05-19)
- [ADR-006: DrHook.Engine](../../docs/adrs/drhook/ADR-006-drhook-engine.md) — amended 2026-05-19 to narrow scope to Layer 3
- [`docs/limits/drhook-testability.md`](../../docs/limits/drhook-testability.md) — testability as designed-in constraint
- [DrHook.Poc archive (`../DrHook.Poc`)](../../../DrHook.Poc) — original netcoredbg-based PoC; observation archive remains useful as reference material
- Mercury session 2026-05-17 decision `drhook-engine-poc-direction` — original PoC direction (pre-policy framing); superseded on the L1/L2-native point by ADR-009
- Mercury session 2026-05-17 finding `dh-010-correction` — corrects the 2026-04-06 over-reach
- Mercury session 2026-05-19 decision `drhook-engine-scope-narrowed` — the ADR-009 cascade record
- [.mcp.json](../../.mcp.json) — DrHook MCP routes to `dotnet run --project src/DrHook.Mcp` while engine v1 is in flight
