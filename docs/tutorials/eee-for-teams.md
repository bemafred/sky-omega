# EEE for Teams

Extending the EEE lens from individual practice to team collaboration.
The individual tutorial teaches you to recognize which mode of thinking is
appropriate. This tutorial asks: what happens when multiple people must
recognize it together?

> **Prerequisites:** Read [Learning to Navigate EEE](learning-to-navigate-eee.md)
> first. This tutorial assumes you understand the three modes, the forbidden
> transition, and the navigation metaphor.

---

## Why Teams Need EEE More

Individual mistakes are local. If you skip Epistemics on your own, you
build something fragile and learn from the pain. The cost is bounded by
one person's time.

Team mistakes amplify. When one person skips Epistemics, their unvalidated
assumptions become shared commitments the moment they're communicated.
Semantic drift -- gradual undetected change in meaning while form remains
stable -- is hard enough to catch alone. In a team, it spreads through
conversations, documents, and code reviews before anyone notices.

Three team-specific risks make EEE more necessary at scale:

1. **Assumption propagation.** An individual's untested assumption, once
   shared in a meeting or merged in a PR, becomes the team's operating
   premise. Rolling it back requires social coordination, not just code
   changes.

2. **Mode disagreement.** Two people working on the same problem may be in
   different modes. One is exploring (Emergence), the other is building
   (Engineering). Neither is wrong individually, but together they produce
   conflict disguised as technical disagreement.

3. **Invisible transitions.** An individual can feel the shift from "I'm
   exploring" to "I'm building." In a team, the shift happens across
   conversations and days. No single moment marks the transition, so no
   one asks whether Epistemics was completed.

---

## The Three Modes at Team Scale

The navigation metaphor from the individual tutorial extends naturally.
Individual EEE is learning to drive. Team EEE is sailing together.

Driving is solo competence -- you read the road, you decide, you act. Sailing
with a crew requires shared awareness: everyone must know the wind, the
course, and what the helmsman intends. Individual skill is necessary but
not sufficient. The crew must also share a model of what's happening and
what's about to happen.

### Emergence at Team Scale

**Individual:** "What exists that I haven't seen?"

**Team:** "What exists that *we* haven't seen -- and are we all looking in
the same direction?"

Team Emergence requires coordinated exploration. Without it, team members
explore redundantly or, worse, explore different things while believing
they're aligned. The goal is collective exploration with shared reporting:
who found what door, what's behind it, and which doors remain unopened.

Cheap experiments still matter, but they must be visible. A team member
running a spike that others don't know about produces knowledge locked in
one person's head.

### Epistemics at Team Scale

**Individual:** "What assumptions am I making?"

**Team:** "What assumptions are *we* making -- and does everyone agree on
which ones have been validated?"

This is the hardest mode for teams. Surfacing assumptions requires
psychological safety. Questioning shared premises can feel like questioning
colleagues. But Epistemics demands exactly this: make claims explicit, then
try to falsify them.

Team Epistemics also requires consensus on what counts as validation. One
person's "we proved it works" may be another's "we ran it once on test data."
The team must agree on what evidence moves a claim from assumption to
knowledge.

### Engineering at Team Scale

**Individual:** "What am I enforcing?"

**Team:** "What are *we* enforcing -- and does everyone know the constraints?"

Team Engineering works when constraints are explicit, shared, and
mechanically enforced where possible. Code review, CI pipelines, type
systems, and linters are Engineering tools because they enforce known rules
without requiring individual vigilance.

Engineering fails at team scale when constraints are implicit -- known to
some members but not others, or "understood" differently by different people.

---

## The Forbidden Transition

Emergence directly to Engineering is the forbidden transition. For
individuals, this means the spike becomes the system. For teams, the
consequences are worse.

When a team skips Epistemics:

- **Hidden assumptions become shared commitments.** An individual's untested
  idea, once discussed in a standup, becomes "what we decided." But no
  epistemic work was done -- no one asked what would falsify it.

- **Technical debt becomes organizational debt.** An individual can refactor
  their own spike. A team that shipped a spike has dependencies, downstream
  consumers, and institutional memory built around the unvalidated design.
  Fixing it requires coordination across people and systems.

- **Disagreements become political.** Without Epistemics, there is no shared
  method for resolving competing claims. "My approach is better" versus
  "No, mine is" has no resolution mechanism. Teams fall back to authority,
  seniority, or exhaustion rather than evidence.

---

## Phase Detection for Teams

The individual tutorial provides diagnostic questions for recognizing which
mode you're in. Here are the team equivalents:

### Are we in Emergence?

- Are we all exploring the same thing, or have some people already decided?
- Are findings being shared, or is exploration happening in silos?
- Can every team member name at least one option we haven't considered?
- Are we comfortable with not knowing yet?

### Are we in Epistemics?

- Does everyone agree on what's currently uncertain?
- Can the team articulate its assumptions as a list?
- Has anyone tried to falsify the leading approach?
- Do we agree on what evidence would change our minds?

### Are we in Engineering?

