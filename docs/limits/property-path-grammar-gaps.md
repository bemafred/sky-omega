# Limit: Property-path grammar — three combinations not yet handled by the parser

**Status:**        Triggered
**Surfaced:**      2026-04-28, via a parse-only sweep of all 1,199 WDBench `paths` + `c2rpqs` queries against `wiki-21b-ref`. The sweep was triggered by 3 visible parser failures during the post-cancellation-fix rerun (commit `527016f`); the sweep characterized the full inventory upfront rather than discovering shapes one-by-one across multi-hour runs.
**Last reviewed:** 2026-04-28
**Promotes to:**   ADR for a property-path grammar completeness pass. Trigger: scheduled engineering window for SPARQL spec hardening; ideally before any externally-published WDBench results, since these shapes show up in real-world property-path workloads.

## Description

The Mercury SPARQL parser handles SPARQL 1.1 property-path expressions by recursive descent in `ParsePredicateOrPath` (`src/Mercury/Sparql/Parsing/SparqlParser.cs:1246`). Sequences (`/`), alternatives (`|`), inverse (`^`), groups `(...)`, quantifiers (`*`, `+`, `?`), and negated property sets (`!(...)`) are each handled. The continuation logic that allows `<P1>/<P2>/<P3>` to chain into a sequence path lives at lines 1432–1538 — but it only runs after a base term parsed by `ParseTerm()`. When `ParsePredicateOrPath` returns from a parenthesized or inverse-prefixed branch (lines 1313, 1341, 1345), the continuation logic is skipped, leaving sequences/alternatives inside groups and quantifiers on inverse-grouped expressions unsupported.

A parse-only sweep over the WDBench `paths` (660 queries) + `c2rpqs` (539 queries) categories — totaling 1,199 — found **12 failures (1.0%)** in three distinct grammar combinations:

### Shape 1 — Inverse-quantified group: `^(P)*`, `^(P)+`, `^(P)?`

**Failures: 2** (paths/00072, paths/00102)

```sparql
SELECT * WHERE { <Q390192> ^(<P40>)* ?x1 }
```

The parser at line 1316–1346 handles `^(path)` (single application, returning `PropertyPath.InverseGroup`) but does not check for a trailing quantifier after the closing `)`. A trailing `*`/`+`/`?` is left for the outer parser, which fails with "Incomplete triple pattern - expected object" because the quantifier appears where an object should be.

**Semantic fix**: `^(P)*` is equivalent to `(^P)*` — quantified inverse traversal. Could be a parse-time rewrite or a new set of `PathType` values (`InverseGroupedZeroOrMore`, etc.) with runtime BFS in inverse direction.

### Shape 2 — Inverse of grouped alternative with quantifier: `^((A|B))+`

**Failures: 5** (paths/00656, 00657, 00658, 00659, 00660)

```sparql
SELECT * WHERE { ?x1 ^((<P22>|<P25>))+ <Q390192> }
```

Combines doubly-nested grouping with the inverse-quantified-group issue from Shape 1. Currently fails with "Incomplete triple pattern - expected predicate and object after subject" — the outer parser doesn't recognize `^` as a valid path-prefix when followed by these structures.

**Semantic fix**: `^((A|B))+` is equivalent to traversing zero-or-more inverses of the alternative `(A|B)`. The grammar gap is the same as Shape 1 plus the doubly-nested-group resolution from Shape 3.

### Shape 3 — Sequence/alternative inside group with non-trivial first element: `(^A/B)`, `((expr))+`

**Failures: 5** (paths/00067, 00214, 00251, 00654, 00655)

```sparql
SELECT * WHERE { ?x1 (^<P171>/<P225>) ?x2 }
SELECT * WHERE { <Q3454165> ((^<P161>/<P161>))+ ?x1 }
```

When `ParsePredicateOrPath` is called recursively from inside `(...)` at line 1260, it parses only the FIRST inner element (e.g., `^<P>`) and returns. Lines 1262–1264 then expect `)` immediately, but find `/` or `|` and throw "Expected ')' after grouped path expression". Sequences/alternatives that *start* with a non-base-term element (inverse, nested group) inside a parenthesized group cannot be expressed.

