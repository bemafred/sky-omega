# EEE Methodology: Transitions and Constraints

A practical companion to [The Science of EEE](science-of-eee.md).

```
          ┌─────────────────────────────────────────┐
          │                                         │
          ▼                                         │
    ┌───────────┐       ┌─────────────┐       ┌─────────────┐
    │           │       │             │       │             │
    │ EMERGENCE │──────▶│ EPISTEMICS  │──────▶│ ENGINEERING │
    │           │       │             │       │             │
    └─────┬─────┘       └─────────────┘       └─────────────┘
          │ ▲                                       │
          │ └───────────────────────────────────────┘
          │                                   
          │         ╔═══════════════╗         
          └ ─ ─ ─ ─▶║   FORBIDDEN   ║─ ─ ─ ─ ─ ✗
                    ╚═══════════════╝         
```

——

## Valid Transitions

### Emergence → Epistemics

Explore possibility space, probe boundaries.

**Code:** Spike to see if an idea even makes sense.

**Example:** “Can we parse this format at all?” → Quick parser test.

**Outcome:** Falsified or worth investigating.

### Epistemics → Engineering

Validate assumptions, make constraints explicit.

**Code:** Experiment to test specific claims.

**Example:** “Will this approach scale to 10M records?” → Benchmark.

**Outcome:** Falsified or validated — now you know.

### Engineering → Emergence

Build production system from validated model.

**Discovery:** Implementation reveals unexpected behavior.

**Example:** “Edge case we never considered” → New unknown.

Return to exploration.

——

## Forbidden Transition

### Emergence → Engineering ✗

Idea → Production code without validation.

The spike *becomes* the system.

“It worked in my test” → Now it’s load-bearing.

——

## Where Existing Practices Fit

EEE is the missing upstream discipline — complementary, not competing.

### TDD — Test-Driven Development

**Lives in:** Engineering

**Strength:** “Does this code do what I intended?”

**Assumes:** You know what to test.

**EEE adds:** How do you know your tests test the right thing?

### BDD — Behavior-Driven Development

**Lives in:** Epistemics → Engineering boundary

**Strength:** “Given/When/Then” makes behavior explicit.

**Assumes:** Stakeholders know what behavior they want.

**EEE adds:** How do you know those behaviors are the right ones?

### DDD — Domain-Driven Design

**Lives in:** Epistemics

**Strength:** Ubiquitous language, bounded contexts, aggregates.

**Assumes:** The domain can be modeled coherently.

**EEE adds:** Where did the domain model come from? Was it discovered (Emergence) or assumed?

——

## The Integration

None of these practices are wrong. All are valuable.

EEE asks: **What comes before?**

- DDD gives you a domain model — but did you validate the ontology?
- BDD gives you behaviors — but did you explore alternatives?
- TDD gives you confidence — but confidence in what?

EEE is the upstream discipline that makes the others trustworthy.