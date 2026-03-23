# ADR-004 - DrHook: Runtime Observation Substrate for AI Coding Workflows

## Status
Proposed (2026-03-22)

## Context

AI coding workflows follow a two-step cycle: **Compile → Test**. When tests pass, the workflow concludes — but passing tests do not confirm that generated code matches execution reality. Two gaps remain invisible:

1. **Diagnostic gap**: Infinite loops, deadlocks, and resource exhaustion produce no signal in log-based debugging. An AI coding agent cannot observe what it cannot measure.

2. **Epistemic gap**: Green tests validate expected behavior but never surface unexpected behavior. Generated code may pass all assertions while exhibiting thread starvation, GC pressure, lock contention, or silent exception swallowing that only manifests under production conditions.

The hypothesis: extending AI coding workflows to **Compile → Test → Inspect** closes both gaps. Runtime observation provides the missing signal — not by replacing tests, but by making execution behavior queryable.

This hypothesis was validated empirically in the [DrHook.Poc](https://github.com/bemafred/DrHook.Poc) proof of concept (2026-03-21), which demonstrated:

- Tight loop detection via EventPipe thread sampling
- Recursive call descent via DAP step-into
- Exception filter breakpoints (all throws, user-unhandled)
- Mutable state inspection with configurable depth
- Async code thread migration tracking
- Noise filtering (indexer exceptions, interface reimplementations, self-referential properties)

Seven empirical observation documents confirm the hypothesis across distinct scenarios.

## Decision

DrHook joins Sky Omega as a **runtime observation substrate** — a peer MCP server alongside Mercury, not a Mercury client or plugin.

### Architecture

DrHook provides two observation layers:

```
┌─────────────────────────────────────────────────────┐
│              MCP stdio Server (drhook-mcp)          │
│         JSON-RPC 2.0 over stdin/stdout              │
└──────────────────┬──────────────────────────────────┘
                   │
        ┌──────────┴──────────┐
        │                     │
   Layer 1: Observation  Layer 2: Stepping
   EventPipe / passive   DAP / controlled
   StackInspector        SteppingSessionManager
                         DapClient → netcoredbg
```

**Layer 1 — Observation (EventPipe):**
Passive profiling of running .NET processes. Thread sampling, exception tracing, GC events, lock contention. Results are summarized with anomaly detection (hotspot >80%, GC pressure >5 events, contention, exceptions, idle).

**Layer 2 — Stepping (DAP):**
Controlled execution via Debug Adapter Protocol. Attach to process, set breakpoints (source line, function entry, exception filter), step through code, inspect variables at arbitrary depth. Uses netcoredbg as the DAP backend.

### MCP Tools (13)

| Category | Tool | Purpose |
|----------|------|---------|
| Observation | `drhook:processes` | List running .NET processes with versions |
| Observation | `drhook:snapshot` | Capture EventPipe trace with anomaly detection |
| Navigation | `drhook:step-launch` | Attach to process, set initial breakpoint |
| Navigation | `drhook:step-next` | Step over one line |
| Navigation | `drhook:step-into` | Descend into method call |
| Navigation | `drhook:step-out` | Return to caller frame |
| Flow | `drhook:step-continue` | Resume until breakpoint |
| Flow | `drhook:step-pause` | Interrupt running process |
| Breakpoints | `drhook:step-breakpoint` | Source line breakpoint (optional condition) |
| Breakpoints | `drhook:step-break-function` | Function entry breakpoint (optional condition) |
| Breakpoints | `drhook:step-break-exception` | Exception filter (`all` or `user-unhandled`) |
| Inspection | `drhook:step-vars` | Inspect local variables with configurable depth |
| Lifecycle | `drhook:step-stop` | End session, detach from process |

### Relationship to Other Substrates

DrHook is a **peer** of Mercury, not a consumer:

```
Claude (or any MCP client)
    ├── mercury-mcp    (semantic memory)
    ├── drhook-mcp     (runtime observation)
    └── other servers
```

Integration between Mercury and DrHook is a **client concern**. A client may choose to store DrHook observations as Mercury triples, but DrHook has no knowledge of or dependency on Mercury. This preserves sovereignty and composability.

### Project Structure

Following Mercury's shim/library separation (ADR-018, ADR-019):

```
src/
├── DrHook/                        # Core library
│   ├── Diagnostics/               # EventPipe observation
│   │   ├── ProcessAttacher.cs     # Process discovery
│   │   └── StackInspector.cs      # Trace capture and summarization
│   └── Stepping/                  # DAP stepping
│       ├── DapClient.cs           # DAP wire protocol
│       ├── SteppingSessionManager.cs  # Session lifecycle
│       └── NetCoreDbgLocator.cs   # Cross-platform binary discovery
│
├── DrHook.Mcp/                    # MCP server shim → drhook-mcp global tool
│   └── Program.cs                 # Tool registration, hosting
│
tests/
├── DrHook.Tests/                  # xUnit tests
│
examples/
├── DrHook.Examples/               # Inspection target scenarios
```

**Solution placement:** `2 Substrates / DrHook` — DrHook is infrastructure (a substrate for runtime observation), alongside Mercury (knowledge substrate) and Minerva (inference substrate).

### Dependencies

| Component | Dependencies | Sovereign? |
|-----------|-------------|------------|
| DrHook (core) | BCL + 2 first-party Microsoft packages | Yes |
| DrHook.Mcp | ModelContextProtocol SDK, Microsoft.Extensions.Hosting | Host-layer deps (per convention) |
| DAP backend | netcoredbg (MIT, Samsung) | Vendorable |
| EventPipe | Microsoft.Diagnostics.NETCore.Client | Runtime-native client |
| Trace parsing | Microsoft.Diagnostics.Tracing.TraceEvent | First-party |

The two Microsoft NuGet packages (`Microsoft.Diagnostics.NETCore.Client`, `Microsoft.Diagnostics.Tracing.TraceEvent`) are the official .NET runtime diagnostic clients — they implement the IPC protocol to the runtime's built-in diagnostic server. These are not third-party abstractions; they are the protocol itself.

### Global Tool Packaging

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>drhook-mcp</ToolCommandName>
<PackageId>SkyOmega.DrHook.Mcp</PackageId>
```

Installed via `tools/install-tools.sh` alongside Mercury tools.

### Sovereignty Path

The long-term path to full sovereignty:

1. **Current (v1):** netcoredbg as DAP backend — validates the model, provides full stepping
2. **Future (DrHook.Engine):** Sovereign C# port via P/Invoke to `dbgshim` — the small native library shipping with every .NET runtime (~dozen exported functions). This follows the pattern proven by Minerva for Metal/CUDA/Accelerate hardware interop.

When DrHook.Engine replaces netcoredbg:
- No external binary dependency
- Type-specific exception filters (direct ICorDebug access)
- Hit-count breakpoints
- Full control over stepping semantics

## Consequences

### Positive

- Sky Omega gains runtime observation — the missing third step in AI coding workflows
- Peer MCP server design preserves composability and sovereignty
- Empirically validated hypothesis reduces implementation risk
- Shim/library separation enables testing without MCP protocol overhead
- Sovereignty path is clear and follows established patterns (Minerva P/Invoke)

### Trade-offs

- Two additional NuGet dependencies in core library (both first-party Microsoft)
- netcoredbg runtime dependency until DrHook.Engine replaces it
- Apple Silicon: netcoredbg must be built from source (no pre-built ARM64 binaries)
- Stepping layer introduces Heisenberg effects (observation perturbs execution)

### Implementation Tracking

Detailed implementation ADRs will be tracked in [docs/adrs/drhook/](drhook/README.md).

## References

- [DrHook.Poc repository](https://github.com/bemafred/DrHook.Poc) — proof of concept with empirical validation
- [DrHook.Poc ADR-001](https://github.com/bemafred/DrHook.Poc/blob/main/docs/adrs/ADR-001-drhook-poc-hypothesis.md) — original hypothesis and falsification criteria
- [DrHook.Poc ADR-002](https://github.com/bemafred/DrHook.Poc/blob/main/docs/adrs/ADR-002-complete-dap-stepping-operations.md) — DAP stepping operations
- [ADR-001](ADR-001-sky-omega-1.0-operational-scope.md) — Sky Omega 1.0 operational scope
- [ADR-018 (Mercury)](mercury/ADR-018-cli-library-extraction.md) — CLI library extraction pattern
- [ADR-019 (Mercury)](mercury/ADR-019-global-tool-packaging.md) — Global tool packaging pattern
