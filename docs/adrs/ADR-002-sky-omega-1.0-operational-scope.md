# ADR-002 - Sky Omega 1.0.0 Operational Scope: CLI and MCP First

## Status
Accepted (2026-01-31)

## Implementation Summary

Core success criteria achieved:

- ✅ SPARQL LOAD wiring across CLI, MCP, and HTTP surfaces (v0.5.3)
- ✅ SPARQL Update sequences with semicolon separators (v1.0.0)
- ✅ USING / USING NAMED clause parsing in updates (v0.6.0)
- ✅ Content negotiation for CONSTRUCT/DESCRIBE (v0.6.0-beta.1)
- ✅ W3C Update tests validating resulting graph state (v1.0.0)
- ✅ Accurate SPARQL service description with sd:feature declarations (v1.0.0)
- ✅ 100% W3C SPARQL 1.1 Query conformance (418/418)
- ✅ 89% W3C SPARQL 1.1 Update conformance (84/94)

### Known Update Limitations

10 W3C Update edge cases deferred (complex semantics with limited practical impact):
- USING clause dataset restriction (USING without USING NAMED)
- Blank node identity preservation across sequence operations
- DELETE/INSERT combined operation ordering

## Context

Sky Omega has reached a stage where the core RDF/SPARQL substrate (**Mercury**) demonstrates high reliability, strong internal discipline, and broad W3C conformance coverage. The remaining gaps identified during recent inspection are not related to storage correctness, query execution, or algorithmic soundness, but instead concern:

- Incomplete wiring of already-implemented capabilities (e.g. SPARQL LOAD)
- Thin protocol-surface gaps (content negotiation, update sequencing)
- Entry-point inconsistencies between CLI, HTTP, and MCP surfaces
- W3C Update conformance tests validating execution success but not resulting graph state

At the same time, Sky Omega's broader cognitive components — **Lucy** (semantic memory), **James** (orchestration), **Sky** (LLM interaction), and **Minerva** (local LLM substrate) — introduce additional nondeterminism, operational complexity, and evolving semantics that are orthogonal to the current maturity level of Mercury and its operational interfaces.

Without an explicit scope decision, there is a risk of:
- Diluting focus by advancing cognitive components prematurely
- Allowing "almost production-ready" CLI and MCP surfaces to linger without consolidation
- Introducing AI-related variability before the operational substrate is fully locked down

An Architectural Decision is required to explicitly define what Sky Omega **1.0.0** is, and just as importantly, what it is not.

## Decision

Sky Omega **1.0.0** will focus exclusively on making **Mercury** and its **CLI** and **MCP** interfaces production-ready, operational, and reliable.

### In scope for 1.0.0

- Mercury RDF/SPARQL substrate
- SPARQL query and update execution
- Command-line interface (CLI) as a human-facing operational surface
- MCP interface as a tool-facing operational surface
- Completion and wiring of already-implemented functionality, including:
    - Proper wiring of SPARQL `LOAD` across CLI, MCP, and HTTP surfaces
    - Support for SPARQL Update sequences (semicolon-separated statements)
    - Propagation of `USING` / `USING NAMED` clauses into update execution
    - Correct content negotiation for `SELECT`/`ASK` vs `CONSTRUCT`/`DESCRIBE`
    - Accurate SPARQL service description reflecting actual capabilities
- Reliability tightening measures:
    - W3C SPARQL Update tests validating resulting graph state, not only execution success
    - Clear documentation of any intentionally simplified update semantics

CLI and MCP components are conceptually part of **Mira** (the interaction surface layer) and may be organized under a virtual solution folder to reflect this architectural relationship, without introducing additional code-level abstractions at 1.0.0.

### Explicitly out of scope for 1.0.0

- **Lucy**: higher-level semantic memory beyond Mercury's RDF substrate
- **James**: reasoning, orchestration, or executive cognition
- **Sky**: LLM interaction and conversational interfaces
- **Minerva**: local LLM inference substrate (despite being infrastructural)

These components are deferred to **Sky Omega 2.0.0**.

## Rationale

1. **Operational reality precedes cognition**  
   Cognitive and agentic systems amplify the properties of their underlying substrate. If the CLI and MCP surfaces are not fully reliable and protocol-correct, higher-level cognition will amplify ambiguity and inconsistency.

2. **CLI and MCP are the true leverage points**  
   The CLI and MCP interfaces are how humans, tools, and future cognitive components interact with Mercury. Making these surfaces production-ready creates immediate practical value and a stable foundation for all future work.

3. **Determinism before nondeterminism**  
   Introducing Minerva or other LLM-driven components changes latency characteristics, failure modes, reproducibility, and epistemic guarantees. Deferring them preserves a deterministic, inspectable 1.0.0 baseline.

4. **Evidence-based maturity**  
   Current gaps are thin wiring and protocol issues, not architectural flaws. This is precisely the right moment to consolidate and declare a stable 1.0.0 rather than continuing to evolve breadth-first.

## Consequences

### Positive

- Sky Omega 1.0.0 becomes:
    - Operationally useful
    - Scriptable and automatable
    - Trustworthy as infrastructure
- Mercury transitions from "impressive code" to dependable system component
- CLI and MCP become stable anchors for future development
- Sky Omega 2.0.0 can build cognition on a proven operational base

### Trade-offs

- Cognitive demonstrations and AI integration are intentionally deferred
- External perception may shift from "AI system" to "infrastructure tool" at 1.0.0

These trade-offs are intentional and aligned with long-term system integrity.

## Notes

This ADR does not reduce ambition; it sequences it.  
Sky Omega 1.0.0 establishes a reliable operational substrate.  
Sky Omega 2.0.0 will build composed cognition on top of it.