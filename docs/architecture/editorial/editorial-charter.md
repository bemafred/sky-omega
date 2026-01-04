# Editorial Charter - Sky Omega

## Purpose

This charter defines the editorial authority, semantic rules, and documentation boundaries for the **Sky Omega ecosystem**, consisting of:

- **Sky Omega** - core implementation and architectural truth
- **Sky Omega Public** - public-facing narrative and explanation
- **VGR.Demo.Domain** - reference architecture demonstrating principles in practice

The purpose of this charter is to ensure **semantic coherence**, **conceptual integrity**, and **epistemic cleanliness** across all documentation, now and in the future.

This is not a style guide.
It is a **governance document for meaning**.

---

## Authority

- **Sky Omega is the epistemic root** of the ecosystem.
- All canonical definitions originate in Sky Omega.
- Sky Omega has a **single editorial authority**.
- The system has **not** been designed by a committee.

Other repositories may *reference*, *summarize*, or *demonstrate* concepts - they may **never redefine them**.

---

## Canonical Truth

A concept is considered **canonical** if and only if:

- It is defined in Sky Omega documentation, and
- That definition is explicit or intentionally normative.

If a conflict exists between repositories:

> **Sky Omega is always correct.**

---

## Repository Roles

### Sky Omega

Role:
- Architectural truth
- Concept definition
- Implementation reality

Allowed:
- Precise terminology
- Architectural depth
- Internal structure
- Normative statements

Forbidden:
- Public-facing persuasion
- Rhetorical abstraction
- Demo-specific assumptions

---

### Sky Omega Public

Role:
- Public explanation
- Philosophical framing
- Architectural legitimacy

Allowed:
- Non-technical descriptions
- Architecture-level explanations when warranted
- Selected code fragments **only as illustrative examples**

Forbidden:
- Implementation detail
- Normative redefinition
- New concepts not defined in Sky Omega

Sky Omega Public **derives meaning** - it does not create it.

---

### VGR.Demo.Domain

Role:
- Reference architecture
- Demonstration of E-Clean & Semantic Architecture
- Concrete instantiation

Allowed:
- Practical examples
- Applied patterns
- Demonstrable mappings to Sky Omega concepts

Forbidden:
- New theory
- Alternative interpretations
- Conceptual generalization beyond what is defined upstream

The demo proves ideas - it does not invent them.

---

## Concept Integrity Rules

- Each concept has **one canonical meaning**.
- A concept may have **layered explanations**, but not layered meanings.
- Terminology must be used **semantically consistently** across repositories.
- If a concept is simplified in public material, the simplification must:
  - Preserve truth
  - Be traceable to the canonical definition

---

## Concept Evolution

Concepts may evolve, but only through an explicit process:

- Refinement and clarification are allowed.
- Renaming, merging, or retiring concepts:
  - Must be **explicitly proposed**
  - Must receive **editorial approval**
- Silent drift is not permitted.

---

## Documentation as Infrastructure

Documentation is treated as **semantic infrastructure**, not commentary.

This implies:
- Consistency over completeness
- Legitimacy over persuasion
- Precision over verbosity

If documentation contradicts implementation intent, the documentation is wrong.

---

## AI and Tooling

This charter is designed to be executable by:
- Human editors
- AI systems
- Automated documentation tooling

Any AI operating on these repositories must:
- Respect canonical sources
- Avoid speculative completion
- Propose structural changes, never apply them silently

---

## Final Principle

> Meaning precedes structure.
> Structure precedes implementation.
> Demonstration never precedes legitimacy.

This principle governs all documentation in the Sky Omega ecosystem.

---

**Status:** Canonical  
**Scope:** Sky Omega and all derived repositories  
**Authority:** Sole editorial authority (Sky Omega)
