# ADR-055: One WHERE Parse for Queries — Parse a Query's WHERE Once, by the Pattern Tree, and Evaluate Every Query Form Through It

## Status

**Status:** Proposed — 2026-10-27

Found while probing for [ADR-053](ADR-053-one-rdf-term-one-atom.md): `SELECT ?o WHERE { { ?s ?p ?o } UNION { GRAPH ?g
{ ?s ?p ?o } } }` fails to parse, and it fails the same way on the source before
[ADR-052](ADR-052-update-where-one-pattern-path.md). This is the query-side counterpart of ADR-052: an Update's WHERE
is now parsed once by the pattern tree, while a query's WHERE is still parsed twice, and the first parse decides
where the query goes. It also closes ADR-052's follow-up "queries still guess where their WHERE begins", and the
divergence register's A4 (two group-body parsers) and B3 (flat `GraphPattern` WHERE parser, parse-then-discard).

## Context

### The defects (probe on 1.8.23)

Data: the default graph holds `<s> <p> <o>`; the named graph `<g>` holds `<s> <p> <o2>` and `<s> <q> <o>`. Expected
results are from SPARQL 1.1 Query: §18 algebra, §16.2 ASK, §16.4 DESCRIBE.

| # | Query (abbreviated) | Expected | 1.8.23 |
|---|---|---|---|
| S1 | `{ ?s ?p ?o } UNION { GRAPH ?g { ?s ?p ?o } }` | 3 rows | parse error |
| S2 | `{ { GRAPH ?g { ?s ?p ?o } } }` | 2 rows | parse error |
| S5 | `?s ?p ?o OPTIONAL { { GRAPH ?g { ?s <q> ?z } } }` | 1 row, `?g` bound | parse error |
| S6 | `?s ?p ?o . { GRAPH ?g { ?s ?x ?o } }` | 1 row | parse error |
| S9 | `{ { VALUES ?x { 1 2 } } }` | 2 rows | parse error |
| A6 | `ASK { { … } UNION { GRAPH ?g { … } } }` | true | parse error |
| E1 | `SELECT * {}` | 1 row (the empty solution) | 0 rows |
| E5 | `SELECT * { MINUS { ?s ?p ?o } }` | 1 row | 0 rows |
| A1 | `ASK {}` | true | false |
| A2 | `ASK { VALUES ?x { 1 } }` | true | false |
| C1 | `CONSTRUCT { <a> <b> ?x } WHERE { BIND(1 AS ?x) }` | 1 triple | 0 |
| C2 | `CONSTRUCT { ?s ?p ?o } WHERE { GRAPH ?g { ?s ?p ?o } }` | 2 triples | 0 |
| C3 | `CONSTRUCT { <a> <b> ?x } WHERE { VALUES ?x { 1 } }` | 1 triple | 0 |
| C4 | `CONSTRUCT { <a> <b> <c> } WHERE { }` | 1 triple | 0 |
| D1 | `DESCRIBE <s>` (no WHERE) | the triples about `<s>` | 0 |
| D2 | `DESCRIBE ?s WHERE { GRAPH ?g { ?s ?p ?o } }` | triples | 0 |
| D3 | `DESCRIBE ?s WHERE { VALUES ?s { <s> } }` | triples | 0 |

The same probe found correct results for MINUS and FILTER EXISTS over GRAPH, a sub-SELECT containing `{ GRAPH … }`,
UNION with BIND, OPTIONAL-only groups, BIND with FILTER, `SELECT (1 AS ?x) {}`, and ASK over GRAPH, FILTER(true)
and OPTIONAL.

C2 is the plain way to export a named graph. D1 is DESCRIBE's most common form.

### Root cause: two WHERE parses, and the first one routes

`SparqlParser.ParseQuery` parses the WHERE with the flat `GraphPattern` grammar (`ParseGroupGraphPatternSub`,
`ParseGroupOrUnionGraphPattern`, `ParseNestedGroupGraphPattern`, `ParseOptional`, … in `SparqlParser.Clauses.cs`) into
`Query.WhereClause.Pattern`. Execution then parses the same text again with the pattern tree (`ParsePatternTreeAt`) and
evaluates that with `TreeJoinExecutor`. Since ADR-047 the tree is the only evaluator. The flat parse survives for
three jobs:

1. **It consumes the WHERE text**, so the parser can continue to the solution modifiers. Its nested-group branch
   handles FILTER, OPTIONAL, SERVICE, BIND, nested groups and triples, but not GRAPH or VALUES, so GRAPH is read as a
   subject. That is every parse error in the table.
