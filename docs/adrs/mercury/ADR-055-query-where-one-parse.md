# ADR-055: One WHERE Parse for Queries — Parse a Query's WHERE Once, by the Pattern Tree, and Evaluate Every Query Form Through It

## Status

**Status:** Accepted — 2026-10-27

Found while probing for [ADR-053](ADR-053-one-rdf-term-one-atom.md): `SELECT ?o WHERE { { ?s ?p ?o } UNION { GRAPH ?g
{ ?s ?p ?o } } }` fails to parse, and it fails the same way on the source before
[ADR-052](ADR-052-update-where-one-pattern-path.md). This is the query-side counterpart of ADR-052: an Update's WHERE
is now parsed once by the pattern tree, while a query's WHERE is still parsed twice, and the first parse decides
where the query goes. It also closes ADR-052's follow-up "queries still guess where their WHERE begins", and the
divergence register's A4 (two group-body parsers) and B3 (flat `GraphPattern` WHERE parser, parse-then-discard).

Accepted with Q1–Q3 resolved. Parts of D1 (the recorded WHERE start) and D2 (the empty WHERE through the tree, for
SELECT, ASK and CONSTRUCT) shipped first, in 1.8.25. The stack probes for validation step 4 are in
[`tools/probes/stack/`](../../../tools/probes/stack/README.md).

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
| S10 | `{ … } UNION { … } UNION { … }` at the top level | one row per matching branch | parse error ("UNION requires graph patterns enclosed in braces"); the same UNION nested in a group, or in an Update WHERE, works |
| A6 | `ASK { { … } UNION { GRAPH ?g { … } } }` | true | parse error |
| E1 | `SELECT * {}` | 1 row (the empty solution) | 0 rows |
| E5 | `SELECT * { MINUS { ?s ?p ?o } }` | 1 row | 0 rows |
| E6 | `SELECT * { { BIND("x" AS ?a) } UNION { BIND("y" AS ?b) } }` | 2 rows (`a=x`; `b=y`) | 1 row, `a=x b=y` |
| E7 | `SELECT (COUNT(*) AS ?n) { { BIND(1 AS ?a) } UNION { BIND(2 AS ?b) } }` | 1 row, `n=2` | 0 rows |
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
UNION with BIND when a branch has a triple pattern, OPTIONAL-only groups, BIND with FILTER, `SELECT (1 AS ?x) {}`, and ASK over GRAPH, FILTER(true)
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
     returns no rows: E1, E5. With BINDs it returns exactly one row holding every BIND, whatever the group
     structure: E6 merges two UNION branches into one row, and E7 counts nothing. (Found in the 1.8.24 live check.)
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
- `GraphPattern` stays as scratch storage: the planner's per-BGP input (`TreeJoinExecutor.ReorderBySelectivity`) and
  the tree parser's path, SERVICE and quoted-triple expansion. (Update templates are `TemplateQuad[]` and
  `INSERT/DELETE DATA` is `QuadData[]` since 1.8.25.)

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

  **Done first, 2026-10-27 (with the LIMIT 0 and silent-drop fixes):** `WhereClause.GroupStart` is recorded by
  `ParseWhereClause` and carried as `QueryBuffer.WhereGroupStart`; the three brace-guessing methods are deleted (the
  guess had made a CONSTRUCT without top-level triples evaluate its template as the WHERE). SELECT, ASK and CONSTRUCT
  evaluate every WHERE through the tree: `EmptyPattern`, its constructor, `ExecuteEmptyPatternToMaterialized`,
  `ExecuteAsk`'s pattern-less evaluator and its three delegating branches, and CONSTRUCT's `PatternCount == 0` gate
  are deleted, with the BIND re-evaluation and UNION-BIND fields only `EmptyPattern` set. The tree route keeps zero rows
  for an aggregate without GROUP BY, so `COUNT(*)` over no solutions is one row with 0 (it returned no row, with or
  without triple patterns). `EmptyGroupTests` (20) cover E1, E5–E7, A1, A2, C1, C3, C4 and C2. DESCRIBE, the flat
  parse, and the rest of this decision remain.

### D3. DESCRIBE without WHERE

