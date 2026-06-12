# ADR-047: One execution path — route the default query path through the unified tree executor

## Status

**Status:** Proposed — 2026-06-11 (Emergence. ADR-045 unified the GRAPH path onto `TreeJoinExecutor` and deleted the divergent GRAPH executor; the **default** query path still runs the older `QueryPlanner` + slot-based operators. Two executors for one algebra is the same divergence ADR-045 named, relocated. This ADR proposes completing the cutover. Unlike the GRAPH path, the default path carries the selectivity **planner**, so **performance parity** — not only correctness parity — is the central open question. **The 2026-06-12 spike validated the mechanism** — see *Validation to date* — so the load-bearing risk is substantially down; **broad-scale validation remains** before Accepted.)

## Validation to date (2026-06-12)

Two probes have de-risked both halves; neither is the full corpus/scale validation the Quality guarantee requires, but both point the right way.

- **Correctness — the differential gate** (`ffbfe44`, `DefaultVsTreeDifferentialTests`): runs each query through both executors and compares solution bags. A battery covering the whole algebra (paths `* ? ^ | +`, EXISTS/NOT EXISTS, GROUP BY + HAVING, nested OPTIONAL, aggregation, sub-SELECT, multi-var VALUES join, FILTER functions) is **equivalent** through the tree; the only divergences were three named gaps, and **all three are now CLOSED** (`fc0384c`, step 2): VALUES numeric canonicalization (the tree now produces typed literals), zero-length-path graph-term membership (the reflexive is node-gated for variable-bound values, while a constant endpoint still matches — W3C `zero_or_one_set_start/end`), and VALUES-after-triple (not a tree bug — the tree cross-joins per §18 where the old path drops the inline data; the cutover adopts the correct behaviour). The differential battery now has **no open tree bug** — every case is equivalent, except the one where the tree is correct and the old path is wrong. W3C SPARQL conformance green throughout.
- **Performance — the planner spike** (`ed994f1`, `DefaultPathPlannerSpike`): reorder the tree's BGP run by the existing `QueryPlanner.OptimizePatternOrder` selectivity model (correctness-neutral — a BGP join is commutative). On a 2-pattern join with pessimal source order (50,000-row pattern first, 5-row second), with statistics collected:

  | path | mean |
  |---|---|
  | old path (QueryPlanner) | 22.7 µs |
  | tree, source order (unplanned) | 26.2 ms (~2,500× slower — planning is essential) |
  | tree, selectivity reorder (planned) | **10.6 µs (~2× faster than the old path)** |

  A selectivity-planned tree does not merely *match* the old path — it **beats it ~2×**, on the same planning model with leaner execution. The planner-as-tree-reordering-pass (the proposed design) works.