**Semantic fix**: Extend the recursive call at line 1260 to also handle continuation operators (`/`, `|`) after the first element. Or factor the continuation logic at lines 1432–1538 into a method that can run after any parsed expression, not only after a base term. Both approaches require care for SPARQL operator precedence (`/` binds tighter than `|`).

## Trigger condition

Already triggered by the WDBench cold baseline rerun. Effects:

- 12 of 1,199 queries (1.0%) report parse failures
- The remaining 1,187 (99.0%) are unaffected
- No data corruption — failures are at parse time, do not pollute timing measurements for other queries

The parse-failure shape is **not a runtime defect**: the queries are recognized as malformed, an error is returned, the harness records the failure cleanly. Compare to the cancellation bug (now resolved in commit `527016f`) which silently corrupted measurements over multi-hour wall-clock; this is a clean, characterized, narrow gap.

## Current state

Mercury 1.7.45 parses 99% of WDBench property-path queries. The W3C SPARQL 1.1 conformance suite passes 100% (618 query + 94 update + 103 syntax tests) — the gaps surface only on real-world WDBench shapes that use combinations the W3C test suite does not exercise.

This is itself a finding: **WDBench surfaces grammar combinations the W3C SPARQL 1.1 conformance suite leaves uncovered.** A future contribution back to the W3C test suite (or to a successor benchmark) would tighten the conformance contract.

The 12 affected query files are listed exactly above and reproducible via:

```bash
# Parse-only sweep — under a minute
dotnet run /tmp/parse_sweep.cs   # see commit-attached script in this entry's history
```

## Candidate mitigations

**Option A — parse-time rewrite (cheaper).** For Shapes 1 and 2, transform at parse time: `^(P)*` → equivalent path with the inverse direction encoded on a quantified node. For Shape 3, add a small inner loop in the post-parens-recursion branch to consume `/` and `|` operators with proper precedence. Estimated effort: 4–6 hours including tests. Risk: possible regression on already-supported nesting cases without thorough test coverage.

**Option B — refactor `ParsePredicateOrPath` for compositionality.** Split into `ParsePathPrimary` (atom: term / `^X` / `(X)` / `!X`) and `ParsePathExpr` (composition: applies quantifiers, sequences, alternatives, with proper precedence). All composition logic runs after any primary, not just after a base term. Estimated effort: 1–2 days including a regression sweep over the W3C SPARQL 1.1 syntax test suite (103 tests) and the property-path execution tests. Risk: lower long-term — the composition vs primary split matches the SPARQL grammar EBNF directly and is easier to extend for future grammar additions.

**Option B is recommended** when the engineering window opens. Property-path grammar will not see further additions in SPARQL 1.2; the refactor is a one-time cost that closes the gap permanently.

**Interim posture**: WDBench writeups disclose the 1.0% parse-failure rate honestly, name the three shapes, and link to this register entry. Treating the WDBench run as an advanced conformance check that surfaces gaps the W3C suite does not exercise is the right framing — the run is doing useful epistemic work even when it fails on these queries.

## References

- `src/Mercury/Sparql/Parsing/SparqlParser.cs:1246` — `ParsePredicateOrPath`, the affected entry point
- `src/Mercury/Sparql/Parsing/SparqlParser.cs:1316-1346` — `^(...)` handling without trailing-quantifier check (Shapes 1 + 2)
- `src/Mercury/Sparql/Parsing/SparqlParser.cs:1260-1264` — recursive call inside `(...)` that fails on continuation operators (Shape 3)
- `src/Mercury/Sparql/Parsing/SparqlParser.cs:1432-1538` — existing continuation logic (currently bound to base-term parsing only)
- `src/Mercury/Sparql/Types/PropertyPath.cs` — the path-shape AST; new `PathType` values likely needed for Option A
- `docs/limits/cancellable-executor-paths.md` — sibling limits entry; the cancellation issue was on the runtime side, this one is on the parser side; both surfaced from the same WDBench cold baseline run
- `docs/validations/wdbench-cold-baseline-21b-2026-04-27.jsonl` — the original (corrupted) run that surfaced 3 of these failures
- `docs/validations/wdbench-paths-c2rpqs-rerun-21b-2026-04-28.jsonl` — the rerun (in flight) that will produce the clean disclosure-marked baseline