`DESCRIBE <iri> …` describes the IRIs it names (§16.4). It is evaluated over one empty solution, so `DescribeResults`
gets the constant targets and no bound variables. A DESCRIBE with a WHERE describes its constant targets and the
values its variables take in the WHERE's solutions.

Found while routing the empty WHERE (1.8.24): DESCRIBE has never described anything through the facade.
`SparqlEngine.ExecuteTriples` runs `Execute()`, the SELECT path, and reads each row's first three bindings as a
triple; `ExecuteDescribe` is reached only by direct callers. `ParseDescribeQuery` reads `*` but not the target list,
so after `DESCRIBE ?s` or `DESCRIBE <iri>` it never sees the WHERE keyword: `DESCRIBE ?s WHERE { … }` has no WHERE
group. D3 therefore also parses [11] DescribeQuery's `( VarOrIri+ | '*' )` and routes the facade through
`ExecuteDescribe`.

Implemented 2026-10-28. The targets are a `DescribeClause` read from the source by `SparqlParser.DescribeTargetReader`,
which the parser checks them with and `ExecuteDescribe` collects them with. Variable targets take their values from
`Execute()`, so the solution modifiers apply. `DescribeResults` describes each resource once, returns a triple between
two described resources once, and grows its buffer for long literals. CONSTRUCT now reads its solutions from
`Execute()` too: its own row source had ignored ORDER BY with LIMIT, and OFFSET. `DescribeTests` (12) and
`ConstructSolutionTests` (3).

**Which graphs, decided 2026-10-28** (after the 1.8.27 dogfood check, where `DESCRIBE <iri>` described nothing in the
memory store). §16.4 leaves the description to the service. A DESCRIBE reads the default graph and every named graph,
each triple once; with FROM or FROM NAMED, only the graphs they name. It reads them through the query's temporal
clause (AS OF, DURING, ALL VERSIONS), by way of `TemporalScope`, which the triple scan now also uses. The target list
now ends at the first token that is not a variable, IRI or prefixed name, so `orders:x` is a target and the temporal
keywords end the list. `DescribeTests` (17).

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

Each tree node kind maps to one operator (Q1). Nineteen of the twenty-two operator types exist already; three are added:

| Tree node (`PatternKind`) | Operator | Symbol | Notes |
|---|---|---|---|
| `Triple`, no path | `TriplePatternScan` | ⊳ | As today, with the planner's estimate when a store is given |
| `Triple` with a property path | **`PathScan`** (new) | ⇝ | The path as written, e.g. `?s <a>/<b>* ?o`. Today it is shown as a join of `<a>` and `<b>` with `?` for the middle term |
| `GroupHeader` | `NestedLoopJoin` | ⋈ | "Join *n* patterns"; its children in source order, the order `TreeJoinExecutor` runs them (`reorderBgp` is off by default). A group with one child is shown as that child |
| `UnionHeader` | `Union` | ∪ | One child per branch, any number of branches |
| `OptionalHeader` | `LeftOuterJoin` | ⟕ | Its group as the child, FILTERs included |
| `MinusTriple`, `MinusHeader` | `Minus` | − | Its group as the child |
| `GraphHeader` | `GraphScan` / `GraphVariable` | ▣ / ▢ | Its group as the child |
| `ExistsHeader` | **`Exists`** (new) | ∃ | Under the FILTER that contains it, with its group as the child |
| `NotExistsHeader` | **`NotExists`** (new) | ∄ | Likewise |
| `Filter` | `Filter` | σ | Last in the group it belongs to; a FILTER applies to its whole group |
| `Bind` | `Bind` | ← | At its position in the group |
| `ValuesHeader` (+ `ValuesEntry`) | `Values` | ≡ | At its position; the description names the variables and the row count |
| `SubSelectHeader` | `SubQuery` | ⊂ | The sub-SELECT's own plan, modifiers and WHERE, as children |
| `ServiceHeader` | `Service` | ⇄ | Endpoint and SILENT; the remote WHERE is not planned |

The node walk also fixes four defects of today's EXPLAIN that have nothing to do with the flat parse's grammar:
- Every plan shows `⌊ LIMIT 0`, because 0 is the no-limit value. A Slice node appears only when the query has LIMIT
  or OFFSET.
