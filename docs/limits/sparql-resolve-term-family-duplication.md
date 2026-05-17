# Limit: SPARQL ResolveTerm-family duplication across pattern operators

Status:        **Latent** (characterized during ADR-044 Phase 0 surface analysis)
Surfaced:      2026-05-17, during ADR-044 Phase 0 consolidation when the four near-identical `ResolveTerm`-shaped methods were found to have substantive feature-set divergences. ADR-044 fallback clause kept the per-site canonicalization edits; this entry holds the duplication as separate debt.
Last reviewed: 2026-05-17
Promotes to:   ADR when (a) a third meaningful divergence appears (a fifth `ResolveTerm`-shaped method, or a fifth feature axis), OR (b) a bug surfaces from the duplication (an edit landed in only some of the copies), OR (c) ADR-044's per-site canonicalization edits prove painful at maintenance time, OR (d) the substrate-discipline rule of "one canonical implementation" becomes load-bearing for a different reason.

## Description

Mercury's SPARQL execution layer has four near-identical methods that resolve a `Term` (subject / predicate / object) into a `ReadOnlySpan<char>` suitable for atom-store lookup. They share the same prefix-expansion logic, the same `'a'` shorthand for `rdf:type`, and the same `_expandedSubject` / `_expandedPredicate` / `_expandedObject` position-field idiom. They diverge on which higher-level features they support.

| Method | File | Signature | Synthetic terms | Blank nodes | Numeric literal expansion | Typed value formatting |
|---|---|---|---|---|---|---|
| `ResolveSlotTerm` | `src/Mercury/Sparql/Execution/QueryResults.Patterns.cs:74` | `(TermType, start, length, position)` | no | no | yes | no |
| `ResolveTerm` | `src/Mercury/Sparql/Execution/Operators/MultiPatternScan.cs:932` | `(Term, ref BindingTable, position)` | yes | yes | no | yes |
| `ResolveTermWithStorage` | `src/Mercury/Sparql/Execution/Operators/TriplePatternScan.cs:1492` | `(Term, position)` | yes | yes | no | partial |
| `ResolveTermForQuery` | `src/Mercury/Sparql/Execution/Operators/TriplePatternScan.cs:1583` | `(Term)` | yes | yes | no | no |

Plus a fifth-cousin: `ExpandPathPredicate` at `src/Mercury/Sparql/Execution/Operators/TriplePatternScan.cs:1659` reproduces the prefix-expansion logic without the variable / synthetic / blank-node handling, used by `Alternative` path predicates whose representation is start/length offsets rather than `Term` structs.

The shared parts (prefix expansion + `'a'` shorthand + position-field idiom) are byte-identical across the five locations. The divergent parts reflect different use-case feature requirements.

## Trigger condition

Any of the following:

- A bug that lands an edit in some but not all copies (e.g., a fix to prefix-expansion that misses one of the five places).
- A new operator that needs its own `ResolveTerm`-shaped method, making the duplication 5-way → 6-way.
- A new feature axis (e.g., language-tag rewriting, datatype normalization) that needs to land in some but not all copies, surfacing the "which version do I add it to?" question repeatedly.
- ADR-044's per-site canonicalization edits land in only some sites because of an oversight enabled by the duplication.

## Current state

During ADR-044 Phase 0 (2026-05-17) the duplication was characterized but the consolidation was *not* attempted because the divergent feature sets (synthetic terms, blank-node handling, numeric literal expansion, typed-value formatting) span five distinct axes. A single consolidated helper would need feature flags / conditional branches on all five axes — a Christmas-tree pattern that trades one debt for another. Partial consolidation (extract just the prefix-expansion logic) was rejected for the same reason: it leaves the divergent parts in each caller and the partial helper invites future drift on a different axis.

ADR-044's Decision Part 2 included a fallback clause for exactly this case:

> If Phase 0 turns out non-trivial (the discriminator differences are more substantive than they appear at a glance), it gets its own ADR and ADR-044's Part 2 keeps the per-site edits as a fallback — but the duplication remains a debt to pay separately.

This entry is that "paid separately" placeholder.

## Candidate mitigations

1. **Visitor / strategy pattern.** Extract `IResolveTermStrategy` with methods for each phase (synthetic-term lookup, blank-node handling, prefix expansion, variable binding lookup, typed-value formatting). Each caller injects its strategy. Pros: clean separation of cross-cutting prefix expansion from per-call-site feature toggles. Cons: introduces an interface for shared infrastructure, which Mercury's BCL-only discipline tends to avoid.

2. **Source-generator-driven boilerplate elimination.** Tag each variant with attributes (`[ResolveTerm(SyntheticTerms=true, BlankNodes=true, NumericExpansion=false)]`); a source generator emits the per-variant method with the requested feature set. Pros: zero runtime cost, type-safe. Cons: source generators are new substrate complexity.

3. **Partial consolidation: prefix-expansion only.** Extract just the prefix-expansion + `'a'` shorthand into one helper (`TermResolver.ExpandPrefix`). Each `ResolveTerm`-shaped method delegates the prefix part, keeps everything else. Pros: closes the byte-identical part of the duplication. Cons: doesn't address the variable-binding lookup duplication or the position-field idiom duplication.

4. **Accept and audit.** Leave the duplication; introduce a CI check or audit script that compares the prefix-expansion blocks for byte-identity. Pros: zero refactoring cost. Cons: drift detection is reactive, not preventive.

5. **Wait for the third occurrence.** The current four-way duplication may be a coincidence of how the code evolved. If a fifth `ResolveTerm`-shaped method appears for a new operator, that's the right time to invest in consolidation — the cost of *not* consolidating is now demonstrably linear in operator count.

## References

- [ADR-044](../adrs/mercury/ADR-044-sparql-update-literal-canonicalization.md) — Decision Part 2 fallback clause; Phase 0 surface analysis that surfaced this.
- Phase 0 InstantiateTerm consolidation (the *other* half of Phase 0): `UpdateExecutor.InstantiateTerm` consolidated from `InstantiateTerm` + `InstantiateTermFromSpan` (byte-identical, trivial). The asymmetry between the two consolidation attempts (one trivial, one substantive) is what motivated splitting Phase 0 outcomes between the ADR (the easy half) and this limit (the deferred half).
- `_expandedSubject` / `_expandedPredicate` / `_expandedObject` position-field idiom — used identically across all four `ResolveTerm`-shaped methods; the canonicalization addition (ADR-044 Part 3) extends this idiom.
