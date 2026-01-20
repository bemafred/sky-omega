# ADR-012: W3C SPARQL Conformance Fix Plan

**Status:** In Progress
**Created:** 2026-01-19
**Updated:** 2026-01-20
**Baseline:** 3785 tests total, 3656 passing (96.6%), 114 failing, 15 skipped

## Context

Mercury's SPARQL engine has achieved 95% W3C conformance. The remaining 171 failing tests cluster around specific areas:

| Category | Failures | Root Cause |
|----------|----------|------------|
| Aggregates (SUM, AVG, MIN, MAX) | ~15 | Numeric type handling in aggregation |
| GROUP_CONCAT | ~5 | Subquery aggregation interaction |
| Functions | ~73 | Various function edge cases |
| Property Paths | ~23 | Complex path syntax parsing |
| Subqueries | ~12 | Scope and projection issues |
| Negation | ~11 | NOT EXISTS / MINUS edge cases |
| Bindings/VALUES | ~10 | Multi-variable VALUES parsing |
| Other | ~22 | Misc edge cases |

## Plan: Phased Approach

### Phase 1: Numeric Aggregates ✅ COMPLETED
**Target:** ~15-20 tests
**Effort:** Small
**Files:** `QueryResults.Modifiers.cs`
**Completed:** 2026-01-19

**Issue:** SUM, AVG, MIN, MAX fail on decimal/double data because:
1. Numeric type promotion isn't handling `xsd:decimal` and `xsd:double` correctly
2. Aggregates return wrong type or string representation

**Result:** All core aggregate tests pass:
- COUNT 1-8b: ✅ All 8 passing
- GROUP_CONCAT 1-5: ✅ All 5 passing
- SUM, SUM with GROUP BY: ✅ Both passing
- AVG, AVG with GROUP BY, AVG empty: ✅ All 3 passing
- MIN, MIN with GROUP BY: ✅ Both passing
- MAX, MAX with GROUP BY: ✅ Both passing
- SAMPLE: ✅ Passing

**Note:** 2 error propagation edge cases (agg-err-01, agg-err-02) remain failing - these test aggregate behavior when FILTER errors occur and are tracked separately.

---

### Phase 2: GROUP_CONCAT Edge Cases ✅ COMPLETED
**Target:** ~5 tests
**Effort:** Small
**Files:** `QueryResults.Modifiers.cs`
**Completed:** 2026-01-19

**Issue:** GROUP_CONCAT with subqueries produces wrong row counts.

**Result:** All 5 W3C GROUP_CONCAT tests pass:
- GROUP_CONCAT 1: ✅ Passing
- GROUP_CONCAT 2: ✅ Passing (subquery case from ADR)
- GROUP_CONCAT with SEPARATOR: ✅ Passing
- GROUP_CONCAT with same language tag: ✅ Passing
- GROUP_CONCAT with different language tags: ✅ Passing

---

### Phase 3: SPARQL Functions — 22% (13/59)
**Target:** ~30-40 tests (subset of 73)
**Effort:** Medium
**Files:** `FilterEvaluator.Functions.cs`

**Current status (as of 2026-01-19):**

| Function | Status | Notes |
|----------|--------|-------|
| isNumeric, ABS | ✅ Pass | |
| MINUTES, SECONDS, HOURS | ✅ Pass | DateTime extraction working |
| IF, COALESCE | ✅ Pass (basic) | Error propagation cases fail |
| REPLACE (overlap, capture) | ✅ Pass | Basic cases work |
| CEIL, FLOOR, ROUND | ❌ Fail | Numeric rounding |
| CONCAT (4 tests) | ❌ Fail | String concatenation |
| SUBSTR (4 tests) | ❌ Fail | Substring extraction |
| UCASE, LCASE (4 tests) | ❌ Fail | Case conversion |
| ENCODE_FOR_URI (2 tests) | ❌ Fail | IRI encoding |
| MD5, SHA1, SHA256, SHA384, SHA512 | ❌ Fail | Hash functions (10 tests) |
| TIMEZONE, TZ | ❌ Fail | Timezone extraction |
| BNODE(str) | ❌ Fail | Blank node creation |
| STRBEFORE, STRAFTER (4 tests) | ❌ Fail | String boundary cases |
| REPLACE (basic, 'i' option) | ❌ Fail | Regex flags |
| IF error propagation | ❌ Fail | |
| COALESCE() without args | ❌ Fail | |

