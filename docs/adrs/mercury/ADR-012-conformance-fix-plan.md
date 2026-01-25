# ADR-012: W3C SPARQL Conformance Fix Plan

**Status:** In Progress
**Created:** 2026-01-19
**Updated:** 2026-01-25
**Baseline:** 1904 W3C tests total, 1791 passing (94%), 97 failing, 16 skipped
**Current:** 1905 W3C tests, 1885 passing (99%), 4 failing, 16 skipped

## Context

Mercury's SPARQL engine has achieved 99% W3C conformance (1885/1905 tests). For SPARQL 1.1 Query specifically: 212/224 passing (95%). The remaining failing tests cluster around specific areas:

| Category | Failures | Root Cause |
|----------|----------|------------|
| Property Paths | ✅ 0 | All property path tests pass (pp16, pp28a fixed 2026-01-22) |
| String Functions | ✅ 0 | All string function tests pass (STRBEFORE/STRAFTER fixed 2026-01-25) |
| Hash Functions | ✅ 0 | All hash function tests pass |
| RDF Term Functions | ✅ 0 | All RDF term function tests pass |
| DateTime Functions | ✅ 0 | All datetime function tests pass |
| MINUS/NOT EXISTS | ✅ 0 | All 12 tests pass; ExecuteToMaterialized EXISTS fixed 2026-01-25 |
| Aggregates | ~2 | Expressions inside aggregates (agg-err-01, agg-err-02) |
| GROUP BY / HAVING | ✅ 0 | agg-group-builtin, group04 fixed 2026-01-25 |
| EXISTS edge cases | ✅ 0 | All 6 tests pass |
| VALUES | ✅ 0 | All tests pass |
| Project expressions | ✅ 0 | Comparison operators fixed 2026-01-24 |
| BIND scoping | ~1 | Variable scoping in nested groups (bind10) |

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

### Phase 3: SPARQL Functions — ~20 tests failing (was 46)
**Target:** Reduce function failures from 46 to ~10
**Effort:** Medium-Large
**Files:** `FilterEvaluator.Functions.cs`, `BindExpressionEvaluator.cs`, `FilterEvaluator.cs`
**Updated:** 2026-01-24

**Fixed (2026-01-24):**
- ✅ TZ function in BindExpressionEvaluator - extracts timezone string ("Z", "+HH:MM", "-HH:MM")
- ✅ TIMEZONE function in BindExpressionEvaluator - returns xsd:dayTimeDuration
- ✅ Comparison operators (=, ==, !=, <, >, <=, >=) in SELECT expressions
- ✅ Parenthesized comparison expressions now evaluate correctly
- ✅ BNODE function added (counter state issue remains for multi-expression queries)

**Fixed (2026-01-22):**
- ✅ STRBEFORE/STRAFTER basic tests pass (language tag preservation)
- ✅ UCASE/LCASE preserve language tags
- ✅ SUBSTR preserves language tags
- ✅ Hash functions (MD5, SHA1, SHA256, SHA384, SHA512) all pass
- ✅ Added `GetLangTagOrDatatype()` method to Value struct

**Current failing function tests (~20 total):**

| Category | Failing Tests | Notes |
|----------|--------------|-------|
| String (~8) | STRBEFORE/STRAFTER datatyping (2), CONCAT (2), REPLACE (2), non-BMP (2) | Argument compatibility rules |
| RDF Terms (~4) | IRI/URI edge cases (2), UUID (1), BNODE (1) | Pattern matching, BNODE counter state |
| DateTime (1) | NOW (1) | Dynamic value comparison |
| Error handling (~4) | IF (1), COALESCE (1), AVG error (2) | Error propagation |

**Root causes:**
1. STRBEFORE/STRAFTER datatyping: Argument compatibility rules for language tags and typed strings
2. REPLACE: Regex replacement not yet implemented in BindExpressionEvaluator
3. BNODE: Counter state not shared across multiple SELECT expressions in same query
4. IF/COALESCE: Error propagation semantics

**Completed:**
- Basic CONCAT, SUBSTR, UCASE, LCASE work in FILTER and SELECT expressions
- CEIL, FLOOR, ROUND, ABS in BindExpressionEvaluator
- Language tag preservation for UCASE/LCASE/STRBEFORE/STRAFTER/SUBSTR
- Empty string result handling (quoted `""` for SELECT projections)
- TZ/TIMEZONE functions for datetime values
- Comparison operators in SELECT/BIND expressions

**Verification:**
```bash
dotnet test --filter "FullyQualifiedName~Sparql11_QueryEval" tests/Mercury.Tests
```

