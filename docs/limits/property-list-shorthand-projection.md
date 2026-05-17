# Limit: Property-list shorthand drops non-first-predicate object projections

Status:        **Triggered** (reproducible on 1.7.70 via `mercury_query` MCP)
Surfaced:      2026-05-17, during dogfood verification of the new `<urn:sky-omega:discipline:recall>` triple inserted to teach `text:match` over `CONTAINS`
Last reviewed: 2026-05-17
Promotes to:   ADR when the surface (SPARQL executor binding propagation vs MCP result serialization) is localized. Triggered status warrants an investigation, not a deferral.

## Description

When a SPARQL query uses property-list shorthand — the `;` continuation chaining multiple predicates on the same subject — only the **first** predicate's object variable projects correctly into the SELECT result. Subsequent predicates' object variables render as empty cells in the result row, even though:

- The row is returned (count = 1 when the BGP matches),
- The subject variable (and other first-position variables) render correctly,
- Any FILTER referencing the missing variable behaves as if the binding exists (so internally the value is bound; only the projection-to-result layer is dropping it).

The same triples written out as separate `.`-terminated patterns project correctly. The bug is specific to the `;` shorthand.

## Trigger condition

Any SELECT query against any graph, on the current Mercury 1.7.70 substrate, that:

1. Uses property-list shorthand (`?subject pred1 ?obj1 ; pred2 ?obj2 [...]`),
2. Projects `?obj2` (or any non-first predicate's object variable) in the SELECT clause.

Reproducer (against the `<urn:sky-omega:discipline:recall>` graph populated 2026-05-17):

```sparql
PREFIX sky: <urn:sky-omega:>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?rule ?label WHERE {
  GRAPH <urn:sky-omega:discipline:recall> {
    ?rule a sky:RecallDiscipline ;
          rdfs:label ?label .
  }
}
```

Result via `mercury_query`:
```
rule	label
<urn:sky-omega:data:text-match-over-contains>	

1 result(s)
```

Expected: `?label` column populated with `"Use text:match for substring recall, not CONTAINS"`.

Same query rewritten without `;`:

```sparql
SELECT ?rule ?label WHERE {
  GRAPH <urn:sky-omega:discipline:recall> {
    ?rule a sky:RecallDiscipline .
    ?rule rdfs:label ?label .
  }
}
```

Renders the literal correctly.

The bug affects both **literal** and **URI** projections — tested with `?rule a sky:RecallDiscipline ; rdfs:label ?label ; a ?t .` where `?t` (URI type) also rendered empty.

## Current state

Surface unconfirmed. The bug was observed via the **MCP `mercury_query` tool** (global Release 1.7.70). It has not yet been verified whether the same query shape renders correctly via:

- The Mercury CLI REPL (`:query` command)
- The SPARQL HTTP endpoint (`http://localhost:3031/sparql`)
- The embeddable .NET API (`SparqlEngine.Execute`)

If only `mercury_query` is affected, the bug is in `Mercury.Mcp` result serialization. If all surfaces are affected, the bug is in the SPARQL executor's BGP binding propagation or in the `QueryResults.Patterns` enumerator that backs the projection pipeline.

The fact that:

- The row count is correct,
- The first predicate's object renders,
- FILTER on the missing variable behaves correctly (verified during initial repro on the `text:match(?comment, "trigram")` query — the filter matched, returning the row),

suggests the binding IS internally correct and the bug is downstream of execution — most likely in the result-projection layer or the MCP serialization.

## Severity

**High.** Property-list shorthand is one of the most common SPARQL idioms; every example in MERCURY.md's "Querying Patterns" section uses it; the newly-shipped `text:match` example uses it. Any agent or user querying Mercury via the MCP surface following these examples will see empty cells where stored values should appear.

W3C SPARQL conformance suites likely don't trigger this — the W3C tests use canonical shapes that may not exercise property-list shorthand chained beyond two predicates with all objects projected.

## Candidate mitigations

1. **Localize the surface.** Run the same reproducer query through `mercury -m` CLI REPL and through the SPARQL HTTP endpoint. If both render correctly, the bug is in `Mercury.Mcp.MercuryTools.Query` or the MCP result-formatting code path. If both also drop the values, the bug is in the SPARQL executor.

2. **Inspect `QueryExecutor` BGP execution** for property-list shorthand. The SPARQL parser expands `?s pred1 ?o1 ; pred2 ?o2` into two TriplePatternScans on the same subject; the executor should propagate bindings across both scans into the result row. Check whether the second scan's object binding is being overwritten or skipped when the result row is assembled.

3. **Inspect `MercuryTools.Query` result serialization** in `Mercury.Mcp`. If the executor produces correct bindings but the MCP serializer reads the wrong column index for non-first-predicate variables, the bug is here.

4. **Add a regression test.** A direct test against the property-list shorthand projection shape — `?s p1 ?o1 ; p2 ?o2` with SELECT projecting both `?o1` and `?o2` — should be added to `tests/Mercury.Tests/Sparql/` once the bug is fixed, to prevent recurrence.

## Workaround

Rewrite property-list shorthand chains as separate `.`-terminated patterns when querying via the MCP surface:

```sparql
# Instead of:
?rule a sky:RecallDiscipline ;
      rdfs:label ?label ;
      rdfs:comment ?comment .

# Use:
?rule a sky:RecallDiscipline .
?rule rdfs:label ?label .
?rule rdfs:comment ?comment .
```

Both shapes are spec-equivalent SPARQL.

## References

- Reproducer: this document's "Trigger condition" section, executed against the `<urn:sky-omega:discipline:recall>` graph populated 2026-05-17.
- Related: MERCURY.md "Querying Patterns" examples — all use property-list shorthand and would be bitten when run via MCP.
- Mercury MCP version: 1.7.70+6338a87 (`~/.dotnet/tools/mercury-mcp`).
- Substrate at observation: Cognitive profile, `~/Library/SkyOmega/stores/mcp`, 2,151 quads at observation time.
