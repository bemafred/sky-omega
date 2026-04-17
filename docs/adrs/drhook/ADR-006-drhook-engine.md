# ADR-006: DrHook.Engine — BCL-only Runtime Inspection Substrate

**Status:** Proposed — 2026-04-17
**Context:** Emergence during parser-fix session (2026-04-17) — raised as a substrate-independence follow-up after the netcoredbg func-eval limitation (ADR-006 predecessor, now consolidated into ADR-002) made clear that wrapping an external debugger is architecturally fragile.

## Problem

DrHook today depends on two external artifacts:

1. **netcoredbg** — a separate-process DAP server. The func-eval deadlock on macOS/ARM64 (documented in the ADR-002 amendment) removed `drhook_step_eval` and watch-mode from DrHook's tool surface. netcoredbg's macOS/ARM64 maintenance has been dormant since 2023.
2. **Microsoft.Diagnostics.NETCore.Client** — a NuGet package, pulled in for EventPipe stack inspection.

Both violate the substrate-independence principle that Mercury (BCL-only) and Minerva (BCL-only, hardware via P/Invoke) already honor. As long as DrHook wraps externally-maintained tools, its reliability tracks theirs — and as the func-eval episode shows, that ceiling is reachable.

## Drivers

1. **Substrate independence as a design invariant.** Three substrates, one principle. DrHook as the odd substrate out is a growing liability, not an incidental quirk.
2. **Failure ownership.** The func-eval deadlock cost an entire workstream (eval, watch, conditional breakpoints). With a native implementation, the same class of bug would be ours to fix, not ours to work around.
3. **Platform reach.** netcoredbg's macOS/ARM64 bitrot is not unique — any future platform shift (Linux/ARM64, Windows/ARM64) exposes DrHook to the same dependency-rot risk. Native implementation decouples us from that timeline.
4. **Protocol surface is tractable.** The .NET runtime already exposes the capabilities we need via documented protocols (Diagnostic IPC, ICorDebug). The NuGet package is a convenience wrapper, not a necessity.

## Decision (sketch)

Build **DrHook.Engine** as a new BCL-only project that replaces both external dependencies. The public DrHook MCP surface (tool names, schemas) remains unchanged; the engine is swapped underneath.

### Three interface layers

**Layer 1 — Diagnostic IPC (sockets/named pipes, BCL-supported).**
The `DiagnosticServer` protocol the .NET runtime speaks is already used for EventPipe session setup and process control. BCL `System.Net.Sockets` (Linux/macOS) and `System.IO.Pipes.NamedPipeClientStream` (Windows) are sufficient. Protocol is binary, versioned, documented.

**Layer 2 — EventPipe protocol parsed in-house.**
The NetTrace binary format is documented. We already consume EventPipe streams via the NuGet package; re-implementing the parser in Mercury-style (zero-GC, span-based, ref-struct readers) aligns with Sky Omega's parser idiom and removes the package dependency.

**Layer 3 — ICorDebug via P/Invoke.**
The COM API netcoredbg wraps internally. Callable from managed .NET with interop scaffolding (COM marshaling, `ComImport`, V-table callbacks). This is the most ambitious layer and the one that replaces the stepping/eval capability netcoredbg currently provides.

### Integration

DrHook.Core (the existing library) exposes the stepping and observation APIs. DrHook.Engine sits underneath, implementing the concrete protocols. `DapClient` and `ProcessAttacher` become thin facades that delegate to the engine.

## Consequences

**Gained:**
- Native control of the failure surface. Bugs we can fix.
- Substrate-independence restored across the three substrates.
- Platform-portability tracks BCL, not netcoredbg.
- Zero-GC / span-based parsing idiom extends to runtime inspection.

**Lost / traded:**
- Significant engineering investment. ICorDebug via P/Invoke is not trivial — COM interop, V-table callbacks, managed/unmanaged lifetime management.
- Short-term functional parity with netcoredbg requires replicating a mature debugger's feature set.
- Risk of reproducing netcoredbg's bugs in a different form before we reproduce its strengths.

**Explicitly out of scope for this ADR:**
- Replacing `MCP SDK` (ADR-003) — that's application-layer plumbing, not a runtime dependency.
- Supporting non-.NET runtimes.
- Rewriting the DAP protocol surface (DrHook.Engine can still speak DAP outward if useful; the point is that the implementation is native, not that the wire format changes).

## Continuous Observation Surface (added 2026-04-17)

DrHook today is request/response: you ask for a snapshot, you get a snapshot. macOS Activity Monitor raises the adjacent question — can DrHook be a deep, continuously-refreshing observation surface for .NET processes?

**Scope decision (2026-04-17):** yes, within the .NET focus. DrHook becomes the reference .NET runtime-observation substrate, at a depth Activity Monitor can't reach (GC heap, assemblies, JIT state, per-thread managed/native split). **Non-.NET processes are explicitly out of scope** — others build adapters for their preferred stack (Python, JVM, Go, native) as they see fit. The naming principle holds: DrHook is *the .NET substrate*, and compromising that identity for generic process monitoring would weaken both it and any other stack-specific tool built in parallel.

