using System;
using System.Collections.Generic;
using System.Linq;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-045 Step 3 — the metamorphic <c>default ≡ named</c> mirror gate (first increment).
///
/// The invariant: evaluating a pattern against the default graph and against a named graph is the SAME
/// operation with one parameter — the active graph ("a default graph is also a graph"). So for any query over
/// the default graph, wrapping its WHERE body in <c>GRAPH &lt;m&gt; { … }</c> over the SAME triples mirrored
/// into <c>&lt;m&gt;</c> MUST yield identical results. Where it does not, the GRAPH path has dropped a feature
/// — the recurring divergence class ADR-045 deletes at the Step 4 executor cutover.
///
/// This runs through the SHIPPING engine (<see cref="SparqlEngine"/>), so the known-divergent cases are RED
/// today by design — each is asserted with its CURRENT (divergent) equivalence so the gate is green and the
/// surface is locked: a regression flipping a TRUE case to divergent fails here, and Step 4 flips the FALSE
/// cases to TRUE (which then fails until the executor is genuinely unified). The data is loaded into both the
/// default graph and the mirror graph; Mercury's default-graph scan is the unnamed graph only (not the union),
/// so the comparison is clean.
/// </summary>
public class GraphMirrorGateTests : IDisposable
{
    private const string MirrorGraph = "<urn:test:mirror>";

    private readonly ITestOutputHelper _output;
    private readonly string _testPath;
    private readonly QuadStore _store;

