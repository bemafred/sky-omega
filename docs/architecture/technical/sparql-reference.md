# SPARQL Engine Reference

Detailed reference for Mercury's SPARQL engine, including supported features, operators, and result formats.

## SPARQL Engine (`SkyOmega.Mercury.Sparql`)

`SparqlParser` is a `ref struct` that parses SPARQL queries from `ReadOnlySpan<char>`.

Key components:
- `SparqlParser` - Zero-GC query parser
- `QueryExecutor` - Zero-GC query execution with specialized operators
- `FilterEvaluator` - SPARQL FILTER expression evaluation
- `RdfParser` - N-Triples parsing utilities

## Supported SPARQL Features

| Category | Features |
|----------|----------|
| Query types | SELECT, ASK, CONSTRUCT, DESCRIBE |
| Graph patterns | Basic patterns, OPTIONAL, UNION, MINUS, GRAPH (IRI and variable, multiple), Subqueries (single and multiple), SERVICE |
| Federated query | SERVICE \<uri\> { patterns }, SERVICE SILENT, SERVICE ?variable (requires ISparqlServiceExecutor) |
| Property paths | ^iri (inverse), iri* (zero+), iri+ (one+), iri? (optional), path/path, path\|path, !(iri\|iri) (negated set) |
| Filtering | FILTER, VALUES, EXISTS, NOT EXISTS, IN, NOT IN |
| Filter functions | BOUND, IF, COALESCE, REGEX, REPLACE, sameTerm, text:match |
| Type checking | isIRI, isURI, isBlank, isLiteral, isNumeric |
| String functions | STR, STRLEN, SUBSTR, CONTAINS, STRSTARTS, STRENDS, STRBEFORE, STRAFTER, CONCAT, UCASE, LCASE, ENCODE_FOR_URI |
| Numeric functions | ABS, ROUND, CEIL, FLOOR, RAND |
| RDF term functions | LANG, DATATYPE, LANGMATCHES, IRI, URI, STRDT, STRLANG, BNODE |
| Hash functions | MD5, SHA1, SHA256, SHA384, SHA512 |
| UUID functions | UUID, STRUUID (uses time-ordered UUID v7) |
| DateTime functions | NOW, YEAR, MONTH, DAY, HOURS, MINUTES, SECONDS, TZ, TIMEZONE |
| Date arithmetic | dateTime or date ± xsd:dayTimeDuration or xsd:yearMonthDuration, dateTime − dateTime, date − date (XPath 3.1; an extension to SPARQL 1.1) |
| Casts | xsd:integer, xsd:decimal, xsd:float, xsd:double, xsd:boolean, xsd:string, xsd:dateTime |
| Computed values | BIND (arithmetic expressions) |
| Aggregation | GROUP BY, HAVING, COUNT, SUM, AVG, MIN, MAX, GROUP_CONCAT, SAMPLE |
| Modifiers | DISTINCT, REDUCED, ORDER BY (ASC/DESC), LIMIT, OFFSET |
| Dataset | FROM, FROM NAMED (cross-graph joins supported) |
| Memory time (Cognitive SPARQL) | MEMORIZED and FORGOTTEN after a triple pattern, with SINCE and BEFORE; INCLUDING FORGOTTEN after the query |
| SPARQL-star | Quoted triples (`<< s p o >>`), expanded to reification at parse time |
| SPARQL Update | INSERT DATA, DELETE DATA, DELETE WHERE, DELETE/INSERT WHERE (WITH clause), CLEAR, DROP, CREATE, COPY, MOVE, ADD, LOAD |

**Note:** Subqueries with UNION (e.g., `{ SELECT ?x WHERE { {?x ?p ?o} UNION {?s ?x ?o} } }`) are fully supported. Implementation uses `BoxedSubQueryExecutor` to materialize UNION branch results independently, achieving 100% W3C SPARQL 1.1 Update conformance (94/94 tests).

### Expression values ([ADR-056](../../adrs/mercury/ADR-056-expression-value-is-the-term.md))

An expression evaluates to an RDF term or an error, by the SPARQL operator mapping (§17.3):
- **A value keeps its term.** `BIND(?o AS ?x)` binds the stored term, so `"01"^^xsd:short` stays that term, and
  `STR`, `DATATYPE` and `sameTerm` read it. A constant is the literal it abbreviates: `01` is `"01"^^xsd:integer`,
  `1.0` is an `xsd:decimal`, and `1e3` is an `xsd:double`. A computed value is written in its type's canonical form,
  so `BIND(1 + 1 AS ?x)` binds `"2"^^xsd:integer` and `BIND("a" AS ?x)` binds `"a"`.
