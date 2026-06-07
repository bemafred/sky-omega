# SPARQL pattern-model redesign POCs (ADR-045)

Investigation POCs (not permanent tools) for the ADR-045 (B) nesting pattern-model unification —
*a default graph is also a graph; one path, active-graph as a parameter.*

- **`probe-pattern-arena.cs`** — validates the candidate primitive: a pooled, index-child node arena.
  Demonstrated jointly: full nesting (GRAPH / UNION / OPTIONAL), active-graph threaded as a single
  evaluator parameter (default = the unnamed graph), a calibrated depth guard, and **~0 steady-state
  allocation** (0 bytes/cycle over 200k build+eval cycles). Run from repo root:
  `dotnet run --no-cache poc/adr-045-pattern-model/probe-pattern-arena.cs`.
- **`GraphProbe/`** — Mercury cost-shape. Shows the current GRAPH path materializes the full matching
  set **before** LIMIT: LIMIT-2 cost scales with total rows (~1.7 ms @ 5k → ~15.9 ms @ 50k), so LIMIT
  bounds *output*, not *work*. This is the source of the **LIMIT-pushdown requirement** for the unified
  ADR-045 executor.

See **ADR-045** and the `ck:` design-knowledge graph (`ck:lesson-index-child-arena`,
`ck:lesson-materialize-then-filter`, `ck:obs-graph-limit-pushdown`).