    public GraphMirrorGateTests(ITestOutputHelper output)
    {
        _output = output;
        var tempPath = TempPath.Test("mirror-gate");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        // Identical triples in the default graph AND the mirror graph: default ≡ named must agree.
        (string s, string p, string o)[] data =
        {
            ("<urn:a>", "<urn:p>", "<urn:v1>"),
            ("<urn:a>", "<urn:q>", "\"qa\""),
            ("<urn:b>", "<urn:p>", "<urn:v2>"),
            ("<urn:b>", "<urn:q>", "\"qb\""),
        };
        _store.BeginBatch();
        foreach (var (s, p, o) in data)
        {
            _store.AddCurrentBatched(s, p, o);                       // default graph
            _store.AddCurrentBatched(s, p, o, MirrorGraph);          // mirror graph
        }
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    // ── The metamorphic battery ──────────────────────────────────────────────────────────────────────
    // expectedEquivalent encodes TODAY's shipping behaviour. TRUE = the GRAPH path already matches the default
    // path (live regression coverage); FALSE = a divergence ADR-045 Step 4 must close (flip to TRUE then).

    [Theory]
    // Holds today — the GRAPH path already matches the default path (live regression coverage). Note
    // filter-subject-pname: a (non-hyphenated) prefixed name resolves correctly in a FILTER inside GRAPH. An
    // earlier dogfood scare turned out to be a hyphen-in-PNAME FILTER-expression bug (the parser reads
    // `p:a-b-c` as `p:a - b - c`), position-independent and almost certainly NOT GRAPH-specific — so this gate,
    // which sees only path DIVERGENCE and never a bug COMMON to both paths, is blind to it by design.
    // property-list (the historical 1.7.71 instance) is fixed.
    [InlineData("bgp", true, "SELECT ?s ?o WHERE { ?s <urn:p> ?o }")]
    [InlineData("filter-object-iri", true, "SELECT ?s WHERE { ?s <urn:p> ?o FILTER(?o = <urn:v1>) }")]
    [InlineData("filter-subject-full-iri", true, "SELECT ?o WHERE { ?s <urn:p> ?o FILTER(?s = <urn:a>) }")]
    [InlineData("filter-subject-pname", true, "PREFIX ex: <urn:> SELECT ?o WHERE { ?s <urn:p> ?o FILTER(?s = ex:a) }")]
    [InlineData("property-list", true, "SELECT ?o1 ?o2 WHERE { ?s <urn:p> ?o1 ; <urn:q> ?o2 }")]
    // ALL FIXED by the ADR-045 cutover: layer 1 (the live execution wire) closed VALUES-in-GRAPH; layer 2 (the flat
    // parser consuming GRAPH-internal operators so the parse succeeds and routes to the wire) closed BIND / UNION /
    // OPTIONAL-in-GRAPH. The GRAPH path no longer diverges from the default — a default graph is also a graph.
    [InlineData("bind", true, "SELECT ?l WHERE { ?s <urn:p> ?o BIND(STR(?o) AS ?l) }")]
    [InlineData("values", true, "SELECT ?o WHERE { VALUES ?s { <urn:a> } ?s <urn:p> ?o }")]
    [InlineData("optional", true, "SELECT ?s ?x WHERE { ?s <urn:p> ?o OPTIONAL { ?s <urn:q> ?x } }")]
    [InlineData("union", true, "SELECT ?s WHERE { { ?s <urn:p> ?o } UNION { ?s <urn:q> ?o } }")]
    public void DefaultEquivNamed_CharacterizesTheGraphPathDivergenceSurface(string name, bool expectedEquivalent, string query)
    {
        string mirrored = MirrorWhereIntoGraph(query, MirrorGraph);
        var original = SparqlEngine.Query(_store, query);
        var mirror = SparqlEngine.Query(_store, mirrored);

        bool equivalent = ResultsEquivalent(original, mirror);
        _output.WriteLine($"[{name}] equivalent={equivalent} (expected {expectedEquivalent})");
        _output.WriteLine($"  original: {Describe(original)}");
        _output.WriteLine($"  mirrored: {mirrored}");
        _output.WriteLine($"  mirror:   {Describe(mirror)}");

        Assert.Equal(expectedEquivalent, equivalent);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wrap the WHERE group body in <c>GRAPH &lt;g&gt; { … }</c>, leaving the prologue and solution modifiers
    /// in place. Simple brace matching — sufficient for the controlled queries here; the corpus-wide version
    /// (transforming through the parsed query) is a later Step 3 increment.
    /// </summary>
    private static string MirrorWhereIntoGraph(string query, string graphIri)
    {
        int w = query.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        if (w < 0)
            throw new ArgumentException("Mirror transform requires an explicit WHERE keyword");
        int open = query.IndexOf('{', w);
        if (open < 0)
            throw new ArgumentException("No '{' after WHERE");

        int depth = 0, close = -1;
        for (int i = open; i < query.Length; i++)
        {
            if (query[i] == '{') depth++;
            else if (query[i] == '}' && --depth == 0) { close = i; break; }
        }
        if (close < 0)
            throw new ArgumentException("Unbalanced braces in WHERE clause");

        string body = query.Substring(open + 1, close - open - 1);
        return query[..(open + 1)] + " GRAPH " + graphIri + " {" + body + "} " + query[close..];
    }

    /// <summary>True if two query results are equal as SPARQL solution bags (or equal ASK answers).</summary>
    private static bool ResultsEquivalent(QueryResult a, QueryResult b)
    {
        if (!a.Success || !b.Success)
            return false;
        if (a.Kind != b.Kind)
            return false;
        if (a.AskResult is not null || b.AskResult is not null)
            return a.AskResult == b.AskResult;

        var rowsA = Canonicalize(a.Rows);
        var rowsB = Canonicalize(b.Rows);
        return rowsA.SequenceEqual(rowsB);
    }

    /// <summary>Render a SELECT result as a sorted multiset of rows (variable-sorted key=value strings).</summary>
    private static List<string> Canonicalize(List<Dictionary<string, string>>? rows)
    {
        var canon = new List<string>();
        if (rows is null)
            return canon;
        foreach (var row in rows)
            canon.Add(string.Join("|", row.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                          .Select(kv => kv.Key + "=" + kv.Value)));
        canon.Sort(StringComparer.Ordinal);
        return canon;
    }

    private static string Describe(QueryResult r)
    {
        if (!r.Success)
            return $"ERROR: {r.ErrorMessage}";
        if (r.AskResult is not null)
            return $"ASK={r.AskResult}";
        return $"{r.Rows?.Count ?? 0} row(s): [{string.Join("; ", Canonicalize(r.Rows))}]";
    }
}
