# Turtle

## Canonical Definition

Turtle is a **human-readable serialization format for RDF**.

It provides a compact and expressive syntax for writing RDF statements, making semantic data:

- Easier to author
- Easier to review
- Easier to reason about during development and inspection

Turtle does not change the meaning of RDF; it only affects **how that meaning is written and read**.

---

## Non-Goals
- Turtle is not a data model.
- It is not a query language.
- It is not an alternative to RDF.
- It does not add semantics beyond what RDF defines.

---

## Notes

- Turtle is often used for configuration, domain catalogs, and test fixtures.
- Its readability supports epistemic cleanliness by making meaning visible.
- Sky Omega uses Turtle where human inspection of semantic structures is valuable.