**Verification:**
```bash
dotnet test --filter "FullyQualifiedName~Sparql11_QueryEval" tests/Mercury.Tests | grep -E "functions"
```

---

### Phase 4: Property Path Parsing — 39% (13/33)
**Target:** ~10 tests (subset of 23)
**Effort:** Medium-Large
**Files:** `SparqlParser.cs`, `Operators.cs`

**Current status (as of 2026-01-19):**

| Test | Status | Notes |
|------|--------|-------|
| pp01 Simple path | ✅ Pass | |
| pp03 Simple path with loop | ✅ Pass | |
| pp06 Path with two graphs | ✅ Pass | |
| pp08 Reverse path | ✅ Pass | |
| pp11, pp21, pp23, pp25 Diamond patterns | ✅ Pass | :p+ working |
| pp31 Operator precedence 2 | ✅ Pass | |
| pp37 Nested (*)* | ✅ Pass | |
| ZeroOrX terms, * and ? with end constant | ✅ Pass | |
| pp02 Star path (*) | ❌ Fail | Zero-or-more |
| pp07, pp09, pp10, pp12, pp14, pp16 | ❌ Fail | Various path patterns |
| pp28a (:p/:p)? | ❌ Fail | Grouped path with modifier |
| pp30, pp32, pp33 Operator precedence | ❌ Fail | Precedence issues |
| pp34, pp35 Named Graph paths | ❌ Fail | |
| pp36 Arbitrary path with bound endpoints | ❌ Fail | |
| Negated Property Sets (4 tests) | ❌ Fail | inverse, direct+inverse, 'a', '^a' |
| * and ? with start constant on empty | ❌ Fail | |

**Known parsing gaps:**
1. Negated property sets with both direct and inverse: `!(ex:p | ^ex:q)`
2. Complex nested alternatives: `(p1|p2)/(p3|p4)`
3. Modifiers on grouped paths: `(p1/p2)+`

**Verification:**
```bash
dotnet test --filter "FullyQualifiedName~property-path" tests/Mercury.Tests
```

---

### Phase 5: Subquery Scope — 71% (10/14)
**Target:** ~12 tests
**Effort:** Medium
**Files:** `QueryExecutor.cs`, `BoxedSubQueryExecutor.cs`, `Operators.cs`, `SparqlParser.Clauses.cs`

**Current status (as of 2026-01-20):**

| Test | Status | Notes |
|------|--------|-------|
| sq01 Subquery within graph pattern | ✅ Pass | |
| sq02 Graph variable bound | ✅ Pass | |
| sq03 Graph variable not bound | ✅ Pass | |
| sq04 Default graph does not apply | ✅ Pass | |
| sq05, sq06 FROM NAMED applies | ✅ Pass | |
| sq08 Subquery with aggregate | ✅ Pass | Fixed 2026-01-20 |
| sq09 Nested Subqueries | ✅ Pass | Fixed 2026-01-20 |
| sq10 Subquery with EXISTS | ✅ Pass | Fixed 2026-01-20 |
| Post-subquery VALUES | ❌ Fail | |
| sq07 Subquery with FROM | ❌ Fail | |
| sq11 Subquery limit per resource | ❌ Fail | Blank node property list syntax |
| sq13 Subqueries don't inject bindings | ❌ Fail | Blank node property list syntax |
| sq14 Limit by resource | ❌ Fail | Blank node property list syntax |

