# ADR-048: Efficient SERVICE federation — bound the response, don't pull the relation

## Status

**Status:** Accepted — 2026-06-19 (Epistemics. ADR-047 made the unified `TreeJoinExecutor` the sole SPARQL execution path; its `ServiceStep` evaluates a `SERVICE` clause by sending an **unconstrained** `SELECT * WHERE { … }` to the remote endpoint, materialising every returned row into a `List<MaterializedRow>`, and joining locally via the tree's nested-loop `Join`. That is correct for small/bounded remotes, but against a result-capping endpoint it can return a **silently truncated** relation — a wrong answer, not merely a slow one.

The approach is **validated and approved**: the **VALUES bound-join pushdown** is the convergent, field-standard fix, and its correctness is proven *by construction* (see *Correctness*) — it preserves the exact join multiset and is the mechanism that prevents cap-truncation. What remains is engineering and magnitude-measurement, not a decision. The one design refinement on the way to Accepted — applying the lesson of the ADR-045/047 divergence cleanup — was to **reject the temp-store indexed join-back (the originally-drafted mechanism #2) as a substrate divergence** (see *Divergence review*); the ADR-047 spare of `ServiceMaterializer` is reconsidered accordingly.)

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

## Decision

Evolve the tree's `ServiceStep` into a proper federation operator with **two** convergent mechanisms — both of which keep the single tree `Join` as the only join-back:

1. **VALUES bound-join pushdown — the mechanism.** When the SERVICE body shares variables with already-bound input rows, inject **one** `VALUES (?j …) { (k₁ …) (k₂ …) … }` block carrying the *distinct* bound join-key tuples into the remote query, in disjoint batches (a configurable batch size, FedX-style ~15–50 keys/request — a round-trips-vs-request-size knob that does not affect correctness). The remote constrains its results to those keys; the existing nested-loop `Join` then completes locally. Falls back to the unconstrained body only when there are **no** shared/bound join variables (a SERVICE-only query — there is nothing to push). The tree's raw-substring body construction already preserves FILTERs (the old path's triple-only `BuildSparqlQuery` did not), so the `VALUES` block composes by wrapping the existing body.

   *This is the whole federation-specific concern:* bounding a remote, capability-opaque, size-unknowable, result-capping **fetch**. That is genuinely different from a local scan, so a federation-specific *fetch* construction is justified — but it reuses, and does not fork, the join.

2. **Bounded acceptance — don't trust the remote.** Guard the remote response with the substrate's existing unbounded-result posture (a `MaxResultRows`-analogous cap → fail-fast; under `SERVICE SILENT`, treat a tripped guard as a failed endpoint → the empty multiset, exactly as `ServiceCall`'s `catch when (silent)` does today), so a runaway or misbehaving endpoint cannot OOM the executor. Pair with `LIMIT`/pagination where the endpoint supports it. With the bound-join in place a guard trip is far less likely (the response is key-bounded), but the guard is the correctness backstop for the SERVICE-only fallback and for high-fan-out keys.

These wire into `TreeJoinExecutor.ServiceStep`; **the rest of the tree, and the one `Join`, are unchanged.**

### Divergence review (applying the ADR-045/047 lesson)

The original draft had a third mechanism: route a large (even pushdown-reduced) result into the spared `ServiceMaterializer` temp store and probe it via `IndexedServicePatternScan` (O(N·log M)) instead of the nested-loop `Join` (O(N·M)). **Rejected — it is a substrate divergence of exactly the kind that cost the ADR-045/047 cleanup.** Two reasons, decisive together:

- **It is a second join-back implementation.** The tree has *one* `Join`, used by every operator and already keyed on binding hashes (`Compatible`). A SERVICE-only indexed join-back is a parallel join path — the two-implementations smell, gated by a threshold (`IndexedThreshold`), in a base substrate.
- **It mis-classifies a general concern as federation-specific.** "The nested-loop `Join` is O(N·M)" is true for *every* join in the tree, not just SERVICE. If join cardinality ever becomes a measured bottleneck, the convergent fix is to improve the *one* `Join` (a hash join over the binding hashes it already computes), which benefits every operator — not to bolt a B+Tree temp store onto federation alone. Solve general problems generally; solve federation-specific problems (the *fetch*) specifically.

Consequence: **ADR-048 does not wire `ServiceMaterializer`.** That reconsiders the ADR-047 spare, whose stated purpose was to be "the tool ADR-048 wires in." Recommended: let `ServiceMaterializer` (and `QuadStorePool`/`IndexedServicePatternScan` if they have no other consumer) become deletion candidates rather than dormant parallel machinery — a dormant second implementation is the latent catastrophe, not a saving. (Flagged as a decision, since the spare was an explicit ADR-047 call; deletion is a separate, verifiable step, not pre-shipped here.)

## Correctness (resolved in Epistemics — why this is Accepted)

The questions that gated Accepted are resolved by reasoning grounded in the actual `Join`/`Compatible` (`TreeJoinExecutor.cs:863–900`):

- **Multiplicity is preserved (the linchpin).** `Join` emits one merged row per *compatible* (l, r) pair, where compatible means r agrees with l on every shared variable. Any remote row r whose join-key is **not** among the input's keys is compatible with **no** l and contributes nothing. So restricting the remote to the input's join-keys drops only non-joining rows: `Join(input, fetch_all)` ≡ `Join(input, fetch_for_input_keys)` exactly — **provided** (a) the pushed key tuples are **distinct** (a duplicated `VALUES` tuple would multiply r, since `VALUES` is a multiset join) and (b) batches **partition** the key set (each key in exactly one batch; results unioned). Both are construction invariants, not measurements.
- **Partial-key-bound inputs.** Push the `VALUES` for the join variables bound in the input (all materialised input rows are bound), restricted to those that also occur in the SERVICE body. Unbound/non-shared body variables come back free and the local `Join` still matches on every shared variable. A subset `VALUES` is a valid (weaker) restriction — correct, just less reductive.
- **`SERVICE SILENT` + the guard.** A tripped response-guard under `SILENT` is treated as a failed endpoint → the empty multiset (the existing `ServiceCall` semantics); non-`SILENT` fails fast. No new SILENT path.

What remains is **engineering and measurement, not decision** — Completed-phase work:

- **Magnitude, not direction.** That the bound-join transfers no more than the unconstrained fetch for the same keys is certain by construction; *how much* it reduces transfer/latency is measured during the build, against a faithfully-mocked **capping** endpoint, alongside a correctness check (unconstrained → truncated wrong answer under a cap; bound-join → complete).
- **A query-recording mock executor** is the verification mechanism: the current canned mock ignores the query string, so the build must add a mock that records and asserts the emitted `VALUES` block — the capability must be *checked*, not assumed present (else it is untrusted-nothing).
- **One calibrated constant:** the `VALUES` batch size (round-trips vs request-size; FedX uses ~15–50), calibrated with ≥1 run, basis documented. (The old `IndexedThreshold` is gone with mechanism #2.)

## Consequences

- **One join path preserved.** The federation logic is contained in `ServiceStep`'s *fetch construction* (the `VALUES` block) plus the response guard; the single tree `Join` is reused, not forked. Every non-SERVICE operator is untouched.
- **Correctness at scale**, not just speed: the bound-join is the mechanism that keeps a result-capping endpoint from silently truncating a join.
- **`ServiceMaterializer` is *not* wired** (see *Divergence review*) — it becomes a deletion candidate rather than dormant parallel machinery. If join cardinality ever needs addressing, the convergent home is a general hash `Join`, separately and for all operators.
- **Cost:** net-new but contained federation-fetch complexity (distinct-key extraction, batched `VALUES` construction, the guard), verified by a query-recording test — which is why it is its own ADR rather than a clause in the ADR-047 cutover.

## Related

- [ADR-047](ADR-047-default-path-cutover.md) — made the tree the sole path and spared `ServiceMaterializer` for this work; this ADR reconsiders that spare (the bound-join needs no temp store).
- [ADR-045](ADR-045-graph-clause-feature-parity.md) — the "one path" lineage this continues. Refinement: it is federation's *fetch* that legitimately differs (remote, capability-opaque, capped), **not** its join — the join stays the one tree `Join`.
- [`docs/limits/service-unconstrained-fetch.md`](../../limits/service-unconstrained-fetch.md) — the characterised-but-deferred entry for the current unconstrained-fetch behaviour; promotes to this ADR.
