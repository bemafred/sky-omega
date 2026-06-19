# Limit: SERVICE fetches the remote relation unconstrained (no bound-join / pushdown)

**Status:**        Monitoring
**Surfaced:**      2026-06-18, during the ADR-047 SERVICE cutover (B3) — the decision to spare `ServiceMaterializer` rather than port the old path's pushdown/spill
**Last reviewed:** 2026-06-19
**Promotes to:**   [ADR-048](../adrs/mercury/ADR-048-service-federation-efficiency.md) (Accepted 2026-06-19) — VALUES bound-join + bounded acceptance (the temp-store indexed join-back was rejected as a substrate divergence). Independently triggered if a real federated query against a result-capping endpoint returns a silently-truncated (wrong) answer.

## Description

The unified `TreeJoinExecutor.ServiceStep` (the sole SERVICE path since ADR-047 B3) evaluates a `SERVICE` clause by sending an **unconstrained** `SELECT * WHERE { <body> }` to the remote endpoint, materialising **every** returned row into a `List<MaterializedRow>`, and joining locally via the tree's **nested-loop** `Join` (O(N·M), `TreeJoinExecutor.cs`). No bound values from the outer solution are pushed into the remote query.

Two consequences, in order of severity:

- **Correctness under a result cap (the severe one).** Real endpoints cap results (Wikidata's WDQS truncates + times out; many cap at 10k–100k rows). An unconstrained fetch of a large remote relation can therefore come back **capped/truncated**, and the local join then yields a **wrong, incomplete answer with no error**. "Fetch everything, join locally" does not degrade gracefully at scale — it becomes *incorrect*.
- **Transfer + intermediate memory.** Even under no cap, the whole remote relation matching the body is transferred and held in `List<MaterializedRow>` before the local join filters it; a large remote × large local join is quadratic.

This is **not a regression** introduced by ADR-047: the deleted old path also fetched unconstrained for the common constant-single-endpoint case (it pushed bound vars only for variable endpoints and 2nd-plus multi-`SERVICE` clauses, and threshold-routed ≥500-row results into a B+Tree temp store). ADR-047 declined to *add* a better-than-old capability mid-cutover and **spared `ServiceMaterializer`** (the temp-store machinery: `QuadStorePool`, `IndexedServicePatternScan`, threshold routing) as the tool a federation effort might wire in — though ADR-048 (now Accepted) rejected that temp-store join-back as a divergence and does **not** wire it (the bound-join needs no temp store; see its *Divergence review*).

The field's answer is the **bound join / VALUES pushdown** (FedX): push the join keys, don't pull the relation — which bounds the response to the actual keys and so dodges the cap-truncation. Established elsewhere; **unmeasured in Mercury** (the canned mock executor ignores the query string, so no current test exercises pushdown).

## Trigger condition

Promote to engineering (build ADR-048) when **any** of:

- A real federated workload runs `SERVICE` against a capping endpoint where the body's remote relation exceeds the cap → silent truncation is now an *observed correctness bug*, not a latent one.
- A federation use case (cross-instance Lucy/James memory transfer, or a public endpoint) becomes a named consumer with a transfer/latency requirement the unconstrained fetch cannot meet.
- The cognitive layers (1.8.x+) begin issuing `SERVICE` queries at a scale where the O(N·M) local join or the unbounded intermediate is a measured cost.

Until then: SERVICE is correct for **small/bounded** remote relations (the common dogfood case), and the gap is named here rather than silent. The **bounded-acceptance** guard (a `MaxResultRows`-analogous fail-fast on the remote response) is the cheapest partial mitigation and could land ahead of the full ADR-048 if a runaway endpoint is observed.
