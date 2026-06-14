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
    // ADR-047 top-K (ORDER BY + LIMIT streams into a bounded heap): DESC, OFFSET, LIMIT > set size, multi-key, numeric.
    [InlineData("order-by-desc-limit", true, "SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY DESC(?o) LIMIT 1")]
    [InlineData("order-by-offset-limit", true, "SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY ?o LIMIT 1 OFFSET 1")]
    [InlineData("order-by-limit-exceeds-set", true, "SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY ?o LIMIT 5")]
    [InlineData("order-by-multivar-limit", true, "SELECT ?s ?o WHERE { ?s <urn:p> ?o } ORDER BY ?s ?o LIMIT 1")]
    [InlineData("order-by-numeric-limit", true, "SELECT ?age WHERE { ?s <urn:age> ?age } ORDER BY ?age LIMIT 1")]
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
    // ── ADR-047 step 2: gaps CLOSED on the tree — now equivalent (regression coverage). ──
    // Numeric/boolean VALUES tokens canonicalize to a typed literal so they join a stored typed literal;
    // a zero-length-path reflexive over a VARIABLE-bound value is gated on graph-node membership (SPARQL §9.3).
    [InlineData("values-numeric-join", true, "SELECT ?s WHERE { ?s <urn:age> ?age VALUES ?age { 25 } }")]
    [InlineData("zero-length-path-values", true, "SELECT ?v WHERE { VALUES ?v { <urn:zzz> } ?v <urn:p>? ?v }")]
    // ── Tree is MORE correct than the old path — the cutover IMPROVES this, it is NOT a tree regression. ──
    // VALUES after a triple cross-joins it (SPARQL §18); the old default path drops the inline data. Stays divergent
    // by design until the flip, when the tree's (correct) behaviour becomes the shipping behaviour.
    [InlineData("values-after-triple", false, "SELECT ?x WHERE { ?s <urn:p> ?o VALUES ?x { 1 2 } }")]
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

    [Fact]
    public void ValuesAfterTriple_TheTreeResultIsTheCorrectCrossJoin()
    {
        // The one remaining divergence is the tree being MORE correct, not a tree bug. SPARQL §18: VALUES in a group
        // joins; with no shared variable that is a cross-product. The store has 2 <urn:p> triples, so the result is
        // 2 × {1,2} = 4 rows. The tree produces this; the old default path drops the inline data. The cutover adopts
        // the tree's (correct) behaviour — so this case being divergent is an improvement the flip lands, not a risk.
        const string query = "SELECT ?x WHERE { ?s <urn:p> ?o VALUES ?x { 1 2 } }";

        var tree = SparqlEngine.QueryViaTreeForDifferential(_store, query);
        Assert.True(tree.Success, tree.ErrorMessage);
        Assert.Equal(4, tree.Rows!.Count);

        var old = SparqlEngine.Query(_store, query);
        Assert.NotEqual(4, old.Rows!.Count); // the old path drops VALUES-after-triple — the bug the cutover fixes
    }

    [Theory]
    // WDBench breadth (reorder=true truthy, 2026-06-13) surfaced ~101 c2rpqs divergences. Triage — Martin (RDF
    // authority) concurring — established the DOMINANT signature is the TREE being W3C-CORRECT and the OLD path
    // BUGGY, not the reverse: a `?`/`*` path whose subject is bound by a prior pattern must emit the zero-length
    // reflexive (?x = ?o) for each bound ?o (SPARQL 1.1 §9.3 — a zero-length path binds (n,n) for every node n in the
    // graph). The old path drops those reflexives in the JOIN context — though it emits them fine for a CONSTANT
    // subject (see path-question-in-graph above), which is exactly why W3C conformance + the 35 hand-built cases
    // never caught it. The cutover ADOPTS the tree's correct behaviour. So these assert the TREE, not old≡tree.
    //
    // Fixture: a→v1, b→v2 (urn:p); v1→v2 (urn:next). Each bound ?o ∈ {v1, v2} contributes its reflexive; v1 also
    // reaches v2. The W3C-correct bag is the three solutions {(a,v1,v1), (a,v1,v2), (b,v2,v2)} — three rows. The two
    // x=v2 rows under SELECT ?x are NOT double-emission: they are two distinct mappings (a,v1,v2)+(b,v2,v2) colliding
    // on the projected column (bag semantics). The old path returns one row — it dropped both reflexive solutions.
    [InlineData("zoo-grouped", "SELECT ?s ?o ?x WHERE { ?s <urn:p> ?o . ?o (<urn:next>)? ?x }")]
    [InlineData("zoo-bare", "SELECT ?s ?o ?x WHERE { ?s <urn:p> ?o . ?o <urn:next>? ?x }")]
    [InlineData("zom", "SELECT ?s ?o ?x WHERE { ?s <urn:p> ?o . ?o <urn:next>* ?x }")]
    [InlineData("zoo-proj-x", "SELECT ?x WHERE { ?s <urn:p> ?o . ?o (<urn:next>)? ?x }")]
    public void PathReflexive_BoundSubject_TreeIsW3CCorrect_OldDropsReflexives(string name, string query)
    {
        var tree = SparqlEngine.QueryViaTreeForDifferential(_store, query);
        var old = SparqlEngine.Query(_store, query);
        _output.WriteLine($"[{name}] tree: {Describe(tree)}");
        _output.WriteLine($"[{name}] old:  {Describe(old)}");
        Assert.True(tree.Success, $"[{name}] tree: {tree.ErrorMessage}");
        Assert.True(old.Success, $"[{name}] old: {old.ErrorMessage}");

        // The tree emits the zero-length reflexive per bound ?o — the three W3C-correct solutions.
        Assert.True(tree.Rows!.Count == 3, $"[{name}] expected the 3 W3C solutions; got {Describe(tree)}");
        // The old path drops the reflexives in the join context — one row. This is the bug the cutover fixes; the
        // assertion pins it so a future change to the (soon-deleted) old path can't silently re-flip the divergence.
        Assert.True(old.Rows!.Count == 1, $"[{name}] expected the old path to drop the reflexives (1 row); got {Describe(old)}");
    }

    [Fact]
    public void PathReflexive_BoundSubject_TheTreeBagIsTheThreeW3CSolutions()
    {
        // Pin the exact W3C-correct bag (not just the count): the three distinct solution mappings.
        var tree = SparqlEngine.QueryViaTreeForDifferential(_store,
            "SELECT ?s ?o ?x WHERE { ?s <urn:p> ?o . ?o <urn:next>? ?x }");
        Assert.True(tree.Success, tree.ErrorMessage);
        Assert.Equal(
            new List<string>
            {
                "o=<urn:v1>|s=<urn:a>|x=<urn:v1>", // zero-length at v1
                "o=<urn:v1>|s=<urn:a>|x=<urn:v2>", // v1 —next→ v2
                "o=<urn:v2>|s=<urn:b>|x=<urn:v2>", // zero-length at v2
            },
            Canonicalize(tree.Rows));
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
