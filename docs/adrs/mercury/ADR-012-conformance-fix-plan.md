# ADR-012: W3C SPARQL Conformance Fix Plan

**Status:** Proposed
**Created:** 2026-01-19
**Baseline:** 3768 tests total, 3583 passing (95.1%), 171 failing, 14 ignored

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

### Phase 1: Numeric Aggregates (Quick Win)
**Target:** ~15-20 tests
**Effort:** Small
**Files:** `QueryResults.Modifiers.cs`

**Issue:** SUM, AVG, MIN, MAX fail on decimal/double data because:
1. Numeric type promotion isn't handling `xsd:decimal` and `xsd:double` correctly
2. Aggregates return wrong type or string representation

**Fix:**
- Audit `ComputeAggregates()` in `QueryResults.Modifiers.cs`
- Ensure proper type promotion: int → decimal → double
- Return correct XSD datatype in result

**Verification:**
```bash
dotnet test --filter "Name~SUM" tests/Mercury.Tests
dotnet test --filter "Name~AVG" tests/Mercury.Tests
dotnet test --filter "Name~MIN" tests/Mercury.Tests
dotnet test --filter "Name~MAX" tests/Mercury.Tests
```

---

### Phase 2: GROUP_CONCAT Edge Cases
**Target:** ~5 tests
**Effort:** Small
**Files:** `QueryResults.Modifiers.cs`

**Issue:** GROUP_CONCAT with subqueries produces wrong row counts.

Example failing test (`agg-groupconcat-2`):
```sparql
SELECT (COUNT(*) AS ?c) {
    {SELECT ?p (GROUP_CONCAT(?o) AS ?g) WHERE { [] ?p ?o } GROUP BY ?p}
    FILTER(...)
}
```
Expected: 1 row, Actual: 2 rows

**Fix:**
- The outer COUNT(*) isn't properly counting filtered subquery results
- Need to verify subquery result materialization before outer aggregation

**Verification:**
```bash
dotnet test --filter "Name~GROUP_CONCAT" tests/Mercury.Tests
```

---

### Phase 3: SPARQL Functions
**Target:** ~30-40 tests (subset of 73)
**Effort:** Medium
**Files:** `FilterEvaluator.Functions.cs`

**Priority functions (highest impact):**

| Function | Tests | Issue |
|----------|-------|-------|
| MINUTES, SECONDS, HOURS | ~10 | DateTime extraction |
| ENCODE_FOR_URI | ~3 | IRI encoding |
| STRBEFORE, STRAFTER | ~5 | String boundary cases |
| REPLACE | ~5 | Regex replacement |
| IF, COALESCE | ~5 | Error propagation |

**Approach:**
1. Run function tests in isolation to identify exact failures
2. Fix one function family at a time
3. Add unit tests for edge cases discovered

**Verification:**
```bash
dotnet test --filter "FullyQualifiedName~functions" tests/Mercury.Tests
```

---

### Phase 4: Property Path Parsing
**Target:** ~10 tests (subset of 23)
**Effort:** Medium-Large
**Files:** `SparqlParser.cs`, `Operators.cs`

**Known parsing gaps:**
1. Negated property sets with both direct and inverse: `!(ex:p | ^ex:q)`
2. Complex nested alternatives: `(p1|p2)/(p3|p4)`
3. Modifiers on grouped paths: `(p1/p2)+`

**Approach:**
1. Audit `ParsePathSegment()` and `ParsePredicateOrPath()`
2. Add test cases for each syntax variant
3. Fix parsing before execution

**Verification:**
```bash
dotnet test --filter "Name~pp" tests/Mercury.Tests
```

---

### Phase 5: Subquery Scope
**Target:** ~12 tests
**Effort:** Medium
**Files:** `QueryExecutor.cs`, `BoxedSubQueryExecutor.cs`

**Issues:**
1. Variable projection from subquery to outer query
2. Aggregates in subqueries interacting with outer GROUP BY
3. BIND scope in nested groups (known: test_62a)

**Approach:**
1. Trace through `SubQueryScan` execution path
2. Verify variable binding tables are correctly scoped
3. Add explicit scope boundary handling

---

### Phase 6: Negation (NOT EXISTS, MINUS)
**Target:** ~11 tests
**Effort:** Medium
**Files:** `Operators.cs` (MINUS operator)

**Issues:**
1. MINUS with multiple patterns
2. NOT EXISTS with OPTIONAL inside
3. Blank node comparison semantics

---

### Phase 7: VALUES Clause
**Target:** ~10 tests
**Effort:** Small-Medium
**Files:** `SparqlParser.cs`

**Issues:**
1. Multi-variable VALUES: `VALUES (?x ?y) { (:a :b) (:c :d) }`
2. UNDEF handling in VALUES
3. VALUES in subqueries

---

## Execution Strategy

### Principles
1. **One phase at a time** - Complete and verify before moving on
2. **Tests drive fixes** - Run specific test filters, fix, verify
3. **No regressions** - Full test suite after each phase
4. **Document learnings** - Update CLAUDE.md with any new patterns

### Success Criteria per Phase

| Phase | Tests Fixed | Cumulative Pass Rate |
|-------|-------------|---------------------|
| 1 | +15 | 95.5% |
| 2 | +5 | 95.6% |
| 3 | +35 | 96.5% |
| 4 | +10 | 96.8% |
| 5 | +12 | 97.1% |
| 6 | +11 | 97.4% |
| 7 | +10 | 97.7% |

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

## Starting Point: Phase 1

**Recommended first action:** Investigate SUM aggregate failure.

```bash
# Get test details
cat tests/w3c-rdf-tests/sparql/sparql11/aggregates/agg-sum-01.rq
cat tests/w3c-rdf-tests/sparql/sparql11/aggregates/agg-numeric.ttl
```

The data has decimals `1.0, 2.2, 3.5` and query is `SELECT (SUM(?o) AS ?sum) WHERE { ?s :dec ?o }`.
Expected sum: `6.7` as `xsd:decimal`.

Debug path:
1. Check `QueryResults.Modifiers.cs` line ~370+ for `ComputeAggregates()`
2. Verify decimal parsing in literal handling
3. Verify sum computation and result type

## Out of Scope

The following are intentionally not addressed (per SkipList):
- RDF 1.2 features (VERSION, annotations, base direction)
- Entailment regimes
- Graph Store HTTP Protocol
- SPARQL Protocol server tests
- SERVICE requiring network
- NFC normalization on IRIs
