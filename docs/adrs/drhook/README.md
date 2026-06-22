# DrHook Architecture Decision Records

Intent and rationale: [ADR-004 — DrHook: Runtime Observation Substrate](../ADR-004-drhook-runtime-observation-substrate.md)

| ADR                                                              | Title | Status |
|------------------------------------------------------------------|-------|--------|
| [ADR-001](ADR-001-breakpoint-registry.md)                        | Breakpoint Registry | Completed |
| [ADR-002](ADR-002-expression-evaluation.md)                      | Expression Evaluation | Completed |
| [ADR-003](ADR-003-mcp-sdk-adoption.md)                           | MCP SDK Adoption | Completed |
| [ADR-004](ADR-004-step-run.md)                                   | Process-Owning Stepping | Completed |
| [ADR-005](ADR-005-expression-evaluation-diagnosis-correction.md) | Expression Evaluation Diagnosis Correction: netcoredbg, Not CoreCLR | Accepted |
| [ADR-006](ADR-006-drhook-engine.md)                              | DrHook.Engine — Native ICorDebug Replacement for netcoredbg | Accepted |
| [ADR-007](ADR-007-teardown-concurrency-test-debug.md)            | Teardown and Concurrency Hardening; Substrate-Aligned Test-Runner Debugging; Integration-Test Mechanism Characterization | Accepted |
| [ADR-008](ADR-008-process-lifecycle-discipline.md)               | Process Lifecycle Discipline — Natural Exit by Default; Explicit `Abandon` for Forced Termination | Completed |
| [ADR-009](ADR-009-test-debugging-mcp-surface.md)                 | Test-Debugging MCP Surface — MTP-First Launch + Universal Attach + Project-Aware Mode Selection | Superseded by ADR-010 |
| [ADR-010](ADR-010-mcp-tool-surface-redesign.md)                  | MCP Tool Surface Redesign — Semantic Naming and Established-Debugger Alignment | Accepted |
| [ADR-011](ADR-011-lifecycle-console-dashboard.md)                | Debug-Session Lifecycle (stop/detach/kill) and Debuggee Console I/O — Isolation, Surfacing, and the DrHook Dashboard | Accepted |
| [ADR-012](ADR-012-debug-state-surfaces.md)                       | Debug-State Surfaces — a Surface-Agnostic Model and its First Human Views (TUI Dashboard, Avalonia Sibling) | Proposed |
| [ADR-013](ADR-013-value-type-refstruct-inspection.md)            | Inspection of Value Types and Ref Structs — VALUETYPE + BYREF Field Expansion, Span-Aware Expression Evaluation | Accepted |
| [ADR-014](ADR-014-inspection-fault-containment.md)              | DrHook Inspection Robustness — Value-Type Read Safety and Fault Containment (a frame's shape must never crash the engine) | Completed |
