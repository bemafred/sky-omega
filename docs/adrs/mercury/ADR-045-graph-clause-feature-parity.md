# ADR-045: One pattern path — eliminate the divergent GRAPH parse/execution path

## Status

**Status:** Accepted — 2026-06-06 (Proposed → Accepted same day. Approach approved in design review: the divergent GRAPH path is a construction artifact to **delete**, not a design to patch. The tactical-vs-strategic split in the Proposed draft was withdrawn — there is no legitimate tactical option. Engineering pending.)

## Context

### Two presenting defects, both GRAPH-only

**D1 — `VALUES` inside `GRAPH` does not constrain (wrong results).**
```sparql
SELECT ?s ?o WHERE { GRAPH <g> { VALUES ?s { <iri> } . ?s <p> ?o } }
```
returns *all* subjects with `<p>` in `<g>`, ignoring `VALUES` (placement-independent). `FILTER(?s = <iri>)` constrains correctly.

**D2 — `BIND` inside `GRAPH` fails to parse:**
```sparql
SELECT ?label ?v WHERE { GRAPH <g> { ?s <p> ?v . BIND("x" AS ?label) } }
```
throws `Expected ')' after grouped path expression`. UNION is **not** required — bare `GRAPH { triple . BIND }` already fails.

### Both are valid SPARQL 1.1 — missing implementation, not misuse

`Bind` and `InlineData` are both `GraphPatternNotTriples`, legal in any `GroupGraphPatternSub`; a `GraphGraphPattern` body *is* a `GroupGraphPattern`:

```
[55] GroupGraphPatternSub   ::= TriplesBlock? ( GraphPatternNotTriples '.'? TriplesBlock? )*
[56] GraphPatternNotTriples  ::= GroupOrUnionGraphPattern | OptionalGraphPattern | MinusGraphPattern
                               | GraphGraphPattern | ServiceGraphPattern | Filter | Bind | InlineData
[58] GraphGraphPattern       ::= 'GRAPH' VarOrIri GroupGraphPattern
[60] Bind                    ::= 'BIND' '(' Expression 'AS' Var ')'
[61] InlineData              ::= 'VALUES' DataBlock
```
`InlineData` is normatively *"combined … by a **join** operation"*; a string literal is a valid `Expression`; `BIND` ends the preceding BGP and only requires a fresh target variable.

### The principle: a default graph is also a graph

An RDF dataset is one **default (unnamed) graph** plus N named graphs. The algebra evaluates a pattern against an **active graph** (SPARQL §18.4); `GRAPH g { P }` does exactly one thing — rebind the active graph for `P`. **Default-graph evaluation is the same operation with active graph = the unnamed graph.** "Default" and "named" are not two cases; they are one operation with one parameter.

There is **no performance basis** for a separate path either: Mercury's storage is GSPO-keyed, so the active graph is *already* a parameter of every scan. A separate default-graph path buys nothing — it only drops features and accretes bugs.

### Root cause: a divergent reimplementation at both layers (a construction artifact)

The default-graph path and the GRAPH path are separate code, and the GRAPH copy lacks parity:

- **D1 (execution).** A GRAPH query with no top-level triples routes to the GRAPH path (`QueryExecutor.cs:490`/`:762-791`; `TriplePatternCount` excludes in-GRAPH triples — `PatternSlot.cs:798`/`:1646`) and is wrapped by `QueryResults.FromMaterializedWithGraphContext`, whose constructor **hardcodes `_hasValues = false` (`QueryResults.cs:302`)**, so the gate `if (_hasValues) …` (`:903-907`) never runs. The checker `MatchesValuesConstraint()` (`QueryResults.Patterns.cs:2799-2845`) is correct but gated off. Non-GRAPH constructors set `_hasValues = buffer.HasValues` (`:494`/`:542`/`:683`). (Plus parse-time mis-attachment: `VALUES` lands on the parent `GraphPattern` — `SparqlParser.Clauses.cs:3870-3876`/`:3462`; `GraphClause` has no Values storage.)
- **D2 (parse).** `ParseGraph` hand-rolls its own body loops (`SparqlParser.Clauses.cs:3826` outer, `:3901` inner — triples only) with **no `BIND` branch**, so `BIND` is read as a bareword IRI and the following `(` throws at `SparqlParser.cs:1264`. The shared parsers `ParseGroupGraphPatternSub` (`:2202`) / `ParseNestedGroupGraphPattern` (`:2350`) dispatch `BIND` (`ParseBind`, `:3573`) — the GRAPH path never reaches them.

