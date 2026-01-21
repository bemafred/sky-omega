# ADR-012: W3C SPARQL Conformance Fix Plan

**Status:** In Progress
**Created:** 2026-01-19
**Updated:** 2026-01-21
**Baseline:** 1904 W3C tests total, 1791 passing (94%), 97 failing, 16 skipped
**Current:** 3795 tests total, 3686 passing (97%), 93 failing, 16 skipped

## Context

Mercury's SPARQL engine has achieved 94% W3C conformance. The remaining 97 failing SPARQL Query tests cluster around specific areas:

| Category | Failures | Root Cause |
|----------|----------|------------|
| Property Paths | 18 | Zero-or-more (*), negated property sets, grouped paths |
| String Functions | 23 | STRLEN, SUBSTR, STRAFTER, STRBEFORE, CONCAT, ENCODE_FOR_URI, REPLACE |
| Hash Functions | 10 | MD5, SHA1, SHA256, SHA384, SHA512 (Unicode handling) |
| RDF Term Functions | 10 | STRDT, STRLANG, BNODE, IRI/URI, UUID/STRUUID |
| DateTime Functions | 3 | NOW, TIMEZONE, TZ |
| MINUS/NOT EXISTS | 8 | Complex patterns, GRAPH interaction |
| Aggregates DISTINCT | 6 | DISTINCT with GROUP BY |
| GROUP BY / HAVING | 3 | Built-in functions in GROUP BY, HAVING conditions |
| EXISTS edge cases | 4 | GRAPH context, external bindings |
| VALUES | 2 | Inline VALUES, post-subquery VALUES |
| Other | 10 | BIND scoping, IF/COALESCE error cases, IN/NOT IN |

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

### Phase 3: SPARQL Functions — 46 tests failing
**Target:** Reduce function failures from 46 to ~10
**Effort:** Medium-Large
**Files:** `FilterEvaluator.Functions.cs`, `BindExpressionEvaluator.cs`
**Updated:** 2026-01-20

**Current failing function tests (46 total):**

| Category | Failing Tests | Notes |
|----------|--------------|-------|
| String (23) | STRLEN (2), SUBSTR (4), STRSTARTS (1), STRAFTER (2), STRBEFORE (2), UCASE (1), LCASE (1), CONCAT (2), ENCODE_FOR_URI (2), REPLACE (2) | Non-BMP Unicode handling |
| Hash (10) | MD5 (2), SHA1 (2), SHA256 (2), SHA384 (2), SHA512 (2) | Unicode input handling |
| RDF Terms (10) | STRDT (2), STRLANG (2), BNODE (2), IRI/URI (2), UUID (2), STRUUID (1) | Type error handling |
| DateTime (3) | NOW (1), TIMEZONE (1), TZ (1) | Format/binding issues |

**Root causes:**
1. Non-BMP Unicode (surrogate pairs) not handled correctly in string functions
2. Hash functions may have encoding issues with Unicode input
3. Type error propagation in STRDT/STRLANG
4. UUID/STRUUID not yet implemented in BIND expressions

**Previously completed:**
- Basic CONCAT, SUBSTR, UCASE, LCASE work in FILTER expressions
- CEIL, FLOOR, ROUND, ABS added to BindExpressionEvaluator

**Verification:**
```bash
dotnet test --filter "FullyQualifiedName~Sparql11_QueryEval" tests/Mercury.Tests
```

---

### Phase 4: Property Path Parsing — 18 tests failing
**Target:** Reduce property path failures from 18 to ~5
**Effort:** Medium-Large
**Files:** `SparqlParser.cs`, `Operators.cs`
**Updated:** 2026-01-20

**Failing property path tests (18 total):**

| Test | Issue |
|------|-------|
| pp02 Star path (*) | Zero-or-more semantics on empty result |
| pp07, pp09 | Reverse/sequence path combinations |
| pp10 | Path with negation |
| pp12, pp14, pp16 | Variable length / star paths |
| pp28a | Grouped path with modifier (:p/:p)? |
| pp30, pp32, pp33 | Operator precedence issues |
| pp34, pp35 | Named graph paths |
| pp36 | Arbitrary path with bound endpoints |
| Negated Property Sets (4) | inverse, direct+inverse, 'a', '^a' |
| Zero-or-x start constant (2) | * and ? on empty dataset |

**Root causes:**
1. Zero-or-more (*) and zero-or-one (?) need to emit reflexive bindings even on empty data
2. Negated property set parsing doesn't handle mixed direct/inverse correctly
3. Grouped paths with modifiers like `(p1/p2)?` need special handling
4. Named graph context lost in some path operators

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

### Phase 6: Negation (NOT EXISTS, MINUS) — 8 tests failing
**Target:** Reduce negation failures from 8 to ~2
**Effort:** Medium
**Files:** `Operators.cs` (MINUS operator), `FilterEvaluator.cs`
**Updated:** 2026-01-20

**Failing negation tests (8 total):**

| Test | Issue |
|------|-------|
| Exists within graph pattern | GRAPH context not propagated to EXISTS |
| GRAPH variable inside EXISTS bound to external | External binding scope |
| Subsets by exclusion (NOT EXISTS) | Set operation pattern |
| Subsets by exclusion (MINUS) | Set operation pattern |
| Medical temporal proximity (NOT EXISTS) | Complex NOT EXISTS pattern |
| MINUS from fully/partially bound minuend (2) | Bound minuend handling |
| outer GRAPH operator MINUS disjointness | GRAPH + MINUS interaction |
| Positive EXISTS 1 | Edge case |

**Root causes:**
1. GRAPH context not properly passed into EXISTS/NOT EXISTS evaluation
2. MINUS operator doesn't handle some binding patterns correctly
3. External variable bindings not visible inside EXISTS

**Note:** pp10 (Path with negation) is tracked in Phase 4 (Property Paths).

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
| 3 | SPARQL Functions | 46 | ~10 | In Progress |
| 4 | Property Paths | 18 | ~5 | In Progress |
| 5 | Subquery Scope | 1 (+2 skip) | 0 | ✅ Nearly Done |
| 6 | Negation (EXISTS/MINUS) | 8 | ~2 | In Progress |
| 7 | VALUES Clause | 0 | 0 | ✅ Done |
| 8 | XSD Cast Functions | 0 | 0 | ✅ Done |

**Current Progress:** 95 failing tests total (120 passing, 9 skipped out of 224)

**Recommended priority:**
1. **Phase 6** (Negation) - 8 tests, GRAPH context issue is key
2. **Phase 4** (Property Paths) - 18 tests, complex parser work
3. **Phase 3** (Functions) - 46 tests, mostly Unicode edge cases

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

**Priority 1: Negation/EXISTS (8 tests)**
```bash
dotnet test --filter "Name~exists" tests/Mercury.Tests
dotnet test --filter "Name~MINUS" tests/Mercury.Tests
```
Key issue: GRAPH context propagation into EXISTS evaluation.

**Priority 2: Property Paths (18 tests)**
```bash
dotnet test --filter "Name~pp" tests/Mercury.Tests
```
Focus on zero-or-more/zero-or-one reflexive bindings on empty data.

**Priority 3: Functions (46 tests)**
Most failures are Unicode edge cases (non-BMP characters). Lower priority as core functionality works.

## Out of Scope

The following are intentionally not addressed (per SkipList):
- RDF 1.2 features (VERSION, annotations, base direction)
- Entailment regimes
- Graph Store HTTP Protocol
- SPARQL Protocol server tests
- SERVICE requiring network
- NFC normalization on IRIs
