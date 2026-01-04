# E-Clean

## Status
Canonical

## Canonical Definition
**E-Clean (Epistemic Clean)** is the foundational discipline ensuring software systems remain epistemically sound—that software *means what it says* and keeps meaning it as systems evolve.

E-Clean maintains strict separation between what is **known**, **assumed**, **undefined**, and **accidental**.

> "If the system compiles, it must also *make sense*."

## Core Principles
- Explicit domain vocabularies
- Stable semantic boundaries
- Elimination of ambiguous abstractions
- Mechanical verification of architectural rules

## The Problem It Solves
Systems deteriorate not from incorrect dependencies, but from eroding meaning. Common failure modes:
- Generic abstractions that lose domain specificity
- Semantic dumping grounds where concepts blur
- Unenforced ubiquitous language that drifts from intent

## Non-Goals
- Runtime performance optimization (orthogonal concern)
- Code style or formatting enforcement
- Traditional "clean code" aesthetics without semantic grounding

## See Also
- [Semantic Architecture](semantic-architecture.md) - Structural implementation of E-Clean principles
- [Epistemic Cleanliness](epistemic-cleanliness.md) - Related concept
- [Semantic Drift](semantic-drift.md) - The problem E-Clean prevents