**Fixed (2026-01-20):**
- sq08: Subquery with aggregate now works correctly
- sq09: Nested subqueries (subquery within subquery) now execute correctly
- sq10: Subquery with EXISTS filter now evaluates correctly
- `a` keyword: Now properly expands to `<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>`
- `FILTER(EXISTS {...})` syntax: Now correctly parsed into ExistsFilter

**Commits:**
- `4bb127f` Fix nested subqueries and 'a' keyword expansion in SPARQL executor
- `af0de6a` Fix EXISTS filter parsing and evaluation in subquery context

**Remaining Issues:**
1. sq11, sq13, sq14 use blank node property list syntax (`[ predicate object ]`) which requires expansion
2. sq07 has FROM clause in subquery (dataset scope)

---

### Phase 6: Negation (NOT EXISTS, MINUS) — 32% (7/22)
**Target:** ~11 tests
**Effort:** Medium
**Files:** `Operators.cs` (MINUS operator)

**Current status (as of 2026-01-19):**

| Test | Status | Notes |
|------|--------|-------|
| exists01 Exists with one constant | ✅ Pass | |
| exists02 Exists with ground triple | ✅ Pass | |
| exists04 Nested positive exists | ✅ Pass | |
| exists05 Nested negative exists in positive | ✅ Pass | |
| Positive EXISTS 2 | ✅ Pass | |
| sq10 Subquery with exists | ✅ Pass | |
| exists03 Exists within graph pattern | ❌ Fail | GRAPH context |
| GRAPH variable inside EXISTS | ❌ Fail | External binding |
| Subsets by exclusion (NOT EXISTS) | ❌ Fail | |
| Subsets by exclusion (MINUS) | ❌ Fail | |
| Medical temporal proximity (NOT EXISTS) | ❌ Fail | |
| subset-01, subset-02, subset-03, set-equals-1 | ❌ Fail | Set operations |
| Positive EXISTS 1 | ❌ Fail | |
| MINUS from fully/partially bound minuend | ❌ Fail | |
| GRAPH operator with MINUS disjointness | ❌ Fail | |
| pp10 Path with negation | ❌ Fail | |

**Issues:**
1. MINUS with multiple patterns
2. NOT EXISTS with OPTIONAL inside
3. Blank node comparison semantics
4. EXISTS within GRAPH patterns

---

### Phase 7: VALUES Clause — 30% (3/10)
**Target:** ~10 tests
**Effort:** Small-Medium
**Files:** `SparqlParser.cs`

**Current status (as of 2026-01-19):**

| Test | Status | Notes |
|------|--------|-------|
| values1 Post-query VALUES with subj-var, 1 row | ✅ Pass | Single variable works |
| values2 Post-query VALUES with obj-var, 1 row | ✅ Pass | |
| values6 Post-query VALUES with pred-var, 1 row | ✅ Pass | |
| values3 Post-query VALUES with 2 obj-vars | ❌ Fail | Multi-variable |
| values4 Post-query VALUES with UNDEF | ❌ Fail | UNDEF handling |
| values5 Post-query VALUES 2 rows with UNDEF | ❌ Fail | |
| values7 Post-query VALUES with OPTIONAL | ❌ Fail | |
| values8 Post-query VALUES with subj/obj-vars | ❌ Fail | Multi-variable |
| inline1 Inline VALUES graph pattern | ❌ Fail | Inline VALUES |
| inline2 Post-subquery VALUES | ❌ Fail | VALUES in subquery |

**Issues:**
1. Multi-variable VALUES: `VALUES (?x ?y) { (:a :b) (:c :d) }`
2. UNDEF handling in VALUES
3. VALUES in subqueries
4. Inline VALUES graph patterns

---

### Phase 8: XSD Cast Functions ✅ COMPLETED
**Target:** 6 tests
**Effort:** Medium
**Files:** `BindExpressionEvaluator.cs`, `FilterEvaluator.cs`, `SparqlTypes.cs`, `SparqlResultComparer.cs`
**Completed:** 2026-01-19

