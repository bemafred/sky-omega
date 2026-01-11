# Technical Domain

## Canonical Definition

The Technical Domain is the part of a system concerned with **how things are implemented**, rather than what they mean.

It contains:

- Infrastructure concerns
- Framework integrations
- Performance, scalability, and reliability mechanisms
- Technical abstractions required to realize the system in practice

The Technical Domain exists to **serve the Semantic Core**, translating authoritative business meaning into executable, operable systems without redefining that meaning.

---

## Non-Goals

- The Technical Domain does not define business concepts.
- It does not own domain meaning or rules.
- It does not decide what is true in the business domain.
- It is not an architectural authority.

---

## Notes

- The Technical Domain may change frequently as technology evolves.
- Clean separation from the Semantic Core prevents technical drift from corrupting meaning.
- This distinction goes beyond traditional “layers” by enforcing epistemic, not just structural, boundaries.
