# Semantic Core

## Canonical Definition

The Semantic Core is the part of a system where **business meaning is defined, structured, and preserved independently of technical concerns**.

The Semantic Core contains:

- Canonical business concepts
- Their relationships
- The rules that govern their validity, interpretation, and evolution

It represents the **business domain as meaning**, not as implementation, ensuring that domain intent remains stable even as technology, tooling, and delivery mechanisms change.

**The Semantic Core is the sole authoritative source of business meaning for the system.**

---

## Non-Goals

- The Semantic Core is not a persistence layer.
- It is not a service boundary or deployment unit.
- It is not a set of database schemas.
- It does not concern itself with performance, infrastructure, or frameworks.

---

## Notes

- The Semantic Core overlaps conceptually with the domain model in Domain-Driven Design, but is not limited to DDD’s tactical patterns or object-oriented expression.
- It may be expressed using entities, value objects, graphs, rules, or other semantic forms, as long as meaning remains explicit and authoritative.
- It relies on E-Clean to ensure that only legitimate domain knowledge is encoded.
- VGR.Demo.Domain demonstrates a concrete business-domain Semantic Core.