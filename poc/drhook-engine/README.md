# DrHook.Engine PoC

A proof-of-concept for a **BCL-only runtime-inspection substrate** for .NET processes. Targets replacement of DrHook v1's external dependencies — netcoredbg (Samsung, MIT, separate-process DAP server) and `Microsoft.Diagnostics.NETCore.Client` (NuGet) — with native protocol implementations under Sky Omega's parser/storage idiom.

The PoC validates substrate hypotheses in isolation. **No engineering integrates back into sky-omega until each Emergence probe falsifies or confirms its hypothesis.** Promotion path: PoC → ADR-006 rewrite (from evidence, not memory) → engine engineering in `src/DrHook.Engine/`.

## Why this exists

Three substrates, one principle. Mercury is BCL-only. Minerva is BCL-only (hardware via P/Invoke). DrHook v1 is the odd substrate out — it wraps netcoredbg and a NuGet diagnostics client. Substrate-independence is the design invariant that makes Sky Omega's other substrates robust; the netcoredbg func-eval gap on macOS/ARM64 was a symptom of that architectural inconsistency, not the motivation for the rewrite.

The PoC's question is **not** "how do we recover netcoredbg's gaps" — that would be working around someone else's problem inside a wrapper, still not substrate work. The question is: **can a BCL-only client talk to the .NET runtime's diagnostic surfaces directly, with neither netcoredbg nor any external NuGet?**

netcoredbg's source is reference material for understanding what the runtime expects on each protocol. It is not a template for what we build. We learn what protocols the .NET runtime speaks and implement them ourselves.

## EEE staging

This PoC follows EEE methodology strictly:

- **Emergence** — surface unknown unknowns about each diagnostic-protocol layer. Smallest falsifiable probes, single-file, no abstractions. Each probe answers one hypothesis or surfaces a protocol unknown we didn't anticipate.
- **Epistemics** — survey what each protocol documents against what it actually does on the wire. Cross-check Microsoft specs against captured traces. Identify divergences between documentation and observed behavior.
- **Engineering** — only after Emergence and Epistemics produce evidence-grounded design grammar. Each layer becomes a substrate component in `src/DrHook.Engine/` only when its protocol has been characterized end-to-end.

Premature jumps from Emergence to Engineering produce vibe-coded substrate work. The PoC's discipline is to refuse that path even when a layer looks easy.

## Protocol layers

Three layers, increasing in interop complexity:

1. **Diagnostic IPC** — Unix domain socket (macOS/Linux) / named pipe (Windows) protocol the .NET runtime speaks for EventPipe session control and process metadata. Binary, versioned, documented in [dotnet/diagnostics — IPC Protocol](https://github.com/dotnet/diagnostics/blob/main/documentation/design-docs/ipc-protocol.md). BCL coverage: `System.Net.Sockets.Socket` with `AddressFamily.Unix`, `System.IO.Pipes.NamedPipeClientStream`. **Smallest layer; first Emergence probes target this.**

2. **EventPipe NetTrace** — binary trace format streamed by the runtime once a session is configured. Documented in [dotnet/runtime — EventPipe](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/eventpipe/eventpipe-design.md). Mercury-style parsing idiom applies: `ReadOnlySpan<byte>`, ref-struct readers, zero-GC where the hot path warrants.

3. **ICorDebug** — COM API for breakpoints, stepping, variable inspection, expression evaluation. The most ambitious layer; requires COM interop, V-table callbacks, managed/unmanaged lifetime management on Unix-flavored COM. **Deliberately deferred** until Layers 1 and 2 produce substrate-design grammar — approaching ICorDebug without protocol fluency from the layers below produces brittle interop code.

## First Emergence probe

> **Hypothesis:** A BCL-only client can connect to the .NET Diagnostic IPC socket of a running `dotnet` process and round-trip one well-formed message (e.g., `ProcessInfo`) without `Microsoft.Diagnostics.NETCore.Client`, without netcoredbg, without any NuGet.

Single file-based `.cs` app per [CLAUDE.md File-Based Apps](../../CLAUDE.md#file-based-apps-net-10). Run against any live `dotnet` process. Either it works in ~200 lines or it surfaces protocol unknowns the documentation didn't prepare us for. Either outcome moves Emergence → Epistemics on Layer 1.

The probe is not yet written. The decision to write it is deliberate — Emergence begins by reading the protocol spec and netcoredbg's IPC handling first, then drafting the probe with that grounding. See [Mercury session 2026-05-17 decision `drhook-engine-poc-direction`](../../docs/limits/drhook-testability.md#references) for the directional commitment that brought us here.

## Testability is a designed-in architectural constraint

DrHook v1's integration tests drove a live netcoredbg subprocess and were deleted from the solution on 2026-04-06 — the live-external-tool dependency made them unmaintainable. See [docs/limits/drhook-testability.md](../../docs/limits/drhook-testability.md).

The PoC and the engine that follows are built under the inverse discipline:

1. **Protocol-trace fixtures.** Each probe captures the wire bytes it produced. Fixtures live alongside the probe (`fixtures/` subdirectory once Layer 1 produces its first capture). Subsequent test runs verify against the bytes, not against the external runtime's live behavior. Captures are version-tagged (`runtime: 10.0.0`, `os: darwin-arm64`) so substrate-version-specific deltas are visible.
2. **In-process synthetic targets.** Where a live .NET process is needed, the test owns it end-to-end — a minimal in-process worker class library that the test starts, observes, and stops. No external subprocess. No platform variance baked into the test surface.
3. **Layer falsifiability.** Each layer (Diagnostic IPC client, EventPipe parser, ICorDebug interop) is testable as a pure-data transformation against fixtures. Live-process integration is smoke testing on top of layer tests, not the substrate's verification surface.

A substrate that observes other .NET processes deterministically must itself be observable deterministically. That's the bar.

## Scope discipline

**In scope for this PoC:**
- Diagnostic IPC client (Layer 1) — socket discovery, connect, message round-trip, process listing, EventPipe session control
- EventPipe NetTrace parser (Layer 2) — decode trace buffers without `Microsoft.Diagnostics.NETCore.Client`
- Protocol-trace fixture capture and replay tooling
- A minimal in-process synthetic target library

**Out of scope for this PoC** (deferred to engine engineering once Layers 1-2 land):
- ICorDebug interop (Layer 3)
- Replacing DrHook v1's MCP surface
- Eval recovery, conditional breakpoints, watch mode — downstream from Layer 3
- Production substrate concerns (configuration surface, error contracts, logging, telemetry)
- Windows-specific implementations (Unix-first; Windows added once protocol idiom is established)
- Cross-runtime support (.NET Framework, Mono) — not on the trajectory

**Explicitly rejected as PoC scope:**
- "Fix netcoredbg's func-eval deadlock" — fixing someone else's external dependency, not substrate work
- "Implement client-side conditional-breakpoint evaluation in current DrHook" — working around the gap inside the wrapper, still not substrate work
- Any path that ends with DrHook v1 staying as the production substrate

## How to run a probe

Each probe is a file-based `.cs` script. Shebang line, no `.csproj`, not part of `SkyOmega.sln`. Run with `./<probe>.cs` or `dotnet <probe>.cs`. Probes are throwaway artifacts in spirit even though they live in the repo — they capture findings, not production code.

Probes are numbered (`01-diagnostic-ipc-probe.cs`, `02-...`) to make the Emergence sequence visible.

## Layout

```
poc/drhook-engine/
├── README.md                       # this file
├── 01-diagnostic-ipc-probe.cs      # (to be written) BCL-only socket round-trip
├── fixtures/                       # captured wire bytes, version-tagged
└── findings/                       # Markdown notes per probe — hypothesis, outcome, surprises
```

`findings/<NN>-<probe-name>.md` is the Epistemics-phase output for each Emergence probe. It records what the probe was testing, what happened, and what unknowns were surfaced. Findings feed the eventual ADR-006 rewrite.

## References

- [ADR-006: DrHook.Engine — BCL-only Runtime Inspection Substrate](../../docs/adrs/drhook/ADR-006-drhook-engine.md) — current Proposed draft (rewritten after PoC produces evidence pack)
- [docs/limits/drhook-testability.md](../../docs/limits/drhook-testability.md) — testability as designed-in constraint
- [DrHook.Poc archive (`../DrHook.Poc`)](../../../DrHook.Poc) — original PoC reference for the netcoredbg-based DrHook v1 path; protocol observations remain useful as reference material
- Mercury session 2026-05-17 decision `drhook-engine-poc-direction` — directional commitment that scoped this PoC
- Mercury session 2026-05-17 finding `dh-010-correction` — corrects the 2026-04-06 over-reach that originally framed DrHook.Engine motivation as platform-level CoreCLR impossibility
- [.mcp.json](../../.mcp.json) — DrHook MCP routes to `dotnet run --project src/DrHook.Mcp`, intentionally; the PoC is a separate artifact and does not change current DrHook MCP routing