### A recurring class — now four instances

| Version | Defect | Layer |
|---|---|---|
| 1.7.71 | property-list `;` shorthand dropped inside GRAPH | parse |
| 1.8.3 | aggregation dropped on the top-level-GRAPH path | exec |
| **D1** | `VALUES` join ignored inside GRAPH | exec |
| **D2** | `BIND` unparseable inside GRAPH | parse |

Each was point-fixed before. The pattern: every feature added to the shared path must be re-added to the GRAPH path by hand, and conformance never catches the omission. This is an artifact of feature-by-feature code generation mirroring the nearest path instead of generalizing to the active-graph abstraction.

### Why conformance (421/421 Query) misses it

The suite exercises these features on the default-graph path and has almost no GRAPH-context cases. The one `VALUES`-in-GRAPH W3C case (`bindings/graph.rq`) is the variable-graph, VALUES-only shape (a special branch). Two SPARQL **1.2** files contain `GRAPH ?g { ?s ?p ?o . BIND(… AS ?t) }` but the runner only maps `sparql11` manifests (`W3CTestContext.cs:132-145`), so they never execute.

## Decision

**One pattern path, parameterized by active graph. The default graph is the unnamed graph. Special cases are degenerate instances, not parallel code.**

- **Parser.** `GraphGraphPattern` parses `'GRAPH' VarOrIri` and then **recurses into the single `GroupGraphPattern` parser** for its body. Delete `ParseGraph`'s hand-rolled body loops. There is no GRAPH-specific body grammar; BIND/UNION/FILTER/property-list-shorthand inside GRAPH work because the shared parser handles them.
- **Executor.** One group evaluator parameterized by an active-graph atom id. `GRAPH <g>` sets it; `GRAPH ?g` drives it with a named-graph iterator binding `?g`; the top level uses the default-graph atom. Delete `FromMaterializedWithGraphContext`'s separate flag wiring (`_hasValues = false`). VALUES/FILTER/BIND/aggregation are graph-agnostic and run identically regardless of active graph.

The fix **removes** code. There is **no tactical-patch variant** — patching the divergent path is the fifth point-fix of the same class and perpetuates the artifact (see Alternatives).

## Quality guarantee

Two structural guarantees; supporting gates verify and keep them.

1. **Correct by construction (the guarantee).** With one path, a "GRAPH-only bug in feature X" is *unrepresentable* — there is no GRAPH-only code for X to be wrong. We delete our way to correctness, not test our way. This is the no-behavior-flags rule (`feedback_no_behavior_flags`): the divergent paths *are* the anti-pattern.
2. **The invariant `default ≡ named`, checked over the whole corpus.** A metamorphic harness takes every existing default-graph query test, mirrors its data into a named graph, wraps the WHERE body in `GRAPH <urn:test:mirror> { … }`, and asserts identical results. This converts the entire current and future test corpus into GRAPH coverage automatically and is self-maintaining: any reintroduced divergence turns it red. **It lands before the executor change**, so it is the gate, not an afterthought.
3. **Test the entry point.** Conformance and tests run *through* `SparqlEngine.Update` / the query facade, never bypass into library methods (the multi-op lesson, [ADR-046](ADR-046-multi-operation-update-facade.md)).
4. **Close the intake gap.** Load the `sparql12` manifest (`W3CTestContext.cs:132-145`).
5. **Zero-GC gate.** BenchmarkDotNet allocation assertions on the unified parse + eval hot path — predictable latency is the substrate's point; the refactor must hold zero-GC.
6. **Live oracle.** Mercury is our own semantic memory, so the unified path is dogfooded every session; DrHook observes the active-graph parameter threading at runtime rather than trusting the read.

## Consequences

- **Positive:** the recurring GRAPH-divergence class is closed at the root; named-graph recall (the dogfood workload) becomes trustworthy for VALUES/BIND/aggregation; future group-pattern features get GRAPH support for free, by construction.
- **Cost/risk:** touches hot, `ref struct`, zero-GC parser/executor code; must be benchmarked allocation-neutral and pass the full W3C suite. Unification may surface further latent GRAPH-only gaps — a feature, not a bug: the mirror suite finds them before users do.

## Alternatives considered

