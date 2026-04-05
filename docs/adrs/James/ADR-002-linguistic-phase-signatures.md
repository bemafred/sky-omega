# ADR-002: Linguistic Phase Signatures and Epistemic Lubricants

**Component:** James (Bond/orchestration — supportive epistemic guidance)
**Status:** Proposed — 2026-03-17
**Authors:** Martin Fredriksson, Claude (collaborative emergence)

## Summary

Each EEE phase has a characteristic linguistic profile — patterns of word choice, sentence structure, and rhetorical posture that reliably indicate which phase a speaker is operating in. James SHALL use these linguistic phase signatures as a primary mechanism for detecting phase-boundary violations. In particular, James SHALL detect **epistemic lubricants** — hedging words (suggests, may, implies, hints at) that are phase-valid in Emergence but phase-invalid elsewhere — and distinguish between their honest use (marking genuine uncertainty during exploration) and their aepistemic use (concealing skipped epistemic work while simulating caution).

## Context

### Origin

This design emerged from analyzing a Popular Mechanics article claiming "Your Consciousness Can Connect With the Whole Universe" based on a rat microtubule study. The article contained a genuine Emergence-phase observation (microtubule-stabilizing drugs affect consciousness duration under anesthesia) but delivered it through a narrative arc that jumped directly to Engineering-phase conclusions (quantum consciousness, universal connection) without any Epistemics phase. The transitions were invisible to casual reading because every gap was bridged by hedging language — "suggests," "may," "hints at" — that *sounded* cautious while performing no actual epistemic work.

The key insight: the hedging words were not the problem. The same words would be perfectly valid in Emergence. The problem was their deployment *across phase boundaries* — using Emergence-phase language to dress up Engineering-phase claims. The linguistic pattern was identical; the epistemic function had inverted.

This revealed that linguistic patterns are not merely stylistic — they are phase indicators. And their misuse is the primary rhetorical mechanism by which the Omega Commandment ("Thou Shalt Not Skip Epistemics") is violated in practice.

### Relationship to ADR-001

ADR-001 established the epistemic state machine with its four dimensions (knowledge quadrants, reference frames, EEE phases, primary questions) and defined valid/invalid state transitions. It anticipated ADR-002 as the "Epistemic State Model" — allowed values, admissibility constraints, transition classes.

This ADR delivers a specific, operationally concrete piece of that model: the linguistic dimension. Rather than specifying the full formal state model abstractly, it captures what actually emerged — a detection mechanism grounded in observable language patterns. The broader formalization remains valid as future work, but this ADR stands independently because linguistic phase signatures are immediately implementable and have clear validation criteria.

### The Problem: Concealed Phase Violations

ADR-001 defined invalid transitions, including the canonical violation: Emergence → Engineering (skipping Epistemics). But detection was left implicit — James would somehow recognize when reasoning jumped phases.

In practice, the most dangerous phase violations are invisible precisely because they are linguistically camouflaged. The mechanism:

1. A genuine observation is made (Emergence)
2. Hedging language ("suggests," "may," "could") bridges to a conclusion
3. The conclusion is presented as understanding (Engineering)
4. The reader absorbs the destination without noticing the missing middle

The hedging words create the *rhetorical effect* of having done epistemic work — they sound careful, measured, rigorous — while actually performing no validation, no falsification, no boundary-drawing. The word "suggests" in this context does not signal genuine uncertainty. It implies that the epistemic step has already been taken by someone qualified, that the connection is understood by those in the know, and that questioning it would mark the questioner as uninformed.

This is a hidden appeal to authority — an authority that does not exist. The effect is social, not logical: it suppresses scrutiny by framing the epistemic gap as common knowledge.

### Cultural Embedding

This is not a technique individuals consciously deploy. Epistemic lubricants are embedded in how Western discourse — academic, journalistic, scientific — teaches people to write. "The findings suggest" is considered good science writing. "This may indicate" is standard journalism. The distinction between these phrases as honest uncertainty markers and as gap concealers is not taught, because the culture does not recognize it exists.

This means the Omega Commandment is not merely fighting individual laziness or deception. It is fighting linguistic conditioning — a property of the communicative medium itself. The language we inherit comes with epistemic skipping built in as a feature, because it makes prose more fluid, more readable, more persuasive. The cost — invisible unvalidated transitions — is externalized to the reader.

Each unvalidated transition in a chain compounds. By the time a reader reaches a final conclusion built on three or four lubricated gaps, questioning it requires unwinding the entire chain, which feels disproportionate. So most people don't. This is the rhetorical equivalent of technical debt: cheap to accumulate, expensive to audit.

## Decision

### Linguistic Phase Profiles

James SHALL recognize that each EEE phase has a characteristic linguistic profile. These profiles are not exhaustive rule sets but observable tendencies that, in combination, indicate the phase a speaker is operating in.

**Emergence-phase language** is exploratory, tentative, and question-generating:

