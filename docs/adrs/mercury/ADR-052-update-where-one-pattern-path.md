# ADR-052: One Pattern Path for SPARQL UPDATE — Parse and Evaluate the Update WHERE Through the Pattern Tree

## Status

**Status:** Accepted — 2026-10-27 (Proposed → Accepted the same day, after review)

Found by dogfooding on 2026-10-27: a Mercury memory write in the DrHook finding 99 session failed to parse. Reproduced
on source 1.8.21 with a facade-level script (`SparqlEngine.Update` then `SparqlEngine.Query`; table below). This
completes for UPDATE what [ADR-045](ADR-045-graph-clause-feature-parity.md) and
[ADR-047](ADR-047-default-path-cutover.md) did for queries.

## Context

### The defects

Each row runs on a fresh store, with IRIs as objects so numeric-literal typing (see *Related defect*) can't confound
it. **Every WHERE in this table returns the correct rows when run as a `SELECT`.** The query engine is right; only the
Update path is wrong.

| # | Update | Result | Correct |
|---|---|---|---|
| 1 | `INSERT { <s> <p> ?o } WHERE { VALUES ?o { "a" "b" } }` | OK, 0 inserted | 2 inserted |
| 2 | `INSERT { <s> <p> ?x } WHERE { BIND("b" AS ?x) }` | OK, 0 inserted | 1 inserted |
| 3 | `INSERT { … } WHERE { <s> <q> ?v OPTIONAL { GRAPH <g> { … } } }` | parse error: `Expected ';' or end of update request` | 1 inserted |
| 4 | `DELETE WHERE { GRAPH <g> { ?s <p> ?v . ?s <k> <one> } }` | **deletes 2** (also `<b>`, which has no `<k>`) | deletes 1 |
| 5 | `WITH <g> DELETE { ?s <p> ?v } WHERE { ?s <p> ?v FILTER(?v = 1) }` | **deletes 2** (FILTER ignored) | deletes 1 |
| 6 | `WITH <g> INSERT { GRAPH <h> { ?s <c> ?v } } WHERE { ?s <p> ?v . ?s <k> <one> }` | OK, 0 inserted | 1 inserted into `<h>` |

1 and 2 are silent data loss. 4 and 5 silently delete triples the request did not match, which is worse. 3 is
rejected outright, so it fails safe. None of these is reached by the W3C Update suite, which passes 94/94.

### Root cause: Update has its own WHERE grammar and its own evaluator

Queries converged in ADR-045/047. Their WHERE is evaluated by `TreeJoinExecutor` over the `PatternArray` that
`SparqlParser.ParsePatternTree` builds, using one grammar for every group. UPDATE did not converge, on either side:

- **Parse.** `ParseModify` and `ParseDeleteWhere` (`SparqlParser.Update.cs`) parse the WHERE into the legacy flat
  `GraphPattern` through `ParseGroupGraphPatternSub` (`SparqlParser.Clauses.cs`). That copy of the grammar has drifted.
  For example, `ParseOptional` handles only FILTER, SERVICE and triples, so `OPTIONAL { GRAPH … }` leaves text
  unconsumed and ADR-046's whole-request check rejects it (#3). A SELECT with the same group works because the tree
  parser handles it.
