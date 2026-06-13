# Limit: Unbounded result-set materialization at scale

**Status:**        Monitoring
**Surfaced:**      2026-06-13, via the ADR-047 materialization discussion — the "ordered, unbounded query across all 21.3 B Wikidata" thought experiment
**Last reviewed:** 2026-06-13
**Promotes to:**   ADR/guard when the upgraded global Mercury tool (CLI + MCP) ships on the tree-unified path — a **fail-fast guard is a ship-gate** for that release. Independently triggered if any query path can return more than ~RAM/row-size rows without a bound.

## Description

`SparqlEngine` drains the query iterator into a fully-materialized `List<Dictionary<string,string>>` (`src/Mercury/SparqlEngine.cs:300`, drained by `while (results.MoveNext())` at `:354`/`:379`/`:406`). The **entire** result set is built on the managed heap before the caller sees the first row — the *result floor*. There is no row cap, no memory budget, no timeout-with-partial-results, and no streaming hand-off.

Concretely, a solution row projecting ~3 IRI-valued variables is ~500 B–1 KB on the heap (the `Dictionary`, its internal arrays, and the key/value strings). So:

- **~tens of millions of rows OOMs the process** (well under 50 GB on a 128 GB host; far less on the M1). The fall-over point is **unmeasured**.
- An *unbounded* result across the 21.3 B Reference store is not merely slow — `21.3 B × ~500 B ≈ ~10 TB`, physically impossible in RAM, and held before row one is emitted.
- **ORDER BY without LIMIT is strictly worse**: it must materialize *all* rows before returning *any*, and Mercury's ORDER BY is an **in-memory sort only** (`MoveNextOrdered` → `List<MaterializedRow>.Sort`) — there is no external/disk sort. DISTINCT over a high-cardinality projection is the same shape.

Both executors share this — it is **not** a tree-vs-old-path regression. ADR-047's aggregate fold (`FoldFlatBgpAggregate`) and ORDER BY + LIMIT top-K (`StreamFlatBgpTopK`) bound the *reducible* cases to O(1)/O(K). **This entry is the residual: the non-reducible large-result case**, which neither fix addresses and which a disk spill *alone* cannot fix (the result floor holds — the sorted set is collected into `List<Dictionary>` regardless).

## Trigger condition

- Any query whose result exceeds ~`available-RAM / per-row-bytes` rows. Most acute shapes: unbounded `ORDER BY` / `DISTINCT`, or a plain `SELECT` over a high-cardinality pattern, at Reference scale.
- **Held latent today only by usage, not by design**: the 21.3 B Reference store is query-validated with *bounded* benchmark queries (WDBench carries `LIMIT`s and a cancellation timeout); the MCP semantic-memory store is small. Nothing structurally prevents an unbounded query from being issued — including a future autonomous loop or a careless MCP call — and it would OOM the tool, not error cleanly.

## Current state

