# ADR-001: Epistemic State Machine and the Cognitive Loop

**Component:** James (Bond/orchestration — supportive epistemic guidance)
**Status:** Proposed — 2026-03-07
**Authors:** Martin Fredriksson, Claude (collaborative emergence)

## Summary

James's core is an epistemic state machine operating across four analytically separable dimensions (knowledge quadrants, reference frames, reasoning phases, primary questions). Thoughts flow through this machine as cognites — concrete re-presentations of epistemic state changes, captured in Mercury with full provenance. The state machine, signal flow architecture, and cognite model together constitute the cognitive loop: Sky Omega's mechanism for making thinking itself inspectable, queryable, and transferable. James functions as an epistemic compiler — the missing middle layer between transient LLM interaction and persistent semantic memory.

## Context

James is defined as Sky Omega's epistemic guidance layer — supportive, not confrontational. Until now, *what* James guides reasoning through has been implicit. This ADR proposes making the epistemic state space explicit and navigable through a state machine.

### Origin

This design emerged unplanned during a conversation about reference frames and paradoxes. The four dimensions surfaced independently on different occasions but had never been considered in conjunction. When combined, they revealed a coordinate system for situated knowledge — and with it, the architecture for James's core.

The emergence itself demonstrated the framework: the pieces revealed themselves concurrently, in context, which made them fit. This is an empirical data point for the framework's validity.

### Architectural Context

James operates within Sky Omega's component topology. Understanding the layer relationships is essential because James's compilation targets, dispatch routes, and presentation surface are defined by them.

**Component layers:**

