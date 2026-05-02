# ADR-006: MCP Surface Discipline — Destructive Operations Excluded

**Status:** Completed — 2026-05-02

**Context:** Sky Omega exposes MCP servers for `mercury-mcp`, `drhook-mcp`, and (future) `minerva-mcp`. The MCP surface is what an AI agent actually sees and can invoke during a session. Surfacing reflects governance: every tool exposed is a tool an AI can call autonomously, often without an explicit human "are you sure?" prompt.

## Problem

The current Mercury MCP server exposes `mercury_prune` (`src/Mercury.Mcp/MercuryTools.cs:171`). Pruning is a destructive operation: it scans the primary store, copies surviving quads to a secondary, and switches the active pointer. The original primary's data is no longer accessible without the source dataset and a re-bulk-load.

An AI agent with the Mercury MCP attached can invoke `mercury_prune` directly. The MCP protocol provides no inherent human-in-the-loop checkpoint — the tool call goes through and the operation runs. In the cognitive partnership model (ADR-005, the substrate-independence and governed-automation theses), decisions of consequence pass through human review. Pruning is exactly such a decision: it shapes the canonical state of a memory substrate that the human relies on as ground truth.

The same principle generalizes:
- **DrHook MCP** exposes process attach, breakpoints, expression evaluation. Some of these mutate the running process (e.g., function-eval side effects); a sovereign AI invocation could disturb a debugging session in ways the human didn't authorize.
- **Future Minerva MCP** could expose model loading, weight modification, fine-tuning. Each is destructive in the model-state sense.
- **Future agents** (Lucy/James/Sky composition roots) will have MCP surfaces of their own.

Without a discipline, every new MCP server arrives with the question "should this tool be exposed?" answered ad-hoc. The result is a slow drift toward broad surfaces with destructive verbs reachable by autonomous agents — exactly the sovereign-AI deployment pattern the project explicitly rejects.

## Decision

**MCP-exposed tools must not include operations whose effects an AI shouldn't initiate autonomously.** Destructive operations are CLI-only.

### Classification

A tool surface element is classified along two axes:

| Axis | Values |
|------|--------|
| **Reversibility** | reversible \| recoverable-with-source \| irreversible |
| **Authority** | informational \| advisory \| operative |

MCP exposure is permitted for tools that are **(reversible OR recoverable-with-source) AND (informational OR advisory)**. Anything that is `irreversible` OR `operative` requires CLI invocation.

### Concrete classifications

| Tool | Substrate | Class | MCP-exposed? |
|------|-----------|-------|--------------|
| `mercury_query` | Mercury | reversible / informational | ✅ yes |
| `mercury_stats` | Mercury | reversible / informational | ✅ yes |
| `mercury_graphs` | Mercury | reversible / informational | ✅ yes |
| `mercury_store` (path lookup) | Mercury | reversible / informational | ✅ yes |
| `mercury_version` | Mercury | reversible / informational | ✅ yes |
| `mercury_update` | Mercury | recoverable / operative (writes triples) | ⚠️ kept — within session graph discipline; MCP is the substrate's only write path for AI memory accumulation. |
| `mercury_prune` | Mercury | recoverable-with-source / **operative** | ❌ **REMOVE** |
| `drhook_processes` | DrHook | reversible / informational | ✅ yes |
| `drhook_snapshot` | DrHook | reversible / informational | ✅ yes |
| `drhook_step_*` (eval, breakpoint, etc.) | DrHook | varies / operative on debugged process | ⚠️ retain for now; classify per-tool when DrHook MCP review is opened. Eval that mutates is a candidate for removal under this ADR. |

### Rationale for `mercury_update` retention

Memory accumulation across sessions is the substrate's reason for being. The AI's own session-graph writes ARE the cognitive partnership in practice. Removing `mercury_update` would disable the reflexive-memory discipline (`feedback_memory_reflex`). The discipline is preserved by the per-session-graph isolation: AI writes go into a dated session graph, which is reviewable post-hoc and does not mutate other sessions' data. This is `recoverable / operative`-with-isolation, not `irreversible / operative`.

`mercury_prune`, by contrast, mutates the canonical aggregate — it deletes (or reorganizes via copy-and-switch) data that may have been carefully accumulated across many sessions. There is no isolation; one prune call affects the whole substrate.

### CLI is the audit channel

When pruning (or any destructive operation) is needed, it runs as a `mercury prune` CLI invocation. The shell history of the human running it is the audit trail. The decision to invoke is in the human's command record, not buried in chat transcript.

## Implementation

1. Remove the `Prune` method from `src/Mercury.Mcp/MercuryTools.cs` (lines 171–220).
2. Verify `mercury prune` CLI continues to work (`src/Mercury.Cli/`).
3. Update `MERCURY.md` "Available Tools" table to remove the prune entry; add a note: "Pruning is a destructive operation. Use the `mercury prune` CLI directly — see ADR-006."
4. Audit DrHook MCP surface against this ADR's classification when the DrHook engine ADR (ADR-006/drhook) is implemented.
5. New MCP servers must reference ADR-006 in their initial design.

## Consequences

**Positive:**
- Sovereign-AI failure mode for destructive ops is structurally impossible — the AI cannot invoke what isn't exposed.
- The classification framework makes future MCP additions defensible. "Why is this tool exposed?" gets a clear answer.
- Audit trail for destructive operations is in shell history (the right place), not chat transcript (the wrong place).
- Aligns with the cognitive-partnership model: human is structurally in the loop for consequential decisions.

**Negative:**
- An AI agent that legitimately needs to prune (e.g., maintenance task in an autonomous agent context) cannot do so via MCP. This is intentional — that use case requires explicit human authorization and is not the partnership Sky Omega is designed for.
- Adds a small documentation burden: each new MCP tool needs a classification.

**Neutral:**
- Discoverability of the prune capability slightly decreases for end users — they must read docs/ADRs/CLI help, not just inspect the MCP surface.

## Status transitions

- **Proposed** — 2026-04-30. Pending review.
- **Accepted** — when the classification framework is reviewed and the implementation lands.
- **Completed** — when (1) `mercury_prune` is removed from MCP, (2) MERCURY.md is updated, (3) DrHook MCP surface has been audited against this ADR.

## References

- ADR-005 — Cognitive Component Libraries (the components that will host future MCP surfaces)
- ADR-007 — Sealed Substrate Immutability (companion ADR; pruning Reference is a related concern handled there)
- `src/Mercury.Mcp/MercuryTools.cs` — implementation site
- `MERCURY.md` — documentation site
- Memory: `feedback_together_not_parallel`, `project_governed_automation_thesis` — the principles this ADR operationalizes
