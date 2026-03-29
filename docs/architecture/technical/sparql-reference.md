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
| Computed values | BIND (arithmetic expressions) |
| Aggregation | GROUP BY, HAVING, COUNT, SUM, AVG, MIN, MAX, GROUP_CONCAT, SAMPLE |
| Modifiers | DISTINCT, REDUCED, ORDER BY (ASC/DESC), LIMIT, OFFSET |
| Dataset | FROM, FROM NAMED (cross-graph joins supported) |
| Temporal queries | AS OF (point-in-time), DURING (range), ALL VERSIONS (history) |
| SPARQL-star | Quoted triples (`<< s p o >>`), expanded to reification at parse time |
| SPARQL Update | INSERT DATA, DELETE DATA, DELETE WHERE, DELETE/INSERT WHERE (WITH clause), CLEAR, DROP, CREATE, COPY, MOVE, ADD, LOAD |

**Note:** Subqueries with UNION (e.g., `{ SELECT ?x WHERE { {?x ?p ?o} UNION {?s ?x ?o} } }`) are fully supported. Implementation uses `BoxedSubQueryExecutor` to materialize UNION branch results independently, achieving 100% W3C SPARQL 1.1 Update conformance (94/94 tests).

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
- `SyntheticTermHelper` - Resolves synthetic terms from SPARQL-star expansion
- `SlotTriplePatternScan` - Slot-based variant reading from 64-byte PatternSlot
- `SlotMultiPatternScan` - Slot-based multi-pattern join reading from byte[] buffer
- `ServiceScan` - Executes SERVICE clause against remote SPARQL endpoint via ISparqlServiceExecutor
- Filter/BIND/MINUS/VALUES evaluation integrated into result iteration

## SPARQL EXPLAIN

`SparqlExplainer` generates query execution plans for analysis and debugging.

**Operator symbols:**

| Symbol | Operator | Description |
|--------|----------|-------------|
| ⊳ | TriplePatternScan | Scan index for triple pattern |
| ⋈ | NestedLoopJoin | Join two patterns |
| ⟕ | LeftOuterJoin | OPTIONAL pattern |
| ∪ | Union | UNION alternatives |
| σ | Filter | FILTER expression |
| γ | GroupBy | GROUP BY with aggregation |
| ↑ | Sort | ORDER BY |
| ⌊ | Slice | LIMIT/OFFSET |
| π | Project | SELECT projection |

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
- DateTime literals support both `xsd:date` and `xsd:dateTime` formats
- Default mode is `Current` (equivalent to calling `QueryCurrent()`)

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