2. **Its counts route each query form.** All of the following key off the flat parse's `PatternCount` /
   `TriplePatternCount` and `Has*` flags, copied into `QueryBuffer` by `QueryBufferAdapter`:
   - `QueryExecutor.Execute` sends a WHERE without top-level triples to `QueryResults.EmptyPattern` (its own
     BIND/FILTER/SELECT-expression evaluator), unless it has VALUES, a sub-SELECT or SERVICE. With none of those it
     returns no rows: E1, E5.
   - `ExecuteAsk` has a third evaluator for a pattern-less WHERE, and returns false when there are no BINDs or
     FILTERs: A1, A2.
   - `ExecuteConstruct` returns empty when `PatternCount == 0` and there is no sub-SELECT: C1–C4.
   - `ExecuteDescribe` returns empty when `PatternCount == 0`, whether the WHERE is absent (D1), GRAPH-only (D2) or
     VALUES-only (D3).

   The GRAPH-only route to the tree only works because the flat parse can read a top-level GRAPH. The same structures
   are ADR-052's six count-gated Update routes, on the query side.
3. **Its offsets locate the WHERE for the second parse.** `QueryExecutor.FindWhereGroupStart` guesses the opening `{`
   from the flat pattern's earliest subject offset, the last top-level `WHERE` keyword before it, and as a fallback
   the first GRAPH header or the first `{` in the text. Five ADR-047 fixes went into that guess (template brace,
   nested WHERE, nested GRAPH in EXISTS, sub-SELECT-only WHERE, multi-statement Update). The sub-SELECT step guesses
   separately: `TreeJoinExecutor` takes `_source.IndexOf('{', subStart)`, "the first brace after the SELECT clause".
   That brace is wrong for a projection that contains one, such as `(EXISTS { … } AS ?b)` or `(CONCAT("{", ?x) AS
   ?y)`.

Two more readers depend on the flat parse:
- **EXPLAIN** (`SparqlExplain.BuildPlanTree`) describes the flat pattern, not the tree that runs. It can't describe
  any query in the table's first block, because those fail to parse.
- **A sub-SELECT's own clauses** are parsed by `ParseSubSelect`, whose body goes through `ParseSubSelectPatterns`: a
  second flat group-body parser (divergence A4).

### Why patching is the wrong fix

Adding GRAPH and VALUES to `ParseNestedGroupGraphPattern` removes the parse errors and leaves the routing:
- C2, D2 and D3 stay empty;
- `ASK {}` stays false;
- the next group form the flat grammar lacks fails the same way.

The flat parser is a drifted copy of a grammar the pattern tree already implements completely. ADR-052 made that
same call for Update.

## Decision

**A query's WHERE is parsed once, by the pattern-tree grammar, and every query form evaluates that tree.** The query
parser keeps what only it parses (prologue, SELECT clause, CONSTRUCT template, DESCRIBE targets, dataset, solution
modifiers, temporal clause) and stops building a `GraphPattern` for the WHERE.

### D1. Parse

- `ParseQuery` consumes the WHERE group with `ParsePatternTreeAt`, as Update does since ADR-052
  (`SparqlParser.ParseWhereGroup`), and records its position as `Query.WhereGroupStart`. The `WHERE` keyword is
  optional (`SELECT * {}`). A DESCRIBE without a WHERE records -1.
- `SubSelect.WhereGroupStart` is recorded the same way, by the sub-SELECT's parse, and replaces
  `ParseSubSelectPatterns`.
- A syntax error anywhere in the WHERE fails the parse, as the pattern tree's strict closing has done since ADR-052.
- **`$x` is a variable everywhere** ([108] `Var ::= VAR1 | VAR2`). Nineteen parser sites test only for `?`:
  - `SELECT $s WHERE { ?s … }` parses and returns 0 rows;
  - `VALUES $x { 1 }` fails to parse;
  - `$s` in a triple pattern and in ORDER BY works.

  Every site uses one variable-start test (`<https://sky-omega.org/bugs/mercury-parser-dollar-variables>`).
- Deleted: `ParseGroupGraphPatternSub`, `ParseGroupOrUnionGraphPattern`, `ParseNestedGroupGraphPattern` and the rest
  of the flat WHERE family in `SparqlParser.Clauses.cs` that nothing else calls; `ParseSubSelectPatterns`;
  `WhereClause` and `Query.WhereClause`.
