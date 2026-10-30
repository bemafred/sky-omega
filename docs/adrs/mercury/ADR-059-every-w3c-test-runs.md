# ADR-059: Every W3C Test Runs — Each Manifest in the Test Data Is Loaded, and a Test That Cannot Pass Is a Listed Known Failure, Not an Absent One

## Status

**Status:** Proposed — 2026-10-30

Martin, 2026-10-30: *"That there are tests available that we do not run is a flaw. It should be fixed."* The
conformance counts in STATISTICS.md and the README are of the manifests the suite loads, and about 1,400 W3C tests in
the two submodules are in no loaded manifest. A test that is not loaded does not fail, is not skipped, and is not
counted: the conformance claim is silent about it.

## Context

### What the suite loads today

`W3CTestContext.GetManifestPath` maps each test kind to one manifest:

- the five RDF formats to the RDF 1.1 manifests (`rdf/rdf11/rdf-*`);
- SPARQL to `sparql/sparql10/manifest.ttl`, `sparql/sparql11/manifest-sparql11-query.ttl` and
  `manifest-sparql11-update.ttl`;
- JSON-LD to `tests/toRdf-manifest.jsonld`.

### What is not loaded

Counted from the manifests on 2026-10-30 (rdf-tests `d01b141`, json-ld-api `5551473`). The RDF 1.2 row is measured;
the others are counts of test entries in the manifests.