**Issue:** W3C XSD cast tests failing due to multiple interacting bugs:
1. `ParseTypedLiteralString` didn't detect URIs (values like `<http://...>`)
2. `CastToString` didn't strip angle brackets from URIs
3. `TruncateTo` in `BindingTable` didn't reclaim string buffer space during backtracking
4. Numeric value comparison didn't handle different representations (e.g., "3.333E1" vs "33.33")
5. Structural hash included Unbound bindings

**Fix:**
- Added URI detection in `ParseTypedLiteralString` to return `ValueType.Uri` for `<...>` values
- `CastToString` now strips angle brackets from URIs when casting to `xsd:string`
- `TruncateTo` now reclaims string buffer space based on last retained binding
- Added `AreNumericValuesEqual` for value-based numeric comparison in result comparer
- Structural hash now skips Unbound bindings
- Refactored: consolidated `ParseTypedLiteralString` into `Value.ParseFromBinding` static method

**Result:** All 6 XSD cast tests pass (boolean, integer, float, double, decimal, string)

**Commits:**
- `ac770a4` Fix W3C SPARQL 1.1 XSD cast function conformance tests
- `be25b30` Refactor: extract Value.ParseFromBinding to eliminate duplication

---

## Execution Strategy

### Principles
1. **One phase at a time** - Complete and verify before moving on
2. **Tests drive fixes** - Run specific test filters, fix, verify
3. **No regressions** - Full test suite after each phase
4. **Document learnings** - Update CLAUDE.md with any new patterns

### Success Criteria per Phase

| Phase | Description | Current | Target | Status |
|-------|-------------|---------|--------|--------|
| 1 | Numeric Aggregates | ~95% | 100% | ✅ Done |
| 2 | GROUP_CONCAT | 100% | 100% | ✅ Done |
| 3 | SPARQL Functions | 22% (13/59) | ~70% | In Progress |
| 4 | Property Paths | 39% (13/33) | ~70% | In Progress |
| 5 | Subquery Scope | 71% (10/14) | ~90% | In Progress |
| 6 | Negation (EXISTS/MINUS) | 32% (7/22) | ~80% | In Progress |
| 7 | VALUES Clause | 30% (3/10) | ~90% | In Progress |
| 8 | XSD Cast Functions | 100% | 100% | ✅ Done |

**Current Progress:** Phases 1, 2, and 8 complete. Phase 5 at 71%. Phases 3, 4, 6, 7 need work.

**Recommended priority:**
1. **Phase 5** (Subqueries) - 71% done, closest to completion
2. **Phase 3** (Functions) - Highest test count, likely quick fixes for many
3. **Phase 7** (VALUES) - Small scope, well-defined issues

### Commands for Each Phase

```bash
# Run specific category
dotnet test --filter "Name~aggregates" tests/Mercury.Tests

# Run single test by name
dotnet test --filter "FullyQualifiedName~SUM" tests/Mercury.Tests

# Full conformance suite
dotnet test --filter "FullyQualifiedName~W3C" tests/Mercury.Tests

# Verbose output for debugging
dotnet test --filter "Name~SUM" tests/Mercury.Tests -v d
```

## Next Steps: Phase 3 (SPARQL Functions)

**Recommended action:** Investigate SPARQL function edge cases.

```bash
# Run function tests to identify failures
dotnet test --filter "FullyQualifiedName~functions" tests/Mercury.Tests

# Priority areas: MINUTES, SECONDS, HOURS, ENCODE_FOR_URI, STRBEFORE, STRAFTER
```

Focus on DateTime extraction functions and string boundary cases first.

## Out of Scope

The following are intentionally not addressed (per SkipList):
- RDF 1.2 features (VERSION, annotations, base direction)
- Entailment regimes
- Graph Store HTTP Protocol
- SPARQL Protocol server tests
- SERVICE requiring network
- NFC normalization on IRIs