- **Tactical patch of the divergent path** (the withdrawn "Part 1": wire `_hasValues` in the GRAPH constructor; add a BIND branch to `ParseGraph`). **Rejected.** It is the fifth point-fix of the same class and entrenches the parallel path. The divergent path is the defect; adding features to it moves the wrong way.
- **Document as limits, defer.** Rejected — silent wrong results (D1) and a hard parse failure (D2) on valid queries central to the dogfood workload.

## Engineering order

1. **Parser unification** (parser-first: lower risk, immediately kills the BIND / UNION / property-list-in-GRAPH class) — `GraphGraphPattern` recurses into the shared body parser; delete `ParseGraph`'s body loops.
2. **Metamorphic mirror suite** (the gate) — corpus-wide `default ≡ named` harness, plus explicit D1/D2 cases.
3. **Executor unification** — active-graph-parameterized evaluator; delete `FromMaterializedWithGraphContext`.
4. **Gates** — full W3C 421/421 *through the facade*; `sparql12` manifest loaded; zero-GC allocation check; dogfood + DrHook observation.

## Implementation progress

**Reframe (grounded 2026-06-07).** The index-child arena is *not* greenfield: `PatternArray`/`PatternSlot` (`src/Mercury/Sparql/Patterns/PatternSlot.cs`) is *already* an index-child buffer where `GraphHeader`/`ExistsHeader`/`ValuesHeader` carry child ranges. It was only partially recursive (UNION/OPTIONAL flat-encoded in the AST; the GRAPH body routed through the impoverished `GraphClause`). So the build **completes the existing buffer to uniform recursion** rather than adding a parallel arena (which would itself be the anti-pattern). The probe (`poc/adr-045-pattern-model/probe-pattern-arena.cs`) validated the principle; the codebase already half-implemented it. The Engineering order above is refined into:

- [x] **Step 1 — uniform-recursion foundation** (2026-06-07). `PatternArray` extended with nestable `GroupHeader`/`UnionHeader`/`OptionalHeader`/`MinusHeader` (reusing the `GraphHeader` child-range layout), `SubtreeEnd` + `DirectChildEnumerator` for skip-aware direct-child iteration, and a uniform `GetChildren`. Validated by `tests/Mercury.Tests/Sparql/PatternTreeNestingTests.cs`: nested Group/Union/Optional/Graph compose; direct-child iteration skips nested subtrees; one recursive walk threads the active graph as a parameter (default = the unnamed graph). Mercury builds 0/0; 17 existing slot tests + the new test green.
- [~] **Step 2 — recursive parser** producing the tree directly, retiring the flat `GraphPattern` + `GraphClause` AST and `ParseGraph`. **First increment landed (2026-06-07): the spine.** `src/Mercury/Sparql/Parsing/SparqlParser.PatternTree.cs` — a new partial-struct method `ParsePatternTree` that emits the `PatternArray` tree directly from source, built *alongside* the untouched shipping parser (test/harness-only until cutover) and reusing the shipping leaf-parsers verbatim (`ParseTerm`, `ParsePredicateOrPath`, `ParseValuesValue`) + cursor primitives. The decisive construction: **`GraphGraphPattern` recurses into the *same* group-body loop** (`ParseGroupBodyTree`) as the default graph, so BIND / VALUES / FILTER / property-list shorthand inside GRAPH become children of the `GraphHeader` *by construction* — the divergence-class defects are unrepresentable. UNION needs its header kind only after the first branch parses, handled by the new `PatternArray.WrapSubtree` (collect-then-parent on the append-only buffer). Handled: Group / GRAPH / `{}` / UNION / OPTIONAL / MINUS / TriplesBlock (`;` shorthand) / FILTER (bracketed + built-in) / BIND / single-variable VALUES. Validated by `tests/Mercury.Tests/Sparql/PatternTreeParserTests.cs` (5 structural tests: D1 VALUES-in-GRAPH, D2 BIND-in-GRAPH, 1.7.71 property-list-in-GRAPH, multi-branch UNION/`WrapSubtree`, mixed-nesting active-graph threading). Mercury builds 0/0 Debug+Release; **full suite green 4,532 passed / 0 failed / 6 skipped — zero regression.**
  - [ ] **Remaining Step 2 increments** (before cutover; each throws an explicit `SparqlParseException` today rather than mis-parsing): sub-SELECT, SERVICE, FILTER (NOT) EXISTS, multi-variable VALUES, RDF-star quoted triples, blank-node property lists, collections, property-path sequence expansion. The transitional FILTER/BIND span-capture duplication (vs. the shipping `ParseFilter`/`ParseBind`) is deleted at Step 4 cutover, not kept.