- A FILTER inside OPTIONAL is drawn at the outer level.
- FILTER EXISTS / NOT EXISTS is not drawn at all, and the second branch of `{ … } UNION { BIND(…) }` is drawn empty.
- In the text format, a node's descendants lose the `│` line of the levels above them.

EXPLAIN ANALYZE still reports totals only, now from the one route (D2). The JSON format gains the three operator
names, and `docs/architecture/technical/sparql-reference.md` lists all twenty-two symbols (it lists nine today).

### D7. SELECT * is the WHERE's in-scope variables

The in-scope variables of the tree (§18.2.1, computed once from the parsed tree) are the variable list of a
`SELECT *`, including variables bound in no row. This is what the facade's result header needs; see *Related* for the
facade defect that header is part of.

**Implemented 2026-10-28.** `SparqlParser.AddInScopeVariables` is the one reading of §18.2.1 over a parsed tree. It
fills an `InScopeVariables` (pooled spans of the source, each variable once, in the order the query first mentions
it). Four consumers that each had their own reading now use it:
- The facade's `SELECT *` header (`QueryExecutor.InScopeVariableNames`: the WHERE's variables, then the trailing
  VALUES'). It lists variables no row binds, and a query with no solutions keeps its header.
- `DESCRIBE *`, which described every binding in the rows, including a pattern's blank node.
- A sub-SELECT's projection (`TreeJoinExecutor.OutputVarNames`). Its `SELECT *` case scanned the source text and
  missed the sub-SELECT's own trailing VALUES.
- The BIND scope check (`ThrowIfInScope`), which missed VALUES and SERVICE variables.

`SELECT DISTINCT *`, in a query or a sub-SELECT, now projects before DISTINCT (§18.2.5), so two solutions that differ
only in a pattern's blank node are one row. Tests: `InScopeVariablesTests` (14). Stack probes unchanged (23–39 KB).

## Open questions (all resolved 2026-10-27)

- **Q1. EXPLAIN's vocabulary. Resolved 2026-10-27.** `ExplainOperatorType` already has operators for sub-SELECT,
  SERVICE, VALUES, MINUS and GRAPH variables; only paths and EXISTS / NOT EXISTS lack one. D6 maps every
  `PatternKind` to an operator and adds `PathScan`, `Exists` and `NotExists`. The 20 `SparqlExplainTests` assert
  symbols and words (⊳, ⋈ "Join", ⟕ "OPTIONAL", ∪ "Alternative", σ "FILTER", ⌊ "LIMIT 10", …) that the mapping keeps,
  so they stand unchanged; one test per row of the D6 table is added, plus S1 and S10. Evidence: a probe of
  `SparqlEngine.Explain` on 1.8.24 over nine shapes. The 3-way UNION threw; the other eight drew a wrong or partial plan.
