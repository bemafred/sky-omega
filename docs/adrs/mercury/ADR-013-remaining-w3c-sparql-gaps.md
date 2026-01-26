# ADR-013: Remaining W3C SPARQL Conformance Gaps

## Status

Proposed (January 2026)

## Context

Sky Omega v0.6.1 achieved 100% W3C SPARQL 1.1 conformance for both Query (418/418) and Update (94/94) test suites. However, three high-complexity tests remain intentionally skipped due to architectural constraints and implementation complexity.

This ADR documents these gaps, analyzes their requirements, and establishes criteria for future implementation while respecting Mercury's hard constraints around memory safety and zero-GC design.

### Hard Constraints (Non-Negotiable)

Per [ADR-003](ADR-003-buffer-pattern.md), [ADR-009](ADR-009-stack-overflow-mitigation.md), and [ADR-011](ADR-011-queryresults-stack-reduction.md):

1. **Stack Safety**: Large ref structs (~22KB for QueryResults) must not cause stack overflow on Windows (1MB stack limit)
2. **No Unbounded Recursion**: Recursive algorithms must have explicit depth limits or use iterative alternatives
3. **Zero-GC for Simple Queries**: Common query patterns must remain allocation-free
4. **Materialization for Complexity**: Complex paths may heap-allocate via `List<MaterializedRow>` pattern
5. **No Thread Hacks**: Never spawn threads solely for stack space

### Current Skip List

```csharp
// tests/Mercury.Tests/W3C/W3CTestContext.cs
public static readonly Dictionary<string, string> KnownFailures = new()
{
    ["constructlist"] = "RDF collection construction in CONSTRUCT not implemented",
    ["agg-empty-group-count-graph"] = "COUNT without GROUP BY inside GRAPH not implemented",
    ["bindings/manifest#graph"] = "VALUES inside GRAPH binding same variable as graph name not implemented",
};
```

## Remaining Gaps Analysis

### Gap 1: `constructlist` — RDF Collection Syntax in CONSTRUCT Templates

**Test Query:**
```sparql
PREFIX : <http://example.org/>
CONSTRUCT { (?s ?o) :prop ?p } WHERE { ?s ?p ?o }
```

**Expected Output:**
```turtle
(:s1 :o1) :prop :p .
(:s2 :o1) :prop :p .
```

The `(?s ?o)` syntax is RDF collection shorthand, expanding to:
```turtle
_:b1 rdf:first :s1 ;
     rdf:rest [ rdf:first :o1 ; rdf:rest rdf:nil ] .
_:b1 :prop :p .
```

#### Requirements

| Requirement | Complexity | Memory Impact |
|-------------|------------|---------------|
| Parse `(...)` in CONSTRUCT template | Medium | Minimal (parse-time) |
| Generate fresh blank nodes per list element | Medium | Per-result allocation |
| Handle nested lists `((a b) c)` | High | Recursive structure |
| Track blank node scope per result row | High | State management |

#### Implementation Risks

1. **Unbounded Nesting**: Lists can nest arbitrarily deep `((((a))))` — requires iteration, not recursion
2. **Blank Node Generation**: Each result row needs unique blank nodes for its list structure
3. **Memory**: List expansion is multiplicative — `(?a ?b ?c)` creates 6 triples per input row

#### Proposed Approach

```csharp
// In ConstructResults.SubstituteTerm()
if (IsListPattern(term))
{
    // Use iterative list expansion with explicit depth limit
    const int MaxListDepth = 16;
    return ExpandListIteratively(term, bindings, MaxListDepth);
}
```

**Estimated Effort**: 2-3 days

---

### Gap 2: `agg-empty-group-count-graph` — Aggregate Subquery Inside GRAPH

**Test Query:**
```sparql
PREFIX : <http://example/>
SELECT ?g ?c WHERE {
   GRAPH ?g { SELECT (count(*) AS ?c) WHERE { ?s :p ?x } }
}
```

**Expected Result:**
| ?g | ?c |
|----|-----|
| `<empty.ttl>` | 0 |
| `<singleton.ttl>` | 1 |

Key behavior: Empty graphs must return `COUNT(*) = 0`, not be skipped.

#### Requirements

| Requirement | Complexity | Memory Impact |
|-------------|------------|---------------|
| Enumerate all named graphs | Low | Graph IRI iteration |
| Execute subquery per graph context | Medium | Per-graph executor |
| Handle empty group aggregation | High | Semantic complexity |
| Preserve `?g` binding through subquery | Medium | Binding propagation |

#### Implementation Risks

