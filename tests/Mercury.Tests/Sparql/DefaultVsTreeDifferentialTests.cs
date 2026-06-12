using System;
using System.Collections.Generic;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-047 step 1 — the <c>default ≡ tree</c> differential gate.
///
/// The cutover replaces the old default-path executor (QueryPlanner + slot-based operators) with the unified
/// <c>TreeJoinExecutor</c> the GRAPH path already uses. Before that flip, every query must produce the SAME solution
/// bag through the tree as through the old path. This harness runs each query BOTH ways — the shipping facade
/// (<see cref="SparqlEngine.Query"/>, the old path) and the forced tree path
/// (<see cref="SparqlEngine.QueryViaTreeForDifferential"/>) — and compares the bags. The methodology is ADR-045's
/// metamorphic mirror gate, applied to the two executors instead of default-vs-named.
///
/// <c>expectedEquivalent</c> encodes TODAY's parity: TRUE = the tree already matches the old path (live regression
/// coverage — a regression that breaks parity fails here); FALSE = a known parity gap ADR-047 step 2 must close
/// (flip to TRUE then). The known FALSE cases are the correctness gaps the VALUES investigation surfaced
/// (ck:obs-values-join-default-path-incomplete): VALUES numeric canonicalization, VALUES cross-join with a triple,
/// and the zero-length-path graph-term-membership semantic.
/// </summary>
public class DefaultVsTreeDifferentialTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public DefaultVsTreeDifferentialTests(ITestOutputHelper output)
    {
        _output = output;
        var tempPath = TempPath.Test("default-vs-tree");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        _store.BeginBatch();
        _store.AddCurrentBatched("<urn:a>", "<urn:p>", "<urn:v1>");
        _store.AddCurrentBatched("<urn:b>", "<urn:p>", "<urn:v2>");
        _store.AddCurrentBatched("<urn:a>", "<urn:q>", "\"qa\"");
        _store.AddCurrentBatched("<urn:b>", "<urn:q>", "\"qb\"");
        _store.AddCurrentBatched("<urn:v1>", "<urn:next>", "<urn:v2>");
        _store.AddCurrentBatched("<urn:a>", "<urn:age>", "\"25\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        _store.AddCurrentBatched("<urn:b>", "<urn:age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    // ── Equivalent today: the tree already matches the old path (regression coverage). ──
    [InlineData("bgp", true, "SELECT ?s ?o WHERE { ?s <urn:p> ?o }")]
    [InlineData("join", true, "SELECT ?s ?x WHERE { ?s <urn:p> ?o . ?o <urn:next> ?x }")]
    [InlineData("filter", true, "SELECT ?s WHERE { ?s <urn:p> ?o FILTER(?o = <urn:v1>) }")]
    [InlineData("bind", true, "SELECT ?l WHERE { ?s <urn:p> ?o BIND(STR(?o) AS ?l) }")]
    [InlineData("optional", true, "SELECT ?s ?x WHERE { ?s <urn:p> ?o OPTIONAL { ?o <urn:next> ?x } }")]
    [InlineData("union", true, "SELECT ?s WHERE { { ?s <urn:p> ?o } UNION { ?s <urn:q> ?o } }")]
    [InlineData("minus", true, "SELECT ?s WHERE { ?s <urn:p> ?o MINUS { ?s <urn:q> ?x } }")]
    [InlineData("distinct", true, "SELECT DISTINCT ?p WHERE { ?s ?p ?o }")]
    [InlineData("order-by-limit", true, "SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY ?o LIMIT 1")]
    [InlineData("aggregate-count", true, "SELECT (COUNT(?o) AS ?c) WHERE { ?s <urn:p> ?o }")]
    [InlineData("property-path-plus", true, "SELECT ?s ?x WHERE { ?s <urn:next>+ ?x }")]
    [InlineData("sub-select", true, "SELECT ?o WHERE { { SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY ?o LIMIT 1 } }")]
    [InlineData("values-before-join", true, "SELECT ?o WHERE { VALUES ?s { <urn:a> } ?s <urn:p> ?o }")]
    [InlineData("path-star", true, "SELECT ?x WHERE { <urn:v1> <urn:next>* ?x }")]
    [InlineData("path-question-in-graph", true, "SELECT ?x WHERE { <urn:v1> <urn:next>? ?x }")]
    [InlineData("path-inverse", true, "SELECT ?s WHERE { ?s ^<urn:next> <urn:v2> }")]
    [InlineData("path-alternative", true, "SELECT ?o WHERE { <urn:a> <urn:p>|<urn:q> ?o }")]
    [InlineData("exists", true, "SELECT ?s WHERE { ?s <urn:p> ?o FILTER EXISTS { ?s <urn:q> ?x } }")]
    [InlineData("not-exists", true, "SELECT ?s WHERE { ?s <urn:p> ?o FILTER NOT EXISTS { ?s <urn:next> ?x } }")]
    [InlineData("group-by-having", true, "SELECT ?p (COUNT(?o) AS ?c) WHERE { ?s ?p ?o } GROUP BY ?p HAVING (COUNT(?o) > 1)")]
    [InlineData("nested-optional", true, "SELECT ?s ?y WHERE { ?s <urn:p> ?o OPTIONAL { ?o <urn:next> ?x OPTIONAL { ?x <urn:p> ?y } } }")]
    [InlineData("multivar-values-all-join", true, "SELECT ?o WHERE { VALUES (?s ?o) { (<urn:a> <urn:v1>) } ?s <urn:p> ?o }")]
    [InlineData("filter-bound", true, "SELECT ?s WHERE { ?s <urn:p> ?o OPTIONAL { ?o <urn:next> ?x } FILTER(BOUND(?x)) }")]
    [InlineData("filter-regex", true, "SELECT ?s WHERE { ?s <urn:q> ?o FILTER(REGEX(?o, \"^q\")) }")]
    // ── Known parity gaps (ADR-047 step 2 closes these; flip to true then). ──
    [InlineData("values-numeric-join", false, "SELECT ?s WHERE { ?s <urn:age> ?age VALUES ?age { 25 } }")]
    [InlineData("values-after-triple", false, "SELECT ?x WHERE { ?s <urn:p> ?o VALUES ?x { 1 2 } }")]
    [InlineData("zero-length-path-values", false, "SELECT ?v WHERE { VALUES ?v { <urn:zzz> } ?v <urn:p>? ?v }")]
    public void DefaultEquivTree_CharacterizesTheParitySurface(string name, bool expectedEquivalent, string query)
    {
        var old = SparqlEngine.Query(_store, query);
        var tree = SparqlEngine.QueryViaTreeForDifferential(_store, query);

        bool equivalent = ResultsEquivalent(old, tree);
        _output.WriteLine($"[{name}] equivalent={equivalent} (expected {expectedEquivalent})");
        _output.WriteLine($"  old:  {Describe(old)}");
        _output.WriteLine($"  tree: {Describe(tree)}");

        Assert.Equal(expectedEquivalent, equivalent);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────

    private static bool ResultsEquivalent(QueryResult a, QueryResult b)
    {
        if (a.Success != b.Success)
            return false;
        if (!a.Success)
            return true; // both errored — treat as equivalent (an error is not a result divergence)
        return Canonicalize(a.Rows).SequenceEqual(Canonicalize(b.Rows));
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
        return $"{r.Rows?.Count ?? 0} row(s): [{string.Join("; ", Canonicalize(r.Rows))}]";
    }
}