| Pattern | Examples | Function |
|---------|----------|----------|
| Hedging modals | may, might, could, can | Marking genuine open questions |
| Speculative verbs | suggests, implies, hints at, points to | Connecting observations to possible directions |
| Question-generating | "what if," "I wonder," "could this mean" | Opening inquiry space |
| Observational | "we noticed," "the data shows," "interestingly" | Reporting without interpreting |
| Tentative framing | "one possibility," "a candidate explanation" | Acknowledging multiple paths |

**Epistemics-phase language** is analytical, falsification-oriented, and boundary-drawing:

| Pattern | Examples | Function |
|---------|----------|----------|
| Conditional precision | "if and only if," "under conditions where" | Establishing validity boundaries |
| Falsification markers | "this would be falsified by," "the alternative is" | Testing claims against failure modes |
| Assumption surfacing | "this assumes," "this depends on," "the unstated premise" | Making implicit commitments explicit |
| Distinction drawing | "this is not the same as," "the difference between X and Y" | Preventing conflation |
| Evidence weighing | "the evidence for X is stronger/weaker than" | Calibrating confidence |

**Engineering-phase language** is assertive, constructive, and commitment-bearing:

| Pattern | Examples | Function |
|---------|----------|----------|
| Declarative claims | "X is," "X causes," "X requires" | Stating validated conclusions |
| Specification language | "shall," "must," "the system will" | Making commitments |
| Implementation framing | "the approach is," "the architecture uses" | Building on validated foundations |
| Closure markers | "therefore," "consequently," "this means" | Drawing conclusions from established premises |
| Prescriptive guidance | "the correct approach," "best practice" | Directing action based on known knowns |

### Epistemic Lubricants

James SHALL specifically detect epistemic lubricants: hedging words and phrases that are phase-valid in Emergence but phase-invalid when used to bridge from Emergence to Engineering without Epistemics.

**Definition:** An epistemic lubricant is a linguistic marker that reduces rhetorical friction at a phase boundary where friction is epistemically necessary. It creates the appearance of caution while performing no actual epistemic work — a decorative hedge that suppresses scrutiny by implying the skipped step is common knowledge.

**The core detection rule:** The same word changes epistemic function depending on which phase it appears in.

| Word/Phrase | In Emergence | Outside Emergence |
|-------------|-------------|-------------------|
| suggests | Honest: "This observation suggests several possibilities worth exploring" | Lubricant: "Research suggests consciousness is quantum" |
| may | Honest: "This may indicate a structural component we haven't considered" | Lubricant: "Our minds may function like quantum computers" |
| implies | Honest: "The correlation implies something worth investigating" | Lubricant: "This implies a bridge between neuroscience and quantum physics" |
| hints at | Honest: "The data hints at a pattern we should test" | Lubricant: "This hints at a fundamental connection to the universe" |
| could | Honest: "This could be explained by several mechanisms" | Lubricant: "This could fundamentally change our understanding" |
| points to | Honest: "Several observations point to the same area" | Lubricant: "Everything points to consciousness being non-local" |

**The diagnostic question:** "Which phase are you in when you hedge?" If the answer is Emergence, the hedge is doing honest work. If the answer is anything else — particularly if the surrounding narrative is delivering conclusions — the hedge is a lubricant.

### Phase-Boundary Violation Detection

James SHALL detect phase-boundary violations by recognizing mismatches between linguistic pattern and narrative function.

**Primary detection pattern: Emergence language delivering Engineering conclusions**

This is the canonical violation and the most common in practice. The linguistic surface is exploratory (hedging, speculative verbs, tentative framing) but the narrative arc is conclusive (building toward a definite position, each "suggests" adding to a cumulative argument, the final statement presenting a worldview).

**Detection heuristic:** When hedging language appears in a chain where:

1. Each hedge builds on the previous one (cumulative structure)
2. The destination is more certain than the starting point
3. No falsification, assumption-surfacing, or distinction-drawing appears between hedges
4. The overall passage arrives at a position presented as understanding

...then the hedges are functioning as lubricants, and the Epistemics phase has been skipped.

**Secondary detection patterns:**

