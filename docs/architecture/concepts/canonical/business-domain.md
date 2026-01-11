# Business Domain

## Canonical Definition

The Business Domain is the **real-world area of meaning, intent, and constraints** that a system exists to serve.

It encompasses:

- The concepts that matter in the problem space
- The relationships between those concepts
- The rules, obligations, and invariants that arise from reality, policy, or practice

The Business Domain exists **independently of software**.
It is not created by the system, but **reflected, represented, and constrained** by it.

In Semantic Architecture, the Business Domain is made explicit and authoritative through the **Semantic Core**.

---

## Non-Goals

- The Business Domain is not a software model.
- It is not a database schema or API surface.
- It is not owned by technical implementation.
- It is not defined by frameworks or architectural styles.

---

## Notes

- The Business Domain is the source of truth for *what must be represented*.
- The Semantic Core is the system’s formal expression of the Business Domain.
- Technical Domain concerns exist only to serve the Business Domain.
- This concept aligns with Domain-Driven Design’s notion of a domain, but is not limited to DDD practices or object-oriented modeling.