---

### Phase 4: Property Path Parsing — ✅ COMPLETED
**Target:** All property path tests passing
**Effort:** Medium-Large
**Files:** `SparqlParser.cs`, `Operators.cs`, `QueryResults.Modifiers.cs`
**Completed:** 2026-01-22

**Fixed property path tests:**
- pp30 ✅ sequence-within-alternative operator precedence (2026-01-21)
- pp31 ✅ grouped path followed by path continuation (2026-01-21)
- pp32 ✅ inverse predicate in sequence within alternative (2026-01-21)
- pp33 ✅ grouped alternative as first step of sequence (2026-01-21)
- pp06, pp07, pp34, pp35 ✅ Named graph paths (2026-01-21)
- pp16 ✅ Zero-or-more reflexive pairs for all graph nodes (2026-01-22)
- pp28a ✅ Grouped zero-or-one path (:p/:p)? (2026-01-22)

**Fixes applied (2026-01-22):**
1. Implemented `_isGroupedZeroOrOne` handling in Operators.cs
2. Fixed zero-or-more (`*`) to discover ALL nodes in graph for reflexive pairs
3. Fixed ORDER BY term type ordering (IRIs < Literals per SPARQL spec)

**Verification:**
```bash
dotnet test --filter "Name~pp" tests/Mercury.Tests
```

---

### Phase 5: Subquery Scope — ✅ Mostly Complete
**Target:** ~12 tests
**Effort:** Medium
**Files:** `QueryExecutor.cs`, `BoxedSubQueryExecutor.cs`, `Operators.cs`, `SparqlParser.Clauses.cs`
**Updated:** 2026-01-20

**Current status:** 11/14 passing (sq12 and sq14 skipped, Post-subquery VALUES failing)

| Test | Status | Notes |
|------|--------|-------|
| sq01-sq10 | ✅ Pass | Core subquery functionality works |
| sq11 Subquery limit per resource | ✅ Pass | Fixed |
| sq12 CONSTRUCT with built-ins | ⏭️ Skip | CONSTRUCT subquery not yet supported |
| sq13 Don't inject bindings | ✅ Pass | Fixed |
| sq14 Limit by resource | ⏭️ Skip | CONSTRUCT subquery not yet supported |
| Post-subquery VALUES | ❌ Fail | VALUES in bindings/ category |

**Previously fixed:**
- sq07: GRAPH clause inside subquery
- sq08: Subquery with aggregate
- sq09: Nested subqueries
- sq10: Subquery with EXISTS filter
- `a` keyword expansion to rdf:type
- `FILTER(EXISTS {...})` syntax parsing

**Remaining:** CONSTRUCT-type subqueries (sq12, sq14) would require CONSTRUCT inside SELECT which is non-standard.

---

### Phase 6: Negation (NOT EXISTS, MINUS) — ✅ COMPLETED
**Target:** 12/12 negation tests passing
**Effort:** Medium
**Files:** `Operators.cs` (MINUS operator), `FilterEvaluator.cs`
**Completed:** 2026-01-21

**Result:** All 12 W3C negation tests now pass:
- EXISTS within GRAPH pattern: ✅
- GRAPH variable inside EXISTS bound to external: ✅
- Subsets by exclusion (NOT EXISTS): ✅
- Subsets by exclusion (MINUS): ✅
- Medical temporal proximity (NOT EXISTS): ✅
- MINUS from fully/partially bound minuend: ✅
- outer GRAPH operator MINUS disjointness: ✅
- Positive EXISTS tests: ✅
- Nested MINUS: ✅

**Fixes applied:**
1. GRAPH context properly propagated into EXISTS/NOT EXISTS evaluation
2. MINUS operator handling for bound minuend patterns
3. External variable bindings visible inside EXISTS
4. EXISTS within GRAPH pattern for W3C conformance

---

### Phase 7: VALUES Clause ✅ COMPLETED
**Target:** Reduce VALUES failures to 0
**Effort:** Small
**Files:** `SparqlParser.Clauses.cs`, `SparqlTypes.cs`, `QueryResults.Patterns.cs`, `Operators.cs`
**Completed:** 2026-01-20

**Previously failing VALUES tests (2 total):**

| Test | Issue |
|------|-------|
| Inline VALUES graph pattern | VALUES inside WHERE clause not joined correctly |
| Post-subquery VALUES | VALUES after subquery not applied |

