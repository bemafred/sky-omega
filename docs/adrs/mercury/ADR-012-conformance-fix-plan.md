# ADR-012: W3C SPARQL Conformance Fix Plan

**Status:** In Progress
**Created:** 2026-01-19
**Updated:** 2026-01-22
**Baseline:** 1904 W3C tests total, 1791 passing (94%), 97 failing, 16 skipped
**Current:** 1904 W3C tests, 1847 passing (97%), 41 failing, 16 skipped

## Context

Mercury's SPARQL engine has achieved 97% W3C conformance (1847/1904 tests). For SPARQL 1.1 Query specifically: 174/224 passing (78%). The remaining 43 failing tests cluster around specific areas:

| Category | Failures | Root Cause |
|----------|----------|------------|
| Property Paths | ✅ 0 | All property path tests pass (pp16, pp28a fixed 2026-01-22) |
| String Functions | ~12 | STRBEFORE/STRAFTER datatyping, CONCAT, REPLACE, non-BMP Unicode |
| Hash Functions | ✅ 0 | All hash function tests pass |
| RDF Term Functions | ~6 | IRI/URI edge cases, UUID/STRUUID pattern matching |
| DateTime Functions | 3 | NOW, TIMEZONE, TZ |
| MINUS/NOT EXISTS | ✅ 0 | All 12 tests pass |
| Aggregates | ~4 | Error propagation in AVG, aggregate edge cases |
| GROUP BY / HAVING | ~3 | Built-in functions in GROUP BY, HAVING conditions |
| EXISTS edge cases | ✅ 0 | All 6 tests pass |
| VALUES | ✅ 0 | All tests pass |
| Project expressions | ~3 | Expression error handling, unbound variables |
| Other | ~8 | IF/COALESCE error propagation |

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

### Phase 3: SPARQL Functions — ~18 tests failing (was 46)
**Target:** Reduce function failures from 46 to ~10
**Effort:** Medium-Large
**Files:** `FilterEvaluator.Functions.cs`, `BindExpressionEvaluator.cs`, `FilterEvaluator.cs`
**Updated:** 2026-01-22

**Fixed (2026-01-22):**
- ✅ STRBEFORE/STRAFTER basic tests pass (language tag preservation)
- ✅ UCASE/LCASE preserve language tags
- ✅ SUBSTR preserves language tags
- ✅ Hash functions (MD5, SHA1, SHA256, SHA384, SHA512) all pass
- ✅ Added `GetLangTagOrDatatype()` method to Value struct

**Current failing function tests (~18 total):**

| Category | Failing Tests | Notes |
|----------|--------------|-------|
| String (~8) | STRBEFORE/STRAFTER datatyping (2), CONCAT (2), REPLACE (2), non-BMP (2) | Argument compatibility rules |
| RDF Terms (~6) | IRI/URI edge cases (2), UUID (2), STRUUID (1), BNODE (1) | Pattern matching, type errors |
| DateTime (3) | NOW (1), TIMEZONE (1), TZ (1) | Format/binding issues |
| Error handling (~4) | IF (1), COALESCE (1), AVG error (2) | Error propagation |

**Root causes:**
1. STRBEFORE/STRAFTER datatyping: Argument compatibility rules for language tags and typed strings
2. REPLACE: Regex replacement not yet implemented in BindExpressionEvaluator
3. UUID/STRUUID: Pattern matching test expects specific UUID format validation
4. IF/COALESCE: Error propagation semantics

**Completed:**
- Basic CONCAT, SUBSTR, UCASE, LCASE work in FILTER and SELECT expressions
- CEIL, FLOOR, ROUND, ABS in BindExpressionEvaluator
- Language tag preservation for UCASE/LCASE/STRBEFORE/STRAFTER/SUBSTR
- Empty string result handling (quoted `""` for SELECT projections)

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
| 3 | SPARQL Functions | ~18 | ~10 | In Progress (was 46) |
| 4 | Property Paths | 0 | 0 | ✅ Done (was 6) |
| 5 | Subquery Scope | 1 (+2 skip) | 0 | ✅ Nearly Done |
| 6 | Negation (EXISTS/MINUS) | 0 | 0 | ✅ Done |
| 7 | VALUES Clause | 0 | 0 | ✅ Done |
| 8 | XSD Cast Functions | 0 | 0 | ✅ Done |

**Current Progress:** 43 failing tests total (174 passing, 7 skipped out of 224) — 78% conformance

**Recommended priority:**
1. **Phase 3** (Functions) - ~18 tests, STRBEFORE/STRAFTER datatyping, REPLACE, UUID/STRUUID

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

**Priority 1: Remaining Functions (~18 tests)**
Focus areas:
- STRBEFORE/STRAFTER datatyping (argument compatibility for language tags)
- REPLACE function implementation in BindExpressionEvaluator
- UUID/STRUUID pattern matching
- IF/COALESCE error propagation

**Recently Completed (2026-01-22):**
- ✅ Property paths: pp16 (zero-or-more reflexive for all graph nodes), pp28a (grouped zero-or-one)
- ✅ ORDER BY: Fixed term type ordering (IRIs < Literals per SPARQL spec)
- ✅ String functions: UCASE/LCASE/STRBEFORE/STRAFTER/SUBSTR language tag preservation
- ✅ Hash functions: MD5, SHA1, SHA256, SHA384, SHA512 all pass
- ✅ Empty string handling for STRBEFORE/STRAFTER

**Previously Completed:**
- ✅ Negation/EXISTS (12/12 tests) - GRAPH context, MINUS semantics
- ✅ pp30-pp33 property path grouping and sequences (2026-01-21)
- ✅ pp06, pp07, pp34, pp35 named graph paths (2026-01-21)

## Out of Scope

The following are intentionally not addressed (per SkipList):
- RDF 1.2 features (VERSION, annotations, base direction)
- Entailment regimes
- Graph Store HTTP Protocol
- SPARQL Protocol server tests
- SERVICE requiring network
- NFC normalization on IRIs