Monitoring. The reducible cases are bounded (ADR-047). The **fail-fast guard is now shipped** (mitigation 1 below): a query that would materialize more than `StorageOptions.MaxResultRows` (default 10,000,000; `0` = unbounded) rows throws `ResultLimitExceededException` — surfaced as a clean failed `QueryResult`, not an OOM — checked at all four accumulation sites (the result drain, the ORDER BY collect, the GROUP BY group set, and the tree's BGP leaf). That meets the ship-gate: the global tool fails *legibly* on an unbounded query instead of dying. **What remains** is the *capability* to actually return a large result without holding it all (streaming presentation + external-sort ORDER BY, mitigations 2–3) and the **unmeasured fall-over point** (the cap is a safe default, not a characterized OOM threshold).

## How other RDF substrates handle this (verified 2026-06-13)

The field has confronted this for years. Two complementary answers, and **no serious engine returns arbitrarily large result sets** — they all bound it:

- **Streaming projection is universal.** Jena (lazy `ResultSet`), RDF4J (lazy `QueryResult`, `QueryResults.stream`, direct `TupleQueryResultHandler` stream-to-disk), and QLever stream a plain `SELECT` row-by-row and never materialize the full bag. **Mercury is the outlier here** — it collects into `List<Dictionary>`.
- **ORDER BY is bounded by spill, top-K, or cap.** Apache Jena's `SortedDataBag` is a hybrid in-memory→disk **external merge sort**, but the spill threshold **defaults to `-1` — always in-memory, never spills** unless configured. Jena also has [JENA-89] *"Avoid a total sort for ORDER BY + LIMIT"* — i.e. a **top-K, exactly the ADR-047 optimization** (independent validation that this is the right move). RDF4J's `OrderIterator` sorts in memory (docs explicitly caution memory for large sets).
- **Hard result caps as a safety net.** Virtuoso ships `ResultSetMaxRows` (default **10 000**, per the DBpedia endpoint config) and an absolute **2²⁰ = 1 048 576** HTTP-response cap. QLever's interface returns only a few results unless explicitly asked for more/all.
- **Anytime / partial results.** Virtuoso's *Anytime Query* (v6+) returns best-effort **partial** results on timeout as a normal `200 OK` (the client checks HTTP headers), governed by `MaxQueryExecutionTime`. This is the operational answer for public endpoints over large stores.

**Takeaway:** Mercury is currently the *least-defended* of the comparison set at scale — it materializes (where the field streams), sorts in-memory only (where Jena spills), and has no cap/timeout/anytime (where Virtuoso and QLever bound by design). A result cap is not a workaround; it is what mature engines do deliberately.

## Candidate mitigations

In rough order of cost-to-value; the guard is the committed ship-gate, the rest are the real-capability arc.

1. **Fail-fast guard (ship-gate, cheap, both executors). — SHIPPED 2026-06-13.** Committed design: a **row-count cap** (`StorageOptions.MaxResultRows`, default 10 M, `0` = unbounded), **fail-fast typed error** (`ResultLimitExceededException`, mirroring Virtuoso's `ResultSetMaxRows`). `ResultLimitExceededException.ThrowIfExceeded` is called at every row-accumulation site — `SparqlEngine`'s SELECT/CONSTRUCT/DESCRIBE drains, `CollectAndSortResults` (ORDER BY), `CollectAndGroupResults` (GROUP BY), and `TreeJoinExecutor.JoinAt`'s leaf — because ORDER BY/GROUP BY/the tree materialize before the drain runs. Default is uniform and per-store-tunable (set explicitly lower on a memory-constrained host, higher on a big-memory Reference host); a *per-profile default* was considered and deferred — the real differentiator is host RAM, not profile, so a uniform predictable default + explicit override is the honest contract. `ResultLimitGuardTests` pins all four sites + opt-out + under-cap.
2. **Streaming result presentation.** Yield each `MoveNext` row to an output writer/`IAsyncEnumerable` instead of collecting into `List<Dictionary>`. The internal `QueryResults.MoveNext` **already streams** the regular case, so a plain `SELECT` becomes fully streaming with a thin presentation change. Table-stakes vs the field.
3. **External-sort ORDER BY.** A `MaterializedRow` variant of the spill pattern (the existing `ExternalSorter<T>` requires `T : unmanaged`, so it cannot hold a row as-is — needs a row-serializing sibling or a sort over packed rows). Spill sorted runs, k-way merge, stream the merged output. Pairs with (2); mirrors Jena's `SortedDataBag`.
4. **Anytime / partial + timeout.** `QueryCancellation` already threads a per-`MoveNext` cancellation check (the WDBench timeout work); returning the partial bag with an explicit "incomplete" flag rather than discarding it is a smaller add on top.

## References

- `src/Mercury/SparqlEngine.cs:300` — the `List<Dictionary<string,string>>` result collection (the result floor); drained at `:354`/`:379`/`:406`
- `src/Mercury/Sparql/Execution/QueryResults.Modifiers.cs` — `MoveNextOrdered` (in-memory `List<MaterializedRow>.Sort`, no external sort)
- [ADR-047](../adrs/mercury/ADR-047-default-path-cutover.md) — the materialization discussion; the aggregate fold + top-K bound the *reducible* cases, this is the residual
- [External-merge intermediate disk pressure](external-merge-intermediate-disk-pressure.md), [ExternalSorter FD-pool bypass](externalsorter-fd-pool-bypass.md) — the existing bulk-load spill machinery a row external-sort would extend
- Field comparison (verified 2026-06-13): [Jena SortedDataBag](https://jena.apache.org/documentation/javadoc/arq/org.apache.jena.arq/org/apache/jena/atlas/data/SortedDataBag.html), [JENA-89 (top-K for ORDER BY + LIMIT)](https://issues.apache.org/jira/browse/JENA-89), [Virtuoso Anytime Queries](https://docs.openlinksw.com/virtuoso/anytimequeries/), [Virtuoso 2²⁰ HTTP result cap (#700)](https://github.com/openlink/virtuoso-opensource/issues/700), [QLever on Wikidata](https://qlever.dev/wikidata), [RDF4J QueryResults streaming](https://rdf4j.org/documentation/tutorials/getting-started/)
