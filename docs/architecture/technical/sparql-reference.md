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
| Temporal queries | AS OF (point-in-time), DURING (range), ALL VERSIONS (history) |
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

## Temporal SPARQL Extensions

| Mode | Syntax | Storage Method | Description |
|------|--------|----------------|-------------|
| Current | (default) | `QueryCurrent()` | Data valid at `UtcNow` |
| AS OF | `AS OF "date"^^xsd:date` | `QueryAsOf()` | Data valid at specific time |
| DURING | `DURING ["start"^^xsd:date, "end"^^xsd:date]` | `QueryChanges()` | Data overlapping period |
| ALL VERSIONS | `ALL VERSIONS` | `QueryEvolution()` | Complete history |

**Design notes:**
- Temporal clauses come after LIMIT/OFFSET in solution modifiers
- DateTime literals support both `xsd:date` and `xsd:dateTime` formats; the time is a literal, not an expression
- Default mode is `Current` (equivalent to calling `QueryCurrent()`)
- `AS OF t` is the store as it stood at the instant t (ValidFrom ≤ t < ValidTo): it keeps what was older than t and
  leaves out what came later.
- `DURING [s, e]` is every version valid at some moment in the range (ValidFrom < e and ValidTo > s). A triple
  recorded before s and still valid is in it, so neither clause selects "recorded since t". Filter on a recorded time
  instead: `FILTER(?created >= NOW() - "P7D"^^xsd:dayTimeDuration)` keeps the last week's records.

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

`SparqlHttpServer` implements W3C SPARQL 1.1 Protocol using BCL HttpListener.

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
