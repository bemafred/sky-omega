# ADR-001: Interaction Surface Architecture

**Component:** Mira (embodied integration surface — perception and expression)
**Status:** Proposed
**Date:** 2026-03-08

## Summary

Mira is Sky Omega's body — the set of surfaces through which the system perceives input and expresses output. She provides a uniform surface abstraction that decouples the cognitive core (James, Sky, Lucy) from the modalities through which interaction occurs. Terminal, chat, email, voice, Teams, IDE extensions, MCP servers — all implement Mira. The same cognitive loop produces the same reasoning regardless of which surface carries it.

## Context

From the canonical definition: Mira is the embodied integration surface of Sky Omega. She provides the interfaces through which the system both perceives and expresses itself. Mira enables presence not by reasoning, but by faithful embodiment of what the system is, knows, and is doing.

### What Already Exists

Mercury v1.3.7 already has operational Mira surfaces, though they predate the formal component naming:

| Surface | Implementation | Status |
|---------|---------------|--------|
| Mercury CLI (`mercury`) | Interactive REPL, persistent store | Production |
| Mercury MCP (`mercury-mcp`) | Claude Code integration, semantic memory | Production |
| SPARQL Protocol | W3C HTTP endpoints (GET/POST) | Production |
| SPARQL CLI (`mercury-sparql`) | Query-only CLI | Production |
| Turtle CLI (`mercury-turtle`) | RDF parser/formatter | Production |

These are all **Mercury-direct** surfaces — they speak SPARQL and triples. Sky Omega 2.0 surfaces will speak cognitive operations through James, with Lucy and Sky handling memory and language respectively.

### What Mira Is Not

- Not a cognitive component — Mira presents, she does not reason
- Not a decision maker — James governs control flow, Sky generates language
- Not a memory system — Lucy handles persistence
- Not a language generator — Sky produces language; Mira delivers it

### The Surface Problem

Different modalities have fundamentally different characteristics:

| Modality | Input | Output | Latency | Richness |
|----------|-------|--------|---------|----------|
| Terminal/CLI | Text commands | Text + formatting | Immediate | Low (monospace text) |
| Chat (web) | Text + attachments | Text + rich media | Near-real-time | Medium |
| Email | Structured text | Structured text | Async (minutes-hours) | Medium |
| Voice | Audio stream | Audio stream | Real-time | Low (no visual) |
| Teams/Slack | Text + cards | Text + cards + actions | Near-real-time | Medium |
| IDE extension | Code context + commands | Code suggestions + diagnostics | Immediate | High (code-aware) |
| MCP server | Tool calls (JSON) | Tool results (JSON) | Immediate | Structured |

The cognitive core must not know or care which surface is active. The same epistemic state machine, the same cognite encoding, the same reasoning trace — regardless of whether the human is typing in a terminal or speaking to a microphone.

## Decision

### 1) Uniform surface contract

Mira SHALL define a surface contract that all modalities implement:

**Inbound (perception):**
- Receive input from the external world (human or system)
- Normalize to a common input model (text content, metadata, modality hints)
- Forward to James for epistemic processing

**Outbound (expression):**
- Receive compiled output from James (cognites, responses, guidance)
- Adapt to modality-specific presentation (formatting, cards, audio, code actions)
- Deliver to the external world

The contract is the boundary. Everything inside (James, Sky, Lucy, Mercury) is modality-agnostic. Everything outside is modality-specific.

### 2) Surface registry

Mira SHALL maintain a registry of active surfaces. Each surface registers its capabilities:

| Capability | Description |
|-----------|-------------|
| Modality | Text, voice, structured, code |
| Directionality | Input-only, output-only, bidirectional |
| Latency class | Real-time, near-real-time, async |
| Rich content support | Plain text, markdown, cards, media, code |
| Streaming support | Whether the surface supports incremental delivery |

James MAY use surface capabilities to adjust output — a voice surface gets concise responses; an IDE surface gets code-centric output; an email surface gets structured summaries.

### 3) Existing Mercury surfaces as v0 Mira implementations

The existing CLI, MCP, and SPARQL Protocol surfaces SHALL be recognized as Mira implementations. They currently bypass James (connecting directly to Mercury), which is correct for v1.x. As James comes online in 2.0, these surfaces will route through James while maintaining backward compatibility for direct Mercury access.

### 4) Mira is downstream, never upstream

Mira SHALL never inject epistemic content. She translates format and modality, not meaning. If a voice surface receives audio, Mira transcribes it and forwards text to James. If James returns a cognite, Mira formats it for the active surface. The epistemic content is invariant across the translation.

### 5) Observable behavior

Mira SHALL make system behavior observable rather than implicit. This means:

- Epistemic state is presentable (if James exposes it, Mira can show it)
- Reasoning traces are navigable (if Lucy stores them, Mira can surface them)
- Transition warnings are displayable (if James detects an invalid transition, Mira can present the redirect)

The degree of visibility is surface-appropriate and user-configurable — a developer in an IDE may want full epistemic state; a voice user may want none.

## Consequences

### What This Enables

- **Modality independence** — the cognitive core works identically across all surfaces
- **Surface-appropriate output** — same reasoning, different presentation
- **Incremental adoption** — existing Mercury surfaces are already Mira v0
- **New surfaces without core changes** — adding Teams or voice is a surface implementation, not a cognitive change

### What Requires Experimentation

- Surface contract specifics — the exact input/output model
- Voice integration — transcription pipeline, latency requirements
- IDE extension architecture — code context extraction, diagnostic presentation
- Email async model — how James handles minutes-to-hours latency between turns
- Surface capability negotiation — how James adapts output to surface capabilities
- Epistemic state presentation — what to show, when, how much

### Follow-up ADRs

**Mira ADR-002: Surface Contract Specification** — The exact interface that surface implementations must satisfy. Input/output models, capability declaration, lifecycle management.

**Mira ADR-003: MCP Surface Evolution** — How the existing Mercury MCP server evolves from direct Mercury access to James-mediated interaction while preserving backward compatibility.

## References

- `docs/architecture/concepts/canonical/mira.md` — Canonical definition
- `docs/adrs/James/ADR-001-epistemic-state-machine.md` — Signal flow architecture, Mira's downstream role
- `src/Mercury/Mercury.Cli/` — Existing CLI surface
- `src/Mercury/Mercury.Mcp/` — Existing MCP surface
- `src/Mercury/Sparql/Protocol/` — Existing SPARQL HTTP surface