- **Arithmetic** promotes integer → decimal → float → double. `7 / 2` is `"3.5"^^xsd:decimal`. A string is not
  coerced to a number: `"5" + 1` is an error.
- **Date arithmetic** follows XPath 3.1's operator mapping (Functions and Operators §9.7), an extension: SPARQL 1.1
  maps `+` and `-` on numbers only.
  - A date or dateTime plus or minus an `xsd:dayTimeDuration` or `xsd:yearMonthDuration` (either order for `+`) is
    moved by it, keeping its timezone: `NOW() - "P7D"^^xsd:dayTimeDuration` is a week ago. Months move the month,
    and a day past its end becomes its last day: `"2000-01-31"^^xsd:date + "P1M"^^xsd:yearMonthDuration` is
    `"2000-02-29"`.
  - A dateTime minus a dateTime, or a date minus a date, is the `xsd:dayTimeDuration` between them, in canonical
    form (`P1DT1H1.5S`, `PT0S`, `-P1D`). It is an error when one has a timezone and the other hasn't.
  - A plain `xsd:duration`, a result outside the years 1 to 9999, and any other operands are errors. SUM and AVG
    stay numeric (op:numeric-add, §18.5.1).
- **`=` and `!=`** compare numbers, strings, booleans, dateTimes, dates and durations by value. Durations are equal
  when their months and seconds are, across `xsd:duration` and its two subtypes (`"P0M"` equals `"PT0S"`).
  - Values in two different value spaces (a number and a string) are not equal.
  - A literal of an unknown datatype, or an ill-typed one, is equal only to the same term, and an error otherwise.
  - Language tags compare ignoring case.
  - A date or dateTime with a timezone and one without are ordered only when they are more than 14 hours apart.
- **`<` `>` `<=` `>=`** are defined within those value spaces only. Durations are ordered two dayTimeDurations or two
  yearMonthDurations at a time. Comparing two IRIs is an error.
- **Errors propagate.** `!`, `&&` and `||` follow the §17.2 truth tables, so `!?w` with `?w` unbound is an error. FILTER
  keeps a row only when the effective boolean value is true. An error leaves a BIND variable unbound.
- **Datatypes in constants** are expanded through the prologue (`"a"^^t:type1`). An undeclared prefix is an error,
  `xsd:` included.

## Query Execution Model

1. Parse query → `Query` struct with patterns, filters, modifiers
2. Build execution plan → Stack of operators (TriplePatternScan, MultiPatternScan)
3. Execute → Pull-based iteration through operator pipeline

## Operator Pipeline

One file per type in `Sparql/Execution/Operators/`:

- `TriplePatternScan` - Scans single pattern, binds variables from matching triples
- `MultiPatternScan` - Nested loop join for up to 12 patterns with backtracking (supports SPARQL-star expansion)
- `VariableGraphScan` - Scans patterns across all named graphs, binding a graph variable
- `DefaultGraphUnionScan` - Unions pattern results across multiple default graphs (FROM clauses)
- `CrossGraphMultiPatternScan` - Cross-graph joins where patterns match in different graphs
- `SubQueryScan` - Executes nested SELECT subquery, projects selected variables
- `BoxedSubQueryExecutor` - Isolates subquery scan stack usage in a class (breaks ref struct chain)
- `SubQueryGroupedRow` - GROUP BY / aggregate computation for subqueries
- `SubQueryJoinScan` - Joins subquery results with outer patterns via nested loop
- `QueryCancellation` - Thread-static cancellation token for query operators
- `SyntheticTerms` - The terms an expansion adds: rdf: IRIs, unbounded join variables (names without a sigil), CONSTRUCT template list nodes
- `SlotTriplePatternScan` - Slot-based variant reading from 64-byte PatternSlot
- `SlotMultiPatternScan` - Slot-based multi-pattern join reading from byte[] buffer
- `ServiceScan` - Executes SERVICE clause against remote SPARQL endpoint via ISparqlServiceExecutor
- Filter/BIND/MINUS/VALUES evaluation integrated into result iteration

## SPARQL EXPLAIN

`SparqlExplainer` generates query execution plans for analysis and debugging.

**Operator symbols.** The WHERE part of a plan is the pattern tree that executes, one operator per node (ADR-055 D6):