- **Q2. Parser-structure tests. Resolved 2026-10-27.** The rule: a test asserts behavior unless its subject is the
  parser, and a test of the parser asserts on the tree. A census of `tests/Mercury.Tests` on 1.8.25 found five kinds
  of dependence on code D1, D2 and D5 delete:

  | Kind | Size | Where | What happens |
  |---|---|---|---|
  | A. Setup assertions in tests that execute | 40 tests, ~84 assertions | Subquery, Service, PropertyPath, Filter, Patterns, QueryForms, Graph | Deleted. The result assertions after them are the test; a construct the parser failed to recognize would fail them. |
  | B. Parser-structure tests | ~50 tests, ~110 assertions | SparqlParserTests (WHERE cases), Service (10), Subquery (4), PropertyPath (6), SparqlStar (3), Filter, Patterns, SubqueryAggregate | Rewritten against the tree, in the `PatternTreeParserTests` style (node kinds, spans, children, path kind, quoted-triple expansion): one test per construct, not one per old test. |
  | C. `QueryBuffer` WHERE flags | 11 tests, 24 assertions | PatternSlotTests | Deleted with the fields (D5). The `PatternSlot` / `PatternArray` tests stay. |
  | D. Dead `FilterAnalyzer` code | 16 of 17 tests | FilterPushdownTests | Deleted with the code. `GetFilterVariables`, `ContainsExists`, `GetEarliestApplicablePattern`, `AnalyzeFilters`, `BuildLevelFilters` and `GetUnpushableFilters` read the flat pattern and have no production caller; production filter placement is the tree's, and `DetectTextMatchFilters` (the one live method) reads the tree. The one test that executes a query becomes a facade test. |
  | E. Test-only execution entries | 60 `ExecuteToMaterialized` calls (6 files), 14 buffer-constructor executors (Graph) | QueryExecutorTests.* | Run through `QueryExecutor.Execute()`, the entry `SparqlEngine.Query` uses, with their assertions unchanged. `ExecuteToMaterialized`, `MaterializedQueryResults`, `WrapResultsAsMaterialized`, the `QueryBuffer` constructor of `QueryExecutor` and `BuildGraphPattern` are deleted (D2, D5). These tests exercised an execution path production never runs. |

  Done 2026-10-27, before D1:
  - D (`c6ef26f`): 11 tests, with the dead analyzer code.
  - E (`44bf124`): all 60 calls and 14 executors moved to `Execute()`; the test-only entries are deleted.
  - C, with what it uncovered: `QueryResults.FromMaterialized` and `VariableGraphExecutor` had no caller (`795421c`).
    Every `QueryResults` constructor then set its FILTER/EXISTS/MINUS/VALUES/OPTIONAL flags false, so the flags and
    the 58 methods only they reached are deleted (`400ce92`, `QueryResults.Patterns.cs` 2,768 → ~140 lines). Then
    `QueryBuffer`'s WHERE-derived members, its pooled pattern slots, `IDisposable` and the adapter's
    `ConvertGraphPattern` are deleted, with 20 tests of them (D5 done except the WHERE parse itself).

  Not affected: Update-template and CONSTRUCT-template assertions (`TemplateQuad`, `ConstructTemplate` stay), and
  `PlannerSpikeDiagnosticTests`, which has no assertion and is deleted as a finished spike. Kinds C, D and E are
  deletions of code that is dead or test-only; they can land before D1, as their own steps.

