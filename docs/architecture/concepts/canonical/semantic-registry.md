# Semantic Registry

## Canonical Definition

A Semantic Registry is a **runtime-accessible registry of semantic definitions and bindings** that enables systems to reason about meaning programmatically.

While a Domain Catalog exposes *what* concepts exist, a Semantic Registry enables systems to:

- Resolve concepts by identity
- Bind semantic meaning to technical representations
- Perform controlled expansion, lookup, and validation

The Semantic Registry acts as the **operational bridge** between the Semantic Core and the Technical Domain.

---

## Non-Goals

- A Semantic Registry is not a database of business data.
- It is not a replacement for the Semantic Core.
- It does not invent or reinterpret meaning.
- It is not merely a naming service.

---

## Notes

- Semantic Registries often operate at runtime.
- They enable tooling, reflection, and integration without coupling to implementation details.
- VGR.Demo.Domain demonstrates a concrete use of a Semantic Registry.
