# ADR-053: One RDF Term, One Atom — Every SPARQL Term Is Stored and Matched in the Form the RDF Parsers Write

## Status

**Status:** Proposed — 2026-10-27

**Split 2026-10-28:** the evaluator part of D3 (a value keeps its term, decimal is its own type, comparison follows the
operator mapping) is [ADR-056](ADR-056-expression-value-is-the-term.md), which changes no stored data and goes first.
This ADR keeps the write paths, the stored form and the migration (D1, D2, D3's `StoredTerm.FromValue`, D4, D5), with
Q2 and Q3 open.

Found while ruling out confounds for [ADR-052](ADR-052-update-where-one-pattern-path.md) ("Related defect"): `INSERT
DATA { <a> <k> 1 }` stores the bare atom `1`, which no constant pattern then finds. A probe on 1.8.22 showed that this is
one case of a wider defect: the same RDF term reaches the store as different atoms depending on which path wrote it, and
a constant pattern matches only one of them. This is the "numeric literal expansion" axis of
[`docs/limits/sparql-resolve-term-family-duplication.md`](../../limits/sparql-resolve-term-family-duplication.md), now
with a bug behind it, which is that limit's promotion trigger (b).

## Context

### What each path stores

The probe writes one object term through each path into a fresh store, reads the raw atom back with
`QuadStore.QueryCurrent`, then counts matches for the constant pattern `{ ?s <p> TERM }` and for `FILTER(?o = TERM)`. The
Turtle loader is the reference: its output is the form ADR-044 made canonical.

| Term as written | Turtle load | `INSERT DATA` | template constant | `BIND(TERM AS ?x)` |
|---|---|---|---|---|
| `1` | `"1"^^<xsd:integer>` | `1` | `1` | `"1"^^<xsd:integer>` |
| `+1` | `"+1"^^<xsd:integer>` | `+1` | `1` | `"1"^^<xsd:integer>` |
| `01` | `"01"^^<xsd:integer>` | `01` | `01` | `"1"^^<xsd:integer>` |
| `1.0` | `"1.0"^^<xsd:decimal>` | `1.0` | `1.0` | `"1"^^<xsd:integer>` |
| `1e3` | `"1e3"^^<xsd:double>` | `1e3` | `1e3` | `"1"^^<xsd:integer>` |
| `true` | `"true"^^<xsd:boolean>` | `true` | `true` | `"true"^^<xsd:boolean>` |
| `.5` | `".5"^^<xsd:decimal>` | parse error | parse error | parse error |
| `"1"^^xsd:integer` | `"1"^^<xsd:integer>` | `"1"^^xsd:integer` | `"1"^^xsd:integer` | `"1"^^<xsd:integer>` |
| `"x"^^xsd:string` | `"x"^^<xsd:string>` | `"x"^^xsd:string` | `"x"^^xsd:string` | `"x"^^<xsd:string>` |
| `"x"`, `"x"@en`, `"x"@EN` | as written | as written | as written | as written |

(`<xsd:T>` abbreviates the full IRI `<http://www.w3.org/2001/XMLSchema#T>`, which is what is stored.)

Read across a row: four writers, up to four atoms for one term. And the readers disagree too:

- **Constant patterns** expand bare numerics and booleans (`LiteralForm.CanonicalizeNumericOrBoolean`) but not a
  prefixed datatype. So `{ ?s <p> 1 }` misses every `INSERT DATA` numeric, and `{ ?s <p> "1"^^xsd:integer }` misses
  every Turtle-loaded one. Only the full-IRI form matches both Turtle and the full-IRI SPARQL writes.
- **The pattern-tree parser drops a leading `+`.** The template stores `+1` as `1`, and the constant pattern `+1` misses
  the Turtle atom `"+1"^^<xsd:integer>`.
- **`.5` is rejected** by the SPARQL parser, though [147] `DECIMAL ::= [0-9]* '.' [0-9]+` allows it.
- **An undeclared datatype prefix is accepted.** `INSERT DATA { <s> <p> "2026-03-30"^^xsd:date }` with no `PREFIX xsd:`
  succeeds and stores `"2026-03-30"^^xsd:date`. An undeclared prefix in an IRI position is an error, as the spec says.
- **The simple literal and its `xsd:string` form never match each other.** `{ ?s <p> "y" }` misses a Turtle-loaded
  `"y"^^xsd:string`, and `{ ?s <p> "z"^^xsd:string }` misses `"z"`. RDF 1.1 Concepts §3.3 defines a simple literal as
  syntactic sugar for the `xsd:string` literal, so these are one term.

### The expression evaluator loses the term

A `BIND` result is not the term that was bound:

- `BIND(01 AS ?x)` yields `"1"^^xsd:integer`, and `BIND(1.0 AS ?x)` yields `"1"^^xsd:integer`. SPARQL evaluates a
  constant to the RDF term itself, so these must be `"01"^^xsd:integer` and `"1.0"^^xsd:decimal`.
- `DATATYPE(1.0)` returns `xsd:double`; it must be `xsd:decimal`. `BindingValueType` has `Integer`, `Double` and
  `Boolean` and no decimal, so a decimal is carried as a double.
- `BIND(1e3 AS ?x)` projects `1`, and `DATATYPE(1e3)` returns `xsd:integer`. A template fed by it stores
  `"1"^^xsd:integer`: **the value 1000 is written as 1**. `FILTER(?o = 1e3)` also misses a Turtle-loaded `1e3`, while
  `FILTER(?o = 1000)` finds it, so the constant `1e3` is evaluated wrongly rather than the stored atom being read
  wrongly.

### The live memory store

A census of a copy of the Mercury MCP memory store (5,538 current quads, 1.8.22) counted object terms by form:

| Form | Count | Example |
|---|---|---|
| typed, datatype a prefixed name | 819 | `"2026-04-19"^^xsd:date`, `"648"^^xsd:integer` |
| typed, full IRI | 101 | `"2026-04-06"^^<…XMLSchema#date>` |
| typed, datatype empty | 33 | `"2026-03-30"^^` |
| bare numeric | 26 | `28` |

The 819 are mostly `xsd:date`, `xsd:dateTime` and `xsd:integer` values written over MCP with `PREFIX xsd:`. A recall
query that matches a date or a number by constant (`?s sky:date "2026-04-19"^^xsd:date`) works only because it is
written with the same prefix. The same query written with the full IRI, or loaded from Turtle, misses.

The 33 empty-datatype atoms were all written between 2026-03-29 and 2026-04-17, all date or dateTime lexicals on
`date`, `verifiedDate` and `timestamp` predicates. 1.8.22 doesn't produce this form: each case tried stored the prefix
unexpanded instead. The writer that dropped the datatype is gone; the atoms remain, and their datatype can't be
recovered from the atom.

### Root cause: no single owner of "a SPARQL term as stored"

Each site that turns SPARQL text or a binding into an atom key does its own subset of the work:

| Site | Escapes (ADR-044) | Numeric/boolean shorthand | Datatype prefix |
|---|---|---|---|
| `UpdateExecutor.ExpandPrefixedName` (INSERT/DELETE DATA, template constants) | yes | no | no |
| `TriplePatternScan.ResolveTermForQuery` / `ResolveTermWithStorage` (constant patterns) | yes | `CanonicalizeNumericOrBoolean` | no |
| `TreeJoinExecutor` VALUES expansion | — | `CanonicalizeNumericOrBoolean` | no |
| `TriplePatternScan.CanonicalBoundValue` (scan seeded by a binding) | — | by tag; decimal typed as double | — |
| `LiteralForm.TermForBinding` (ADR-052 templates) | — | by lexical form | — |
| `QueryResults.Patterns.ResolveSlotTerm` / `ExpandNumericLiteral` | — | own copy; types `1e3` as decimal | no |
| `VariableGraphExecutor.ResolveSlotTerm` | — | — | — |

The last two may be unreachable since ADR-047. No caller of `VariableGraphExecutor` was found, and
`QueryResults.Patterns`' slot-term resolution wasn't traced. The table was built by reading code; D4 has the
implementation confirm each row by tracing and test.

ADR-052 made templates the sixth site. Each new site picks the subset its author thought of, which is the drift pattern
ADR-045, ADR-047 and ADR-052 removed elsewhere.

### Earlier work on this, and why it is still open

Literal and numeric forms have been worked on three times. Each pass fixed its own scope correctly, and none of them
had the SPARQL write side in scope:

| When | Work | What it fixed | What it left |
|---|---|---|---|
| 2026-05-17, 1.7.73 | [ADR-044](ADR-044-sparql-update-literal-canonicalization.md) | Escapes: a SPARQL literal is stored decoded, as the RDF parsers store it, at every site. | Numerics and booleans by design: "URIs, blank nodes, numerics, booleans pass through unchanged" (they can't contain escapes). Consolidating the term resolvers was deferred to the limits register. |
| 2026-06-20 | [Divergence register](../../divergence/README.md) S1c | One typing rule (`RdfNumeric`: exponent ⇒ double, `.` ⇒ decimal, else integer) shared by Turtle, TriG and `LiteralForm.CanonicalizeNumericOrBoolean`. | Only the rule converged. `UpdateExecutor` never calls it, so `INSERT DATA` and templates don't type numerics at all. |
| 2026-06, ADR-047 cutover | Numeric constant canonicalization | Constant patterns type a bare numeric or boolean before matching (`TriplePatternScan`, VALUES). | Expanding a prefixed datatype in a constant was noted in that work as "gap #2", and deferred. |

So the readers were fixed and the writers were not. A test that writes with `INSERT DATA` and reads with a constant
pattern would have caught it. The cross-format tests (`CrossFormCanonicalizationTests`, `RdfNumericConvergenceTests`)
compare the RDF parsers with `LiteralForm`, not with a SPARQL write.

**Spec adherence.** The current behavior doesn't follow the specs:

- **SPARQL 1.1 Query §4.1.2:** `1` is the abbreviation of `"1"^^xsd:integer`, and `true` of `"true"^^xsd:boolean`.
  SPARQL 1.1 Update's `INSERT DATA` and templates use the same term grammar. Storing the bare atom `1` stores something
  that isn't an RDF term.
- **SPARQL 1.1 Query §4.1.1** and RDF 1.1 Concepts §3.3: a literal's datatype is an IRI, and a prefixed name stands for
  the IRI it expands to. Storing `^^xsd:integer` unexpanded makes the stored term depend on the request's prefix
  declarations, and an undeclared prefix is an error.
- **SPARQL 1.1 Query §17:** evaluating a constant yields that RDF term, so `BIND(1.0 AS ?x)` binds
  `"1.0"^^xsd:decimal`, and `DATATYPE(1.0)` is `xsd:decimal`. `1e3` is the double 1000.
- **RDF 1.1 Concepts §3.3:** a simple literal is the `xsd:string` literal; they are one term.
- **SPARQL grammar [147]:** `.5` is a DECIMAL.

The W3C suites stayed green through all of this because their tests load data from Turtle and compare results by value
or with full IRIs. None of them writes a numeric with `INSERT DATA` and then matches it by constant.

## Decision

**One type, `StoredTerm`, owns the stored form of an RDF term. Every site that writes a term from SPARQL, or matches one
against the store, gets the form from it. The stored form is the one the RDF parsers already write.**

### D1. A SPARQL source term → its stored form

`StoredTerm.FromSource(token, prefixes, source, out scratch)` applies, in this order:

1. **IRI and prefixed name:** expanded through `PrefixExpander` (unchanged; an undeclared prefix is an error).
2. **Numeric shorthand** ([130]–[133], [146]–[155]): `"lexical"^^<xsd:integer | xsd:decimal | xsd:double>`, with the
   lexical exactly as written (sign, leading zeros and exponent case kept), as the Turtle loader stores it. An RDF term
   is identified by its lexical form, so `01` and `1` are different terms and stay different atoms.
3. **Boolean shorthand:** `"true"^^<xsd:boolean>` / `"false"^^<xsd:boolean>` (`RdfNumeric.BooleanLiteral`, unchanged).
4. **Typed literal:** escapes decoded (ADR-044, unchanged) and **the datatype expanded to a full IRI**. An undeclared
   datatype prefix is a parse error, as it is in every other IRI position.
5. **`xsd:string`:** `"x"^^xsd:string` is stored and matched as the simple literal `"x"` (RDF 1.1 Concepts §3.3; this
   is also N-Triples' canonical form). The Turtle, N-Triples, N-Quads, TriG, RDF/XML and JSON-LD loaders apply the same
   rule, so a loaded file and a SPARQL write agree.
6. **Language tag:** kept as written (see *Alternatives considered*).

`FromSource` replaces the term handling in `UpdateExecutor.ExpandPrefixedName`/`GetTermValue`, the constant-term
resolution in `TriplePatternScan`, and the VALUES expansion. `LiteralForm.Canonicalize` and
`CanonicalizeNumericOrBoolean` become its internal steps rather than entry points.

### D2. The SPARQL parser reads numeric literals by the grammar

- A sign is part of the token ([149]–[154] `INTEGER_POSITIVE` … `DOUBLE_NEGATIVE`): the pattern-tree parser keeps `+`.
- `[147] DECIMAL ::= [0-9]* '.' [0-9]+`: a leading `.` begins a decimal in a term position and in an expression.

### D3. A binding → its stored form

The evaluator side of this decision (a value keeps its term, decimal is its own tag, `1e3` is a double, one way from
a value to a binding) is [ADR-056](ADR-056-expression-value-is-the-term.md). What stays here is the stored form.

`StoredTerm.FromValue(value)` turns an evaluated value into its stored form and replaces both `TermForBinding` and
`CanonicalBoundValue`. For that, a value has to keep its term:

- **A constant evaluates to its term.** `BIND(01 AS ?x)` binds `"01"^^xsd:integer`; `BIND(1.0 AS ?x)` binds
  `"1.0"^^xsd:decimal`. The evaluator carries the source term with the value, not just the number.
- **A computed value has its XSD type** (the SPARQL 1.1 §17.3 operator mapping: integer, decimal, float, double), and
  its stored lexical form is that type's canonical representation. `xsd:decimal` becomes its own value tag rather than
  riding on `Double`.
- **`1e3` is a double** with value 1000, wherever it appears.

Expression evaluation for FILTER is unaffected except where a value was wrong (`1e3`, decimal typing). Comparison stays
by value.

### D4. Unreachable copies are deleted

`QueryResults.Patterns`' slot-term resolver and `VariableGraphExecutor` are traced. Whatever is unreachable is deleted;
whatever is reachable calls `StoredTerm`. The limits entry `sparql-resolve-term-family-duplication.md` moves to
Resolved, pointing here.

### D5. Existing stores are migrated, not matched both ways

Stores written before this ADR hold the old forms. The readers do **not** learn to match both forms. That would be a
second canonicalization rule kept forever, which is the divergence this ADR removes. Instead:

- **`mercury --migrate-terms <store>`** rewrites every quad version whose object atom differs from `StoredTerm`'s
  form: a bare numeric or boolean, a datatype that is the prefixed name `xsd:…` (expanded by the XSD namespace, the only
  prefix found in the census), or `"x"^^<xsd:string>`. Each rewrite keeps the version's valid time: `Delete` with the
  version's exact `validFrom`/`validTo` (`Retract`, [ADR-054](ADR-054-delete-ends-valid-time.md) D2), then `Add` of the
  corrected atom with the same interval (see Q1, resolved).
  - It walks every **live version**, read with `QueryEvolution` and skipping entries already `IsDeleted`, not only the
    current one. A version whose valid time ended in the past would otherwise keep the old atom in past views.
  - Retract matches the exact interval (ADR-054 D2). Today's delete marks the first entry whose interval overlaps, and
    two versions that touch at a boundary both overlap one exact interval.
- **What it can't decide, it lists and leaves:** a datatype that is a prefixed name other than `xsd:`, and an empty
  datatype (the 33 atoms above). Each is printed with its graph, subject, predicate and valid-from.
- It reports counts before and after, in the census's categories, and `--dry-run` reports without writing.
- Once every store is migrated, the evaluator's read rule for the old forms ([ADR-056](ADR-056-expression-value-is-the-term.md)
  D7: a stored `^^xsd:T` datatype and a bare numeric) is deleted.
- The MCP memory store is migrated with `mercury-mcp` stopped. A Reference store (read-only, sealed) isn't migrated in
  place; see Q2.

## Open questions (to settle before Accepted)

- **Q1. How is a correction recorded bitemporally? Resolved 2026-10-27** by reading `TemporalQuadIndex` and a probe on a
  Cognitive store:
  - A delete doesn't change the entry's valid time. `DeleteFromTree` sets `IsDeleted` and records the deletion's
    transaction time in `ModifiedAt`. `MatchesTemporalQuery` excludes `IsDeleted` entries from `AsOf` and `Range`
    queries at every time, and `AllTime` (`QueryEvolution`) includes them.
  - So `Delete(old atom, interval)` + `Add(new atom, same interval)` gives exactly the corrected history. The probe's
    quad was valid from t0−2h. After the rewrite, `AS OF t0−1h` returns only the corrected atom, and `AS OF now` the
    same. No point in time returns both.
  - `QueryEvolution` keeps both: the old atom with `IsDeleted = true`, and the new one, whose `TransactionTime` is the
    moment of correction. That is the record of "corrected on …".
  - The deletion's own transaction time (`ModifiedAt`) is stored but not exposed in `ResolvedTemporalQuad`. The new
    entry's transaction time carries the same information for a migration.
- **Q2. `xsd:string` in Reference stores.** D1.5 changes what the loaders write. A Reference store loaded before it (the
  21.3B and 8.17B Wikidata stores) keeps `"x"^^<xsd:string>` atoms. After the change, a query for `"x"^^xsd:string`
  normalizes to `"x"` and misses them, where it matches them today. How many such atoms those stores hold is not known.
  Wikidata's RDF mostly uses simple literals, but that is an expectation, not a count. A census of the sealed atom set
  settles it. If the count is not zero, the choices are: rebuild, accept the miss, or keep D1.5 out until the next
  rebuild.
- **Q3. The 33 empty-datatype atoms.** Their lexicals and predicates suggest `xsd:date`/`xsd:dateTime`, but that is
  inference from shape, which the migration doesn't do. Should they be retyped by hand (one reviewed SPARQL update),
  left as they are, or deleted?

## Consequences

- **Positive:**
  - One term, one atom, whatever wrote it. A recall query matches by constant regardless of whether the memory was
    written by `INSERT DATA`, a template, a BIND or a Turtle file.
  - Writes no longer corrupt values (`1e3` → `1`) or silently accept an undeclared datatype prefix.
  - Seven term-resolution sites become one, and the limits register loses an entry.
- **Negative:**
  - A request with an undeclared datatype prefix, accepted today, fails to parse. Same for the MCP: memory writes that
    relied on the silent acceptance now get an error.
  - The value carrier grows: a constant carries its source term, and decimal is a new tag. This touches the zero-GC
    paths in `FilterEvaluator` and `MaterializedRow`, so allocation is measured (see validation), not assumed.
  - Stores need a migration pass; Reference stores possibly a rebuild (Q2).
- **Neutral:**
  - FILTER comparisons are by value already and don't change, except where a value was wrong.
  - Query output shows full datatype IRIs where it used to echo a prefixed name for SPARQL-written data.

## Alternatives considered

- **Match both forms at read time.** The scan would try `1` and `"1"^^<xsd:integer>`. Rejected: two rules forever, a
  second index lookup per constant, and the next writer still has to choose one.
- **Canonicalize numeric values (`01` → `1`, `1.0` → `1`).** Rejected: RDF identifies a literal by its lexical form and
  datatype, so `"01"^^xsd:integer` and `"1"^^xsd:integer` are different terms (`sameTerm` is false), even though `=` is
  true. The Turtle loader already keeps the lexical; SPARQL writes match it.
- **Lowercase language tags.** RDF 1.1 Concepts allows it ("MAY be converted to lower case") but compares tags
  character by character. Stored `"x"@EN` doesn't match the pattern `"x"@en` today, in any writer, so this is not a
  split between writers. It is a separate choice and changes what users get back. Not in this ADR.
- **Patch `INSERT DATA` to call `CanonicalizeNumericOrBoolean`.** It fixes the first row of the table and leaves the
  rest, and it adds another caller of a partial rule: the patch adds a copy of what already exists.

## Validation plan

1. **Write-path matrix (facade tests).** For every term form in the table above, and every writer (Turtle, N-Triples
   where expressible, `INSERT DATA`, template constant, BIND, VALUES-fed template), assert that the raw atom is the same
   for all writers and that a constant pattern written in any SPARQL form (shorthand, prefixed datatype, full IRI)
   matches it. The probe that produced the table becomes this test.
2. **Expression values.** `BIND(01)`, `BIND(1.0)`, `BIND(1e3)`, `BIND(.5)`, `DATATYPE(1.0)`, `DATATYPE(1e3)`,
   `1e3 = 1000`, and computed results (`1.0 + 1`, `1 / 2`, `2e0 * 3`) against SPARQL 1.1 §17.3.
3. **Parse errors.** An undeclared datatype prefix fails, in INSERT DATA, a template and a query, and nothing runs
   (ADR-046).
4. **W3C conformance stays green**, the RDF loader suites included (D1.5 changes their output).
5. **Allocation.** The existing zero-GC tests for FILTER and scans still pass, and the allocation of a constant-pattern
   scan is measured before and after.
6. **Migration on a copy of the MCP store.** `--dry-run`, then the real run, then the census: 0 bare, 0 `xsd:`-prefixed,
   0 `^^<xsd:string>`, and the 33 empty-datatype atoms listed. Then a recall query matching a migrated date by full-IRI
   constant returns the quad. A past `AS OF` query is checked against the answer to Q1.
7. **Dogfood.** After migrating the live store: a `mercury_query` constant match on a number and on a date that were
   written before the migration.

## Related, not in this ADR

Probing for this ADR also found that **any GRAPH inside a nested group fails to parse in a query**:
`{ { GRAPH ?g { … } } }`, and `{ … } UNION { GRAPH ?g { … } }`, with "Incomplete triple pattern - expected object". It
fails identically on the source before ADR-052. Queries still run the legacy `GraphPattern` parser over their WHERE
before the pattern tree does. Its `ParseNestedGroupGraphPattern` handles FILTER, OPTIONAL, SERVICE, BIND, nested groups
and triples, so `GRAPH` is read as a subject. It is the query-side twin of ADR-052 (and of ADR-052's follow-up "queries
still guess where their WHERE begins"), and the recommended fix is the same: the query WHERE is parsed once, by the
pattern tree. Recorded in Mercury as `<https://sky-omega.org/bugs/mercury-query-nested-graph-legacy-parse>`; proposed as
ADR-055.

Settling Q1 found that **a SPARQL DELETE is retroactive in valid time.** A deleted quad leaves every `AS OF` view, also
those before the delete, because a delete sets `IsDeleted` and leaves `ValidTo` as it was. In the probe, a status that
held from t0−2h and was rewritten with `DELETE { … ?o } INSERT { … "fixed" }` has no status at all `AS OF t0−1h`.
[ADR-001](ADR-001-compaction.md) describes the opposite ("triples are never physically removed, only marked with
end-validity timestamps"), and every memory status rewrite over MCP takes this path. What a SPARQL DELETE means in a
bitemporal store is settled by [ADR-054](ADR-054-delete-ends-valid-time.md): a delete ends the fact, and "never held"
becomes a separate operation, Retract. D5's migration is a retraction ("never held in this form"). It uses Retract, and
it runs after ADR-054's history repair.

## References

- [ADR-044](ADR-044-sparql-update-literal-canonicalization.md): the wrapped-decoded literal form; escapes canonicalized
  at every site, numerics left as they were.
- [ADR-052](ADR-052-update-where-one-pattern-path.md): "Related defect" and the `TermForBinding` /
  `CanonicalBoundValue` follow-up.
- [`docs/limits/sparql-resolve-term-family-duplication.md`](../../limits/sparql-resolve-term-family-duplication.md).
- RDF 1.1 Concepts §3.3 (literals, simple literals as `xsd:string`, term equality); SPARQL 1.1 Query §4.1.2 (numeric
  and boolean shorthand), §17.3 (operator mapping), grammar [130]–[133], [146]–[155].