1. **Implicit Grouping**: `COUNT(*)` without `GROUP BY` creates a single implicit group — even with zero rows, it must return one row with count 0
2. **Graph Iteration**: Must enumerate ALL named graphs, not just those with matching triples
3. **Stack Pressure**: Per-graph subquery execution could create deep call chains

#### Current Behavior vs Required

```
Current:  Empty graph → no results (graph skipped)
Required: Empty graph → one row with ?c = 0
```

#### Proposed Approach

```csharp
// In ExecuteGraphClauses() when subquery has aggregate
if (subQuery.HasAggregates && !hasMatchingTriples)
{
    // Emit empty-group result: single row with aggregate defaults
    EmitEmptyGroupResult(graphIri, subQuery);
}
```

**Estimated Effort**: 3-4 days

---

### Gap 3: `bindings/manifest#graph` — VALUES Binding GRAPH Variable

**Test Query:**
```sparql
SELECT ?g ?t {
    GRAPH ?g {
        VALUES (?g ?t) { (UNDEF "foo") (<empty.ttl> "bar") }
    }
}
```

This tests the interaction between:
1. `GRAPH ?g` — iterate over named graphs
2. `VALUES (?g ...)` — inline bindings that include `?g`
3. `UNDEF` — unbound value in VALUES (wildcard)

#### Requirements

| Requirement | Complexity | Memory Impact |
|-------------|------------|---------------|
| VALUES inside GRAPH clause | Medium | Binding table |
| Same variable in GRAPH and VALUES | High | Scoping rules |
| UNDEF handling in VALUES | Medium | Null propagation |
| Correct join semantics | High | Variable unification |

#### Semantic Complexity

When `?g` appears in both GRAPH and VALUES:
- `VALUES (?g ?t) { (UNDEF "foo") }` — `?g` is unbound, should match ANY named graph
- `VALUES (?g ?t) { (<empty.ttl> "bar") }` — `?g` is bound, GRAPH should only look in `<empty.ttl>`

The variable `?g` has two roles:
1. **GRAPH iteration variable** — determines which graph to search
2. **VALUES binding** — may constrain or leave unbound

#### Current Behavior vs Required

```
Current:  VALUES bindings not propagated to GRAPH clause correctly
Required: ?g binding from VALUES constrains GRAPH; UNDEF means "any graph"
```

#### Proposed Approach

```csharp
// In ExecuteGraphClauses(), before graph iteration:
var graphConstraints = ExtractGraphConstraintsFromValues(pattern);
foreach (var graphIri in EnumerateNamedGraphs())
{
    if (!graphConstraints.Allows(graphIri))
        continue;
    // Execute with ?g bound to graphIri
}
```

**Estimated Effort**: 2-3 days

---

## Implementation Priority

| Gap | Real-World Usage | Spec Importance | Effort | Priority |
|-----|------------------|-----------------|--------|----------|
| `constructlist` | Rare | Medium | 2-3 days | Low |
| `agg-empty-group-count-graph` | Rare | High | 3-4 days | Medium |
| `bindings/manifest#graph` | Very Rare | Low | 2-3 days | Low |

**Recommendation**: Address `agg-empty-group-count-graph` first if pursuing full conformance, as empty-group aggregate semantics are more likely to affect real queries.

## Success Criteria

- [ ] All 3 tests pass in W3C conformance suite
- [ ] No stack overflow on Windows (1MB stack)
- [ ] No unbounded recursion (explicit depth limits)
- [ ] Simple queries remain zero-GC
- [ ] No regression in existing 418 passing tests

## Decision

**Defer implementation** until a concrete use case demands these features. The current 100% conformance on core SPARQL 1.1 Query and Update is sufficient for production use.

If implementation is pursued:
1. Implement one gap at a time with full test coverage
2. Run Windows CI to verify stack safety
3. Benchmark to ensure no performance regression
4. Update STATISTICS.md and CHANGELOG.md

## References

- [ADR-003: Buffer Pattern for Stack Safety](ADR-003-buffer-pattern.md) — Materialization pattern for complex paths
- [ADR-009: Stack Overflow Mitigation](ADR-009-stack-overflow-mitigation.md) — Platform stack limits and mitigations
- [ADR-011: QueryResults Stack Reduction](ADR-011-queryresults-stack-reduction.md) — Discriminated union approach
- [ADR-010: W3C Test Suite Integration](ADR-010-w3c-test-suite-integration.md) — Conformance testing infrastructure
- [W3C SPARQL 1.1 Query](https://www.w3.org/TR/sparql11-query/) — Specification reference
