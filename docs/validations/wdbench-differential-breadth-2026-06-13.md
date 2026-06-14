# WDBench differential breadth — old path vs unified tree (ADR-047)

**Date:** 2026-06-13
**Store:** `wiki-truthy-ref-r1` (1 TB, Reference profile — the WDBench-native truthy Wikidata dataset)
**Harness:** `benchmarks/Mercury.Benchmarks/WdBenchDifferentialRunner.cs` (`wdbench-diff`), tree path with **`--reorder`** (the cutover design — planner selectivity), per-query/per-path 20 s timeout, order-insensitive bag comparison (row count + Σ/⊕ row-hash).
**Guard:** `StorageOptions.MaxResultRows` default 10 M active — huge-result queries trip identically on both paths (`both_failed`), which keeps the comparison memory-bounded and incidentally re-validates the guard on real queries.

## Why this run

The ADR-047 differential gate was 35 hand-built cases — all single-operator property paths, mostly constant-anchored. Breadth is the Accepted-gating step: run WDBench's 2,658 real Wikidata queries (single_bgps, multiple_bgps, opts, paths, c2rpqs) through **both** executors and compare bags. This also exercises the tree on the **Reference** profile for the first time.

## What ran

| category | queries | run | correctness divergences |
|---|---|---|---|
| single_bgps | 280 (complete) | reorder=false | **0** |
| c2rpqs | 522 / 539 | reorder=true | 101 |
| paths | 116 / 660 | reorder=true | 9 (composite-path, tree under-produces) |
| multiple_bgps, opts | — | — | not yet run (queued for the post-fix full run) |

c2rpqs (conjunctions of regular path queries) is the heaviest, property-path-dense category — and where the divergences concentrate.

## The finding: re-triage by *direction* (not agreement)

The differential's implicit oracle is the old path. **That oracle is contaminated for property-path reflexives** — so divergences were re-triaged by which direction is W3C-correct (SPARQL 1.1 §9.3: a zero-length path binds `(n,n)` for every node `n` used in the graph). Of the 101 c2rpqs divergences:

- **90 — `tree > old`: the cutover IMPROVES correctness.** A `?`/`*` path whose subject is **bound by a prior pattern** must emit the zero-length reflexive `?x = ?o` per bound `?o`. The old path drops these in the join context (it emits them fine for a *constant* subject — `path-question-in-graph` is equivalent — which is exactly why W3C conformance + the 35 hand-built cases never caught it). The tree is W3C-correct; these 90 are old-path bugs the cutover fixes. Pinned by `DefaultVsTreeDifferentialTests.PathReflexive_BoundSubject_*` (minimal repro: `?s :p ?o . ?o :next? ?x` → tree 3 rows, old 1).
- **7 — genuine tree bugs.** `tree=0` (6) or `tree<old` (1), all **composite paths that combine sequence + quantifier + alternation, nested** — e.g. `((P31/(P279)*)|(P106/(P279)*))` → 0, `(P279/((P279)*|(P31)*))` → 0, `(P31/P279)` joined twice → under. Single operators, lone groups, and top-level predicate-sequences all work (ladder L1–L5); the nested *combination* does not.

paths (single-path, no join) diverges only in the genuine-bug direction — composite expressions like `((((P31/(P279)?)/(P279)?)/…)` → `tree=0` — confirming the composite-path gap is general, not join-specific.

## Verdict

The cutover is **mostly a correctness win** (90 old-path bugs fixed), gated on **one focused path-engine gap**: the tree's evaluation of composite paths nesting sequence/quantifier/alternation. Not a sprawling rewrite.

## Next

1. Fix the composite-path evaluation in `TriplePatternScan` / the sequence expansion. **Oracle = W3C-correctness**, not the old path — construct small-store cases with hand-computed answers (`(A/B)` in a join, `(A/B*)|(C/D*)`, `A/(B*|C*)`), pin them, fix, verify.
2. Re-run the **full** breadth differential (all 5 categories, reorder=true) post-fix — confirm the 7 collapse while the 90 improvements hold; seal the complete tally.

## References

- Harness: `benchmarks/Mercury.Benchmarks/WdBenchDifferentialRunner.cs`
- Reports (raw JSONL, this directory): `wdbench-differential-reorder-truthy-2026-06-13.jsonl` (c2rpqs, 522), `wdbench-differential-paths-reorder-2026-06-13.jsonl` (116), `wdbench-differential-single_bgps-2026-06-13.jsonl` (280)
- Repro + pinned W3C behaviour: `tests/Mercury.Tests/Sparql/DefaultVsTreeDifferentialTests.cs` (`PathReflexive_BoundSubject_*`)
- [ADR-047](../adrs/mercury/ADR-047-default-path-cutover.md), [unbounded-result-materialization limit](../limits/unbounded-result-materialization.md)
