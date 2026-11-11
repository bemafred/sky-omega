// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Provides context for W3C test execution including path resolution,
/// skip lists, and test discovery utilities.
/// </summary>
public static class W3CTestContext
{
    /// <summary>
    /// Gets the root directory of the W3C RDF tests submodule.
    /// </summary>
    public static string TestsRoot { get; } = FindTestsRoot();

    /// <summary>
    /// Gets whether the W3C test suite is available (submodule initialized).
    /// </summary>
    public static bool IsAvailable => Directory.Exists(TestsRoot) &&
                                       File.Exists(Path.Combine(TestsRoot, "README.md"));

    /// <summary>
    /// Tests to skip with documented reasons.
    /// Key format: "suite/relative/path/to/test" or test ID.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SkipList { get; } = new Dictionary<string, string>
    {
        // RDF 1.2 features not yet implemented
        ["turtle12-version"] = "VERSION directive not implemented (RDF 1.2 feature)",
        ["turtle12-ann"] = "Annotation syntax not implemented (RDF 1.2 feature)",

        // SPARQL 1.1 features not supported
        ["entailment"] = "Entailment regimes not supported",
        ["http-rdf-update"] = "Graph Store HTTP Protocol not implemented",
        ["protocol"] = "SPARQL Protocol tests require HTTP server",
        ["service"] = "SERVICE tests require network access",

        // Known limitations
        ["nfc"] = "NFC normalization not enforced on IRIs",

        // CONSTRUCT/DESCRIBE features not yet fully implemented
        // ["agg-empty-group-count-graph"] = "COUNT without GROUP BY inside GRAPH not implemented",  // FIXED: Now supported
        // ["bindings/manifest#graph"] = "VALUES inside GRAPH binding same variable as graph name not implemented",  // Testing...
        // ["constructlist"] = "RDF collection construction in CONSTRUCT not implemented",  // FIXED: Now supported
    };