- `GraphPattern` stays: Update templates, `INSERT/DELETE DATA` and the planner's per-BGP scratch use it.

### D2. Execute: one route

- SELECT, ASK, CONSTRUCT and DESCRIBE all evaluate the tree rooted at `WhereGroupStart`. None of them inspects pattern
  counts. An empty group yields the one empty solution (§18.2), so `SELECT * {}` has one row, `ASK {}` is true, and
  `CONSTRUCT { <a> <b> <c> } WHERE {}` has one triple. ASK keeps the existence cap (`askExistenceCap`).
- Deleted:
  - `QueryResults.EmptyPattern` and its constructor;
  - `ExecuteEmptyPatternToMaterialized`;
  - `ExecuteAsk`'s pattern-less evaluator;
  - the `PatternCount == 0` gates in `ExecuteConstruct` and `ExecuteDescribe`;
  - `FindWhereGroupStart`, `FirstPatternTermStart`, `LastWhereKeywordAtOrBefore`;
  - the unreachable `QueryResults.FromMaterialized`, `CanJoinRows`, `MergeRows` and `PassesFilters`.

  The modifier layer (`FromMaterializedSimple`) already evaluates SELECT expressions per row, which is what
  `EmptyPattern` duplicated.

### D3. DESCRIBE without WHERE

`DESCRIBE <iri> …` describes the IRIs it names (§16.4). It is evaluated over one empty solution, so `DescribeResults`
gets the constant targets and no bound variables. A DESCRIBE with a WHERE describes its constant targets and the
values its variables take in the WHERE's solutions.

### D4. Sub-SELECT

`TreeJoinExecutor`'s sub-SELECT step evaluates the tree at `SubSelect.WhereGroupStart`, not at the first `{` after
the sub-SELECT's start.

### D5. QueryBuffer

`QueryBuffer` keeps the prologue, dataset, select clause, modifiers, CONSTRUCT template, post-query VALUES and the
temporal clause. These WHERE-derived pieces go, together with `QueryBufferAdapter.ConvertGraphPattern`:
- the pattern slots;
- `FilterCount`, `BindCount`, `FirstBranchBindCount` and the `Minus*` fields;
- `HasGraph`, `TriplePatternCount`, `HasValues`, `HasSubQueries`, `HasService`, `HasBinds`, `HasFilters`, `HasExists`.

Each reader is either deleted (D2) or reads the tree. The `QueryBuffer` constructor path that builds a `GraphPattern`
from slots (`BuildGraphPattern`) goes with them.

### D6. EXPLAIN describes the tree

`SparqlExplain.BuildPlanTree` builds its WHERE part from the `PatternArray` the query runs, walking it with
`EnumerateDirectChildren`. The solution-modifier part, read from the parsed modifiers, is unchanged. EXPLAIN then
shows what executes, including the group forms in the table above.

### D7. SELECT * is the WHERE's in-scope variables

The in-scope variables of the tree (§18.2.1, computed once from the parsed tree) are the variable list of a
`SELECT *`, including variables bound in no row. This is what the facade's result header needs; see *Related* for the
facade defect that header is part of.

## Open questions (to settle before Accepted)

- **Q1. EXPLAIN's vocabulary.** `SparqlExplain` (885 lines) has operator types for the shapes the flat pattern
  modelled. Which tree node kinds it has no operator for (sub-SELECT, SERVICE, VALUES, EXISTS, MINUS, property paths,
  GRAPH variables) needs reading `ExplainOperatorType` and the EXPLAIN tests before D6 is sized.
- **Q2. Parser-structure tests.** About 240 assertions in 15 test files read the flat parse: `WhereClause.Pattern`,
  `PatternCount`, `GetPattern(i)`. The largest are `SparqlParserTests` (71), `QueryExecutorTests.Service` (41),
  `.Subquery` (36) and `.Filter` (27). Each either becomes a result assertion through the facade, or an assertion on
  the parsed tree. The policy is to assert behavior unless the test is about the parser, and where the line falls is
  decided per file before implementation.
- **Q3. `Query` struct size and stack.** `GraphPattern` is a large inline struct (`MaxTriplePatterns = 32`,
  `MaxFilters = 16`, …) inside `Query`. Removing it from `Query` should lower every parse frame. How much is measured
  (ADR-009), not assumed.

## Consequences

