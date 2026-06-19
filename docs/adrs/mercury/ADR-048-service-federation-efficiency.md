# ADR-048: Efficient SERVICE federation — bound the response, don't pull the relation

## Status

**Status:** Proposed — 2026-06-19 (Emergence. ADR-047 made the unified `TreeJoinExecutor` the sole SPARQL execution path; its `ServiceStep` evaluates a `SERVICE` clause by sending an **unconstrained** `SELECT * WHERE { … }` to the remote endpoint, materialising every returned row into a `List<MaterializedRow>`, and joining locally via the tree's nested-loop `Join`. That is correct for small/bounded remotes, but it does not scale — and against a result-capping endpoint it can return a **silently truncated** relation, i.e. a wrong answer, not merely a slow one. ADR-047 deliberately **spared `ServiceMaterializer`** (the temp-store machinery: `QuadStorePool`, `IndexedServicePatternScan`, threshold routing) as the tool a federation-efficiency effort would wire in, rather than delete it and rebuild later. This ADR proposes that effort. It is **Emergence**: the design below is grounded in code-provable facts and the federation literature, but the benefit is **not yet measured in Mercury** — a spike and empirical calibration are required before Accepted.)

## Context

### Where ADR-047 left SERVICE

The tree's `ServiceStep` (`TreeJoinExecutor.cs`) is the whole federation path now:

1. Build `query = "SELECT * WHERE " + innerWhere` — the raw SERVICE body, **no constraint from the outer bindings**.
2. `ServiceCall` invokes the injected `ISparqlServiceExecutor`, collects **all** returned rows into a `List<MaterializedRow>`.
3. `Join(input, serviceRows)` — the tree's **nested-loop** join (`TreeJoinExecutor.cs:861`), O(N·M).

For a constant endpoint this is one round-trip; for a variable endpoint (`SERVICE ?ep`) it is one round-trip per distinct endpoint. Either way the *whole remote relation matching the body* comes back before the local join filters it.

### The defining reality of federation (the premises this ADR answers)

A `SERVICE` call is the one place Mercury reaches a system it does not own:

- **We do not know the endpoint's capabilities** — which optimisations, which result formats, which limits.
- **We cannot control its access strategy** — the remote engine plans its own scan/join.
- **We cannot know the response size in advance** — there is no reliable `COUNT(*)` first (it is another round-trip and is often slow or refused).

These are not solvable; they are the constraints to design *within*.

### Worse than slow: silent truncation

Most public endpoints **cap results** (Wikidata's WDQS truncates and times out; many endpoints cap at 10k–100k rows). So the tree's unconstrained `SELECT * WHERE { body }`, against a large remote relation, does not merely transfer a lot — it can come back **capped/truncated**, and the local join then produces a **wrong (incomplete) answer with no error**. As Martin put it: fetching, say, all of a 21.3 B-triple relation is not physically available as a strategy — the endpoint refuses it first. So "fetch everything, join locally" is not a baseline that degrades gracefully; at scale it is *incorrect*.

### Honest symmetry: this is not a regression introduced by ADR-047

The deleted old path **also** fetched unconstrained for the common constant-single-endpoint case (`ExecuteServiceJoinPhase` fetched once with an *empty* binding table). It only pushed bound variables for variable endpoints and for the 2nd-plus clause in a multi-`SERVICE` join, and it routed ≥ `IndexedThreshold` (500) results into a B+Tree temp store. So the tree matches the old path for the common case and differs only in two narrower ones; ADR-047 declined to *add* a better-than-old capability mid-cutover. This ADR is that addition, not a regression repair.

## Provable facts (code-grounded, not speculation)

- The tree's intermediate join is **nested-loop, O(N·M)** with an O(vars²) per-pair compatibility check (`TreeJoinExecutor.Join`, `Compatible`). For a large remote result M joined with a large local set N this is quadratic.
- The (spared) temp-store path gave **indexed join-back**: `IndexedServicePatternScan` probes a B+Tree per row → O(N·log M), not O(N·M). The machinery still exists (`Federated/ServiceMaterializer.cs`), exercised by `ServiceMaterializerTests`, currently unwired.
- Result caps are documented behaviour of real endpoints; an unconstrained fetch's correctness therefore depends on the remote relation fitting under the cap — which we cannot know (premise 3).

## Field pattern (established elsewhere, unmeasured here)

The dominant federation optimisation is the **bound join / VALUES pushdown** (FedX is the canonical reference; SPLENDID/ANAPSID similar): collect the bound values of the join variables from the local side and send them to the remote — as a `VALUES` block or `FILTER` — so the *remote* does the filtering and returns only rows that can join. This **bounds the response to your actual keys**, which is the direct answer to "we cannot know the size" *and* to the cap-truncation problem (you are no longer asking for the whole relation). This is well-established in the literature; it is **not yet measured in Mercury** — labelled as such.

## Decision (proposed — to be validated, not yet built)

Evolve the tree's `ServiceStep` into a proper federation operator with three composable mechanisms, reusing the spared `ServiceMaterializer`:

1. **VALUES bound-join pushdown (the high-value mechanism).** When the SERVICE body shares variables with already-bound input rows, inject **one** `VALUES (?j …) { (k₁ …) (k₂ …) … }` block carrying the *distinct* bound join-key tuples into the remote query, in batches (a configurable batch size, FedX-style ~15–50 keys/request). The remote constrains its results to those keys; the local join then completes. Falls back to the unconstrained body when there are no shared/bound join variables (e.g. a SERVICE-only query). The tree's raw-substring body construction already preserves FILTERs (the old path's triple-only `BuildSparqlQuery` did not), so a `VALUES` block composes cleanly without rewriting the body.

2. **Local materialisation for reuse (the temp store as a tool).** When a (pushdown-reduced) result set is still large **and** is joined against many input rows, materialise it into the spared `ServiceMaterializer` temp store and probe it via `IndexedServicePatternScan` (O(N·log M)), instead of the tree's nested-loop `Join` over a `List`. This is the temp store used **when judged beneficial** — not a mandatory stage; small/once-used results stay in-memory.

3. **Bounded acceptance (don't trust the remote).** Guard the remote response with the substrate's unbounded-result posture (a `MaxResultRows`-analogous cap → fail-fast), so a runaway or misbehaving endpoint cannot OOM the executor; pair with `LIMIT`/pagination where the endpoint supports it.

These wire into `TreeJoinExecutor.ServiceStep`; the rest of the tree is unchanged. SERVICE earning a federation-specific operator is **justified** rather than a per-operator divergence: federation genuinely *is* different from a local scan — it is remote, capability-opaque, size-unknowable, and result-capped. The other tree operators read local indexes with known cardinality and need none of this.

## What must be validated before Accepted (the Emergence honesty)

This is a drafted decision, not measured engineering. Before it can move to Accepted:

- **A spike** against a real or faithfully-mocked **capping** endpoint, measuring (a) transfer reduction from the bound-join vs the unconstrained fetch, and (b) **correctness under a cap** (the unconstrained path returning a truncated, wrong answer; the bound-join returning the complete one). The benefit is asserted from the literature, not yet from Mercury.
- **Empirical calibration** of every constant (per the empirical-calibration discipline — validate with ≥1 run, document the basis): the `VALUES` batch size, the materialise-vs-in-memory threshold (the old `IndexedThreshold = 500` was uncalibrated for the tree), and the response-guard cap.
- **A query-recording test.** Pushdown is invisible to the current canned mock executor (it ignores the query string), so without a mock that records and asserts the emitted query, the pushdown would be untrusted-nothing. The capability must be *checked*, not just present.
- **Open questions to resolve in Epistemics:** how to cheaply decide "reused across many input rows" (materialisation trigger); partial-key-bound inputs (some join vars bound, some not); `SERVICE SILENT` interaction with the response guard; and whether a batched bound-join changes solution multiplicity vs the unconstrained-then-join bag (it must not).

## Consequences

- **Keeps the tree uniform** for every non-SERVICE operator; the federation operator is contained in `ServiceStep` + the (already-present) `ServiceMaterializer`.
- **`ServiceMaterializer` stays** (and gains a real consumer) instead of being deleted and rebuilt.
- **Correctness at scale**, not just speed: the bound-join is the mechanism that keeps a capping endpoint from silently truncating a join.
- **Cost:** net-new, federation-specific complexity (batch construction, materialisation triggers, the guard), each requiring its own validation — which is exactly why it is its own ADR rather than a clause in the ADR-047 cutover.

## Related

- [ADR-047](ADR-047-default-path-cutover.md) — made the tree the sole path and spared `ServiceMaterializer` for this work.
- [ADR-045](ADR-045-graph-clause-feature-parity.md) — the "one path" lineage this continues (federation is the one operator that legitimately diverges, for the reasons above).
- `docs/limits/` — a characterised-but-deferred entry for the current unconstrained-fetch behaviour should accompany this ADR (the gap is now named, not silent).
