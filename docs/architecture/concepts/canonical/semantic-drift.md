# Semantic Drift

## Canonical Definition

Semantic Drift is the loss or distortion of **intended meaning over time**, without explicit acknowledgement or decision.

Semantic drift occurs when meaning is allowed to change implicitly, for example when:

- Assumptions harden into undocumented facts
- Terminology evolves without redefinition
- Implementations change while semantic intent remains static
- Documentation and behavior diverge silently

Semantic drift is the **epistemic analogue of technical debt**:
where technical debt accumulates when implementation shortcuts are taken, semantic drift accumulates when **meaning shortcuts** are taken.

---

## Non-Goals

- Semantic drift is not simple refactoring or intentional redesign.
- It is not disagreement or evolving understanding made explicit.
- It is not caused by change itself.
- It is not exclusively a technical phenomenon.

---

## Notes

- **Technical debt** can often be repaid; semantic drift often goes unnoticed until it has reshaped the system’s meaning.
- **E-Clean and Epistemic Cleanliness** are *primary countermeasures* against semantic drift.
- **Semantic Architecture** exists largely to *prevent semantic drift* from becoming structural.