- Has the team agreed on "done" in reproducible terms?
- Are constraints encoded in tooling (tests, types, CI) rather than
  depending on individual memory?
- Can a new team member understand what's enforced without asking anyone?
- Is variation being reduced or still being introduced?

### The Most Important Question

"Is everyone on the team in the same mode right now?"

If the answer is no -- if some are exploring while others are building --
that mismatch is the most likely source of conflict, rework, and frustration.
Naming it is the first step toward resolving it.

---

## Mode-Mixing Failures at Team Scale

The individual tutorial describes three failure modes. Teams exhibit the
same patterns with amplified consequences.

### 1. "Ship It Now" -- Engineering in Emergence

Requirements are unclear. The team hasn't converged on what to build. But
deadlines or pressure push toward implementation. Someone writes code.
Others review it. The code is "working" so it gets merged. Now the team
is building on a foundation no one validated.

**Individual version:** Premature structure.
**Team version:** Premature consensus. The code's existence creates false
agreement. "We built it, so we must have decided."

### 2. "We've Always Done It This Way" -- Engineering Without Epistemics

A rule or pattern exists in the codebase. New team members follow it.
Original team members follow it from habit. No one remembers *why* the
rule exists or whether the conditions that motivated it still hold.

**Individual version:** Cargo culting.
**Team version:** Institutional cargo culting. The rule survives team
turnover and becomes more entrenched precisely because no one left can
explain it.

### 3. "We're Still Figuring It Out" -- Emergence in Engineering

The system is in production. Users depend on it. But the team keeps
redesigning core components, introducing new patterns, or "trying things
out" in the production codebase. Each change is individually reasonable.
Collectively, they erode stability.

**Individual version:** Unsafe improvisation.
**Team version:** Collaborative unsafe improvisation, where multiple people
introduce variation simultaneously and no one has a complete picture of
what changed.

### 4. "Let's Discuss This More" -- Stuck in Epistemics

The team has evidence. The assumptions are surfaced. The constraints are
known. But no one transitions to Engineering because there's always one
more question, one more concern, one more scenario to consider.

**Individual version:** Analysis paralysis.
**Team version:** Collective analysis paralysis, amplified by the
democratic instinct that everyone's concern deserves equal investigation
regardless of its likelihood or impact.

---

## Epistemic Governance

Who decides what the team knows? Who declares that an assumption has been
validated? Who authorizes the transition from Epistemics to Engineering?

These are governance questions, and different teams will answer them
differently:

### Architect Authority

One person (or a small group) holds epistemic authority. They decide when
exploration has produced enough options, when assumptions have been
sufficiently validated, and when the team has earned the right to build.

**Strength:** Clear decision-making. Fast transitions.
**Risk:** Single point of failure. If the authority skips Epistemics, the
whole team skips it.

### Team Consensus

The team collectively decides when to transition. Everyone must agree that
assumptions are validated before Engineering begins.

**Strength:** Shared ownership. Multiple perspectives catch blind spots.
**Risk:** Slow transitions. Vocal skeptics can block progress. Consensus
can be confused with unanimity.

### Evidence-Based Thresholds

The team defines upfront what evidence would trigger a transition. "If the
benchmark shows < 100ms latency, we proceed." "If three of five user
interviews confirm this need, we build it."

**Strength:** Objective. Removes personal authority from the decision.
**Risk:** Choosing the wrong threshold. Optimizing for measurability rather
than importance.

### Code Review as Epistemic Gate

Code review is often treated as an Engineering activity (does the code work?
does it follow conventions?). But it can also serve as an epistemic gate:
does this change reflect validated assumptions? Does the PR description
explain *why*, not just *what*?

A review comment that says "I don't understand why this approach was
chosen over X" is an epistemic question. It deserves an epistemic answer --
evidence, reasoning, or an honest "we're not sure yet, this is still
exploratory."

### Testing as Epistemics Boundary Detector

Tests encode what must be true. When a test fails, it signals that a
previously validated assumption no longer holds. This is a transition
signal: the team may need to return from Engineering to Epistemics (or
even Emergence) to understand what changed.

A test suite that no one trusts -- where failures are routinely ignored
or tests are skipped -- is a sign that the team's epistemic foundation
has eroded.

---

## Shared Semantic Memory

Mercury can support team EEE by making decisions queryable rather than
buried in chat history.

### Decisions as Triples

Instead of "we decided this in the Slack thread from three weeks ago,"
store decisions as structured data:

```turtle
@prefix sky: <urn:sky-omega:> .
@prefix dc: <http://purl.org/dc/terms/> .
@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .

<urn:decision:auth-approach> a sky:Decision ;
    dc:title "Use JWT for API authentication" ;
    sky:phase <urn:sky-omega:data:engineering> ;
    sky:rationale "Validated via prototype (3 approaches compared). JWT chosen for statelessness and library maturity." ;
    sky:decidedBy "Team consensus, sprint review 2026-02-10" ;
    dc:date "2026-02-10"^^xsd:date .
```

This is queryable: "What decisions are we currently in Engineering on?"
"What was the rationale for our auth approach?" "Which decisions were made
without an Epistemics phase?"