- **Caveat surfaced**: predicate statistics are **lazy** — `CollectPredicateStatistics` runs on checkpoint/Dispose, not on commit. Without them the planner estimates a uniform default and reorders nothing (both paths then run source-order). The planned tree's advantage, like the old path's, depends on current statistics; the cutover inherits — it does not worsen — this existing dependency.
- **Materialization risk (Tier A)** (`4965b73`, `DefaultPathMaterializationSpike`): the tree **materializes** the BGP intermediate (`List<MaterializedRow>`) where the old path streams. On COUNT over a 1M-row self-join the tree is **4.6× faster** (231 ms vs 1,058 ms) but allocates **3.4×** the memory (365 MB vs 107 MB). A characterized tradeoff — faster, more memory; the risk is a *pathological large-intermediate* query at scale (tens of GB → OOM where the old path streams). Mitigation (stream/spill the tree's aggregate path) is a known engineering option, not a blocker for the common case.
- **Temporal SPARQL** (`TemporalDifferentialTests`): AS OF / DURING / ALL VERSIONS through the tree **match the old path** (the temporal mode + bounds thread into `TriplePatternScan`). The cutover preserves the temporal extensions.
- **Profile equivalence** (`ProfileEquivalenceDifferentialTests`): parsing is profile-independent; execution is validated **equivalent (tree ≡ old) on every writable profile** — Minimal, Cognitive, Graph — for a representative query set. The four profiles are Reference, Minimal, Cognitive, Graph; only Cognitive/Graph are temporal (Reference/Minimal reject AS OF), and only **Reference** carries the 21.3 B Wikidata load (Cognitive's temporal indexes are too heavy at that scale). Reference's execution is validated separately (`SparqlAgainstReferenceProfileTests`) and is the target for the scale run.

**Remaining for Accepted** (breadth + scale, not mechanism): run the now profile-parameterized, temporal-aware differential over a larger corpus — **WDBench** is the right intermediate-scale dogfood (loadable in Cognitive for broad + temporal) — and validate the planner-spike and materialization findings **at scale on Reference** (the 21.3 B Wikidata on disk, or WDBench-scale). Strategy: **Cognitive for broad correctness, Reference for scale performance.**

## Context

### ADR-045 unified one path and left the other

[ADR-045](ADR-045-graph-clause-feature-parity.md) routed GRAPH queries through `TreeJoinExecutor` (the unified, zero-GC tree executor) and deleted the 1,585-line divergent `QueryExecutor.Graph.cs`. But `QueryExecutor.Execute`'s dispatch still sends the **default** query path (non-GRAPH BGP + operators, `TriplePatternCount > 0`) to the older machinery: `SparqlParser` → `QueryBuffer` → `QueryPlanner` (selectivity reordering) → slot-based operators (`TriplePatternScan` / `MultiPatternScan` and the EXISTS/MINUS/aggregation helpers). There are still **two executors for one relational algebra**.

### The recurring class — relocated, not eliminated

ADR-045 named a divergence class: *a separate, less-complete reimplementation of the same algebra drifts from the canonical one, and conformance misses it.* The off-ADR cleanup after ADR-045 (June 2026) is the same class, now on the **default** path:

- **VALUES is incomplete on the default path.** VALUES-only returned nothing (`ck:obs-values-only-empty`); VALUES-after-triple is ignored and a non-join VALUES variable is dropped (`ck:obs-values-join-default-path-incomplete`). The tree handles all of it.
- **The default-path planner crashed on synthetic term offsets** — RDF-star reification (`ck:obs-facade-rdfstar-planner-crash`) and RDF collections — by `Slice`-ing a negative marker offset. The tree never runs the planner, so it was immune; one guard fixed both.

These are not unrelated bugs. They are the **default path lagging the unified executor** — the divergence ADR-045 set out to end, still shipping.

### Why this is harder than the GRAPH cutover

GRAPH queries are typically small and did not use the planner, so ADR-045 was a **correctness** cutover. The default path's reason to exist is **performance**: `QueryPlanner` reorders BGP patterns by selectivity (using `AtomStore` predicate statistics) so the most selective pattern runs first. `TreeJoinExecutor` evaluates patterns in **source order**. For a query whose source order is poor — a high-cardinality pattern first — source-order nested-loop join produces a huge intermediate set the planner would have avoided. At Wikidata scale ([ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md)/[ADR-035](ADR-035-phase7a-metrics-infrastructure.md)) this is the difference between a query that returns and one that does not. So the default cutover needs **performance parity**, not just correctness parity. That is the central unknown — and the reason the default path was not cut over alongside GRAPH.

### The two parity surfaces

- **Correctness parity** — every query must produce the same solution bag through the tree as through the old path. Known gaps so far: VALUES numeric/boolean **canonicalization** (the tree binds `25` raw, the stored term is `"25"^^xsd:integer`); **zero-length property path** graph-term membership (W3C `property-path/values_and_path`: `VALUES ?v {1} ?v <p>? ?v` over an empty graph must be empty); `_defaultGraphs` **FROM-default-union** scanning (the tree scans the real default, not the union of the FROM graphs). The *full* gap list is unknown until a differential gate runs the corpus.
- **Performance parity** — the tree must not regress query latency versus the planned old path. Requires bringing selectivity planning to the tree.

### Why conformance misses it

ADR-045's lesson holds: W3C SPARQL conformance is green on both paths because the divergent cases (VALUES shapes, synthetic-offset planning) sit in the gaps between conformance tests or are exercised through only one path. The cleanup found each by **dogfood and differential**, not by conformance — which is exactly the methodology this ADR institutionalizes.

## Decision

**One execution path. Route the default query path through `TreeJoinExecutor` and delete the old default-path executor — the same cutover ADR-045 made for GRAPH, finished for the default graph.**

End-state: `SparqlParser` → `PatternArray` tree → `TreeJoinExecutor` → `FromMaterializedSimple`, for **every** query (default and GRAPH). The slot-based operators, the buffer-pattern dispatch, and the planner-as-executor are deleted — **except** the planner's selectivity **model**, which is repurposed to reorder the tree's BGP runs.

Two halves, gated independently; the flip happens only when **both** are green:

1. **Correctness parity via a differential gate.** Build a *default ≡ tree* differential harness — the analog of ADR-045's metamorphic mirror gate (`GraphMirrorGateTests`): for every query in the W3C suite + a dogfood corpus, run **both** the old default path and the tree path and compare solution bags. Each divergence is a parity gap; fix it on the tree; lock the gate green. This drives out the **complete** gap list (the three known gaps are the tip).
2. **Performance parity via selectivity-planned tree evaluation.** Reuse the `QueryPlanner` selectivity model (predicate statistics) to **reorder the `PatternArray` BGP runs** before `TreeJoinExecutor` evaluates them — the planner becomes a tree-reordering *pass*, not a separate executor. Validate at scale (WDBench + the Wikidata gradient) that the planned tree matches the old path's latency. Gate on no regression.

There is **no tactical-patch variant.** Patching the old default path is the same point-fix treadmill the June cleanup already walked (VALUES, the RDF-star/collection planner crash); it lets the two implementations drift again. ADR-045's principle: delete the divergent path, don't patch it.

## Quality guarantee

- The differential *default ≡ tree* gate is **green** over the W3C corpus + a dogfood corpus before the flip (correctness).
- A benchmark gate shows the planned tree within an agreed latency bound of the old path on the BGP benchmark and the Wikidata gradient (performance).
- The hot scan loop stays zero-GC — ADR-045's allocation gate (`AllocationTests.GraphPath_HotScanLoop_IsZeroGcPerScanStep`), extended to the default path.
- Full W3C SPARQL conformance stays green throughout.

## Consequences

- **One executor for the whole algebra.** The default ≡ GRAPH divergence is gone at the root, not patched. The off-ADR bug class (*default lags tree*) cannot recur — there is no second implementation to lag.
- **A large code deletion.** The slot-based operators, the buffer-pattern dispatch, and their siblings go (the GRAPH cutover deleted 1,585 lines; the default-path executor is larger).
- **The planner survives as a selectivity *oracle*, not an executor** — feeding the tree-reordering pass. Its synthetic-offset fragility is bounded to that pass.
- **The default path becomes zero-GC** (ADR-045's property), a latency-predictability win that matters at scale.
- **Risk: performance.** If selectivity-planned tree evaluation cannot match the old path at scale, the cutover **stalls at Epistemics** — that is precisely the validation this ADR gates on, and the reason it is Proposed, not Accepted.

## Alternatives considered

- **Keep two executors (status quo).** Rejected: it *is* the divergence ADR-045 named; the cleanup shows it keeps shipping bugs (four in June 2026).
- **Point-fix the old default path forever.** Rejected: the treadmill — each VALUES / planner gap is a separate fix, and the two implementations drift again.
- **Correctness cutover only, keep the planner-executor for "large" queries (size-routed split).** Rejected: a behavior split by query size is the two-path anti-pattern ([[feedback_no_behavior_flags]]); it re-creates two executors under a threshold.

## Engineering order

1. **Differential gate** — the *default ≡ tree* harness over the W3C suite + dogfood; characterize **every** parity gap (Emergence → Epistemics).
2. **Correctness parity** — fix the gaps on the tree: VALUES canonicalization, zero-length-path membership, `_defaultGraphs` union, + whatever the differential surfaces. Gate green.
3. **Planner-integration design** — reorder the `PatternArray` BGP by the `QueryPlanner` selectivity model; validate the design on the BGP benchmark (Epistemics — the central unknown).
4. **Performance parity** — measure the planned tree vs the old path on the benchmark + the Wikidata gradient; gate on no regression.
5. **Flip + delete** — route the default dispatch to the tree; delete the old default-path executor; extend the allocation gate (Engineering).

Steps 1–2 (correctness) are tractable now. Step 3 (planning) is the load-bearing unknown that keeps this Proposed: **Accepted is earned by validating that a selectivity-planned tree matches the old path's performance** — not before.

## References

- [ADR-045](ADR-045-graph-clause-feature-parity.md) — the GRAPH cutover this completes; the metamorphic mirror gate is the template for the differential gate.
- [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) / [ADR-035](ADR-035-phase7a-metrics-infrastructure.md) — Wikidata-scale performance context (why the planner matters).
- `ck:obs-values-only-empty`, `ck:obs-values-join-default-path-incomplete` — the VALUES evidence + the three known correctness gaps.
- `ck:obs-facade-rdfstar-planner-crash` — the planner-fragility evidence (default-path-only crash).