- **Positive:**
  - The table's queries return what the spec says. Nested GRAPH, VALUES in a nested group, empty groups, ASK,
    CONSTRUCT and DESCRIBE stop depending on what a second parser understood.
  - One WHERE grammar for queries and Updates, and no brace guessing.
  - The register's A4 and B3 close, along with ADR-052's last follow-up.
  - Each query is parsed once instead of twice.
- **Negative:**
  - The test rewrite (Q2) is larger than the code change.
  - EXPLAIN output changes shape for queries it handled before; anything comparing it textually needs updating.
  - A query that is a syntax error per the grammar but was accepted by the flat parser now fails, as with ADR-052's
    `DELETE WHERE { FILTER }`.
- **Neutral:** `GraphPattern` stays as the template and planner type. Results of queries that worked before don't
  change; validation 3 checks that.

## Alternatives considered

- **Teach the flat parser GRAPH and VALUES in nested groups.** Rejected: it fixes the parse errors and none of the
  routing defects (C2, D1–D3, A1, A2, E1, E5), and keeps two grammars that drift.
- **Keep the flat parse, but route on the tree.** Rejected: the flat parse would still have to succeed on every query
  just to be discarded, so its grammar gaps would still be parse errors.
- **Parse the whole query, prologue and modifiers included, with a new tree parser.** Larger than needed: the
  non-WHERE clauses have one parser each and no reported defect.

## Validation plan

1. **Facade tests**, one per row of the table, written as inline sequences with the expected value from the spec.
   The rows the probe found correct stay as regression tests.
2. **W3C conformance stays green**, the SPARQL 1.1 Query and Update suites included.
3. **Differential:** results identical before and after for every W3C query, and for a corpus of real MCP recall
   queries (the lucy skill's shapes: `GRAPH ?g` unions, text:match, OPTIONAL, AS OF). Any difference must be a table
   defect being fixed.
4. **Stack** (ADR-009): smallest thread stack for a representative query set before and after (Q3), measured the way
   ADR-052 did.
5. **Parse cost:** parse time of the W3C query corpus before and after, since each query now parses once.
6. **EXPLAIN:** the existing EXPLAIN tests pass or are updated per Q1, and EXPLAIN of S1 shows the UNION and GRAPH it
   runs.
7. **Dogfood:** a `mercury_query` with `{ … } UNION { GRAPH ?g { … } }`, and `DESCRIBE <iri>` against the memory
   store.

## Related, not in this ADR

**The result facade names columns from the first row** (`<https://sky-omega.org/bugs/mercury-facade-first-row-column-names>`).
`SparqlEngine.ExecuteSelect` computes the column names from the first row's bindings, then maps every later row by
position:
- With `SELECT ?s ?l { … OPTIONAL { ?s <label> ?l } } ORDER BY ?s`, every `?l` is dropped when the first row has
  none. `ORDER BY DESC(?s)` returns them.
- In `{ ?s ?p ?o } UNION { BIND(1 AS ?x) }`, the second row comes back as `s=1`. `SELECT *` lists only the first
  row's variables, so `{ ?a ?b ?c }` is reported as `s, p, o`.

It reached `mercury_query`, the HTTP endpoint and the CLI, and it was silent.

**Fixed first, separately (2026-10-27):** `SparqlEngine` now names each row from its own bindings
(`ResultColumnNamingTests`). The `SELECT *` header is the variables some row binds, in the order the query first
mentions them. D7 later adds the variables bound in no row.

## References

- [ADR-052](ADR-052-update-where-one-pattern-path.md) (the same convergence for Update; `ParseWhereGroup`,
  `WhereGroupStart`), [ADR-045](ADR-045-graph-clause-feature-parity.md) and
  [ADR-047](ADR-047-default-path-cutover.md) (the tree as sole evaluator; the `FindWhereGroupStart` fixes).
- [Divergence register](../../divergence/README.md) A4, B3.
- `src/Mercury/Sparql/Parsing/SparqlParser.Clauses.cs`, `src/Mercury/Sparql/Execution/QueryExecutor.cs`,
  `src/Mercury/Sparql/Patterns/PatternSlot.cs` (`QueryBufferAdapter`), `src/Mercury/Sparql/Execution/SparqlExplain.cs`,
  `src/Mercury/Sparql/Execution/TreeJoinExecutor.cs` (sub-SELECT step).
- SPARQL 1.1 Query §16.2 (ASK), §16.4 (DESCRIBE), §18.2 (translation; the empty group), §18.2.1 (in-scope variables).
