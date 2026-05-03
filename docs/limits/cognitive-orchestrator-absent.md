# Limit: Cognitive orchestrator absent — LLM fluency unmediated by substrate

**Status:**        Latent (mitigated by human-in-the-loop)
**Surfaced:**      2026-05-03, during the 21.3 B Round 1 dispatch + chunk-size work, when LLM fluency-driven errors were caught only by substrate measurement and human review (Martin) within a single session.
**Last reviewed:** 2026-05-03

## Description

Sky Omega's architecture (ADR-005) names **James** as the cognitive orchestrator that sits between the LLM (Sky) and the substrates (Mercury, Lucy, Minerva). James's structural role is to **gate LLM expression through substrate measurement before action** — to enforce that fluent assertions are grounded in measurable system state before they become commits, deletions, or external-facing claims.

Today, James does not exist as code. The gating role is played by the human reviewer (Martin or a future operator) plus the LLM agent's own behavioral discipline (memory entries, EEE methodology, project conventions). The substrate provides the raw material for grounding (Mercury holds the actuals, the disk-trace produces ground truth, build outputs reflect reality), but **nothing structurally enforces that LLM-generated assertions pass through substrate verification before action**.

This works because LLM fluency-vs-actuals failures are caught downstream:
- A wrong commit gets caught by tests, by build failure, or by the next observation cycle
- A confidently misstated number gets challenged by the human reviewer
- A drifted framing gets reframed in the next conversational turn

But the catching is reactive, not structural. The agent can drift; the structure relies on the human noticing.

The 2026-05-03 21.3 B Round 1 session surfaced this acutely. Three caught-by-human cases in a single 24-hour window:
1. **Atom-store dispatch bug** — `--profile Reference` silently used HashAtomStore for cycles 1-3 because the LLM agent didn't grep the dispatch path before recommending the launch command.
2. **FD-cap fluency drift** — agent confidently proposed "raise the OS FD limit" before the user forced the third-path question (which produced the chunk-size answer).
3. **Constant-unification drift** — agent committed "1 GB chunks" to the wrong constant in cycle 5's source change, missing the duplicate `DefaultChunkBufferBytes` in `SortedAtomBulkBuilder`. Cycle 5 ran 256 MB chunks unchanged. Caught only by chunk-count math at 30 minutes elapsed.

In all three cases, the failure mode was the same: **fluent narrative produced confident action that didn't match substrate state**. Human review was the only structural gate.

## Why this is a register entry, not just a session memo

Most limits-register entries are scaling thresholds (Mercury memory pressure, atomstore prefix compression). This one is an architectural deferral: a named substrate component (James) is not yet built. The structural risk it covers is real, current, and accumulating with every session that relies on the human-in-the-loop gating.

Per the register's charter ("between Emergence and Engineering — characterized but not acted on"), the cognitive-orchestrator absence fits exactly. The architecture is named (ADR-005). The need is characterized (this entry). The implementation is deferred. The trigger conditions are concrete.

## Trigger condition

This limit moves toward an ADR / James implementation when one of:

1. **Reduced human supervision becomes operational.** Sky Omega instances running in CI, headless, or as background agents — anywhere the human-in-the-loop gate isn't reliably present.
2. **Rate of LLM-generated assertions exceeds reviewer bandwidth.** When sessions become so prolific that the reviewer can no longer ground every assertion, drift wins by volume even when each individual case is catchable.
3. **James becomes a published deliverable.** The Sky Omega 2.0 trajectory promises a cognitive orchestrator; publishing it as a working component requires the substrate-gating mechanism to exist.
4. **Cross-instance epistemic exchange.** When two Sky Omega instances need to exchange learning ("what did the 2026-05-02 instance know that I don't?"), neither can vouch for the other's assertions through human review. Only structural gating provides cross-instance trust.

## Current state

- **Mercury** provides the substrate (queryable persistent memory, including the cognitive-pattern observations from 2026-05-02 / 03 sessions).
- **Lucy** (deep memory) is named, not implemented.
- **James** (orchestration) is named, not implemented.
- **Sky** (the LLM agent) operates with substrate access via tool calls (Mercury MCP, file system, shell) but no enforced gate between fluent output and committed action.
- **Behavioral discipline** lives in memory entries (EEE, force-third-option, no-vibe-coding, checked-nothing) and project-level conventions (CLAUDE.md, AI.md, naming load-bearing). These are policy, not structure — they fire when the agent remembers them.

The current arrangement is sufficient as long as the human reviewer is available, attentive, and applying EEE in real time. Today's three caught-by-human cases all relied on Martin's awareness; without it, all three would have produced wasted work or wrong substrate state.

## Candidate mitigations

1. **Build James as a thin substrate-gating layer first.** A minimal viable orchestrator would intercept LLM tool-call decisions, query Mercury for relevant invariants, and reject or modify the tool call when assertions don't ground. Less ambitious than full cognitive orchestration; addresses the immediate structural gap.

2. **Pre-James harness scaffolding.** Before building James, the agent harness can enforce EEE-style gating for specific high-stakes classes of action: code commits gated on test-pass evidence, destructive operations gated on user confirmation (already in place via ADR-006 for MCP), substrate edits gated on schema-verification reads.

3. **Surface EEE phase explicitly in agent UX.** The current EEE phase (Emergence / Epistemics / Engineering) becomes a visible tag on agent responses. The agent can't silently jump from Emergence to Engineering without producing the Epistemics evidence trail.

4. **Mercury-backed assertion verification.** Tool-call requests that include claims about system state ("the constant is X", "the dispatch routes Y") get challenged: query Mercury (or grep the source) for the asserted state before action. Rejected if the claim doesn't ground.

The natural sequencing is (3) and (2) before (1) and (4) — instrument and gate first, then build the orchestrator on the data the instrumentation produces.

## Why this matters beyond the obvious

Three secondary effects:

1. **The cross-instance learning thesis depends on this.** Sky Omega's promise that one instance can share learning with another (the "true moat" framing) requires that an instance's assertions be machine-verifiable, not just human-reviewable. Without James, cross-instance exchange would propagate fluency-drift across the corpus.

2. **The lock-in ghost asymmetry.** Sky Omega's structural exorcism (BCL-only, P/Invoke to hardware, no package dependencies) terminates the dependency graph at the OS. But fluency-drift is itself a kind of *cognitive lock-in* — accumulated narrative that becomes hard to challenge structurally. James is the cognitive analog of BCL-only: it terminates the chain of unverified assertion at the substrate.

3. **EEE becomes enforceable, not aspirational.** Current EEE practice is behavioral. Without structural enforcement, it gets bypassed when convenient. James makes EEE a property of the system, not a property of the operator's discipline.

## References

- ADR-005 (cognitive component libraries — Lucy, James, Sky, Minerva) — names James as a deferred component
- `urn:sky-omega:pattern:third-path-dimension-shift` (Mercury) — the meta-pattern that surfaces fluency-drift in option framing
- `urn:sky-omega:incident:21b-fd-crash-2026-05-01` (Mercury) — exemplar of fluency caught by substrate measurement
- 2026-05-03 21.3 B Round 1 session — the surfacing memo for this entry; three caught-by-human cases in one day
- `feedback_force_third_option.md` (memory) — behavioral discipline that James would enforce structurally
- `feedback_no_vibe_coding.md` (memory) — same shape: the discipline depends on the operator remembering it
- Feynman, *Cargo Cult Science* (Caltech 1974) — "the first principle is that you must not fool yourself, and you are the easiest person to fool"