### Capability tiers

**Tier 1 — BCL-only, portable, days of work.** Integrate into DrHook.Engine Phase 1:
- Per-process CPU% via `Process.TotalProcessorTime` delta sampling
- Virtual/private/working memory breakdown
- Loaded module list (`Process.Modules` + EventPipe `AssemblyLoader` for managed assemblies)
- Process tree (parent PID via `kinfo_proc` / `/proc`)
- Continuous-refresh surface: streaming MCP tool (or polling with configurable interval) that emits a snapshot sequence

**Tier 2 — macOS P/Invoke, documented APIs, weeks of work.** Integrate into DrHook.Engine Phase 3 (ICorDebug) as companion work, since the mach interop scaffolding is shared:
- Per-thread enumeration with CPU time, state, and managed/native stack (mach `task_threads`, `thread_info`; cross-referenced with EventPipe stack samples)
- Disk I/O counters (`proc_pidinfo` PROC_PIDIOINFO or PROC_PIDTASKINFO)
- Open files / file descriptors (`proc_pidfdinfo`)

**Tier 3 — private APIs, fragile, out of scope.** Leave to the OS's own tool:
- Per-process network (macOS SystemStats.framework is undocumented and churns)
- Energy impact (IOReport)
- GPU usage per process (Metal diagnostics)

### Non-goals (explicit)

- **Non-.NET process monitoring** — by design. Adapters for other runtimes are a separate, parallel effort, ideally with a shared protocol / output format but independent implementations.
- **Competing with Activity Monitor generally** — DrHook is deep .NET, not broad OS.
- **Production telemetry / APM feature parity** — we cover observation; APM tools can consume DrHook's output if they want cross-correlation, but that's integration, not scope creep.

### Integration points

The continuous-observation surface is a new class of MCP tool alongside the existing `drhook_snapshot` — conceptually `drhook_watch` (streaming) or `drhook_monitor` (poll + snapshot series). The existing request/response tools stay as-is for point-in-time inspection.

## Open Questions

1. **Staged vs clean-room.** Can we introduce DrHook.Engine layer-by-layer (Diagnostic IPC first, EventPipe second, ICorDebug last), keeping netcoredbg as a fallback? Or is the ICorDebug layer the riskiest and should be de-risked first?
2. **Func-eval design.** The deadlock on macOS/ARM64 is a netcoredbg bug, but it points to CoreCLR-side fragility in func-eval on that platform. Do we inherit this problem or design around it from the start (e.g., by reading fields directly via stack inspection instead of invoking methods)?
3. **Windows support scope.** Named pipes vs sockets is a platform fork in Layer 1. Decide early whether Windows is a v1 target or deferred.
4. **Testing strategy.** DrHook's integration tests currently drive a live netcoredbg session. DrHook.Engine testing needs in-process .NET runtime inspection that doesn't depend on the thing it's testing. Candidate: a purpose-built test harness CLR (or at minimum, a verified reference implementation cross-check).

## Phases (draft)

### Phase 1 — Diagnostic IPC in BCL
- [ ] Implement `DiagnosticServer` client (process enumeration, EventPipe session start/stop)
- [ ] Replace `Microsoft.Diagnostics.NETCore.Client` in process listing and EventPipe startup
- [ ] Regression: existing DrHook observation tools unchanged

### Phase 2 — EventPipe protocol parser
- [ ] Zero-GC NetTrace parser matching Mercury parser idiom
- [ ] Replace `Microsoft.Diagnostics.NETCore.Client` in stack-inspection path
- [ ] Regression: stack snapshots match prior output byte-for-byte

### Phase 3 — ICorDebug via P/Invoke
- [ ] COM interop scaffolding for ICorDebug interfaces
- [ ] Process attach / launch
- [ ] Breakpoint set / hit / clear
- [ ] Stack frames, variables (without func-eval — direct field/stack inspection)
- [ ] Step over / into / out
- [ ] Regression: stepping integration tests pass without netcoredbg

### Phase 4 — Engine switchover
- [ ] Default to DrHook.Engine; netcoredbg as opt-in fallback
- [ ] Measure failure-mode surface on macOS/ARM64 (the platform where netcoredbg breaks)
- [ ] Retire netcoredbg dependency once parity is verified

## Validation

DrHook.Engine is successful when:
- `DrHook.Core` has zero references to `Microsoft.Diagnostics.NETCore.Client` and zero spawns of netcoredbg
- All existing DrHook MCP tools pass their integration tests against the native engine
- macOS/ARM64 func-eval works (or its absence is an explicit design choice, not a tool limitation)
- The DrHook project line-count remains comparable to Mercury's and Minerva's — native is not a license for bloat

## References

- ADR-002 (DrHook) — Expression Evaluation (amended with netcoredbg func-eval findings)
- ADR-003 (DrHook) — MCP SDK Adoption
- ADR-004 (DrHook) — Process-Owning Stepping via DAP Launch
- Mercury CLAUDE.md — "Zero external dependencies for core library" principle
- Minerva — BCL-only, hardware via P/Invoke (the reference pattern)