- [~] **Step 3 — metamorphic mirror suite** (`default ≡ named`) — the gate, landed before the executor change. **First increment landed (2026-06-07): the harness + a curated battery.** `tests/Mercury.Tests/Sparql/GraphMirrorGateTests.cs` loads identical triples into the default graph *and* `<urn:test:mirror>`, then for each query runs it against the default graph and against a GRAPH-wrapped mirror of its WHERE body through the shipping `SparqlEngine`, comparing results as solution bags. Mercury's default-graph scan is the unnamed graph only (not the union), so the comparison is clean. The battery characterizes and **locks the current divergence surface**: holds today (live regression coverage) — bgp, FILTER on object/subject (IRI and non-hyphen PNAME), property-list (the old 1.7.71 instance, now fixed); **diverges today (the Step 4 targets, asserted at their current divergent behaviour so the gate is green)** — `BIND`-in-GRAPH (D2, parse error), `VALUES`-in-GRAPH (D1, ignored), `OPTIONAL`-in-GRAPH (drops every row), `UNION`-in-GRAPH (parse error). Step 4 flips each `false` to `true`. Two limits, noted in the test: (a) the textual WHERE-wrap suffices for curated queries — the corpus-wide transform (through the parsed query) is the next increment; (b) a metamorphic gate sees only path *divergence*, never a bug *common* to both paths (e.g. the hyphenated-PNAME FILTER bug found and re-attributed this session).
  - [ ] **Remaining Step 3 increments**: corpus-wide application over the `sparql11` (then `sparql12`) manifests via the parsed-query transform; explicit D1/D2 cases asserted through the facade; the `sparql12` manifest intake (`W3CTestContext.cs:132-145`).