| Area | Manifest(s) | Tests | Why not loaded |
|------|-------------|------:|----------------|
| RDF 1.2: Turtle, TriG, N-Triples, N-Quads, RDF/XML | `rdf/rdf12/rdf-*` | 259 | Loaders point at `rdf11` |
| RDF 1.2: canonical N-Triples and N-Quads | `rdf/rdf12/rdf-n-*/c14n` | 43+ | No test kind for `TestNTriplesPositiveC14N` |
| RDF semantics (entailment) | `rdf/rdf11/rdf-mt`, `rdf/rdf12/rdf-semantics` | 95 | No test kind |
| SPARQL 1.2 | `sparql/sparql12` | ~271 | Not mapped |
| SPARQL 1.1 federation | `manifest-sparql11-fed.ttl` (`syntax-fed`, `service`) | 10 | Not mapped |
| SPARQL 1.1 result formats | `manifest-sparql11-results.ttl` (`csv-tsv-res`, `json-res`) | 11 | Not mapped |
| SPARQL 1.1 entailment, service description, protocol, Graph Store protocol | in `manifest-all.ttl` only | 121 | Not mapped |
| SPARQL 1.1 HTTP RDF update (the protocol's earlier draft) | `http-rdf-update` | 18 | In no umbrella manifest |
| JSON-LD fromRdf | `fromRdf-manifest.jsonld` | 54 | Not mapped |
| JSON-LD expand, compact, flatten, HTML, remote documents | five manifests | 758 | Not mapped |

Two more ways a test does not count:

- **A test kind the parser does not recognize** is dropped when the manifest is read; the canonical N-Triples tests
  are an example.
- **A test that returns early** reports passed without testing. Three measurement classes in
  `tests/Mercury.Tests/Compression/` (`LargeFixtureMeasurement`, `ConvertPathThroughputMeasurement`,
  `ParallelBZip2ContentionTrace`) return when `/tmp/multiblock-large.txt.bz2` is missing, which it is everywhere but
  on the machine that made it.

### The RDF 1.2 manifests include the RDF 1.1 ones

Each `rdf/rdf12/rdf-*/manifest.ttl` includes its 1.2 tests and `../../rdf11/rdf-*/manifest.ttl`: "Conformance with
RDF 1.2 specifications can be determined via successfully running the tests for relevant specifications along with the
relevant RDF 1.1 tests." Loading the 1.2 manifest instead of the 1.1 one runs every test once, with no second copy of
the test methods.

## Decision

### D1. Each test kind loads the umbrella manifest that covers it

The RDF formats load `rdf/rdf12/rdf-*`; SPARQL loads `sparql/sparql12/manifest.ttl` and
`sparql/sparql11/manifest-all.ttl` (with `sparql10`); JSON-LD loads each of its manifests. The test methods are named
for the format, not a version (`Turtle_Eval`, not `Turtle11_Eval`).

### D2. A test is passed, failed, a known failure, or does not apply — never absent

A test Mercury cannot pass yet is an entry in `W3CTestContext.KnownFailures`, by its exact test IRI, with its cause and
the ADR or limit that fixes it; it is reported as skipped, and `W3C_RUN_KNOWN_FAILURES=1` runs it. A test that does
not apply by its own terms (the JSON-LD tests with `specVersion` json-ld-1.0) is listed with that reason. Each entry
is removed when its test passes.

### D3. A test that cannot run here reports skipped, with the reason

Not passed. This covers tests that need the network or a fixture that is not present.

### D4. A guard test lists every manifest and checks that a suite loads it

It walks the two submodules for manifest files and fails on one that no suite loads, so a submodule update cannot add
tests that silently never run.

### D5. The counts follow

STATISTICS.md and the README count every loaded test: passed, known failures and tests that do not apply, by suite.

## Phases

1. **RDF 1.2 formats.** The five format loaders take the 1.2 manifests. Measured 2026-10-30: 1,252 tests, 1,106 pass,
   146 fail. Every failure is an RDF 1.2 feature: triple terms `<<( s p o )>>` outside Turtle (and Turtle's eval
   tests, whose expected N-Triples hold triple terms), reified triples and reifiers `<< s p o ~ r >>`, annotations
   `{| … |}`, base direction `"x"@en--ltr`, `VERSION`, and RDF/XML's `rdf:annotation`, `rdf:dir` and triple terms. They
   become known failures. The canonical N-Triples and N-Quads tests get their test kind (`CanonicalForm`): 82 tests,
   48 pass, 34 fail. With both, the format suites run 1,334 tests: 1,154 pass and 180 are known failures.

   The canonical-form tests found three bugs, listed as known failures until they are fixed:
   - **The N-Triples parser decodes UTF-8 as Latin-1**, one character per byte: `café` is stored as `cafÃ©`. Every
     non-ASCII literal or IRI loaded from N-Triples is wrong, `latest-truthy.nt.bz2` included. The N-Quads parser,
     a separate copy of nearly the same grammar, decodes it correctly.
   - **The N-Quads writer does not escape `"` and `\`** in a literal, so it writes `"x"y"`, which is not N-Quads.
     The N-Triples writer escapes both.
   - **Both parsers reject whitespace** between a literal and its language tag or datatype.

   The other canonical-form failures are rules the writers do not follow (`\b` and `\f`, non-ASCII written as
   `\uXXXX`), term identity that ADR-053 decides (language-tag case, `^^xsd:string`), and RDF 1.2 triple terms.
2. **RDF 1.2 features**, to fix phase 1's known failures. Its own ADR: the parsers, the writers, and how the store
   holds triple terms and directional language strings.
3. **SPARQL 1.2**: load, triage, list.
4. **SPARQL 1.1, the rest**: federation (SERVICE against local endpoints built from each test's data), result
   formats, service description and protocol (against `SparqlHttpServer`), Graph Store protocol, entailment.
5. **RDF semantics** entailment tests.
6. **JSON-LD**: fromRdf, then the API manifests per Q1.
7. **The measurement classes** report skipped without their fixture (D3), or move to `benchmarks/`.
8. **The guard** (D4), once every manifest is loaded.

## Open questions

- **Q1. JSON-LD API scope.** Mercury converts JSON-LD to and from RDF. Do expand, compact, flatten, HTML and remote
  documents (758 tests) apply, making Mercury a JSON-LD processor, or are they listed as not applicable?
- **Q2. Graph Store protocol** (14 tests, plus 18 for its earlier draft): implement it in `SparqlHttpServer`, or not
  applicable?
- **Q3. Entailment.** SPARQL 1.1 entailment regimes (70) and the RDF semantics tests (95): implement RDF and RDFS
  entailment, or not applicable? Mercury has an OWL/RDFS reasoner (`src/Mercury/Owl/`).
- **Q4. RDF 1.2 storage.** Phase 2 has to decide how a triple term and a base direction are stored and matched; that
  belongs to its own ADR.

## Consequences

- Known failures go from 6 to 186 at phase 1, and rise as each phase loads more; each is a listed, countable gap.
- The conformance counts grow to the whole test data and stop changing meaning when a submodule updates.
- The suite takes longer to run.

## Validation plan

1. Phase 1: the RDF format suites report 1,334 tests, 0 failed, 180 known failures by exact IRI, and
   `W3C_RUN_KNOWN_FAILURES=1` shows the same 180 failing. (Met 2026-10-30.)
2. Each later phase: the same shape for the suites it loads.
3. The guard (D4) fails on a manifest added to a scratch copy of the submodule, and passes on the real one.

## References

- STATISTICS.md, W3C Conformance — the counts this ADR changes.
- `tests/Mercury.Tests/W3C/W3CTestContext.cs` — `GetManifestPath`, `KnownFailures`, `ShouldSkip`.
- [compression.md](../../architecture/technical/compression.md), Test fixtures — the measurement classes.
- W3C rdf-tests, `rdf/rdf12/rdf-turtle/manifest.ttl` — the include of the RDF 1.1 tests.
