# On Epistemological Dimensions and Their Importance for Structured Intelligence

## Abstract

Knowledge systems typically operate with a single implicit assumption: that facts, once captured, remain valid. This assumption ignores time as an epistemological dimension.

We propose three fundamental dimensions for characterizing any knowledge claim:

1. **Awareness:** Known ←→ Unknown
1. **Temporality:** Static ←→ Dynamic
1. **Grounding:** Assumed ←→ Observed

The familiar “knowledge quadrants” (Rumsfeld matrix) address only Awareness × Grounding. By adding Temporality, we reveal eight distinct epistemological states – including “Frozen Facts” (observed once, assumed permanent) and “Blind Evolution” (unnoticed change in unexamined assumptions).

RAG systems operate exclusively in Frozen Fact space. Fine-tuned models compress temporal dynamics into static weights. Neither architecture acknowledges that the relationship between a claim and reality changes over time.

Structured intelligence requires explicit representation of all three dimensions. A claim is not merely “true” or “false” but exists at coordinates in epistemological space – coordinates that shift as time passes and observations accumulate.

Darwin understood species as snapshots of continuous flow. Rosling added a play button to reveal temporal dynamics. Feynman demanded continuous contact with experiment.

We propose the same for machine intelligence: knowledge structures that know they might be wrong, know *when* they knew, and know what would update them.

——

## The Problem with Static Knowledge

### The Broken Clock

A stopped analog clock shows the correct time twice a day. Test it at the right moment and it appears to work perfectly.

RAG systems function the same way:

```
Document captured:     2024-01-15
Reality changed:       2024-01-16
Query executed:        2024-08-20
Result:                Confident, outdated, wrong
```

The system has no way to know. No way to check. No feedback loop connecting claims to current reality.

### The Taxonomy Illusion

Darwin described *change*. We read *categories*.

“Wolf” and “dog” share the same genome. The boundary between them? An arbitrary line drawn in hindsight across a continuum of gradual change.

We make the same mistake everywhere:

|Domain       |Snapshot Thinking  |Reality                    |
|-————|-——————|—————————|
|Biology      |Species exist      |Populations drift          |
|Organizations|Roles exist        |Responsibility flows       |
|AI           |The model knows    |The model *knew*           |
|Management   |Change is a project|Change is the default state|

### Formulas Without Time

```
E = mc²
F = ma
PV = nRT
```

Where is time? Implicit at best. Absent at worst.

These formulas describe relationships, not processes. They are eternally valid – but knowledge *about* them is not. Our confidence in E = mc² has a history. It was tested, contested, validated, refined. The formula is timeless; our relationship to it is temporal.

——

## The Three Dimensions

### Dimension 1: Awareness (Known ←→ Unknown)

This is the familiar axis from the Rumsfeld matrix:

- **Known:** We are conscious of this claim
- **Unknown:** The claim exists (or could exist) but we are unaware of it

### Dimension 2: Grounding (Assumed ←→ Observed)

Also present in the Rumsfeld matrix:

- **Observed:** Based on direct measurement, evidence, or source
- **Assumed:** Taken for granted without direct verification

### Dimension 3: Temporality (Static ←→ Dynamic)

The missing dimension:

- **Static:** Captured once, assumed to remain valid
- **Dynamic:** Continuously connected to reality, updated when divergence detected

```
                    STATIC                DYNAMIC
                    (snapshot)            (flow)
              ┌─────────────────────┬─────────────────────┐
              │                     │                     │
   OBSERVED   │   Frozen Fact       │   Living Fact       │
              │   ”Document said”   │   ”Reality shows”   │
              │   RAG, categories   │   Sensors, feedback │
              │                     │                     │
              ├─────────────────────┼─────────────────────┤
              │                     │                     │
   ASSUMED    │   Fossil Belief     │   Adaptive Belief   │
              │   ”That’s how it is”│   ”So it seems now” │
              │   Bias, stale       │   Hypothesis, test  │
              │                     │                     │
              └─────────────────────┴─────────────────────┘
```

——

## The Eight Epistemological States

Combining all three dimensions yields eight distinct states:

|#|Aware? |Temporal?|Grounded?|State              |Example                               |
|-|-——|———|———|-——————|—————————————|
|1|Known  |Static   |Observed |**Frozen Fact**    |“Policy says 14 days”                 |
|2|Known  |Dynamic  |Observed |**Living Fact**    |“Current measurement: 42 days”        |
|3|Known  |Static   |Assumed  |**Doctrine**       |“Best practice says…”                 |
|4|Known  |Dynamic  |Assumed  |**Hypothesis**     |“We believe X, testing…”              |
|5|Unknown|Static   |Observed |**Buried Data**    |Data no one examines                  |
|6|Unknown|Dynamic  |Observed |**Invisible Flow** |Real-time data no one sees            |
|7|Unknown|Static   |Assumed  |**Fossil Bias**    |Unconscious institutional assumptions |
|8|Unknown|Dynamic  |Assumed  |**Blind Evolution**|Unnoticed change in unexamined beliefs|

### State Descriptions

**Frozen Fact:** The dominant state in RAG systems. A document was observed, its contents extracted, and validity assumed indefinitely. The clock has stopped; we just haven’t noticed.

**Living Fact:** Knowledge connected to ongoing observation. When reality diverges from the claim, the divergence is detected and the claim updated. Requires sensors, feedback loops, measurement.

**Doctrine:** Explicit beliefs without empirical grounding. “This is best practice.” Useful as starting points, dangerous when ossified.

**Hypothesis:** Explicit beliefs acknowledged as uncertain and under test. The scientific stance: held provisionally, updated on evidence.

**Buried Data:** Observations exist but no one attends to them. The data sits in a database, technically “known” to the organization but functionally unknown.

**Invisible Flow:** Reality is being measured, but no one watches the measurements. Dashboards no one opens. Alerts no one reads.

**Fossil Bias:** The most dangerous state. Assumptions so deep they are invisible, so old they feel like facts, never tested because never questioned. “We’ve always done it this way.”

**Blind Evolution:** Reality is changing, our assumptions are changing with it (through drift, not intention), and we notice neither. We adapt unconsciously, without awareness of what we believed before or what we believe now.

——

## Implications for AI Systems

### RAG: Frozen Facts Only

```
Document → Chunks → Embeddings → Vector DB → Query → Response

Every step assumes temporal validity.
No step checks current reality.
```

RAG systems are architecturally incapable of representing dynamic knowledge. They can only freeze and retrieve.

### Fine-tuning: Compressed Temporality

```
Training data → Weight updates → Static model

The model ”knows” what was true during training.
It has no mechanism to know that anything has changed.
It cannot even represent the question ”is this still true?”
```

Fine-tuning compresses temporal dynamics into static parameters. The model becomes confident about an outdated snapshot.

### What’s Needed: Epistemological Metadata

Every claim must carry its position in epistemological space:

```turtle
:Claim_001 a :Statement ;
    :awareness :Known ;
    :temporality :Static ;
    :grounding :Observed ;
    :epistemologicalState :FrozenFact ;
    :observedAt ”2024-01-15”^^xsd:date ;
    :source :PolicyDocument_2024 ;
    :validationStrategy :ManualReview ;
    :lastValidated ”2024-01-15”^^xsd:date ;
    :staleness P240D ;  # 240 days since validation
    :risk ”High - no feedback loop, aging observation” .
```

The system can then reason:

- This claim is a Frozen Fact
- It was observed 240 days ago
- It has no automatic validation
- It should be flagged for review or connected to a Living Fact source

——

## Movement Through Epistemological Space

Claims don’t stay in one state. They move:

```
Frozen Fact ──────────► Living Fact
    (connect to sensor / feedback loop)

Doctrine ─────────────► Hypothesis  
    (acknowledge uncertainty, design test)

Fossil Bias ──────────► Doctrine ──────────► Hypothesis
    (surface assumption, make explicit, test)

Living Fact ──────────► Frozen Fact
    (sensor fails, feedback loop breaks)
```

A mature knowledge system tracks these transitions:

```turtle
:Claim_001 :transitionedFrom :FrozenFact ;
           :transitionedTo :LivingFact ;
           :transitionDate ”2024-06-15”^^xsd:date ;
           :transitionReason ”Connected to operational metrics feed” ;
           :transitionAgent :DataEngineeringTeam .
```

——

## The Rosling Principle

Hans Rosling added a play button to his charts. Bubbles moved. “Poor countries” became rich. Categories dissolved.

The insight: **Data without time misleads. Add the temporal dimension and static categories reveal themselves as snapshots of continuous flow.**