    /// <summary>
    /// Known failures, by cause: each entry is a failure to fix and remove, not an exclusion (run them with
    /// W3C_RUN_KNOWN_FAILURES=1). The SPARQL 1.0 (DAWG) tests that failed when that suite was first run, 2026-10-28 (see
    /// the Mercury finding mercury-w3c-sparql10-not-run), and the new and changed tests of the W3C suite update of the
    /// same day (rdf-tests f172950 → d01b141).
    /// </summary>
    private static readonly Dictionary<string, string> KnownFailures = new()
    {
        ["data-r2/distinct/manifest#distinct-2"] = "SPARQL 1.0 triage: a simple literal and its xsd:string form are stored as two atoms (ADR-053 D1.5)",
        ["data-r2/distinct/manifest#distinct-9"] = "SPARQL 1.0 triage: a simple literal and its xsd:string form are stored as two atoms (ADR-053 D1.5)",
        ["data-r2/expr-builtin/manifest#dawg-lang-3"] = "SPARQL 1.0 triage: a pattern constant matches a language tag by case (ADR-053, language tags)",
        ["data-r2/syntax-sparql3/manifest#syn-bad-26"] = "SPARQL 1.0 triage: invalid syntax accepted: an expression the grammar does not read (a blank node in FILTER; ?x<?a&&?b>?y)",
        ["data-r2/syntax-sparql3/manifest#syn-bad-29"] = "SPARQL 1.0 triage: invalid syntax accepted: an expression the grammar does not read (a blank node in FILTER; ?x<?a&&?b>?y)",
        ["data-r2/syntax-sparql3/manifest#syn-bad-30"] = "SPARQL 1.0 triage: invalid syntax accepted: an expression the grammar does not read (a blank node in FILTER; ?x<?a&&?b>?y)",

        // The W3C suite update of 2026-10-28 (rdf-tests f172950 -> d01b141), by cause

        // RDF 1.2 (ADR-059 phase 1, 2026-10-30): the format suites load the rdf12 manifests, which include the
        // RDF 1.1 tests. Each entry is one test IRI; its cause names the parser that fails.
        ["rdf/rdf12/rdf-n-quads/syntax#nquads12-01>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-quads/syntax#nquads12-02>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-quads/syntax#nquads12-03>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-quads/syntax#nquads12-bnode-1>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-quads/syntax#nquads12-nested-1>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/syntax#ntriples-version-1>"] = "RDF 1.2: the N-Triples parser does not read VERSION (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/syntax#ntriples12-01>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/syntax#ntriples12-02>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/syntax#ntriples12-03>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/syntax#ntriples12-bnode-1>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/syntax#ntriples12-nested-1>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-07>"] = "RDF 1.2: the RDF/XML parser accepts invalid triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-08>"] = "RDF 1.2: the RDF/XML parser accepts invalid triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-dir-01>"] = "RDF 1.2: the RDF/XML parser reads base direction into the wrong triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-dir-02>"] = "RDF 1.2: the RDF/XML parser reads base direction into the wrong triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-dir-03>"] = "RDF 1.2: the RDF/XML parser reads base direction into the wrong triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-dir-04>"] = "RDF 1.2: the RDF/XML parser reads base direction into the wrong triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-dir-05>"] = "RDF 1.2: the RDF/XML parser reads base direction into the wrong triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-01>"] = "RDF 1.2: the RDF/XML parser reads triple terms into the wrong triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-01>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-02>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-03>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-04>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-05>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-07>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-10>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-1>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-2>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-3>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-4>"] = "RDF 1.2: the TriG parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-bnode-1>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-bnode-2>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-01>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-02>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-1>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-2>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-4>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-bnode-1>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-bnode-2>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-bnode-3>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-compound-1>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-inside-1>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-inside-2>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-nested-1>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-nested-2>"] = "RDF 1.2: the TriG parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-reified-triples-annotation-01>"] = "RDF 1.2: the TriG parser does not read reified triples and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-reified-triples-annotation-02>"] = "RDF 1.2: the TriG parser does not read reified triples and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-reified-triples-annotation-03>"] = "RDF 1.2: the TriG parser does not read reified triples and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-03>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-04>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-05>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-06>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-07>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-rt-08>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-inside-3>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-inside-4>"] = "RDF 1.2: the TriG parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-5>"] = "RDF 1.2: the TriG parser does not read reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-6>"] = "RDF 1.2: the TriG parser does not read reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-06>"] = "RDF 1.2: the TriG parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-08>"] = "RDF 1.2: the TriG parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-09>"] = "RDF 1.2: the TriG parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-11>"] = "RDF 1.2: the TriG parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-7>"] = "RDF 1.2: the TriG parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-ann-8>"] = "RDF 1.2: the TriG parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/syntax#trig12-3>"] = "RDF 1.2: the TriG parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-version-bad-02>"] = "RDF 1.2: the Turtle parser accepts invalid VERSION (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-version-bad-03>"] = "RDF 1.2: the Turtle parser accepts invalid VERSION (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-version-bad-05>"] = "RDF 1.2: the Turtle parser accepts invalid VERSION (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-version-bad-06>"] = "RDF 1.2: the Turtle parser accepts invalid VERSION (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-bad-ann-2>"] = "RDF 1.2: the Turtle parser accepts invalid annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-bad-4>"] = "RDF 1.2: the Turtle parser accepts invalid reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#nt-ttl12-bad-02>"] = "RDF 1.2: the Turtle parser accepts invalid triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#nt-ttl12-bad-03>"] = "RDF 1.2: the Turtle parser accepts invalid triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-10>"] = "RDF 1.2: the Turtle parser does not read annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-4>"] = "RDF 1.2: the Turtle parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-5>"] = "RDF 1.2: the Turtle parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-6>"] = "RDF 1.2: the Turtle parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-bnode-3>"] = "RDF 1.2: the Turtle parser does not read reified triples (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-07>"] = "RDF 1.2: the Turtle parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-08>"] = "RDF 1.2: the Turtle parser does not read reified triples and reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-ann-6>"] = "RDF 1.2: the Turtle parser does not read reifiers (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-11>"] = "RDF 1.2: the Turtle parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-12>"] = "RDF 1.2: the Turtle parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/syntax#turtle12-ann-8>"] = "RDF 1.2: the Turtle parser does not read reifiers and annotations (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-12>"] = "RDF 1.2: the expected N-Quads results hold triple terms, which the N-Quads parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-trig/eval#trig12-annotation-13>"] = "RDF 1.2: the expected N-Quads results hold triple terms, which the N-Quads parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-01>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-02>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-03>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-04>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-05>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-06>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-07>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-08>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-09>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-annotation-13>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-bnode-01>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-bnode-02>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-reified-triples-annotation-01>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-reified-triples-annotation-02>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-reified-triples-annotation-03>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-01>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-02>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-03>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-04>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-05>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-06>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-rt-09>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-tt-01>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-tt-02>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-tt-03>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-tt-04>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-turtle/eval#turtle12-tt-05>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-01>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-02>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-03>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-04>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-05>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-06>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-07>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-08>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-09>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-10>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-11>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-12>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-13>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-14>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-15>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-16>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-an-reif-01>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-02>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-03>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-04>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-05>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-xml/eval#rdf12-xml-tt-06>"] = "RDF 1.2: the expected N-Triples results hold triple terms, which the N-Triples parser does not read (ADR-059 phase 2)",

        // Canonical N-Triples and N-Quads (ADR-059 phase 1, 2026-10-30): the parsers and writers are one each and write
        // canonical form; the triple-term tests remain.
        ["rdf/rdf12/rdf-n-quads/c14n#triple-term-01>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-quads/c14n#triple-term-02>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-quads/c14n#triple-term-03>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-quads/c14n#triple-term-04>"] = "RDF 1.2: the N-Quads parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/c14n#triple-term-01>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/c14n#triple-term-02>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/c14n#triple-term-03>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
        ["rdf/rdf12/rdf-n-triples/c14n#triple-term-04>"] = "RDF 1.2: the N-Triples parser does not read triple terms (ADR-059 phase 2)",
    };


    /// <summary>
    /// Known slow tests that may exceed standard timeout.
    /// Tests listed here are known to take >10 seconds due to complexity.
    /// </summary>
    public static IReadOnlyDictionary<string, int> SlowTests { get; } = new Dictionary<string, int>
    {
        // Property path tests with transitive closure
        ["pp37"] = 60000, // 60 seconds - transitive property paths on large graphs
        ["pp38"] = 60000, // 60 seconds - complex property path expressions

        // Subquery tests with cartesian products
        ["sq14"] = 45000, // 45 seconds - nested subqueries with multiple joins
    };

    /// <summary>
    /// Gets the recommended timeout for a test in milliseconds.
    /// Returns null if the test should use the default timeout.
    /// </summary>
    public static int? GetRecommendedTimeout(string testId)
    {
        // Check for exact match
        if (SlowTests.TryGetValue(testId, out var timeout))
            return timeout;

        // Check for partial match (test ID contains key)
        foreach (var (pattern, recommendedTimeout) in SlowTests)
        {
            if (testId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return recommendedTimeout;
        }

        return null; // Use default timeout
    }

    /// <summary>
    /// Optional: Include only tests matching this pattern.
    /// Set via W3C_INCLUDE_ONLY environment variable.
    /// Example: W3C_INCLUDE_ONLY=bind/ to only run bind tests.
    /// </summary>
    public static string? IncludeOnly { get; } = Environment.GetEnvironmentVariable("W3C_INCLUDE_ONLY");

    /// <summary>
    /// True when the known failures (the SPARQL 1.0 triage) run as ordinary tests rather than skipped ones, to see what
    /// each one does while fixing its cause. Set with the W3C_RUN_KNOWN_FAILURES=1 environment variable.
    /// </summary>
    public static bool RunKnownFailures { get; } = Environment.GetEnvironmentVariable("W3C_RUN_KNOWN_FAILURES") == "1";

    /// <summary>
    /// True when a test is out of scope (<see cref="SkipList"/>): a feature Mercury does not provide. Such tests are
    /// left out of a run. A known failure (the SPARQL 1.0 triage) is not: it runs as a skipped test, counted and with its
    /// cause, so the gap stays visible.
    /// </summary>
    public static bool IsOutOfScope(string testId)
    {
        if (SkipList.ContainsKey(testId))
            return true;
        foreach (var pattern in SkipList.Keys)
        {
            if (testId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a test should be skipped.
    /// </summary>
    /// <param name="testId">The test identifier or path.</param>
    /// <param name="reason">The reason for skipping, if applicable.</param>
    /// <returns>True if the test should be skipped.</returns>
    /// <summary>
    /// True when <paramref name="testId"/> is a known failure (<see cref="KnownFailures"/>), with its cause; false for
    /// every test when W3C_RUN_KNOWN_FAILURES=1. The RDF format suites check only this: the skip list's patterns match
    /// by substring, and "nfc" matched their localName_with_…nfc… tests.
    /// </summary>
    public static bool IsKnownFailure(string testId, out string? cause)
    {
        cause = null;
        if (RunKnownFailures)
            return false;
        foreach (var (failure, knownCause) in KnownFailures)
        {
            if (testId.Contains(failure, StringComparison.Ordinal))
            {
                cause = knownCause;
                return true;
            }
        }
        return false;
    }

        public static bool ShouldSkip(string testId, out string? reason)
    {
        reason = null;

        // If IncludeOnly is set, skip tests that DON'T match
        if (!string.IsNullOrEmpty(IncludeOnly))
        {
            if (!testId.Contains(IncludeOnly, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Not in include filter: {IncludeOnly}";
                return true;
            }
        }

        // Check for exact match
        if (SkipList.TryGetValue(testId, out reason))
            return true;

        if (IsKnownFailure(testId, out reason))
            return true;

        // Check for partial match (e.g., skip entire category)
        foreach (var (pattern, skipReason) in SkipList)
        {
            if (testId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                reason = skipReason;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The manifests in the two W3C submodules that no suite loads, by path under tests/, each with why and the
    /// ADR-059 phase that loads it. Like <see cref="KnownFailures"/>, a listed gap, not an absent one: the guard
    /// (<see cref="W3CManifestCoverageTests"/>) fails on a manifest that is neither loaded nor listed, and on an entry a
    /// suite does load, which is then removed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ManifestsNotLoaded { get; } = new Dictionary<string, string>
    {
        ["w3c-rdf-tests/ns/test-manifest.ttl"] = "The manifest vocabulary (mf:), not a test manifest",
        ["w3c-rdf-tests/rdf/rdf11/manifest.ttl"] = "The RDF 1.1 umbrella: each format's manifest is loaded by its suite, rdf-mt is ADR-059 phase 5",
        ["w3c-rdf-tests/rdf/rdf12/manifest.ttl"] = "The RDF 1.2 umbrella: each format's manifest is loaded by its suite, rdf-semantics is ADR-059 phase 5",
        ["w3c-rdf-tests/sparql/sparql10/manifest-evaluation.ttl"] = "An umbrella of SPARQL 1.0 manifests that sparql10/manifest.ttl also includes, so the suite loads each",
        ["w3c-rdf-tests/sparql/sparql10/manifest-syntax.ttl"] = "An umbrella of SPARQL 1.0 manifests that sparql10/manifest.ttl also includes, so the suite loads each",
        ["w3c-rdf-tests/sparql/sparql11/manifest.ttl"] = "The SPARQL 1.1 umbrella of query, update and result formats: query and update are loaded, result formats are ADR-059 phase 4",
        ["w3c-rdf-tests/sparql/sparql11/manifest-all.ttl"] = "The umbrella of every SPARQL 1.1 manifest: the suites load query and update, the rest is ADR-059 phase 4",
        ["w3c-rdf-tests/rdf/rdf11/rdf-mt/manifest.ttl"] = "RDF 1.1 semantics, entailment tests: no test kind yet (ADR-059 phase 5, Q3)",
        ["w3c-rdf-tests/rdf/rdf11/rdf-mt/az-tests/manifest.ttl"] = "RDF 1.1 semantics, entailment tests: no test kind yet (ADR-059 phase 5, Q3)",
        ["w3c-rdf-tests/rdf/rdf12/rdf-semantics/manifest.ttl"] = "RDF 1.2 semantics, entailment tests: no test kind yet (ADR-059 phase 5, Q3)",

        // Do not apply, triaged 2026-11-11. In no umbrella manifest, and written for RDF 1.0, where "xyz" and
        // "xyz"^^xsd:string are two terms; since RDF 1.1 a simple literal is an xsd:string, so they are one.
        ["w3c-rdf-tests/sparql/sparql10/extended-manifest-evaluation.ttl"] = "The umbrella of sort/extended-manifest.ttl, an extended test (sort-11); does not apply",
        ["w3c-rdf-tests/sparql/sparql10/sort/extended-manifest.ttl"] = "sort-11, an extended test: it expects \"Alice\" … \"Fred\" sorted before \"Alice\"^^xsd:string … \"Fred\"^^xsd:string, which RDF 1.1 makes the same four terms; does not apply",
        ["w3c-rdf-tests/sparql/sparql10/open-world/sameTerm-manifest.ttl"] = "Six sameTerm tests, three of them under mf:StringSimpleLiteralCmp: five expect \"xyz\" and \"xyz\"^^xsd:string to be two terms, and none can run, as their queries are not in open-world/ (expr-builtin/ has queries of the same names, for other data, in tests the suite runs); does not apply",

        ["w3c-rdf-tests/sparql/sparql11/manifest-sparql11-fed.ttl"] = "SPARQL 1.1 federation (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql11/syntax-fed/manifest.ttl"] = "SPARQL 1.1 federation (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql11/service/manifest.ttl"] = "SPARQL 1.1 federation (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql11/manifest-sparql11-results.ttl"] = "SPARQL 1.1 result formats (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql11/csv-tsv-res/manifest.ttl"] = "SPARQL 1.1 result formats (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql11/json-res/manifest.ttl"] = "SPARQL 1.1 result formats (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql11/entailment/manifest.ttl"] = "SPARQL 1.1 entailment regimes (ADR-059 phase 4, Q3)",
        ["w3c-rdf-tests/sparql/sparql11/graph-store-protocol/manifest.ttl"] = "SPARQL 1.1 Graph Store protocol (ADR-059 phase 4, Q2)",
        ["w3c-rdf-tests/sparql/sparql11/graph-store-protocol/manifest-direct.ttl"] = "SPARQL 1.1 Graph Store protocol (ADR-059 phase 4, Q2)",
        ["w3c-rdf-tests/sparql/sparql11/graph-store-protocol/manifest-indirect.ttl"] = "SPARQL 1.1 Graph Store protocol (ADR-059 phase 4, Q2)",
        ["w3c-rdf-tests/sparql/sparql11/http-rdf-update/manifest.ttl"] = "The SPARQL 1.1 Graph Store protocol's earlier draft (ADR-059 phase 4, Q2)",
        ["w3c-rdf-tests/sparql/sparql11/protocol/manifest.ttl"] = "SPARQL 1.1 protocol, to run against SparqlHttpServer (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql11/service-description/manifest.ttl"] = "SPARQL 1.1 service description (ADR-059 phase 4)",
        ["w3c-rdf-tests/sparql/sparql12/manifest.ttl"] = "The SPARQL 1.2 umbrella (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/codepoint-escapes/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/eval-triple-terms/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/expression/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/grouping/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/lang-basedir/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/rdf11/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/syntax/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/syntax-triple-terms-negative/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/syntax-triple-terms-positive/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-rdf-tests/sparql/sparql12/version/manifest.ttl"] = "SPARQL 1.2 (ADR-059 phase 3)",
        ["w3c-json-ld-api/tests/manifest.jsonld"] = "The umbrella of the JSON-LD API manifests: the suite loads toRdf on its own (ADR-059 phase 6)",
        ["w3c-json-ld-api/tests/fromRdf-manifest.jsonld"] = "JSON-LD fromRdf, RDF to JSON-LD (ADR-059 phase 6)",
        ["w3c-json-ld-api/tests/compact-manifest.jsonld"] = "The JSON-LD API's compaction: whether Mercury is a JSON-LD processor is ADR-059 Q1 (phase 6)",
        ["w3c-json-ld-api/tests/expand-manifest.jsonld"] = "The JSON-LD API's expansion: whether Mercury is a JSON-LD processor is ADR-059 Q1 (phase 6)",
        ["w3c-json-ld-api/tests/flatten-manifest.jsonld"] = "The JSON-LD API's flattening: whether Mercury is a JSON-LD processor is ADR-059 Q1 (phase 6)",
        ["w3c-json-ld-api/tests/html-manifest.jsonld"] = "JSON-LD in HTML: whether Mercury is a JSON-LD processor is ADR-059 Q1 (phase 6)",
        ["w3c-json-ld-api/tests/remote-doc-manifest.jsonld"] = "JSON-LD remote documents: whether Mercury is a JSON-LD processor is ADR-059 Q1 (phase 6)",
    };

    /// <summary>The formats whose W3C suites are in the rdf-tests submodule, each with its test class.</summary>
    public static IReadOnlyList<W3CTestFormat> Formats { get; } =
    [
        W3CTestFormat.NTriples, W3CTestFormat.NQuads, W3CTestFormat.Turtle, W3CTestFormat.TriG, W3CTestFormat.RdfXml,
        W3CTestFormat.SparqlQuery, W3CTestFormat.SparqlUpdate,
    ];

    /// <summary>
    /// The suites a format's tests load, the one table the test classes and the guard
    /// (<see cref="W3CManifestCoverageTests"/>) read. Each RDF format loads its RDF 1.1 and its RDF 1.2 manifest: until
    /// rdf-tests 9fafbec (#396, 2026-11-01) the 1.2 manifest included the 1.1 one, and it still says conformance is
    /// "the tests for relevant specifications along with the relevant RDF 1.1 tests". SPARQL queries load the 1.0 and
    /// the 1.1 suite: 1.1 tests what 1.1 added, so core features are tested only in 1.0.
    /// </summary>
    public static IReadOnlyList<W3CTestSuite> SuitesOf(W3CTestFormat format) => format switch
    {
        W3CTestFormat.NTriples => [W3CTestSuite.NTriples11, W3CTestSuite.NTriples12],
        W3CTestFormat.NQuads => [W3CTestSuite.NQuads11, W3CTestSuite.NQuads12],
        W3CTestFormat.Turtle => [W3CTestSuite.Turtle11, W3CTestSuite.Turtle12],
        W3CTestFormat.TriG => [W3CTestSuite.TriG11, W3CTestSuite.TriG12],
        W3CTestFormat.RdfXml => [W3CTestSuite.RdfXml11, W3CTestSuite.RdfXml12],
        W3CTestFormat.SparqlQuery => [W3CTestSuite.Sparql10Query, W3CTestSuite.Sparql11Query],
        W3CTestFormat.SparqlUpdate => [W3CTestSuite.Sparql11Update],
        _ => throw new ArgumentException($"No W3C suites for {format}", nameof(format))
    };

    /// <summary>
    /// The tests of <paramref name="type"/> that a format's test class runs, from every suite of
    /// <see cref="SuitesOf"/>. One manifest parser reads them all, so a manifest two suites include is read once.
    /// Throws when the submodule is missing, since a run without it would pass with no tests.
    /// </summary>
    public static IReadOnlyList<W3CTestCase> TestsOf(W3CTestFormat format, W3CTestType type)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var parser = new W3CManifestParser();
        var tests = new List<W3CTestCase>();
        foreach (var suite in SuitesOf(format))
        {
            var suiteTests = parser.ParseAsync(GetManifestPath(suite)).GetAwaiter().GetResult();
            tests.AddRange(suiteTests.Where(t => t.Type == type));
        }
        return tests;
    }

    /// <summary>
    /// Gets the path to a specific test suite manifest.
    /// </summary>
    public static string GetManifestPath(W3CTestSuite suite) => suite switch
    {
        W3CTestSuite.NTriples11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-n-triples", "manifest.ttl"),
        W3CTestSuite.NTriples12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-n-triples", "manifest.ttl"),
        W3CTestSuite.NQuads11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-n-quads", "manifest.ttl"),
        W3CTestSuite.NQuads12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-n-quads", "manifest.ttl"),
        W3CTestSuite.Turtle11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-turtle", "manifest.ttl"),
        W3CTestSuite.Turtle12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-turtle", "manifest.ttl"),
        W3CTestSuite.TriG11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-trig", "manifest.ttl"),
        W3CTestSuite.TriG12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-trig", "manifest.ttl"),
        W3CTestSuite.RdfXml11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-xml", "manifest.ttl"),
        W3CTestSuite.RdfXml12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-xml", "manifest.ttl"),
        W3CTestSuite.Sparql10Query => Path.Combine(TestsRoot, "sparql", "sparql10", "manifest.ttl"),
        W3CTestSuite.Sparql11Query => Path.Combine(TestsRoot, "sparql", "sparql11", "manifest-sparql11-query.ttl"),
        W3CTestSuite.Sparql11Update => Path.Combine(TestsRoot, "sparql", "sparql11", "manifest-sparql11-update.ttl"),
        _ => throw new ArgumentException($"Unknown test suite: {suite}", nameof(suite))
    };

    /// <summary>
    /// Gets the format being tested by a test suite.
    /// </summary>
    public static W3CTestFormat GetTestFormat(W3CTestSuite suite) => suite switch
    {
        W3CTestSuite.NTriples11 or W3CTestSuite.NTriples12 => W3CTestFormat.NTriples,
        W3CTestSuite.NQuads11 or W3CTestSuite.NQuads12 => W3CTestFormat.NQuads,
        W3CTestSuite.Turtle11 or W3CTestSuite.Turtle12 => W3CTestFormat.Turtle,
        W3CTestSuite.TriG11 or W3CTestSuite.TriG12 => W3CTestFormat.TriG,
        W3CTestSuite.RdfXml11 or W3CTestSuite.RdfXml12 => W3CTestFormat.RdfXml,
        W3CTestSuite.Sparql10Query or W3CTestSuite.Sparql11Query => W3CTestFormat.SparqlQuery,
        W3CTestSuite.Sparql11Update => W3CTestFormat.SparqlUpdate,
        _ => W3CTestFormat.Unknown
    };

    /// <summary>
    /// Loads test cases from a manifest, filtering by type.
    /// </summary>
    public static async Task<IReadOnlyList<W3CTestCase>> LoadTestCasesAsync(
        W3CTestSuite suite,
        W3CTestType? filterType = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Array.Empty<W3CTestCase>();

        var manifestPath = GetManifestPath(suite);
        if (!File.Exists(manifestPath))
            return Array.Empty<W3CTestCase>();

        var parser = new W3CManifestParser();
        var tests = await parser.ParseAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        if (filterType.HasValue)
            tests = tests.Where(t => t.Type == filterType.Value).ToList();

        return tests;
    }

    /// <summary>
    /// Gets test cases suitable for xUnit Theory data.
    /// Returns: [displayName, testCase]
    /// </summary>
    public static async Task<IEnumerable<object[]>> GetTheoryDataAsync(
        W3CTestSuite suite,
        W3CTestType? filterType = null,
        CancellationToken cancellationToken = default)
    {
        var tests = await LoadTestCasesAsync(suite, filterType, cancellationToken);
        return tests.Select(t => new object[] { t.DisplayName, t });
    }

    private static string FindTestsRoot([CallerFilePath] string? callerPath = null)
    {
        // Strategy 1: MSBuild-embedded path (computed from .csproj location at build time).
        // Under NCrunch, uses $(NCrunchOriginalProjectDir) to resolve back to the real
        // source tree where git submodules live (NCrunch workspaces don't copy submodules).
        var assembly = typeof(W3CTestContext).Assembly;
        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key == "W3CRdfTestsRoot" && !string.IsNullOrEmpty(attr.Value))
                return attr.Value;
        }

        // Strategy 2: Walk up from [CallerFilePath] (fallback if metadata not available)
        var dir = callerPath != null
            ? Path.GetDirectoryName(callerPath)
            : Directory.GetCurrentDirectory();

        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "w3c-rdf-tests");
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir, "w3c-rdf-tests");
            if (Directory.Exists(candidate))
                return candidate;

            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: relative to current directory
        return Path.Combine(Directory.GetCurrentDirectory(), "tests", "w3c-rdf-tests");
    }
}

/// <summary>
/// Available W3C test suites.
/// </summary>
public enum W3CTestSuite
{
    // RDF 1.1 Parser Tests
    NTriples11,
    NQuads11,
    Turtle11,
    TriG11,
    RdfXml11,

    // RDF 1.2 Parser Tests (each format loads these and its RDF 1.1 tests: SuitesOf)
    NTriples12,
    NQuads12,
    Turtle12,
    TriG12,
    RdfXml12,

    // SPARQL 1.0 Tests (the DAWG suite: core features the 1.1 suite does not test again)
    Sparql10Query,

    // SPARQL 1.1 Tests
    Sparql11Query,
    Sparql11Update,
}
