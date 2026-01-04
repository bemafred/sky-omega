# Semantic Drift

## Status
Canonical

## Canonical Definition
**Semantic Drift** is the gradual erosion of meaning in software systems over time. Systems deteriorate not from incorrect dependencies or failing tests, but from the slow loss of semantic precision.

## Manifestations

- **Generic abstractions** that lose domain specificity
- **Semantic dumping grounds** where concepts blur together
- **Unenforced ubiquitous language** that drifts from original intent
- **Names that no longer match behavior** after incremental changes
- **Types that encode implementation rather than meaning**

## Why It Matters

Semantic drift is insidious because:
- Tests continue to pass
- Code continues to compile
- Systems continue to function
- But understanding erodes

Eventually, no one knows what the system *means*—only what it *does*.

## Prevention

Semantic drift is prevented through:
- [E-Clean](e-clean.md) discipline
- [Semantic Architecture](semantic-architecture.md) practices
- Mechanical verification of architectural rules
- Explicit domain vocabularies

## Non-Goals
- Detecting all code smells (broader concern)
- Preventing all technical debt (semantic drift is a specific type)
- Automated refactoring (detection, not correction)

## See Also
- [E-Clean](e-clean.md) - Discipline for preventing drift
- [Semantic Architecture](semantic-architecture.md) - Structural countermeasures
