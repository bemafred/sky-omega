# Limit: DrHook substrate untestable — integration tests required live netcoredbg subprocesses

**Status:**        Latent (DrHook.Engine PoC designs testability in from the start)
**Surfaced:**      2026-04-06, during DrHook integration into sky-omega. Re-surfaced 2026-05-17 as a design constraint for the DrHook.Engine PoC.
**Last reviewed:** 2026-05-17
**Promotes to:**   ADR-006 rewrite (DrHook.Engine) — testability must be a first-class design section, not Open Question #4. PoC scaffold lands first; ADR is rewritten from the evidence pack the PoC produces.

## Description

DrHook v1's integration tests drove a live netcoredbg subprocess attached to a pre-built target DLL via DAP. The tests verified session lifecycle, stepping, variable inspection, breakpoint management, and conditional-stopping patterns — 11 integration tests in total (Mercury session 2026-04-06 obs dh-008).

This model fails the testability bar for a substrate:

1. **External-tool coupling.** Tests pass or fail based on netcoredbg's behavior, not on DrHook's logic. netcoredbg's macOS/ARM64 maintenance dormancy since 2023 means tests can drift without DrHook changing.
2. **Live-process fragility.** Subprocess launch timing, DAP handshake delays, OS scheduler variance — all manifest as flakes that look like substrate bugs.
3. **Pre-built target requirement.** `dotnet run --file` hangs DrHook step_run (Mercury obs dh-004, 2026-04-06) because compilation delay competes with the MCP timeout window. Tests must pre-build target DLLs and use `dotnet exec`, adding scaffolding cost per test.
4. **Test deletion as outcome.** `DrHook.Tests` was removed from the solution on 2026-04-06; the row was deleted from `STATISTICS.md` the same day (`029c936`). The tests were not maintainable.

The substrate that should observe other .NET processes deterministically cannot itself be observed deterministically — that's the contradiction.

## Trigger condition

Already triggered. The DrHook.Engine PoC and engine rewrite cannot proceed productively without resolving this. Promotion path:

- **PoC design phase.** Each Emergence probe in `poc/drhook-engine/` captures a recorded protocol trace (Diagnostic IPC frames, EventPipe NetTrace buffer, ICorDebug callback sequence) from a known-good run. Subsequent re-runs verify against the recorded trace, not against a live external tool's behavior.
- **Engine architecture phase.** Each protocol layer (Diagnostic IPC client, EventPipe parser, ICorDebug interop) is testable as a pure-data transformation against fixture traces. Live-process integration tests exist as smoke tests on top of layer tests, never as the substrate's primary verification surface.
- **In-process synthetic targets.** Where live-process behavior must be exercised, the target is a minimal in-process .NET worker library that the test owns end-to-end (start, observe, stop). No external subprocess, no DAP handshake against an external tool, no platform variance baked into the test surface.

## Current state

- DrHook v1 (in `src/DrHook/`, `src/DrHook.Mcp/`) has no test project. The deletion is recorded but not mitigated.
- DrHook.Poc (at `../DrHook.Poc`) has 7 empirical observation sessions documented in its `docs/observations/` — verification by observation, not automated testing. Useful as protocol reference; not a model for substrate verification.
- Recordkeeping debt: anyone who picks up DrHook v1 today has no automated way to verify it works after a change. The MCP surface is exercised manually or via integration with the larger Sky Omega session — neither is reproducible CI.

## Candidate mitigations

1. **Protocol-trace fixtures.** Capture once per protocol layer (Diagnostic IPC handshake, EventPipe session start/stop, ICorDebug attach sequence), commit the bytes under `poc/drhook-engine/fixtures/` or `tests/DrHook.Engine.Tests/Fixtures/`, replay forever. Tests assert that the parser/client produces the expected interpretation. No live process required.

2. **In-process synthetic target library.** A small test-only class library that hosts the minimum worker behavior DrHook needs to inspect — known thread topology, known GC state at known points, known exceptions at known lines. Tests start the worker in-process, observe via DrHook's substrate API, assert. Bypasses netcoredbg/subprocess entirely for the test path.

3. **Layer-by-layer falsifiability.** Each engine layer ships with its own test pyramid. Layer 1 (Diagnostic IPC client) parses fixture frames. Layer 2 (EventPipe parser) decodes fixture buffers. Layer 3 (ICorDebug interop) needs the most thought — likely a combination of in-process attached debugger setup and ICorDebug-callback fixture replays captured from a reference implementation.

4. **Reject the "untestable substrate" framing as architectural failure.** If a substrate layer can only be verified by running it against the very thing it's supposed to replace (netcoredbg), the architecture is wrong. The new substrate's testability is the validation that the abstraction is real.

## References

- Mercury session 2026-04-06 obs dh-008 — "DrHook validated: 11 integration tests against live DAP sessions" — the high-water-mark of the live-process test approach
- `CHANGELOG.md` 2026-04-06 — `DrHook.Tests` row removed from `STATISTICS.md` (commit `029c936`)
- [ADR-006: DrHook.Engine](../adrs/drhook/ADR-006-drhook-engine.md) Open Question #4 — "DrHook.Engine testing needs in-process .NET runtime inspection that doesn't depend on the thing it's testing" — names the problem, defers the answer
- Mercury session 2026-05-17 decision `drhook-engine-poc-direction` — testability designed-in, not deferred
- Mercury session 2026-05-17 finding `dh-010-correction` — corrects the 2026-04-06 over-reach that motivated the original framing
- [poc/drhook-engine/README.md](../../poc/drhook-engine/README.md) — PoC scope, EEE staging, testability discipline
