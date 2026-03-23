# Architecture Decision Records

ADRs document significant architectural decisions with context, rationale, and consequences.

## Cross-Cutting ADRs

Foundational decisions affecting the entire repository:

| ADR | Title | Status |
|-----|-------|--------|
| [ADR-000](ADR-000-repository-restructure.md) | Repository Restructure for Multi-Substrate Architecture | Implemented |
| [ADR-001](ADR-001-sky-omega-1.0-operational-scope.md) | Sky Omega 1.0.0 Operational Scope | Accepted |
| [ADR-002](ADR-002-documentation-and-tutorial-strategy.md) | Documentation and Tutorial Strategy | Phase 1 Accepted |
| [ADR-003](ADR-003-mercury-encapsulation-and-api-surface.md) | Mercury Encapsulation and API Surface | Accepted |
| [ADR-004](ADR-004-drhook-runtime-observation-substrate.md) | DrHook: Runtime Observation Substrate | Proposed |

## Substrate-Specific ADRs

- [Mercury ADRs](mercury/README.md) - Knowledge substrate (RDF storage, SPARQL)
- [Minerva ADRs](minerva/README.md) - Thought substrate (tensor inference)
- [DrHook ADRs](drhook/README.md) - Runtime observation substrate (EventPipe, DAP)
