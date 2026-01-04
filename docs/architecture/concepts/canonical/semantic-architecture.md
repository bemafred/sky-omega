# Semantic Architecture

## Status
Canonical

## Canonical Definition
**Semantic Architecture** is the practice of treating meaning as a primary architectural concern. It provides the structural implementation of [E-Clean](e-clean.md) principles.

## Five Core Principles

1. **Semantic identity supersedes technical role** — What something *means* matters more than what it *does*
2. **Names function as lasting contracts** — Naming is commitment, not convenience
3. **Types establish epistemic boundaries** — The type system encodes knowledge boundaries
4. **Reflection and expressions serve as deliberate architectural instruments** — Not just runtime convenience
5. **Architectural rules require mechanical verification** — If it can't be checked, it will drift

## Why .NET?

The architecture deliberately targets modern .NET and C# because these platforms provide:
- Deep runtime reflection
- Expression tree inspection
- Compiler-enforced constraints
- Robust tooling integration

These capabilities are essential for mechanical semantic verification.

## Non-Goals
- Prescribing specific design patterns (patterns emerge from principles)
- Replacing domain-driven design (complementary, not competing)
- Language-agnostic universality (leverages platform-specific capabilities)

## See Also
- [E-Clean](e-clean.md) - The discipline Semantic Architecture implements
- [Semantic Core](semantic-core.md) - Related infrastructure concept
- [Semantic Drift](semantic-drift.md) - What mechanical verification prevents