| Symbol | Operator | Description |
|--------|----------|-------------|
| → | Query | SELECT / ASK / CONSTRUCT / DESCRIBE |
| ⌊ | Slice | LIMIT / OFFSET (only when the query has them) |
| ↑ | Sort | ORDER BY |
| δ | Distinct | DISTINCT |
| γ | GroupBy | GROUP BY and aggregates |
| η | Having | HAVING |
| π | Project | SELECT projection |
| ⋈ | NestedLoopJoin | A group: its children joined in source order, FILTERs last |
| ⊳ | TriplePatternScan | A triple pattern (with an estimate when a store is given) |
| ⇝ | PathScan | A triple whose predicate is a property path |
| ∪ | Union | UNION, one child per branch |
| ⟕ | LeftOuterJoin | OPTIONAL, with its group |
| − | Minus | MINUS, with its group |
| ▣ | GraphScan | GRAPH with an IRI |
| ▢ | GraphVariable | GRAPH with a variable |
| ∃ | Exists | FILTER EXISTS, with its group |
| ∄ | NotExists | FILTER NOT EXISTS, with its group |
| σ | Filter | FILTER |
| ← | Bind | BIND |
| ≡ | Values | VALUES |
| ⊂ | SubQuery | A sub-SELECT, with its own modifiers and WHERE |
| ⇄ | Service | SERVICE (the remote pattern is not planned) |

## SPARQL Result Formats

| Format | Content-Type | Features |
|--------|--------------|----------|
| JSON | application/sparql-results+json | Full type info, datatypes, language tags |
| XML | application/sparql-results+xml | Full type info, datatypes, language tags |
| CSV | text/csv | Compact, values only (no type info) |
| TSV | text/tab-separated-values | Preserves RDF syntax (brackets, quotes) |

## Content Negotiation

**Supported RDF formats:**

| RDF Format | Content Types | Extensions |
|------------|---------------|------------|
| Turtle | text/turtle, application/x-turtle | .ttl, .turtle |
| N-Triples | application/n-triples, text/plain | .nt, .ntriples |
| RDF/XML | application/rdf+xml, application/xml, text/xml | .rdf, .xml, .rdfxml |
| N-Quads | application/n-quads, text/x-nquads | .nq, .nquads |
| TriG | application/trig | .trig |
| JSON-LD | application/ld+json | .jsonld |

**Supported SPARQL result formats:**

| SPARQL Result Format | Content Types | Extensions |
|---------------------|---------------|------------|
| JSON | application/sparql-results+json, application/json | .json, .srj |
| XML | application/sparql-results+xml, application/xml | .xml, .srx |
| CSV | text/csv | .csv |
| TSV | text/tab-separated-values, text/tsv | .tsv |

## Memory Time: Cognitive SPARQL ([ADR-061](../../adrs/mercury/ADR-061-a-facts-own-times.md))

A Cognitive store keeps, for each period in which a quad is remembered, two times on its own clock: when the quad was
memorized (an `INSERT`) and when it was forgotten (a `DELETE`). No writer states them. Cognitive SPARQL is SPARQL 1.1
with three words that read them:

```
MEMORIZED [?t] [SINCE a] [BEFORE b]      after a triple pattern
FORGOTTEN [?t] [SINCE a] [BEFORE b]      after a triple pattern
INCLUDING FORGOTTEN                      after the query
```

| Query | Matches |
|-------|---------|
| (no word) | What is remembered now. Reads no clock. |
| `?s ?p ?o MEMORIZED ?t` | What is remembered, with `?t` bound to when it was memorized |
| `?s ?p ?o MEMORIZED SINCE a BEFORE b` | What is remembered and was memorized in `[a, b)` |
| `?s ?p ?o FORGOTTEN ?t` | What is remembered and what is forgotten; `?t` is unbound while a memory is remembered |
| `?s ?p ?o FORGOTTEN SINCE a` | What was forgotten at or after `a` |
| `... } INCLUDING FORGOTTEN` | Every pattern of the query matches forgotten memories too |

```sparql
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

# What did we memorize in the last week?
SELECT ?g ?s ?label ?when WHERE {
  GRAPH ?g { ?s rdfs:label ?label MEMORIZED ?when SINCE NOW() - "P7D"^^xsd:dayTimeDuration . }
} ORDER BY DESC(?when)

# What have we forgotten since the first of November, and when?
SELECT ?g ?s ?p ?o ?when WHERE {
  GRAPH ?g { ?s ?p ?o FORGOTTEN ?when SINCE "2026-11-01"^^xsd:date . }
}
```

**The rules:**
- Each matched period is its own solution with its own times. The words apply to the one pattern they follow, so a
  join to an older memory keeps it: last week's finding about a component named in June still finds the component.
- `SINCE` and `BEFORE` work alone, together and in either order. Every range is half-open, `[a, b)`. A range with `a`
  at or after `b` is empty, not an error. Each bound appears at most once per word, and both words may follow one
  pattern, each with its own bounds.