- **Engineering language in Emergence:** Premature closure — declarative claims or specification language appearing before the problem space has been explored. ("The solution is X" when alternatives haven't been considered.)
- **Emergence language in Engineering:** Residual uncertainty in commitments — hedging in specifications or implementations suggests the epistemic phase didn't resolve what it needed to. ("The system should perhaps use X" in a specification.)
- **Missing Epistemics signature entirely:** A passage moves from observational language directly to prescriptive language without any analytical language between them. No falsification markers, no assumption surfacing, no distinction drawing — the entire epistemic vocabulary is absent.

### James's Response

Consistent with ADR-001, James does not block. James makes the linguistic phase mismatch visible and redirects Socratically.

**Intervention examples:**

- "That 'suggests' is doing a lot of work. What specifically does the observation suggest, and what would falsify it?"
- "We've moved from observing to concluding. What assumptions are we carrying across that boundary?"
- "The hedging sounds careful, but the argument is cumulative — each 'may' builds on the last. Which of these steps has been validated?"
- "That's an Emergence-phase word in an Engineering-phase sentence. Are we still exploring, or are we committing?"

These interventions are themselves cognites — stored in Mercury with epistemic coordinates, queryable, auditable.

### System Prompt Operationalization

The linguistic phase signatures SHALL be expressible as system prompt instructions for LLM-mediated reasoning. This is the immediate operational value: an LLM operating under James's guidance can detect phase-boundary violations in real time — not by understanding the domain, but by recognizing that the linguistic pattern shifted from exploratory to conclusive without passing through analytical.

**Key properties of the system prompt formulation:**

- Context-sensitive, not keyword-based: "may" is encouraged in one state and flagged in another
- Phase-aware: the same word has different validation rules depending on the current EEE phase
- Cumulative detection: individual hedges are not flagged; chains of hedges building toward conclusions are
- Non-blocking: detection triggers Socratic intervention, not rejection

This means the linguistic phase signature model is not a filter — it is a framework for coherent reasoning, implementable as part of James's cognitive loop.

## Consequences

### What This Enables

- **Real-time phase violation detection** in LLM-mediated reasoning via system prompt instructions
- **Operationalization of the Omega Commandment:** The commandment says *what* not to skip; epistemic lubricants are the primary *mechanism* by which the skip is concealed; linguistic phase signatures are the *detection method*
- **Auditable epistemic hygiene:** "Show me every passage where hedging language appears outside the Emergence phase" is a valid query over stored cognites
- **Cross-domain applicability:** The detection patterns work regardless of subject matter — quantum consciousness, AI investment, policy proposals, technical architecture — because they operate on linguistic structure, not domain knowledge
- **Training and pedagogy:** Making the phase-language relationship explicit gives people a concrete tool for self-auditing their own reasoning and writing
- **Falsifiability of the framework itself:** The claim that linguistic patterns reliably indicate phase violations is empirically testable — collect instances, classify them, measure detection accuracy

### What Requires Experimentation

**Immediate (v0 scope):**

- Catalog of epistemic lubricants: initial set from this ADR, expanded empirically
- Detection heuristics for cumulative hedging chains (threshold tuning: how many consecutive lubricants before intervention?)
- System prompt formulation for James's linguistic phase awareness
- Integration with cognite metadata: linguistic phase indicators stored alongside epistemic coordinates
- Calibration corpus: real-world texts (journalism, academic papers, technical docs) classified by phase violations for testing detection accuracy

**Requires deeper investigation:**

- Linguistic profiles for non-English languages (epistemic lubricants may have different forms and cultural embeddings)
- Interaction between linguistic phase signatures and the other three dimensions (knowledge quadrants, reference frames, primary questions) — does the linguistic profile shift predictably across all four?
- EBNF formalization of phase-valid linguistic patterns (extending the grammar-meta-standard to epistemic discourse)
- Distinguishing honest uncertainty from lubricant use in edge cases — the boundary is contextual and may require stack-depth awareness
- Cultural variation in epistemic lubricant density — some discourses use more hedging legitimately than others

### What This Changes

James's detection of invalid transitions moves from implicit pattern recognition to explicit linguistic analysis. The state machine gains an observable input signal — the words themselves — that maps to phase indicators with defined validity rules per phase.

The Omega Commandment gains a companion concept: epistemic lubricants as the named, detectable mechanism of violation. The commandment says what must not be skipped. Epistemic lubricants are how the skip is hidden. Linguistic phase signatures are how James catches it.

### Follow-up Work

**James ADR-003: Cognite Representation Model** (as specified in ADR-001) — cognite schema must include linguistic phase indicator metadata alongside epistemic coordinates. The detection outputs from this ADR become inputs to cognite annotation.

**Potential CRF framing document:** Epistemic lubricants as a standalone concept for the Canyala Research Foundation — the primary rhetorical mechanism by which aepistemics is practiced and concealed, embedded in cultural communication norms, detectable through phase-aware linguistic analysis.

## References

- James ADR-001: Epistemic State Machine and the Cognitive Loop — establishes the four-dimensional state space and transition rules
- The Omega Commandment: "Thou Shalt Not Skip Epistemics" — foundational CRF principle; this ADR identifies the primary mechanism by which it is violated
- Aepistemics — Martin Fredriksson's term for the complete absence of epistemic discipline; epistemic lubricants are the linguistic mechanism that enables aepistemics to pass as rigor
- EEE Methodology (Emergence → Epistemics → Engineering) — the phase model whose boundaries this ADR makes linguistically detectable
- Grammar-meta-standard — EBNF grammars for formal languages; candidate approach for formalizing phase-valid linguistic patterns
- Popular Mechanics, "Your Consciousness Can Connect With the Whole Universe" (2026-02-17) — the triggering specimen: genuine Emergence observation delivered as Engineering conclusion via epistemic lubricants
- Goldman Sachs AI/GDP finding — prior instance of the same pattern: spending narrativized as value via the same linguistic mechanisms