- **Q3. `Query` struct size and stack. Resolved 2026-10-27, measured on 1.8.25** (M5 Max, arm64, macOS, Release
  build). Method: struct sizes with `Unsafe.SizeOf` by reflection; each method's frame from the JIT's own prologue
  (`DOTNET_JitDisasm`, fully optimized and Tier 0); and the smallest thread stack a query completes on, by binary
  search with one child process per trial.

  | Struct | Size |
  |---|---|
  | `Query` | 17,784 B |
  | `GraphPattern` (the flat WHERE, inside `Query`) | 15,544 B (87% of `Query`; ADR-009 estimated ~4 KB) |
  | `SubSelect` / `ConstructTemplate` / `SelectClause` | 2,064 / 1,156 / 396 B |
  | `UpdateOperation` | 360 B (two `GraphPattern` templates, ~31 KB, until 1.8.25) |

  | Frame (fully optimized) | Size | Frame | Size |
  |---|---|---|---|
  | `ParseSelectQuery` | 53,184 B | `ParseGroupBodyTree` (recursive) | 31,184 B |
  | `ParseQueryInternal` | 51,440 B | `EmitTriple` | 15,760 B |
  | `ParseConstructQuery` | 50,400 B | `TreeJoinExecutor.AddPathPatterns` | 15,872 B |
  | `SparqlEngine.QueryCore` | 35,856 B | `ParseGroupTree` | 2,208 B |
  | `ParseWhereClause` | 31,136 B | `ParseGroupGraphPattern` | 17,696 B |

  | Smallest stack that completes | Fully optimized | Tier 0 |
  |---|---|---|
  | `SELECT * { ?s ?p ?o }` | 182 KB | 166 KB |
  | a SELECT with OPTIONAL, sub-SELECT, MINUS, NOT EXISTS, GROUP BY, ORDER BY | 197 KB | 182 KB |
  | `INSERT { GRAPH … } WHERE { GRAPH … { … OPTIONAL … } }` (no flat WHERE since ADR-052) | 103 KB | 71 KB |
  | groups nested 5 / 10 deep | 213 / 371 KB | 197 / 229 KB |
  | OPTIONAL nested 5 / 10 deep | 229 / 386 KB | 166 / 213 KB |

  Managed threads get 1,548 KB here (`new Thread()` and the thread pool; the main thread 8 MB), and 1 MB on Windows.
  Nesting deeper than 10 is a parse error (`SparqlParser.DefaultMaxDepth`), so the worst case is about 390 KB: a
  quarter of a macOS thread, 38% of a Windows one.

  What this says:
  1. **The flat parse costs about 80 KB of every query.** A SELECT needs 182 KB where an Update, which has had no flat
     WHERE since ADR-052, needs 103 KB. The difference is `ParseWhereClause` and `ParseGroupGraphPattern` (49 KB)
     plus the `Query` copies in `ParseQueryInternal`, `ParseSelectQuery` and `QueryCore`. D1 removes both: the flat
     frames go, and `Query` drops to about 2.2 KB.
  2. **Each nesting level costs about 32 KB, and D1 as first written does not remove it.** The tree parser borrows two
     flat-parser routines into throwaway `GraphPattern`s: `ParseService` (to step over a SERVICE clause) and
     `AddTriplePatternOrExpand` (quoted triples, blank-node property lists, collections). When optimized, `EmitTriple`
     is inlined into the recursive `ParseGroupBodyTree`, which therefore carries both 15.5 KB structs at every level.
     Both routines belong to the flat family D1 deletes, so **D1 re-homes them in the tree parser**: a SERVICE clause is
     stepped over as a balanced group and recorded by span, and the expansions write their triples into the
     `PatternArray` directly. The recursive frame then holds no `GraphPattern`.
  3. **`TreeJoinExecutor.AddPathPatterns` (16 KB)** holds a scratch `GraphPattern` at execution time. It runs once per
     path triple, not per nesting level, so it is outside this ADR.

  Validation step 4 re-runs these measurements after D1. Targets: a SELECT within 15 KB of the Update case, and each
  nesting level under 4 KB.

  **Measured after D1 (2026-10-27, same method):** `Query` 17,784 → 2,232 B (`WhereClause` 4 B); frames
  `ParseSelectQuery` 53,184 → 6,528 B, `ParseQueryInternal` 51,440 → 4,784 B, `SparqlEngine.QueryCore` 35,856 →
  4,752 B, `ParseGroupBodyTree` 31,184 → 96 B. Smallest completing stack (fully optimized, 8 KB resolution): simple
  SELECT 182 → 23 KB, the complex SELECT 197 → 39 KB, the Update 103 → 23 KB, 10 nested groups 371 → 39 KB,
  10 nested OPTIONALs 386 → 23 KB. Both targets are met: a SELECT now needs what the Update does, and a nesting level
  under 2 KB.

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
4. **Stack** (ADR-009): the Q3 measurements (frames, and the smallest completing stack for the same query set), re-run
   after D1 with the same method. Targets: a SELECT within 15 KB of the Update case; each nesting level under 4 KB.
5. **Parse cost:** parse time of the W3C query corpus before and after, since each query now parses once.
6. **EXPLAIN:** the 20 existing EXPLAIN tests pass unchanged, one new test per D6 row passes, and EXPLAIN of S1 shows the UNION and GRAPH it
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
mentions them. D7 (2026-10-28) made it the in-scope variables, including those bound in no row.

## References

- [ADR-052](ADR-052-update-where-one-pattern-path.md) (the same convergence for Update; `ParseWhereGroup`,
  `WhereGroupStart`), [ADR-045](ADR-045-graph-clause-feature-parity.md) and
  [ADR-047](ADR-047-default-path-cutover.md) (the tree as sole evaluator; the `FindWhereGroupStart` fixes).
- [Divergence register](../../divergence/README.md) A4, B3.
- `src/Mercury/Sparql/Parsing/SparqlParser.Clauses.cs`, `src/Mercury/Sparql/Execution/QueryExecutor.cs`,
  `src/Mercury/Sparql/Patterns/PatternSlot.cs` (`QueryBufferAdapter`), `src/Mercury/Sparql/Execution/SparqlExplain.cs`,
  `src/Mercury/Sparql/Execution/TreeJoinExecutor.cs` (sub-SELECT step).
- SPARQL 1.1 Query §16.2 (ASK), §16.4 (DESCRIBE), §18.2 (translation; the empty group), §18.2.1 (in-scope variables).