**Fix:**
1. Inline VALUES: Added prefix expansion in `MatchesValuesConstraint()` in `QueryResults.Patterns.cs` to expand prefixed names before comparison
2. Post-subquery VALUES: Added `MatchesValuesConstraint()` to `BoxedSubQueryExecutor` in `Operators.cs` to filter subquery results against VALUES clause

**Result:** Both VALUES tests now pass:
- Inline VALUES graph pattern: ✅
- Post-subquery VALUES: ✅

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

| Phase | Description | Failing | Target Remaining | Status |
|-------|-------------|---------|------------------|--------|
| 1 | Numeric Aggregates | 0 | 0 | ✅ Done |
| 2 | GROUP_CONCAT | 0 | 0 | ✅ Done |
| 3 | SPARQL Functions | 0 | 0 | ✅ Done (was 46) |
| 4 | Property Paths | 0 | 0 | ✅ Done (was 6) |
| 5 | Subquery Scope | 1 (+2 skip) | 0 | ✅ Nearly Done |
| 6 | Negation (EXISTS/MINUS) | 0 | 0 | ✅ Done |
| 7 | VALUES Clause | 0 | 0 | ✅ Done |
| 8 | XSD Cast Functions | 0 | 0 | ✅ Done |

**Current Progress:** 3 failing tests total (212 passing, 9 skipped out of 224) — 95% conformance for SPARQL 1.1 Query

**Remaining failures:**
- agg-err-01, agg-err-02: Expressions inside aggregates (e.g., `AVG(IF(...))`)
- bind10: BIND variable scoping in nested groups

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

## Next Steps

**Priority 1: Expressions inside aggregates (~2 tests)**
- agg-err-01: `(MIN(?p) + MAX(?p)) / 2 AS ?c` - post-aggregation expression evaluation
- agg-err-02: `AVG(IF(isNumeric(?p), ?p, COALESCE(...)))` - expressions inside aggregate functions

**Root cause:** The `GroupedRow.UpdateAggregates` method looks up values by variable hash, but when the aggregate argument is an expression (not a simple variable), the hash lookup fails. Fix requires evaluating expressions per-row during aggregation.

**Priority 2: BIND scoping (1 test)**
- bind10: Variables bound by BIND in outer scope should not be visible inside nested groups at filter evaluation time

**Root cause:** SPARQL requires nested groups to be evaluated as independent units. Variables bound by BIND in the outer scope should not propagate into nested groups' filter evaluation context.

**Recently Completed (2026-01-25):**
- ✅ STRBEFORE/STRAFTER datatyping: Fixed `GetLexicalForm()` to handle empty string literals like `""@en`
- ✅ agg-group-builtin: DATATYPE function in GROUP BY now preserves original datatype
- ✅ group04: COALESCE with typed literals preserves datatype annotations
- ✅ HAVING with multiple conditions: `(COUNT(*) > 1) (COUNT(*) < 3)` now works
- ✅ AVG error propagation: Non-numeric values (blank nodes) cause no binding
- ✅ BNODE per-row seed: Correct blank node identity across result rows
- ✅ Expression aggregate evaluation framework added

**Previously Completed (2026-01-24):**
- ✅ TZ function: Extracts timezone string from xsd:dateTime values
- ✅ TIMEZONE function: Returns xsd:dayTimeDuration from xsd:dateTime values
- ✅ Comparison operators (=, ==, !=, <, >, <=, >=) in BindExpressionEvaluator
- ✅ Parenthesized comparison expressions: `(?x = ?y)` now evaluates correctly
- ✅ UUID/STRUUID: Now use time-ordered UUID v7 format
- ✅ NOW/RAND: Dynamic value functions added

**Previously Completed (2026-01-22):**
- ✅ Property paths: pp16 (zero-or-more reflexive for all graph nodes), pp28a (grouped zero-or-one)
- ✅ ORDER BY: Fixed term type ordering (IRIs < Literals per SPARQL spec)
- ✅ String functions: UCASE/LCASE/STRBEFORE/STRAFTER/SUBSTR language tag preservation
- ✅ Hash functions: MD5, SHA1, SHA256, SHA384, SHA512 all pass
- ✅ Empty string handling for STRBEFORE/STRAFTER
- ✅ Negation/EXISTS (12/12 tests) - GRAPH context, MINUS semantics
- ✅ pp30-pp35 property path grouping, sequences, and named graph paths (2026-01-21)

## Out of Scope

The following are intentionally not addressed (per SkipList):
- RDF 1.2 features (VERSION, annotations, base direction)
- Entailment regimes
- Graph Store HTTP Protocol
- SPARQL Protocol server tests
- SERVICE requiring network
- NFC normalization on IRIs