### bootstrap.ttl as Shared Reference

The project's `docs/knowledge/bootstrap.ttl` encodes EEE phases and the
forbidden transition as RDF. This means the methodology itself is
queryable:

```sparql
SELECT ?from ?to WHERE {
  ?t a sky:Constraint ;
     sky:status "forbidden" ;
     sky:from ?from ;
     sky:to ?to .
}
```

A team using Mercury can load this bootstrap and extend it with their own
decisions, principles, and constraints.

### The Federation Vision

Mercury instances can be personal or shared:

- **Personal:** Each team member's MCP store captures their individual
  exploration and learning.
- **Team:** A shared Mercury instance holds team decisions, validated
  assumptions, and agreed constraints.
- **Organizational:** Federated queries across team instances surface
  patterns and shared knowledge.

This is a vision, not a current capability. Mercury supports the storage
and query layers. The federation and curation workflows are future work.

### Knowledge Curation vs. Knowledge Capture

Storing everything is not the goal. A team Mercury instance should contain
curated knowledge -- decisions that were actively validated, assumptions
that were explicitly tested, constraints that were deliberately chosen.
Raw meeting notes and brainstorming sessions belong elsewhere.

The distinction matters: knowledge capture produces noise that makes
querying useless. Knowledge curation produces signal that makes querying
valuable.

---

## Integration with Existing Practices

EEE is not a replacement for existing development practices. It is an
upstream lens that clarifies when those practices are appropriate.

### Agile

| Agile Practice | EEE Mode | Connection |
|---------------|----------|------------|
| Sprint planning | Emergence | Identifying what to explore this sprint |
| Refinement | Epistemics | Surfacing assumptions, defining acceptance criteria |
| Implementation | Engineering | Building within validated constraints |
| Retrospective | Emergence | Discovering what went wrong or unexpectedly well |

The anti-pattern: treating sprint planning as Engineering ("we know what to
build, assign the tasks") when the team is actually in Emergence ("we're
not sure what the right approach is").

### Domain-Driven Design

DDD's ubiquitous language is an Epistemics artifact. It emerges from
exploration (Emergence: what terms do stakeholders actually use?), is
validated through dialogue (Epistemics: do these terms have stable,
shared meaning?), and is enforced in code (Engineering: the type system
reflects the domain model).

The anti-pattern: defining the ubiquitous language in a single workshop
without subsequent validation. The language feels agreed-upon, but
different people carry different meanings for the same terms.

### Architecture Decision Records

ADRs are Epistemics artifacts. They document what was considered
(Emergence), why a particular option was chosen (Epistemics), and what
constraints that choice imposes (Engineering boundary).

The anti-pattern: writing ADRs after implementation to justify decisions
already made. This reverses the EEE flow -- Engineering happened first,
then Epistemics was retrofitted to explain it.

### Code Review

Code review can operate at all three levels:

- **Engineering review:** Does it compile? Does it pass tests? Does it
  follow conventions?
- **Epistemics review:** Does this change reflect a validated assumption?
  Is the approach justified?
- **Emergence review:** Does this change reveal something we didn't know?
  Should we explore further before committing?

Most teams operate only at the Engineering level. The missed opportunity
is treating code review as a place where epistemic questions are welcome.

---

## Honest Boundaries

This tutorial describes a conceptual extension of EEE from individual to
team practice. It is important to be clear about what it does and does
not provide.

**What this tutorial provides:**

- A framework for thinking about EEE in team contexts
- Diagnostic questions adapted for groups
- Patterns that connect EEE to familiar practices
- Vocabulary for naming team-specific failure modes

**What this tutorial does not provide:**

- **Validated workshop formats.** No facilitated EEE workshops have been
  run with teams. The diagnostic questions and governance models are
  extrapolations from individual practice, not tested facilitation
  protocols.
- **Empirical evidence from multi-person teams.** The evidence base for EEE
  is individual practice augmented by LLM dialogue. Team dynamics
  introduce social and organizational factors that have not been tested.
- **Prescriptive governance recommendations.** The architect authority,
  team consensus, and evidence-based threshold models are options, not
  recommendations. Which works best depends on team culture, domain, and
  organizational context.

This is conceptual scaffolding for future workshop patterns. The individual
EEE tutorial was grounded in navigation competence that readers already
possess. This team extension is grounded in team collaboration experience
that most readers also possess -- but the specific connection between EEE
and team dynamics has not yet been validated in practice.

The appropriate EEE mode for this tutorial itself is Emergence: these are
doors we've identified but not yet opened.

---

## See Also

- [Learning to Navigate EEE](learning-to-navigate-eee.md) -- the individual
  EEE tutorial this extends
- [Epistemic Clean Architecture](../process/e-clean-and-semantic-architecture/01-epistemic-clean.md) -- operationalizing Epistemics in system design
- [EEE Methodology Transitions](../process/emergence-epistemology-engineering/eee-methodology-transitions.md) -- valid and forbidden transitions
- [bootstrap.ttl](../knowledge/bootstrap.ttl) -- EEE phases and principles
  as queryable RDF