- [~] **Step 4 — uniform executor walk** (active-graph parameter) → cutover; delete `FromMaterializedWithGraphContext` and the 1,585-line `QueryExecutor.Graph.cs` divergence; flip the mirror-gate `false` baselines to `true`. **First evaluator increment landed (2026-06-07): the uniform walker, alongside.** `tests/Mercury.Tests/Sparql/GraphTreeEvaluatorTests.cs` — the executor analog of the Step 2 parser: one recursive evaluator over the `PatternArray` tree, threading the **active graph as a single parameter** (a GRAPH header rebinds it for its subtree; everything else threads it unchanged), reusing the real store scan (`QuadStore.QueryCurrent`). The proof: parse the GRAPH-wrapped query with the recursive parser, walk it, and the result **equals the shipping engine's default-graph baseline** for the unwrapped query — and the unwrapped query runs through the *same* evaluator. The divergence is dissolved by construction — there is no GRAPH-only evaluation path to be wrong. **All 4 RED gate cases now go GREEN through this one path** (`union`/`optional`/`values` — the non-expression spine — plus `bind`, the 4th, after the BIND+FILTER increment below), along with the FILTER cases. The walker is a correctness **model** (heap solution bags), not the zero-GC production form. Full suite 4,549/0/6.
  - [x] **BIND + FILTER increment** (2026-06-07): the walker now covers the full spine including BIND and FILTER, reusing the **real** `BindExpressionEvaluator` / `FilterEvaluator`. The value-form risk dissolved on grounding: the engine binds matched terms **raw as String** (`STR`'s bracket-stripping and PNAME expansion live inside the evaluators, given the prologue `PrefixMapping[]`), so the walker binds raw and gets real expression semantics for free — `bind` (`STR(?o)→urn:v1`) and `filter-subject-pname` (`ex:a→<urn:a>`) both GREEN. The 4th RED case is closed.
  - [x] **MINUS + EXISTS increment** (2026-06-07): MINUS is a positional anti-join (drop a left solution iff the right side has a compatible solution **sharing ≥1 variable** — disjoint domains do not remove, per §8.3; the right side evaluated independently of the left); FILTER `[NOT] EXISTS` is group-scoped (the body evaluated **seeded with each solution**, so its bound variables constrain the body's scans; non-empty/empty decides). Parser: `EmitFilterTree` now parses bare and parenthesized `FILTER [NOT] EXISTS { … }` into `ExistsHeader`/`NotExistsHeader`; `PatternArray.EnumerateDirectChildren` learned to dispatch the EXISTS child-range (offset `@4` vs the nestable `@16`). All threaded through the active graph: `minus` → `{c}`, `exists` → `{a,b}`, `not-exists` → `{c}`, each matching the default-graph baseline. Full suite 4,552/0/6.
  - [x] **Property paths — ALL forms** (2026-06-07): inverse `^`, `*`/`+`/`?`, sequence `/`, alternative `|`, grouping `( )`, negated property set `!`, and prefixed-name path IRIs — **none deferred** (a deferred path form on the new path is exactly the divergence this ADR deletes — the deferral *is* the anti-pattern). The slot carries the **full path-expression source span** (parser captures it around `ParsePredicateOrPath`); the evaluator runs a complete path-algebra interpreter, materializing the path as a relation (subject→object pairs) over the active graph — composition for `/`, union for `|`, swap for `^`, transitive/reflexive-transitive closure for `+`/`*`, identity for `?`, complement scan for `!` — then filters by the bound endpoints (distinct endpoint pairs per §9). 11 path cases (incl. forward/backward/both-unbound closure, sequence, alternative, grouped, negated, PNAME) all match the default-graph baseline through the active-graph walk. Full suite 4,563/0/6.
  - [x] **Sub-SELECT** (2026-06-07): `{ SELECT … }` is parsed as a `SubSelectHeader` leaf carrying the sub-SELECT source span (the recursive parser reuses the shipping `ParseSubSelectCore`, now `internal`, to advance past it). The evaluator owns the **GRAPH-relevant** part — it walks the sub-SELECT's WHERE through the **same uniform path with the active graph threaded in** (evaluated independently of the outer scope), then joins the projected results with the outer solutions on shared variables. The **solution-modifier / aggregation layer** (projection, DISTINCT, GROUP BY + aggregates, HAVING, ORDER BY, LIMIT, OFFSET) is **reused, not reimplemented** — the inner bag becomes `MaterializedRow`s fed through the shipping `QueryResults.FromMaterializedSimple`; that layer is graph-agnostic and shared, and the cutover inherits it from `SparqlEngine` (this is the principled boundary: the walker owns active-graph pattern evaluation; the modifier layer is shared downstream, not a walker feature). 5 cases — basic nesting, join-on-shared-var, DISTINCT, `COUNT(*)` aggregation, ORDER BY + LIMIT — all match the default-graph baseline through the active-graph walk. Full suite 4,568/0/6.
  - [ ] **Remaining Step 4 increments**: (a) SERVICE / RDF-star quoted triples / blank-node property lists in the evaluator (each throws `NotSupportedException` today rather than mis-evaluating — to be eliminated, not left, per `ck:lesson-deferral-is-the-divergence`); (b) LIMIT-pushdown (`ck:obs-graph-limit-pushdown`); (c) reimplement the model zero-GC over `BindingTable`, wire it into `SparqlEngine`, delete `QueryExecutor.Graph.cs` + `FromMaterializedWithGraphContext`, and flip the mirror-gate `false` baselines to `true` — the actual cutover, gated by the full W3C suite through the facade.

## References

- Prior instances: 1.7.71 (property-list shorthand in GRAPH), 1.8.3 / commit `b1c5c18` (aggregation on the GRAPH path).
- A dogfood lead investigated this session (`ck:obs-pname-filter-graph-divergence`) was **re-attributed away from ADR-045** and is *not* counted as an instance. `FILTER(?v = prefix:local-with-hyphen)` returns nothing on the GRAPH path, but isolation pinned the trigger to the **hyphen in the PNAME local part** (the FILTER expression parser reads `p:a-b` as `p:a - b`): it is **position-independent** (subject and object alike), the same hyphenated PNAME works as a BGP term, and the GRAPH path is clean for non-hyphenated PNAMEs (the Step 3 mirror gate's `filter-subject-pname` case is equivalent). So it is almost certainly a **general FILTER-expression bug, not a GRAPH divergence** — tracked separately. (Two intermediate mischaracterizations were corrected to reach this; and note the metamorphic gate cannot, by construction, see a bug common to both the default and named paths.)
- Root-cause evidence (file:line) and the systemic `graph-path-divergence-class` observation: Mercury session graph `https://sky-omega.dev/sessions/2026-06-06-docs-version-audit/graph`.
- Companion: [ADR-046](ADR-046-multi-operation-update-facade.md) — same "one path, degenerate special case" principle applied to multi-operation UPDATE.