- A bound is an expression without variables, evaluated once per query: a literal, or
  `NOW() - "P7D"^^xsd:dayTimeDuration`. A bound that depends on a variable is a `FILTER` on the bound time:
  `?s ?p ?o MEMORIZED ?t . FILTER(?t >= ?started)`.
- A time is an `xsd:dateTime` in UTC with a millisecond fraction. A bound may also be an `xsd:date`: its first instant.
  A time without a timezone is UTC; a local day carries its offset, `"2026-01-01+01:00"^^xsd:date`.
- In the abbreviated forms the words follow an object and apply to the triple that object completes. In a `,` or `;`
  list each object may carry its own words, and so inside `[ ]`.
- `INCLUDING FORGOTTEN` stands after the solution modifiers of the outermost query, and applies to every pattern of
  it, a subquery's and a property path's included. It does not reach into `SERVICE`.
- Under `INCLUDING FORGOTTEN`, and in a pattern that carries `FORGOTTEN`, a graph variable ranges over every graph that
  holds a period. In the default view it ranges over the graphs that hold something remembered.
- The words work in an Update's `WHERE`: `DELETE { ?s ?p ?o } WHERE { ?s ?p ?o MEMORIZED BEFORE … }` forgets what was
  memorized before a time.
- **Errors:** the words on a pattern with a property-path operator or inside `SERVICE`, `INCLUDING FORGOTTEN` on a
  subquery, and any word on a Graph, Reference or Minimal store, which keeps no times. A standard SPARQL 1.1 query
  cannot reach the words, so its meaning is unchanged.

**What was remembered at a time `t`** has no word of its own. With `b` one millisecond after `t`, two patterns give
each quad of it once, with the bounds of the period that holds `t`:

```sparql
SELECT ?s ?p ?o ?m ?f WHERE {
  { ?s ?p ?o MEMORIZED ?m BEFORE "2026-11-01T12:00:00.001Z"^^xsd:dateTime }
  UNION
  { ?s ?p ?o MEMORIZED ?m BEFORE "2026-11-01T12:00:00.001Z"^^xsd:dateTime
             FORGOTTEN ?f SINCE "2026-11-01T12:00:00.001Z"^^xsd:dateTime }
}
```

When something happened in the world is content, a triple named for its source. The two memory times say only when the
store took a triple in and let it go. The words replace `AS OF`, `DURING` and `ALL VERSIONS`, which are gone (ADR-061
D8). The store's own read of the same times is `QuadStore.QueryMemories` (see [API usage](../../api/api-usage.md)).

## OWL/RDFS Reasoning (`SkyOmega.Mercury.Owl`)

`OwlReasoner` implements forward-chaining rule-based inference for RDFS and OWL ontologies.

**Supported inference rules:**

| Rule Set | Rules | Description |
|----------|-------|-------------|
| RDFS | `RdfsSubClass` | Transitive subClassOf, type inference from class hierarchy |
| RDFS | `RdfsSubProperty` | Transitive subPropertyOf, property inheritance |
| RDFS | `RdfsDomain` | Infer subject type from property domain |
| RDFS | `RdfsRange` | Infer object type from property range |
| OWL | `OwlTransitive` | TransitiveProperty closure |
| OWL | `OwlSymmetric` | SymmetricProperty inverse |
| OWL | `OwlInverse` | inverseOf bidirectional inference |
| OWL | `OwlSameAs` | Identity-based triple copying |
| OWL | `OwlEquivalentClass` | equivalentClass to mutual subClassOf |
| OWL | `OwlEquivalentProperty` | equivalentProperty to mutual subPropertyOf |

**Design notes:**
- Forward-chaining materialization (inferred triples stored in graph)
- Fixed-point iteration (runs until no new facts)
- Configurable max iterations to prevent infinite loops

## SPARQL HTTP Server (`SkyOmega.Mercury.Sparql.Protocol`)

`SparqlHttpServer` implements W3C SPARQL 1.1 Protocol using BCL HttpListener. It and Mercury.Solid's `SolidServer` run on
one listener lifecycle, the internal `HttpListenerLoop`: it starts and stops the listener, accepts each request
without waiting for the one before, answers an exception with 500 and a JSON body (`{"error": …, "message": …}`, the
body of every error either server sends), and closes each response.

**Endpoints:**
- `GET/POST /sparql` - Query endpoint
- `POST /sparql/update` - Update endpoint (when enabled)
- `GET /sparql` (no query) - Service description (Turtle)

**Content negotiation:**

| Accept Header | Result Format |
|---------------|---------------|
| `application/sparql-results+json` | JSON (default) |
| `application/sparql-results+xml` | XML |
| `text/csv` | CSV |
| `text/tab-separated-values` | TSV |