- **Route.** `UpdateExecutor` chooses an evaluator by looking at that flat `GraphPattern`'s counts:
  - `ExecuteModify`: with `PatternCount == 0 && GraphClauseCount == 0 && SubQueryCount == 0`, it treats the WHERE as
    empty and runs the templates once with no bindings. A VALUES- or BIND-only WHERE has none of those, so the template
    variables are unbound and nothing is written (#1, #2).
  - `ExecuteModifyWithSimplePattern` (WITH and one triple): a direct `QueryCurrent` scan that drops every FILTER,
    BIND, OPTIONAL and MINUS beside the triple (#5).
  - `ExecuteModifyWithMultiPattern` (WITH and several triples): runs `QueryExecutor` but instantiates only the
    templates' top-level triples, dropping their GRAPH blocks (#6).
  - `ExecuteDeleteWhere`: three more routes. One is a constant-triple fast path. One is the GRAPH-only route
    `ExecuteDeleteWhereGraphOnly` → `ExecuteGraphClausePatternsForDelete`, which scans only the **first** pattern and
    deletes every match without joining the rest (its comment admits it: "variables would need more sophisticated
    handling for multi-pattern joins") (#4). The third goes through `QueryExecutor`.
  - Every route that does reach `QueryExecutor` gets there through a synthesized `Query` built from the legacy
    `GraphPattern`. `QueryExecutor` then has to rediscover the WHERE's opening brace in the whole update text
    (`FindWhereGroupStart`, which keys on the first triple's offset and falls back to the first `{`). For a WHERE with
    no triples, that fallback is the INSERT template's brace.

This is the divergence class ADR-045 and ADR-047 removed from queries. Six routes, each a partial copy of SPARQL group
semantics, gated by counts from a second, drifted parse. The comments justify most of them by ADR-009 stack pressure,
because `Query` (~8 KB) and `QueryResults` (~22 KB) are large structs. The tree path doesn't carry those structs.

### Why patching is the wrong fix

Each defect has a local patch: make `ParseOptional` accept GRAPH, add `HasValues || HasBinds` to the empty check, add
a FILTER loop to the simple route, join in the GRAPH-only route. Each patch adds to a drifted copy of what the tree
already does correctly, and the next unhandled shape fails the same way. The project rule is to unify the copies, not
patch them (ADR-045, ADR-047, ADR-049).

## Decision

**An Update's WHERE is a GroupGraphPattern, and it is parsed and evaluated exactly like a query's.** UPDATE keeps only
what is specific to UPDATE: the templates, WITH/USING as a dataset, and applying the changes.

1. **Parse with the tree grammar.**
   - `ParseModify` parses its WHERE group with `ParsePatternTree`, the parser queries use. It records the group's
     opening-brace offset on the `UpdateOperation` (`WhereGroupStart`) and builds no legacy `GraphPattern` for it.
   - `ParseDeleteWhere` does the same for its QuadPattern ([41] `DeleteWhere ::= 'DELETE WHERE' QuadPattern`). That is
     triples and GRAPH blocks, a subset of the group grammar, so it is checked with the same parser, and anything
     outside the subset is rejected as the spec requires.
   - The tree is parsed once during `ParseUpdateSequence`, so ADR-046's rule still holds: a syntax error anywhere means
     nothing is executed. It is parsed again when the operation runs, the way queries do it now (the pooled ~64 KB
     buffer is per operation, not a hot path).
2. **One evaluator.**
   - Modify and DELETE WHERE both run `TreeJoinExecutor.Evaluate` over the recorded `WhereGroupStart`, with the
     dataset as the tree's default and named graphs: WITH `<g>` is the default graph; `USING` / `USING NAMED` replace
     it (SPARQL 1.1 Update §3.1.3).
   - Every row returned instantiates the templates once. An empty WHERE (`{}`) gives one row, the empty solution, so
     no special case is needed.
   - Deleted, with nothing replacing them: the count-gated empty route, `ExecuteModifyWithSimplePattern`,
     `ExecuteModifyWithMultiPattern`, `ExecuteDeleteWhereGraphOnly`, `ExecuteGraphClausePatternsForDelete`, the DELETE
     WHERE constant fast path, and the synthesized-`Query` construction. Like ADR-046's N=1, a single constant triple
     is just the general path with one pattern.
3. **One template instantiation.** A single `InstantiateTemplates(MaterializedRow, …)` covers DELETE, INSERT and the
   DELETE WHERE template: top-level triples go into the active graph (WITH or the default), and GRAPH blocks into their
   graph, which may be a variable. That fixes #6 for every route, since only one route remains.
4. **No brace guessing for Update.** Update gives the executor the brace offset the parser recorded, so
   `FindWhereGroupStart` is never used for an Update. Passing the parser's offset for queries too is the same change
   and would retire the heuristic. It is listed as a follow-up, not done here.
5. **Fix the misleading comment.** The header of `SparqlParser.PatternTree.cs` still says the parser is
   "TEST/HARNESS-ONLY" and that multi-variable VALUES is deferred. Both have been false since ADR-047. Correct it, since
   this ADR makes that parser the only Update parser too.

## Related defect: numeric literals typed differently by Update and by queries (not in this ADR's scope)

Found while ruling out confounds for the table above:

- `INSERT DATA { <a> <k> 1 }` stores the bare atom `1`.
- A query reads `1` as `"1"^^xsd:integer`, so `{ <a> <k> 1 }` finds nothing, and neither does the explicit typed form.
  Only data written as `"1"^^xsd:integer` matches.
- `FILTER(?o = 1)` matches both, because it compares values, not atoms.

So the store holds two atoms for one RDF term, depending on which path wrote it. [ADR-044](ADR-044-sparql-update-literal-canonicalization.md)
canonicalized escapes and left numerics "unchanged". This is the "numeric literal expansion" axis that
[`docs/limits/sparql-resolve-term-family-duplication.md`](../../limits/sparql-resolve-term-family-duplication.md)
lists.

The fix is one term-materialization path for INSERT DATA, the templates and the query patterns. That is a different
seam from the WHERE convergence, and stores written before the fix already hold bare numeric atoms (the Mercury MCP
memory store among them), so the fix also needs a migration. **Recommendation:** a separate ADR (053), drafted after
this one is accepted. Not yet checked: how the Turtle and N-Triples loaders store numeric shorthand.

## Consequences

- **Positive:**
  - Every group shape a query supports works in an Update WHERE, including OPTIONAL/MINUS/UNION/EXISTS, BIND, VALUES,
    sub-SELECT, SERVICE and property paths, nested in any combination. These defects can no longer be represented,
    because no second grammar or route exists to leave a shape out of.
  - The silent deletions (#4, #5) and silent losses (#1, #2, #6) stop.
  - About 300 lines of `UpdateExecutor` go away.
- **Negative / risk:**
  - Stack depth. The deleted routes were written to avoid stack overflows (ADR-009). The tree path keeps the large
    structs off the stack and pools its buffer, but this must be measured, not assumed: run the ADR-009 deep-nesting
    and large-template tests through the new route.
  - Performance of a WITH + single-triple DELETE. The direct-scan route skipped the planner. The tree's flat-BGP scan
    should be equivalent; check it with one benchmark before deleting the route.
  - Unchanged behavior that still has to be covered: text:match pre-filtering on Update. `ExecuteGraphViaTree` derives
    trigram hints from the legacy `_cachedPattern`. Update must either get the same hints or keep the current behavior
    (no pre-filter on Update) and say so.
- **Lesson (third time):** the W3C suite passing (94/94 Update) did not mean the facade was correct (ADR-046), and did
  not exclude a second route either. The facade tests below are the boundary that production actually uses.

## Alternatives considered

- **Patch each route (fix `ParseOptional`, widen the empty check, add FILTER to the simple route, join in the
  GRAPH-only route).** Rejected: it adds to the drifted copies, and the next unhandled shape fails silently again. This
  is the pattern ADR-045 calls the recurring GRAPH-divergence bug class.
- **Keep the legacy parse, but always route through `QueryExecutor` via the synthesized `Query`.** Rejected. It fixes
  routing (#1, #2, #4, #5) but not parsing (#3). It also keeps the `FindWhereGroupStart` guess, and keeps two parsers
  that have to agree on the same WHERE.
- **Rewrite the Update as a SELECT string and run it through `SparqlEngine.Query`.** Rejected: it re-tokenizes SPARQL
  in text, loses the prologue's offsets, and is a third path.

## Validation plan

1. **Facade tests** (`SparqlEngine.Update` then `SparqlEngine.Query`): the six table rows, plus OPTIONAL/MINUS/UNION/
   EXISTS/sub-SELECT/property-path WHERE groups, under WITH, USING and USING NAMED, and GRAPH-variable templates. Each
   test asserts the triples present afterwards, not only the affected count.
2. **Equivalence:** for each WHERE in (1), the solutions the Update instantiates equal what `SELECT *` returns for the
   same group and dataset.
3. **Conformance:** the full W3C suite stays green (Update 94/94, and the whole Mercury suite).
4. **Stack:** the ADR-009 tests pass through the new route; record the worst-case stack depth.
5. **Dogfood:** rerun the Mercury MCP update that failed on 2026-10-27, a `DELETE … INSERT … WHERE { OPTIONAL { GRAPH
   … } }` memory-status rewrite, and confirm it applies in one request.
