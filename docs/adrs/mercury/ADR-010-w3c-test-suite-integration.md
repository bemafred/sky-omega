# ADR-010: W3C Test Suite Integration

## Status

In Progress

## Problem

Mercury implements multiple W3C specifications but lacks conformance testing against official test suites:

1. **Unknown conformance gaps** - Custom tests may miss edge cases that W3C tests cover
2. **Specification ambiguity** - W3C tests are the canonical interpretation of spec language
3. **Regression risk** - No way to verify continued compliance as code evolves
4. **Interoperability concerns** - Other RDF tools expect W3C-compliant behavior

Current test coverage is extensive (~70 test files) but entirely hand-written. W3C provides comprehensive test suites that other implementations use for conformance validation.

## W3C Test Resources

All test suites are maintained at [github.com/w3c/rdf-tests](https://github.com/w3c/rdf-tests) under W3C Test Suite License.

### Available Test Suites

| Test Suite        | Manifest Location                                                | Mercury Component               | Priority |
|-------------------|:-----------------------------------------------------------------|---------------------------------|----------|
| N-Triples 1.2     | `rdf/rdf12/rdf-n-triples/`                                       | `NTriplesStreamParser`          | High     |
| N-Quads 1.2       | `rdf/rdf12/rdf-n-quads/`                                         | `NQuadsStreamParser`            | High     |
| Turtle 1.2        | `rdf/rdf12/rdf-turtle/`                                          | `TurtleStreamParser`            | High     |
| TriG 1.2          | `rdf/rdf12/rdf-trig/`                                            | `TriGStreamParser`              | High     |
| RDF/XML 1.1       | `rdf/rdf11/rdf-xml/`                                             | `RdfXmlStreamParser`            | Medium   |
| SPARQL 1.1 Query  | `sparql/sparql11/`                                               | `SparqlParser`, `QueryExecutor` | Critical |
| SPARQL 1.1 Update | `sparql/sparql11/`                                               | `UpdateExecutor`                | Critical |
| RDF-star          | [w3c/rdf-star](https://w3c.github.io/rdf-star/tests/)            | SPARQL-star support             | High     |
| JSON-LD 1.1       | [json-ld-api/tests](https://w3c.github.io/json-ld-api/tests/)    | `JsonLdStreamParser`            | Medium   |

### Test Suite Structure

Each suite uses a `manifest.ttl` file describing tests:

```turtle
@prefix mf: <http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#> .
@prefix rdft: <http://www.w3.org/ns/rdftest#> .

<#test-name> a rdft:TestTurtlePositiveSyntax ;
    mf:name "test-name" ;
    rdfs:comment "Description of what this tests" ;
    mf:action <test-file.ttl> ;
    mf:result <expected-output.nt> .
```

**Test types:**
- `PositiveSyntax` - Parser must accept without error
- `NegativeSyntax` - Parser must reject with error
- `PositiveEval` - Parse and compare output to expected N-Triples
- `NegativeEval` - Parse succeeds but evaluation must fail/differ

## Solution

### Repository Integration

**Option A: Git Submodule (Recommended)**

```bash
git submodule add https://github.com/w3c/rdf-tests.git tests/w3c-rdf-tests
```

Advantages:
- Version-controlled, reproducible
- Clear provenance for compliance claims
- Easy updates via `git submodule update --remote`

Disadvantages:
- Adds ~50MB to fresh clones
- Requires `--recurse-submodules` for CI

**Option B: Build-time Download**

```xml
<!-- In Mercury.Tests.csproj -->
<Target Name="DownloadW3CTests" BeforeTargets="Build" Condition="!Exists('$(W3CTestPath)')">
  <Exec Command="curl -L https://github.com/w3c/rdf-tests/archive/main.zip -o w3c-tests.zip" />
  <Unzip SourceFiles="w3c-tests.zip" DestinationFolder="$(W3CTestPath)" />
</Target>
```

Advantages:
- No submodule complexity
- Can selectively download only needed suites

Disadvantages:
- Network dependency during build
- Version pinning requires manual management

### Test Infrastructure

#### Manifest Parser

Parse W3C manifest files to generate test cases:

```csharp
/// <summary>
/// Parses W3C test manifest files (Turtle format) into test case descriptors.
/// </summary>
public sealed class W3CManifestParser
{
    public record TestCase(
        string Name,
        string Comment,
        TestType Type,
        string ActionPath,
        string? ResultPath);

    public enum TestType
    {
        PositiveSyntax,
        NegativeSyntax,
        PositiveEval,
        NegativeEval,
        QueryEval,
        UpdateEval
    }

    public IEnumerable<TestCase> Parse(string manifestPath)
    {
        // Use TurtleStreamParser to read manifest
        // Extract test entries from mf:entries collection
        // Resolve relative paths against manifest location
    }
}
```

#### xUnit Integration

Use `[Theory]` with `[MemberData]` for dynamic test generation:

```csharp
public class W3CTurtleTests
{
    private static readonly string ManifestPath =
        Path.Combine(TestContext.W3CTestsRoot, "rdf/rdf12/rdf-turtle/manifest.ttl");

    public static IEnumerable<object[]> GetSyntaxTests() =>
        new W3CManifestParser()
            .Parse(ManifestPath)
            .Where(t => t.Type is TestType.PositiveSyntax or TestType.NegativeSyntax)
            .Select(t => new object[] { t.Name, t.ActionPath, t.Type });

    public static IEnumerable<object[]> GetEvalTests() =>
        new W3CManifestParser()
            .Parse(ManifestPath)
            .Where(t => t.Type == TestType.PositiveEval)
            .Select(t => new object[] { t.Name, t.ActionPath, t.ResultPath! });

    [Theory]
    [MemberData(nameof(GetSyntaxTests))]
    public async Task SyntaxTest(string name, string actionPath, TestType type)
    {
        var content = await File.ReadAllTextAsync(actionPath);
        var parsed = false;
        Exception? error = null;

        try
        {
            await TurtleStreamParser.ParseAsync(content, (s, p, o) => { });
            parsed = true;
        }
        catch (Exception ex) { error = ex; }

        if (type == TestType.PositiveSyntax)
            Assert.True(parsed, $"Should parse: {error?.Message}");
        else
            Assert.False(parsed, "Should reject invalid syntax");
    }

    [Theory]
    [MemberData(nameof(GetEvalTests))]
    public async Task EvalTest(string name, string actionPath, string resultPath)
    {
        var triples = new List<(string S, string P, string O)>();
        await TurtleStreamParser.ParseAsync(
            await File.ReadAllTextAsync(actionPath),
            (s, p, o) => triples.Add((s.ToString(), p.ToString(), o.ToString())));

        var expected = await ParseNTriples(resultPath);
        AssertTriplesEquivalent(expected, triples);
    }
}
```

#### Blank Node Isomorphism

W3C evaluation tests require blank node isomorphism checking:

```csharp
/// <summary>
/// Compares two sets of triples for equivalence, treating blank nodes
/// as existentially quantified variables (graph isomorphism).
/// </summary>
public static class BlankNodeIsomorphism
{
    public static bool AreIsomorphic(
        IReadOnlyList<Triple> actual,
        IReadOnlyList<Triple> expected)
    {
        if (actual.Count != expected.Count)
            return false;

        // Build candidate mappings for blank nodes
        // Use backtracking search to find valid mapping
        // Two graphs are isomorphic if a consistent mapping exists
    }
}
```

### Directory Structure

```
tests/
├── Mercury.Tests/
│   ├── W3C/
│   │   ├── W3CManifestParser.cs       # Manifest Turtle parser
│   │   ├── BlankNodeIsomorphism.cs    # Graph comparison
│   │   ├── W3CTestContext.cs          # Path resolution, skip lists
│   │   ├── NTriples/
│   │   │   └── W3CNTriplesTests.cs
│   │   ├── Turtle/
│   │   │   └── W3CTurtleTests.cs
│   │   ├── NQuads/
│   │   │   └── W3CNQuadsTests.cs
│   │   ├── TriG/
│   │   │   └── W3CTriGTests.cs
│   │   ├── Sparql/
│   │   │   ├── W3CSparqlSyntaxTests.cs
│   │   │   ├── W3CSparqlQueryTests.cs
│   │   │   └── W3CSparqlUpdateTests.cs
│   │   └── RdfStar/
│   │       └── W3CRdfStarTests.cs
│   └── ... (existing tests)
└── w3c-rdf-tests/                      # Submodule
    ├── rdf/
    │   ├── rdf11/
    │   └── rdf12/
    └── sparql/
        ├── sparql10/
        └── sparql11/
```

### SPARQL Test Execution

SPARQL tests require additional infrastructure:

```csharp
public class W3CSparqlQueryTests
{
    [Theory]
    [MemberData(nameof(GetQueryEvalTests))]
    public async Task QueryEvalTest(string name, string queryPath, string dataPath, string resultPath)
    {
        // 1. Load data into QuadStore
        using var store = CreateTempStore();
        await LoadData(store, dataPath);

        // 2. Execute query
        var query = await File.ReadAllTextAsync(queryPath);
        var executor = new QueryExecutor(store, query);
        var results = executor.Execute();

        // 3. Compare results
        var expected = await LoadExpectedResults(resultPath);
        AssertResultsEquivalent(expected, results);
    }
}
```

**SPARQL result comparison:**
- SELECT: Compare solution sequences (order matters with ORDER BY)
- ASK: Compare boolean result
- CONSTRUCT/DESCRIBE: Use blank node isomorphism on output graph

### Skip Lists and Known Failures

Not all W3C tests may be applicable or passable initially:

```csharp
public static class W3CTestContext
{
    /// <summary>
    /// Tests to skip with documented reasons.
    /// </summary>
    public static readonly Dictionary<string, string> SkipList = new()
    {
        // Features not yet implemented
        ["sparql11/entailment/rdf01"] = "RDFS entailment not supported",
        ["sparql11/entailment/owl01"] = "OWL entailment regime not supported",

        // Spec interpretation differences
        ["turtle/localname_with_nfc"] = "NFC normalization not enforced",

        // Known bugs (tracked as issues)
        ["ntriples/nt-syntax-bnode-01"] = "Issue #123: blank node label edge case"
    };

    public static bool ShouldSkip(string testName, out string reason)
    {
        return SkipList.TryGetValue(testName, out reason!);
    }
}
```

## Implementation Phases

### Phase 1: Infrastructure (Week 1)

1. Add `w3c-rdf-tests` as git submodule
2. Implement `W3CManifestParser` using existing `TurtleStreamParser`
3. Implement `BlankNodeIsomorphism` for evaluation tests
4. Create `W3CTestContext` with path resolution

**Success criteria:**
- [ ] Can parse any W3C manifest file
- [ ] Blank node isomorphism passes with known-equivalent graphs

### Phase 2: Parser Conformance (Week 2)

1. N-Triples tests (~90 tests)
2. N-Quads tests (~90 tests)
3. Turtle tests (~300+ tests)
4. TriG tests (~250+ tests)

**Success criteria:**
- [x] N-Triples: 100% pass rate (70/70)
- [x] N-Quads: 100% pass rate (87/87)
- [x] Turtle: 100% pass rate (309/309)
- [x] TriG: 100% pass rate (352/352)

### Phase 3: SPARQL Conformance (Week 3-4)

1. SPARQL syntax tests (positive + negative)
2. SPARQL query evaluation tests
3. SPARQL update evaluation tests

**Priority categories:**

| Category       | Test Count (approx) | Priority |
|----------------|---------------------|----------|
| Aggregates     | ~30                 | High     |
| Property paths | ~40                 | High     |
| Subquery       | ~20                 | High     |
| Functions      | ~100                | Medium   |
| Update basic   | ~30                 | High     |
| Update silent  | ~10                 | Low      |

**Success criteria:**
- [ ] Syntax tests: 100% pass rate
- [ ] Query evaluation: >90% pass rate
- [ ] Update evaluation: >85% pass rate
- [ ] All failures documented in skip list with reasons

### Phase 4: RDF-star and JSON-LD (Optional)

1. RDF-star Turtle/TriG syntax
2. RDF-star SPARQL evaluation
3. JSON-LD toRdf/fromRdf tests

**Success criteria:**
- [ ] RDF-star: >90% pass rate
- [x] JSON-LD toRdf: 100% pass rate (461/461 applicable tests)

**JSON-LD Current Status (January 2026):**

| Metric    | Value    |
|-----------|----------|
| Passed    | 461      |
| Failed    | 0        |
| Skipped   | 6        |
| Pass Rate | **100%** |

Skipped tests (out of scope - not JSON-LD 1.1 features):
- 4 tests require `specVersion: json-ld-1.0` (legacy behavior superseded by 1.1)
- 2 tests require `produceGeneralizedRdf: true` (blank node predicates - non-standard RDF)

## CI Integration

```yaml
# .github/workflows/w3c-conformance.yml
name: W3C Conformance

on:
  push:
    branches: [main]
  pull_request:

jobs:
  conformance:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Run W3C Tests
        run: dotnet test --filter "Category=W3C"

      - name: Generate Conformance Report
        run: dotnet run --project tools/ConformanceReport

      - name: Upload Report
        uses: actions/upload-artifact@v4
        with:
          name: w3c-conformance-report
          path: conformance-report.md
```

## Expected Findings

Based on typical edge cases in W3C tests:

| Area | Likely Issues |
|------|---------------|
| Unicode | Surrogate pairs, multi-byte escapes, NFC normalization |
| Whitespace | Comments in unexpected positions, line endings |
| Numeric literals | Precision loss, exponent handling, INF/NaN |
| Blank nodes | Label character restrictions, scoping rules |
| IRIs | Escape sequences, relative resolution, punycode |
| FILTER | Error propagation, type promotion, effective boolean value |
| Property paths | Zero-length paths, cycles, negated property sets |
| Aggregation | NULL handling, GROUP BY with expressions |

## SPARQL Query Test Skipping (January 2026)

The query executor lacks `CancellationToken` support in its core loops, causing certain test categories to hang indefinitely. Until cancellation is implemented, these categories are skipped:

| Category | Tests | Reason |
|----------|------:|--------|
| aggregates/ | 42 | Subquery aggregation not implemented |
| property-path/ | 31 | Transitive paths can loop indefinitely |
| negation/ | 12 | Complex MINUS/NOT EXISTS patterns timeout |
| subquery/ | 14 | Can create unbounded cartesian products |
| exists/ | 6 | EXISTS patterns can be slow |
| **Total skipped** | **~105** | |

**Root cause:** 11+ `while (true)` loops in `Operators.cs` and `QueryResults.cs` never check for cancellation. The test timeout mechanism sets a `CancellationToken`, but the execution code ignores it.

**Fix required:** Thread `CancellationToken` through all operators and add checks in every loop. This is a significant refactor (~1500 lines affected).

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Large test count slows CI | Run full suite nightly; PR runs subset |
| Submodule adds clone time | Document `--depth 1` for shallow clones |
| Test interpretation disputes | Reference W3C errata and mailing lists |
| Spec version mismatch | Target RDF 1.2 / SPARQL 1.1 as baseline |
| Maintenance burden | Automate report generation; track skip list |

## Success Criteria

### Infrastructure
- [ ] Submodule added and documented
- [ ] Manifest parser handles all W3C manifest formats
- [ ] Blank node isomorphism correctly identifies equivalent graphs
- [ ] CI runs W3C tests on every PR

### Conformance Targets

| Suite | Target Pass Rate | Actual | Notes |
|-------|------------------|--------|-------|
| N-Triples | 100% | **100%** | 70/70 passed |
| N-Quads | 100% | **100%** | 87/87 passed |
| Turtle | >95% | **100%** | 309/309 passed |
| TriG | >95% | **100%** | 352/352 passed |
| RDF/XML | >90% | **100%** | 166/166 passed |
| JSON-LD toRdf | >80% | **100%** | 461/461 (6 skipped: 1.0-only, generalized RDF) |
| SPARQL Syntax (positive) | 100% | **100%** | 63/63 passed |
| SPARQL Syntax (negative) | 100% | 25% | 10/40 passed (parser too permissive) |
| SPARQL Query | >90% | - | 221 total, ~105 skipped (see below), ~116 runnable |
| SPARQL Update | >85% | **100%** | 94/94 passed |
| RDF-star | >90% | - | After Phase 4 |
| **Total** | | **98%** | 1,612/1,642 (excluding Query in progress; 6 JSON-LD 1.0/generalized excluded) |

### Documentation
- [ ] Conformance report published with each release
- [ ] Skip list with justifications for each skipped test
- [ ] Known limitations documented in README

## References

- [W3C RDF Test Suites](https://w3c.github.io/rdf-tests/)
- [SPARQL 1.1 Test Case Structure](https://www.w3.org/2009/sparql/docs/tests/README.html)
- [RDF-star Test Suite](https://w3c.github.io/rdf-star/tests/)
- [JSON-LD Test Suite](https://w3c.github.io/json-ld-api/tests/)
- [GitHub: w3c/rdf-tests](https://github.com/w3c/rdf-tests)