We propose the same for knowledge systems: every fact should have a play button. Not literally, but structurally – the ability to see how the claim has changed, when it was last validated, and what would cause it to update.

——

## The Darwin Principle

Species don’t exist. Populations exist, drifting through genetic space. “Species” is a line we draw in hindsight.

The insight: **Categories are conveniences, not realities. The map is not the territory, and the territory is moving.**

Knowledge systems that treat categories as fixed will systematically misrepresent a fluid world.

——

## The Feynman Principle

> “It doesn’t matter how beautiful your theory is, it doesn’t matter how smart you are. If it doesn’t agree with experiment, it’s wrong.”

And:

> “The first principle is that you must not fool yourself – and you are the easiest person to fool.”

The insight: **Knowledge requires continuous contact with reality. The moment you stop checking, you start drifting.**

A knowledge system that cannot be wrong cannot learn. A system that doesn’t check cannot know if it’s wrong.

——

## Implementation in Sky Omega

### Lucy: Epistemologically-Aware Knowledge Graph

Lucy represents every claim with explicit epistemological coordinates:

```turtle
@prefix epi: <https://sky-omega.org/ontology/epistemics#> .

epi:awareness a rdf:Property ;
    rdfs:domain epi:Statement ;
    rdfs:range [ owl:oneOf ( epi:Known epi:Unknown ) ] .

epi:temporality a rdf:Property ;
    rdfs:domain epi:Statement ;
    rdfs:range [ owl:oneOf ( epi:Static epi:Dynamic ) ] .

epi:grounding a rdf:Property ;
    rdfs:domain epi:Statement ;
    rdfs:range [ owl:oneOf ( epi:Observed epi:Assumed ) ] .
```

### James: Epistemological Reasoning

James monitors the epistemological health of the knowledge base:

```
ALERT: 47 claims in Frozen Fact state older than 180 days
ALERT: 12 claims show divergence between Static and Dynamic sources  
SUGGESTION: Claim X (Doctrine) could be converted to Hypothesis with test Y
WARNING: Cluster of Fossil Bias detected in domain Z - recommend review
```

### Mercury: Bitemporal Foundation

Mercury’s bitemporal architecture provides the infrastructure:

- **Valid time:** When was this claim true in the world?
- **Transaction time:** When did we record this claim?

This enables queries like:

```sparql
# What did we believe about X on date D?
# How has our belief about X changed over time?
# Which claims have never been revalidated?
```

——

## Conclusion

The Rumsfeld matrix gave us a tool for thinking about awareness and grounding. By adding temporality as a third dimension, we gain a richer framework for characterizing knowledge – and revealing the blind spots of current AI architectures.

RAG systems freeze facts. Fine-tuned models compress time into weights. Neither can represent the fundamental truth that **the relationship between a claim and reality changes**.

Structured intelligence requires explicit epistemological dimensions: systems that know what they know, know when they knew it, know how they know it, and know what would change their minds.

Darwin, Rosling, and Feynman understood this in their respective domains. It’s time for knowledge systems to catch up.

——

## References

### Foundational

- Rumsfeld, D. (2002). DoD News Briefing. The “known unknowns” framing.
- Polanyi, M. (1966). *The Tacit Dimension*. On knowledge we have but cannot articulate.
- Feynman, R. (1974). *Cargo Cult Science*. Caltech commencement address.

### Temporal Dynamics

- Rosling, H. (2006). TED Talk: *The best stats you’ve ever seen*. Visualization of temporal dynamics.
- Darwin, C. (1859). *On the Origin of Species*. Species as snapshots of continuous variation.

### Knowledge Representation

- W3C. (2004). *OWL Web Ontology Language*. Formal knowledge representation.
- Snodgrass, R. (1999). *Developing Time-Oriented Database Applications in SQL*. Bitemporal data modeling.

### Sky Omega Architecture

- [EEE Methodology](../process/eee-methodology.md) – Emergence, Epistemics, Engineering
- [Lucy Architecture](../architecture/lucy.md) – Semantic memory substrate
- [Mercury Bitemporal Design](../architecture/mercury-bitemporal.md) – Temporal data foundation

——

## Navigation

- Up: [Concepts Index](./index.md)
- Related: [EEE Methodology](../process/eee-methodology.md)
- Related: [Semantic Braid](./semantic-braid.md)