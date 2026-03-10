# ADR-001: Semantic Memory Architecture

**Component:** Lucy (deep memory — structured semantic memory over Mercury)
**Status:** Proposed
**Date:** 2026-03-08

## Summary

Lucy is a convenience layer over Mercury that provides semantic memory operations without requiring callers to speak raw RDF/SPARQL. She translates between cognitive-level operations (remember, recall, forget, associate, consolidate) and Mercury's triple surface. Lucy draws architectural inspiration from neuroscience memory models — distinct systems for encoding, consolidation, retrieval, and decay — adapted to a persistent semantic substrate.

## Context

Mercury is a complete, production-ready RDF engine (v1.3.8, 100% W3C conformance). It speaks triples, atoms, SPARQL, and bitemporal queries. This is powerful but low-level — like having direct access to the storage controller without a filesystem.

James (ADR-001) establishes that cognites — situated epistemic state changes — must be persisted with full provenance (quadrant, reference frame, EEE phase, primary question, temporal coordinates). James stores through Lucy, not directly through Mercury. Sky dispatches through James and receives context enriched by Lucy's recall.

What's missing is the **convenience layer** that translates between cognitive operations and Mercury's triple surface:

- "Remember this" → encode as situated triples with provenance graph
- "What do we know about X?" → construct appropriate SPARQL, filter by epistemic status
- "How did we arrive at this conclusion?" → provenance chain traversal
- "Consolidate session learnings" → selective promotion from working memory to long-term store
- "This turned out to be wrong" → retraction with preserved history (bitemporal)

### Neuroscience-Inspired Memory Model

The following parallels are functional, not metaphorical. They inform API design because the problems are structurally similar — a cognitive system needs distinct memory operations with different characteristics:

| Neuroscience Concept | Lucy Equivalent | Mercury Mechanism |
|---------------------|----------------|-------------------|
| Encoding | `Remember(cognite)` — situate and persist | Insert triples with epistemic coordinate graph |
| Consolidation | `Consolidate()` — promote, merge, prune | Batch operations: merge provisional → confirmed, prune superseded |
| Retrieval | `Recall(query)` — context-sensitive recall | SPARQL with epistemic filters, ranked by relevance/recency |
| Priming | `Prime(context)` — preload relevant knowledge | Prefetch triples matching current epistemic state for braid injection |
| Decay | `Decay()` / pruning — reduce noise over time | Temporal queries + pruning (Mercury.Pruning) for aged provisional knowledge |
| Working memory | Session-scoped graph | Named graph per session, consolidated on close |
| Long-term memory | Persistent store | Default graph + consolidated named graphs |
| Episodic memory | Temporally-situated cognite chains | Bitemporal queries over cognite provenance graphs |
| Semantic memory | Fact-level knowledge (decontextualized) | Triples promoted from episodic to fact status via consolidation |

### What Lucy Is Not

From the canonical definition:

- Not a vector database — meaning is explicit, not embedded
- Not a cache of conversation history — the Semantic Braid handles working context
- Not an inference engine — Lucy stores and retrieves; reasoning belongs to James and Sky
- Not a separate store — Lucy operates over Mercury, not alongside it

## Decision

### 1) Lucy provides a cognitive API surface over Mercury

Lucy SHALL expose operations in cognitive terms, hiding SPARQL and triple mechanics from callers:

**Core operations (initial scope):**

| Operation | Cognitive Function | Mercury Translation |
|-----------|-------------------|-------------------|
| `Encode` | Persist a cognite with epistemic coordinates | Insert triples into provenance-tagged named graph |
| `Recall` | Retrieve knowledge matching a query | SPARQL SELECT with epistemic/temporal filters |
| `Retract` | Mark knowledge as superseded or incorrect | Soft-delete with bitemporal history preservation |
| `Associate` | Link related cognites or facts | Insert triples expressing relationships between named graphs |
| `Consolidate` | Promote working memory to long-term | Batch transfer from session graph to consolidated store |
| `Prime` | Preload context for the current epistemic state | Query and package relevant triples for braid injection |
| `Trace` | Follow the provenance chain of a piece of knowledge | Navigate reification/provenance graphs |

Callers (James, Sky) speak Lucy's API. Lucy speaks Mercury's SPARQL. Mercury speaks triples and B+Trees.

### 2) Named graphs as memory spaces

Lucy SHALL use Mercury's named graph support to isolate memory scopes:

| Graph | Purpose | Lifecycle |
|-------|---------|-----------|
| Session graph | Working memory for current interaction | Created on session start, consolidated or discarded on close |
| Consolidated graph | Long-term knowledge surviving sessions | Grows via consolidation, pruned via decay |
| Provenance graphs | Per-cognite epistemic coordinates and origin | Created per encode, immutable after creation |
| Schema graph | Ontology definitions for cognite structure | Maintained across versions |

### 3) Epistemic status as first-class property

Every piece of knowledge in Lucy SHALL carry explicit epistemic status:

| Status | Meaning | Transitions |
|--------|---------|-------------|
| Provisional | Working hypothesis, not yet validated | → Confirmed, → Retracted |
| Confirmed | Validated through epistemics phase | → Retracted (if falsified) |
| Retracted | Previously held, now known to be wrong | Terminal (history preserved) |
| Assumed | Taken as given, not yet examined | → Provisional (when examined) |

Status transitions are themselves cognites — the act of confirming or retracting is an epistemic event stored with provenance.

### 4) BCL-only, zero external dependencies

Lucy SHALL follow Mercury's BCL-only principle. The convenience layer adds no packages — it composes Mercury's existing capabilities (SPARQL, named graphs, bitemporal queries, pruning).

### 5) Lucy defines cognite storage schema

Lucy SHALL own the RDF schema for cognite representation. This is the bridge between James's epistemic state machine and Mercury's triple surface. The specific schema is deferred to Lucy ADR-002 (Cognite Schema), but the design principle is established: the schema lives in Lucy because Lucy is the storage interface.

## Consequences

### What This Enables

- **James stores without knowing SPARQL** — epistemic compilation targets Lucy's cognitive API
- **Provenance is structural, not bolted on** — every encode carries epistemic coordinates as named graph metadata
- **Memory consolidation is explicit** — the system knows the difference between working memory and long-term knowledge
- **Retraction preserves history** — bitemporal storage means "we used to believe X" is queryable
- **Priming supports the braid** — Lucy can preload relevant context before Sky dispatches to an LLM

### What Requires Experimentation

- Cognite RDF schema (Lucy ADR-002)
- Consolidation heuristics — when and what to promote from session to long-term
- Decay strategies — which provisional knowledge to prune and when
- Recall ranking — how to order results by relevance within epistemic constraints
- Session graph lifecycle — cleanup, merge strategies, conflict resolution

### Follow-up ADRs

**Lucy ADR-002: Cognite Schema** — RDF structure for cognites, epistemic coordinate encoding, provenance graph conventions. Dependency: James ADR-002 (state model) defines the epistemic coordinates that Lucy must encode.

**Lucy ADR-003: Consolidation and Decay** — Heuristics for memory promotion, pruning strategies, interaction with Mercury.Pruning.

## References

- `docs/architecture/concepts/canonical/lucy.md` — Canonical definition
- `docs/adrs/James/ADR-001-epistemic-state-machine.md` — Cognite concept, storage requirements
- `docs/architecture/concepts/canonical/semantic-braid.md` — Working context vs. persistent memory
- `src/Mercury/Storage/QuadStore.cs` — Named graph support
- `src/Mercury/Pruning/` — Pruning infrastructure for decay operations