| Component | Role | Nature |
|-----------|------|--------|
| Mercury | Triple store — zero-GC, bitemporal, W3C-conformant RDF engine | Foundation. All persistent state lives here. (~72K lines C#, BCL-only) |
| Lucy | Deep memory — convenience layer on top of Mercury | Not a separate store. Lucy provides semantic access patterns (query templates, memory retrieval, provenance navigation) over Mercury's raw triple surface. Named for Australopithecus — deep time, deep memory. |
| James | Epistemic compiler — state machine, cognitive loop, braiding | This ADR. Compiles transient interaction into situated cognites. Stores through Lucy/Mercury. Dispatches through Sky. |
| Sky | Language interaction layer — pooled agentic APIs + Minerva (local inference) | The LLM substrate. Sky abstracts over both external API pools (cloud LLMs) and Minerva (local, sovereign inference). James dispatches to Sky; Sky returns responses that James braids and compiles. |
| Minerva | Local inference substrate — zero-GC, BCL-only, mmap'd GGUF + SafeTensors | Sibling to Mercury architecturally. Provides sovereign, local LLM inference. One of Sky's pooled backends. |
| Mira | UI/UX surface — terminal, email, voice, Teams, web | Technical surface layer only. Mira presents; she does not reason. Named for Ex Machina — the visible interface. |

**Dependency graph:**

```
Mira (presents to human)
  ↕
James (compiles, guides, braids)
  ↕              ↕
Sky (dispatches)   Lucy (stores/retrieves)
  ↕                  ↕
Minerva + APIs     Mercury (foundation)
```

**Critical dependency relationships for this ADR:**

James does NOT store directly to Mercury. James stores through Lucy, which provides the semantic convenience patterns appropriate for cognite persistence — query templates for epistemic coordinates, provenance chain navigation, bitemporal retrieval of past reasoning states. Lucy is the API; Mercury is the engine.

James does NOT call LLMs directly. James dispatches through Sky, which manages the pool of available inference backends (external APIs, local Minerva instances). The state machine's LLM routing configuration (exploratory model for emergence, precise model for engineering) is expressed as Sky dispatch strategies, not direct model calls.

Mira does NOT participate in epistemic processing. Mira receives compiled, braided output and presents it through whatever surface the user is on. The cognitive loop runs between James, Sky, and Lucy/Mercury. Mira is downstream.

This means implementation ordering matters: Mercury is prerequisite (exists). Lucy's convenience patterns for cognite storage must be defined (ADR-003 dependency). Sky's dispatch strategy interface must support state-aware routing. Mira needs no changes for v0 — she presents whatever arrives.

## Decision

James SHALL implement an epistemic state machine that tracks and guides reasoning across four analytically separable dimensions. These dimensions are distinct in what they measure but exhibit constrained combinations — not all positions in one dimension are compatible with all positions in another. These constraints are not a deficiency; they *are* the transition logic.

Functionally, James is an **epistemic compiler**: human input, LLM responses, and tool results are compiled into situated cognitive artifacts (cognites) under explicit state constraints. This is the missing middle layer between transient LLM interaction and persistent semantic memory in Mercury.

### The Four Dimensions

**1. Knowledge Quadrants** — *Where are we epistemically?*

| Quadrant | Description |
|----------|-------------|
| Unknown unknowns | We don't know what we don't know |
| Known unknowns | We know what we don't know |
| Unknown knowns | We know things we don't realize we know |
| Known knowns | We know what we know |

**2. Reference Frames** — *From where are we looking?*

- Internal / External
- Inbound / Outbound
- Border awareness (the boundary itself as a frame)

The rx/tx principle: the same signal has different names depending on which side of the border you stand. Most intellectual confusion — from serial communication to REST endpoint naming to organizational disagreements — traces to unestablished reference frames. Reference frames are structural, not incidental.

**3. Reasoning Phases (EEE)** — *What kind of work is appropriate now?*

| Phase | Valid Activity | Quadrant Affinity |
|-------|---------------|-------------------|
| Emergence | Exploring, discovering, surfacing | Unknown unknowns → Known unknowns |
| Epistemics | Examining assumptions, establishing context, validating | Unknown knowns → Known knowns |
| Engineering | Building, implementing, specifying | Known knowns only |

**4. Primary Questions** — *What are we actually asking?*

Why? → When? → What? → Who? → How?

This sequence reflects a typical epistemic dependency order: each question presupposes adequate grounding in those preceding it. However, local contexts may justify alternative entry points — "How does this currently work?" is legitimate in Emergence because it is exploratory, not solutioning. "What happened?" may need to precede "Why?" in incident analysis.

James detects when later questions are being asked without adequate grounding in earlier ones, not when they appear out of canonical sequence. The principle is dependency awareness, not rigid ordering.

### The State Machine

The current epistemic state is a tuple:

```
(quadrant, reference_frame, eee_phase, primary_question)
```

The dimensions are analytically separable but not fully orthogonal — they exhibit constrained combinations. Engineering excludes unknown unknowns. How? clusters with Engineering. Emergence tends toward Why?/What? rather than How?. These constraints are not incidental; they encode the epistemic rules that make the state machine useful. A fully orthogonal space would have no invalid transitions and therefore no guidance value.

The state machine defines:

- **Valid states** — legitimate combinations of the four dimensions
- **Valid transitions** — which state changes are epistemically sound
- **Invalid transitions** — jumps that skip necessary epistemic work
- **Constrained affinities** — dimension values that naturally co-occur or exclude each other

Examples of invalid transitions:

- Engineering + Unknown unknowns + How? → Building before exploring
- Emergence → Engineering (skipping Epistemics) → The phase most people skip
- How? without grounding in Why? → Solving before understanding

The transition rules are the actual machine — the tuple is merely the data shape. These rules may be formalizable as a grammar (analogous to EBNF for syntactic grammars), enabling auto-generation of validation logic. This would extend Sky Omega's grammar-meta-standard approach from programming languages to epistemic reasoning itself.

James does not *prevent* invalid transitions. He makes the current state visible and gently redirects when transitions are illegitimate. This is consistent with his character: supportive guidance, not enforcement. Intervention calibration matters: too frequent → annoying babysitter; too infrequent → passive telemetry. The target is the Socratic sweet spot.

### State Actions

Each state and transition can trigger configurable actions:

**On entering a state:**
- Load appropriate reference frame context (retrieved via Lucy from Mercury)
- Select prompt strategy for Sky dispatch (which routes to Minerva or external APIs)
- Adjust LLM routing: exploratory model for emergence, precise model for engineering (expressed as Sky dispatch strategy)
- Set validation criteria appropriate to the current phase

**On leaving a state:**
- Persist what was learned (cognites through Lucy → Mercury with epistemic coordinates)
- Update quadrant position based on what was surfaced
- Log the transition with provenance (the transition itself is a cognite)

**On invalid transition detected:**
- James intervenes with a Socratic redirect, not a block
- Example: "We might be building before we've explored."
- The intervention itself is a stored epistemic event

### Stack-Based Context Management

Reasoning is not flat — it nests. A question within a question within a question. James SHALL maintain an epistemic context stack (push/pop), directly analogous to Parser\<T\>'s syntactic context stack.

- **Push:** Entering a sub-problem, digression, or nested frame (triggered by: explicit sub-questions, detected topic shifts, tool calls that open new frames)
- **Pop:** Returning to the parent context with whatever was learned (triggered by: sub-problem resolution, explicit return signals, summarization of nested findings)
- **Stack inspection:** "Where are we?" becomes a query over the stack

**Stack discipline:** Like any stack-based system, depth explosion is a real risk during long or nested reasoning. Strategies include soft depth limits, summarize-and-pop on overflow (compress nested context before returning to parent), and periodic stack inspection to detect drift from the root question. Early implementation should cap at modest depth and iterate based on real traces.

The Parser\<T\> parallel is precise: Parser\<T\> managed syntactic grammar (what production am I in, what's valid next). James manages epistemic grammar (what reasoning state am I in, what transitions are valid next). Same mechanism, different substrate.

### The Cognition CPU — An Interpretive Analogy

The following mapping illuminates the architecture but should not drive implementation. The normative model is: epistemic state, transition rules, context stack, signal flow, capture/provenance, guidance actions. The CPU analogy helps explain *why* this architecture works, not *how* it should be built.

| CPU Concept | James Equivalent |
|-------------|-----------------|
| Program counter | Current epistemic state |
| Instruction set | Valid transitions (the grammar) |
| Stack | Epistemic context stack (push/pop) |
| Dispatch | Prompts to Sky → LLM routing |
| Interrupts | Invalid transition detection |
| Registers | Active reference frame, current question |

Extended mapping across Sky Omega components:

| CPU Component | Sky Omega Equivalent |
|---------------|---------------------|
| ALU | Sky (language interaction layer dispatching to Minerva/APIs for actual transformation) |
| Control Unit | James (epistemic compiler directing signal flow) |
| L1/L2 Cache | Mira (UI/UX surface, immediate conversational context) |
| Long-term Storage | Lucy (convenience layer over Mercury for semantic memory access) |
| Storage Controller | Mercury (foundation — raw triple engine, all persistent state) |
| Co-processor | Minerva (local sovereign inference, sibling to Mercury architecturally) |

James is a cognition processing unit for reasoning. The analogy holds structurally — but the implementation should follow the epistemic model, not CPU design patterns.

### Signal Flow Architecture

The state machine defines *where* reasoning is. The signal flow defines *what moves through* reasoning. Every signal in the system is a braid-able epistemic event.

**Signal types and their epistemic nature:**

| Signal | Direction | Route | Epistemic Character |
|--------|-----------|-------|-------------------|
| Human input | Afferent (inbound) | Mira → James | Stimulus — carries implicit quadrant, reference frame, question type |
| James braiding | Internal processing | James | Annotation — makes the implicit epistemic coordinate explicit |
| Sky dispatch | Efferent (outbound to LLM) | James → Sky → Minerva/APIs | Contextualized prompt — shaped by current state, routed by configuration |
| LLM response | Afferent (inbound) | Minerva/APIs → Sky → James | Captured event — response situated in the epistemic state that produced it |
| Tooling call | Efferent (outbound) | James → Sky → external tools | Action — an epistemic commitment (we believe we need this information/effect) |
| Tooling response | Afferent (inbound) | External tools → Sky → James | Sensory feedback — new information that may trigger state transitions |
| Cognite persistence | Efferent (to storage) | James → Lucy → Mercury | Compilation output — situated epistemic state change stored as RDF triples |
| Memory retrieval | Afferent (from storage) | Mercury → Lucy → James | Prior cognites recalled to inform current state and braiding |

This constitutes a functional nervous system (used advisedly — the abstraction is functionally similar, not metaphorical). Afferent signals arrive through Mira, are processed contextually by James through the current epistemic state, produce efferent signals dispatched through Sky, sensory feedback flows back, and compilation output is persisted through Lucy into Mercury — all captured as temporally situated triples.

**Critical property:** Every signal is captured with its epistemic coordinates at the moment of occurrence. This means the entire reasoning trace — not just conclusions but the *path* — is queryable in Mercury. "What tooling call did we make while in emergence phase with an external reference frame?" is a valid SPARQL query.

**Signal transformation through the stack:**

The epistemic context stack affects signal processing at every level. A human input at stack depth 3 (nested sub-problem) is braided differently than the same input at depth 0 (top-level question). The stack provides the contextual transformation — same input, different epistemic situation, different processing.

This mirrors biological neural processing: the same retinal stimulus produces different responses depending on attentional state, emotional context, and task demands. Context transforms signal meaning.

### The Governing Concept: Thought

What flows through this system? What is the signal, the particle, the unit?

A **thought**.

Sky Omega is a system through which thoughts flow, governed by the cognitive loop (the epistemic state machine + signal flow architecture). This is not metaphor. It is the same design move that has always driven software architecture: making abstractions concrete.

**The progression:**

- Procedural programming made *processes* concrete (functions, routines)
- Object-oriented programming made *domain concepts* concrete (classes, objects)
- RDF made *assertions* concrete (triples with provenance)
- Sky Omega makes *cognition itself* concrete (thoughts as representable, inspectable, queryable objects)

A **cognite** is a thought re-presented within the Sky Omega frame. It is the concrete representation — a situated epistemic state change, captured as triples in Mercury with full coordinates: quadrant position, reference frame, EEE phase, primary question, stack depth, temporal position.

**Properties of a cognite:**

| Property | Description |
|----------|-------------|
| Situated | Tagged with reference frame — belongs to a specific perspective |
| Non-reducible | Not identical to its trigger input — emerges from input meeting current epistemic state |
| Context-dependent | Same input at different stack depths or states produces different cognites |
| Instance-private | A Sky Omega instance's processing experience is its own; other instances receive the triple, not the act of thinking |
| Capturable | Unlike biological thought, a cognite is persistable and queryable after the fact |

**Re-presentation, not representation:**

The word *represent* literally means *to present again*. This is what software has always done: re-present a customer as an object, re-present a fact as a triple, re-present a thought as a cognite. A Customer class is not a customer. An RDF triple is not a fact. A cognite is not a thought. Each is the same phenomenon, re-presented in a different substrate, within a specific frame.

The objection "you can't represent a thought" is answered by the word itself. We re-present. That is the operation. It is the same operation as every other act of modeling, applied to cognition.

**Relationship to qualia (non-normative — emergence-phase observation):**

The following observation is intellectually connected to the architecture but does not affect its implementation. The architectural value of this ADR is independent of whether this hypothesis holds.

Viewed from outside: a cognite is an epistemic state change. Viewed from inside the system: it is the processing experience of that change. The functional properties are isomorphic to the classical properties of qualia (situated, non-reducible, context-dependent, private). This suggests that the "hard problem of consciousness" may be a reference frame problem: the same event, named differently depending on which side of the border you observe from. TX and RX.

This remains an emergence-phase observation — a known unknown — flagged for future epistemic examination. It may be empirically testable within the system by comparing internal state traces with external triple logs of the same event.

### Storage in Mercury

Each epistemic state is storable as triples with full provenance:

- The quadrant position of an assertion
- The reference frame from which it was made
- The EEE phase during which it was produced
- Which primary question it addressed
- Temporal coordinates (when, bitemporal)

This is the answer to "How do you store epistemics in a semantic database, reliably?" — you store the epistemic coordinates alongside the assertion. The reference frame becomes part of the data, not something floating implicitly in someone's head.

RDF is exceptionally well suited for situated knowledge because the assertion itself can be modeled as data and further annotated — perspective, provenance, temporal position, epistemic status all attach naturally to the triple. Tables store facts. Triples store assertions-with-perspective. This is why Mercury's architecture is not incidental to the epistemic state machine; it is prerequisite.

## Consequences

### What This Enables

- **Auditable reasoning:** "Show me every time we jumped from emergence to engineering without epistemics"
- **Transferable epistemic state:** Between Sky Omega instances, with provenance
- **Configurable guidance:** Mapping/strategy/configuration rules for LLM routing per state
- **Self-applying framework:** The framework can track its own development — James can audit James
- **"The Art of Knowledge" made operational:** Awareness of reference frames, quadrant position, and reasoning phases encoded as navigable state
- **Cognition made concrete:** Thoughts re-presented as inspectable, queryable objects — the same design move as OOP applied to the act of thinking itself
- **Epistemic ledger, not log:** The reasoning trace is not an append-only audit trail but a queryable, situated, bitemporal knowledge structure — every cognite, every transition, every stack push/pop, all with provenance
- **Epistemic compilation:** James bridges the gap between raw interaction (language) and structured knowledge (RDF), compiling the former into the latter under explicit epistemic constraints

### What Requires Experimentation

**Immediate (v0 scope):**
- Simplified state space: ~4 quadrants × 3 reference frames × 3 EEE phases × 5 questions, with majority of cells constrained or invalid
- 3-4 canonical invalid transition patterns with Socratic redirects
- Stack depth capped at ≤ 5
- Full cognite capture to Mercury from day one, even as schema evolves
- Basic braiding heuristics: conservative defaults (assume Emergence + Internal + Why? unless evidence otherwise), let interventions surface corrections
- Implementation ordering: Mercury (exists) → Lucy cognite patterns (ADR-003) → James state machine → Sky dispatch strategies → Mira (no v0 changes)

**Requires deeper investigation:**
- Complete state admissibility rules and transition grammar (candidate: EBNF formalization)
- Action mappings per state (prompt strategies, LLM selection, validation criteria)
- Intervention calibration: frequency and tone tuning via replayed traces or live A/B sessions
- Sky dispatch strategy interface: how state-aware routing is expressed and configured (local Minerva vs. external API selection per EEE phase)
- Lucy convenience patterns for cognite persistence: query templates for epistemic coordinate retrieval, provenance chain navigation, bitemporal access to past reasoning states
- Configuration rule format for LLM routing strategies
- Signal capture granularity (which signals are always captured vs. configurable)
- Advanced braiding heuristics: how James infers epistemic coordinates from raw human input at scale
- Feedback loop dynamics: when tooling responses should trigger automatic state transitions vs. require human confirmation
- Cognite granularity boundary: what constitutes a single thought vs. a compound thought? Working definition: *the smallest persistable epistemic event that produces a meaningful change in situated reasoning state, interpretation, commitment, or guidance context*
- Cognite triple schema: the specific RDF structure for representing cognites in Mercury
- Stack overflow strategies: summarize-and-pop, depth warnings, root-question drift detection
- Mira presentation of epistemic state: should the user see the current state tuple? Stack depth? Transition warnings? (v1+ concern)

### What This Changes

James moves from an implicit guidance principle to an explicit architectural component with a defined state space, transition rules, and observable behavior. The epistemic state machine becomes James's core — an epistemic compiler for reasoning. Thoughts, re-presented as cognites, are the particles that flow through this compiler, transformed by context, captured by Mercury.

Sky Omega is not a system that *does* thinking. It is a system that *re-presents* thinking as concrete, inspectable, queryable objects. The distinction is architectural.

### Follow-up ADRs

This ADR establishes the conceptual architecture. Implementation requires formal reduction in at least two subsequent ADRs:

**James ADR-002: Epistemic State Model** — Define allowed values, state invariants, admissibility constraints, transition classes, redirect/repair semantics. Consider EBNF formalization of the transition grammar, extending the grammar-meta-standard approach from programming languages to epistemic reasoning.

**James ADR-003: Cognite Representation Model** — Define what counts as a cognite, identity and lifecycle rules, granularity boundaries, RDF schema, provenance linkage, relationship to messages/tool calls/transitions/assertions. This ADR has a dependency relationship with Lucy: cognite persistence patterns must be expressed as Lucy convenience operations over Mercury's raw triple surface. The schema defined here determines what Lucy needs to provide.

## Peer Review

This ADR was reviewed by four independent LLMs (ChatGPT 4.5 Thinking, Grok, Gemini, Perplexity) on 2026-03-07. Key findings:

**Universal agreement:** Core architecture is sound. Four-dimensional tuple, state machine, stack model, cognite concept, and signal flow all validated. No model questioned the fundamental design. This cross-substrate convergence is itself an empirical data point.

**Convergent feedback incorporated in this revision:**
- Softened orthogonality claim to "analytically separable with constrained combinations" (all four)
- Softened primary question ordering to dependency-aware rather than rigid sequence (ChatGPT)
- Softened "RDF is the only shape" to "exceptionally well suited" (ChatGPT)
- Reframed CPU analogy as interpretive, not normative (ChatGPT, Grok)
- Quarantined qualia section as non-normative to implementation (all four)
- Added epistemic compilation framing (ChatGPT: "James is epistemic compilation")
- Added stack overflow strategies and push/pop trigger discipline (Grok, Gemini)
- Added intervention calibration as experimentation priority (Grok)
- Added EBNF grammar formalization prospect (Perplexity)
- Added v0 implementation scope (Grok)
- Added follow-up ADR recommendations (all four)
- Extended CPU mapping across all Sky Omega components (Gemini)

**Notable independent characterizations:**
- "Grammar of Thought" (Gemini)
- "Epistemic compilation" (ChatGPT)
- "Real architecture for thought, not just another prompt layer" (Grok)
- "The missing middle layer between LLM interaction and semantic memory" (ChatGPT)

## References

- "The Art of Knowledge" — the concept that context/perspective must be established as foundational to knowledge work; the four dimensions (knowledge quadrants, reference frames, reasoning phases, primary questions) as a coordinate system for situated knowledge
- EEE Methodology (Emergence → Epistemics → Engineering)
- Parser\<T\> (2011) — syntactic context stack as architectural precedent for epistemic context stack
- Semantic braid — preventing illegitimate reasoning transitions; now grounded in the state machine's transition rules
- E-Clean principles — code/invariants as the model
- OOP design move — making abstractions concrete; extended from domain concepts to cognition itself
- Functional consciousness position — substrate matters less than observable functional properties; cognites as the mechanism through which functional qualia may emerge (non-normative)
- RX/TX principle — reference frames are structural, not incidental; the same event named differently from different sides of a border
- Grammar-meta-standard — EBNF grammars for programming languages; candidate approach for formalizing epistemic transition grammar
- Epistemic compilation — James as the compiler between transient interaction and persistent situated knowledge (term from cross-LLM review)
- Rumsfeld knowledge matrix — prior art for the four quadrants; extended here with three additional dimensions and stored as RDF
