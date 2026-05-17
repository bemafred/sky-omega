# Limit: GRAPH-clause parser drops property-list shorthand continuations

Status:        **Resolved** (fixed 2026-05-17 in 1.7.71 — added `;` continuation handler to `ParseGraph` mirroring the existing handler in `TryParseTriplePattern`)
Surfaced:      2026-05-17, during dogfood verification of the new `<urn:sky-omega:discipline:recall>` triple inserted to teach `text:match` over `CONTAINS`
Last reviewed: 2026-05-17
Promotes to:   N/A — resolved by bug fix, no ADR required.

## Resolution

Added `while (Peek() == ';')` continuation handlers to both pattern-parsing loops inside `ParseGraph` (`src/Mercury/Sparql/Parsing/SparqlParser.Clauses.cs`):

- **Main loop (after line 3973):** handles the canonical `GRAPH <g> { ?s p1 ?o1 ; p2 ?o2 }` shape.
- **Nested-group inline loop (after line 3931):** handles `GRAPH <g> { { ?s p1 ?o1 ; p2 ?o2 } }`.

Both new handlers mirror the existing one in `SparqlParser.cs TryParseTriplePattern` (lines 629-657): skip `;`, check for empty continuation / end-of-list keywords, parse next predicate-object pair with same subject, expand sequence paths if needed.

**Validation:**

- `Select_PropertyListShorthandInGraphClause_ProducesTwoTriplePatterns` — now passes (was the failing test that proved the bug).
- `Select_PropertyListShorthand_ProducesTwoTriplePatternsWithDistinctObjects` — still passes (non-GRAPH case unaffected).
- Full Mercury test suite: 4,459 passed, 0 failed (no regressions).
- End-to-end CLI repro via fresh source build: both `?o1` and `?o2` bindings render correctly.

## Root cause (located 2026-05-17)

**`ParseGraph` in `src/Mercury/Sparql/Parsing/SparqlParser.Clauses.cs` (lines 3805-3987) lacks `;` continuation handling.** The parsing loop inside `GRAPH { ... }` blocks only handles `.`-terminated triple patterns:

```csharp
// Lines 3948-3979 (current, missing the ';' handler)
var subject = ParseTerm();
...
var (predicate, path) = ParsePredicateOrPath();
...
var obj = ParseTerm();
graphClause.AddPattern(new TriplePattern { Subject = subject, ... });

SkipWhitespace();
if (Peek() == '.')
    Advance();
// loop continues
```

When the parser encounters `;` after a triple, the next loop iteration calls `ParseTerm()` on `;`, which returns a default Term (Variable with Length == 0), which triggers the `break` clause on line 3949:

```csharp
if (subject.Type == TermType.Variable && subject.Length == 0)
    break;
```

So the second pattern is silently dropped. The same applies to the nested-group inline loop at lines 3901-3936.

The main BGP parser (`TryParseTriplePattern` in `SparqlParser.cs` lines 629-657) has the correct `while (Peek() == ';')` handler. The fix is to copy that block into `ParseGraph` after each `graphClause.AddPattern` call.

## Symptom (recap)

SELECT against property-list shorthand inside a GRAPH clause:

```sparql
SELECT ?s ?o1 ?o2 WHERE {
  GRAPH <urn:test:g> {
    ?s <urn:p1> ?o1 ;
       <urn:p2> ?o2 .
  }
}
```

Returns a row with `?s` and `?o1` populated; `?o2` is empty. The non-GRAPH'd version of the same query returns both bindings correctly — verified by `mercury -m` against the default graph.

Diagnostic tests in `tests/Mercury.Tests/Sparql/SparqlParserTests.cs`:

- `Select_PropertyListShorthand_ProducesTwoTriplePatternsWithDistinctObjects` — passes. Confirms the main BGP parser handles `;` correctly.
- `Select_PropertyListShorthandInGraphClause_ProducesTwoTriplePatterns` — **fails with `Expected: 2, Actual: 1`**. Confirms the GRAPH-clause parser drops the second pattern.

## Trigger condition

Any SELECT query against any Mercury 1.7.70 substrate that uses property-list shorthand inside a `GRAPH { ... }` block.

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

**Surface localized 2026-05-17: the bug is in the SPARQL executor, NOT in any surface layer.** All three Mercury query surfaces reproduce the same drop:

- **MCP `mercury_query`** (global Release 1.7.70): result row has `rule` populated, `label` empty.
- **Mercury CLI REPL** (`mercury -m`, in-memory store with the same query shape): same drop — the rendered table shows `<urn:test:s>` and `"first-literal"` but the `o2` column is empty.
- **SPARQL HTTP endpoint** (`http://localhost:3030/sparql`, the MCP server's HTTP surface): JSON response is conclusive:
  ```json
  {"head":{"vars":["rule","label"]},"results":{"bindings":[{"rule":{"type":"uri","value":"urn:sky-omega:data:text-match-over-contains"}}]}}
  ```
  The `head.vars` lists both columns, but the row contains only the `rule` binding. The `label` binding is missing from the SPARQL Results JSON — not stripped during rendering, never emitted by the executor.

The workaround (`.`-terminated patterns) renders correctly via HTTP:
```json
{"head":{"vars":["rule","label"]},"results":{"bindings":[{"rule":{"type":"uri","value":"urn:sky-omega:data:text-match-over-contains"},"label":{"type":"literal","value":"Use text:match for substring recall, not CONTAINS"}}]}}
```

Same data, same store, same surface — only the BGP shape differs. The surfaces honestly serialize what the executor produces; the executor is producing a row missing the non-first-predicate bindings.

**Investigation target narrowed to** the SPARQL execution path:
- BGP parsing into `TriplePattern` objects (property-list shorthand → multiple patterns sharing a subject)
- The operator that executes property-list-shorthand patterns — likely `MultiPatternScan` or whichever stage joins multiple TriplePatternScans on a shared subject
- Result-row assembly in `QueryResults` / projection layer

Most likely root cause: either the property-list shorthand expansion in the parser is losing the variable name for non-first-predicate objects, OR the multi-pattern execution operator is only emitting the first scan's bindings into the result row.

The fact that the row count is correct (1 result), `rule` projects, and FILTER on the missing variable behaves correctly (during initial repro on `text:match(?comment, "trigram")` — the filter matched, returning the row) tells us the binding IS being computed during execution and IS visible to the FILTER. The bug is between "binding visible to FILTER" and "binding emitted to result row."

## Severity

**High.** Property-list shorthand is one of the most common SPARQL idioms; every example in MERCURY.md's "Querying Patterns" section uses it; the newly-shipped `text:match` example uses it. Any agent or user querying Mercury via the MCP surface following these examples will see empty cells where stored values should appear.

W3C SPARQL conformance suites likely don't trigger this — the W3C tests use canonical shapes that may not exercise property-list shorthand chained beyond two predicates with all objects projected.

## Candidate mitigations

Surface-localization complete (see Current state above) — the bug is in the SPARQL executor, not in any surface layer.

**Parser cleared 2026-05-17.** A diagnostic test (`SparqlParserTests.Select_PropertyListShorthand_ProducesTwoTriplePatternsWithDistinctObjects`, passing) confirms the parser correctly produces two `TriplePattern` objects for `?s p1 ?o1 ; p2 ?o2`:

- `PatternCount == 2`
- Both patterns share the same subject term (same Start, Length, Type)
- Predicates have distinct source positions
- Both objects are `TermType.Variable`, with distinct source positions and non-zero length

The parser is innocent. The bug is downstream of parsing.

Remaining investigation, in priority order:

1. **`MultiPatternScan` (or whichever operator joins multiple TriplePatternScans on a shared subject).** The operator should emit a result row where every scan's binding contributes. The most likely root cause: the operator emits a single row with only the first scan's binding for the object variable. Check `src/Mercury/Sparql/Execution/Operators/MultiPatternScan.cs` for the row-assembly logic.

2. **`QueryResults` / projection layer.** Even if the operator binds correctly, the SELECT projection may be reading column indexes wrong for non-first-predicate variables. Check `src/Mercury/Sparql/Execution/QueryResults.cs` for the column-to-variable mapping.

3. **Add an end-to-end regression test.** Once the bug is fixed, add a test in `tests/Mercury.Tests/Sparql/` that INSERTs two patterns on a shared subject, runs `SELECT ?s ?o1 ?o2 WHERE { ?s p1 ?o1 ; p2 ?o2 }`, and asserts both `?o1` and `?o2` bindings are present in the result row. The parser-only test added 2026-05-17 covers the parsing layer; the end-to-end test would cover the execution + projection layers.

The fact that W3C SPARQL 1.1 Query conformance (421/421 passing per STATISTICS.md) doesn't catch this is itself evidence that the W3C tests don't exercise property-list shorthand chained beyond two predicates with all objects projected.

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
