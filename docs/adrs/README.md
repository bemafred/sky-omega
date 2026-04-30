# Architecture Decision Records

ADRs document significant architectural decisions with context, rationale, and consequences.

## Cross-Cutting ADRs

Foundational decisions affecting the entire repository:

| ADR | Title | Status |
|-----|-------|--------|
| [ADR-000](ADR-000-repository-restructure.md) | Repository Restructure for Multi-Substrate Architecture | Completed |
| [ADR-001](ADR-001-sky-omega-1.0-operational-scope.md) | Sky Omega 1.0.0 Operational Scope | Accepted |
| [ADR-002](ADR-002-documentation-and-tutorial-strategy.md) | Documentation and Tutorial Strategy | Completed |
| [ADR-003](ADR-003-mercury-encapsulation-and-api-surface.md) | Mercury Encapsulation and API Surface | Completed |
| [ADR-004](ADR-004-drhook-runtime-observation-substrate.md) | DrHook: Runtime Observation Substrate | Proposed |
| [ADR-005](ADR-005-cognitive-component-libraries.md) | Cognitive Component Libraries — Lucy, James, Sky, Minerva | Proposed |
| [ADR-006](ADR-006-mcp-surface-discipline.md) | MCP Surface Discipline — Destructive Operations Excluded | Proposed |
| [ADR-007](ADR-007-sealed-substrate-immutability.md) | Sealed Substrate Immutability — Re-create, Don't Modify | Proposed |
| [ADR-008](ADR-008-workload-profiles-and-validation-attribution.md) | Workload Profiles and Validation Attribution | Proposed |

## Substrate-Specific ADRs

- [Mercury ADRs](mercury/README.md) — Knowledge substrate (RDF storage, SPARQL)
- [Minerva ADRs](minerva/README.md) — Thought substrate (tensor inference)
- [DrHook ADRs](drhook/README.md) — Runtime observation substrate (EventPipe, DAP)

## Cognitive Component ADRs

- [James ADRs](James/) — Cognitive orchestration
- [Lucy ADRs](Lucy/) — Semantic memory
- [Mira ADRs](Mira/) — Interaction surface
- [Sky ADRs](Sky/) — Language and LLM interaction
