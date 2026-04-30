using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Regression tests for SPARQL property-path query failures surfaced by the WDBench
/// cold baseline (2026-04-27). Captures specific shapes that crashed with
/// ArgumentOutOfRangeException for follow-up fixes.
/// </summary>
public class PropertyPathRegressionTests : IDisposable
{
    private readonly string _testDir;

    public PropertyPathRegressionTests()
    {
        var tempPath = TempPath.Test("path_regression");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void SequenceWithZeroOrMore_DoesNotThrowArgumentOutOfRange()
    {
        // WDBench c2rpqs/00001-style: sequence path with embedded ZeroOrMore.
        //   SELECT * WHERE { ?x (P31/(P279)*) <Q3> }
        // Pre-fix: synthetic sequence variables produced by ExpandSequencePath have
        // negative Term.Start as a marker; QueryPlanner.ComputeVariableHash sliced
        // source unconditionally and threw.
        var dir = Path.Combine(_testDir, "seq_zom");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);
        store.AddCurrent("<http://ex/Q1>", "<http://ex/P31>", "<http://ex/Q2>");
        store.AddCurrent("<http://ex/Q2>", "<http://ex/P279>", "<http://ex/Q3>");

        var result = SparqlEngine.Query(store,
            "SELECT * WHERE { ?x (<http://ex/P31>/(<http://ex/P279>)*) <http://ex/Q3> }");

        // The query should at minimum NOT crash. Result correctness is a separate concern;
        // this test is the regression marker for the planner crash.
        Assert.True(result.Success || result.ErrorMessage is null
                    || !result.ErrorMessage.Contains("Specified argument was out of the range"),
            $"Expected no ArgumentOutOfRangeException, got: {result.ErrorMessage}");
    }

    [Fact]
    public void TransitivePath_HonorsCancellationToken()
    {
        // 2026-04-28 WDBench cold baseline observation: c2rpqs query 00137.sparql
        // reported elapsed_us = 17,495,488,600 (4 h 51 m) for a 60 s cancellation cap;
        // paths-category lost ~547 of 660 events to the same hang shape.
        //
        // Root cause: TriplePatternScan.InitializeTransitive (and DiscoverGroupedSequenceStartNodes,
        // ExecuteGroupedSequence, ExecuteInverseGroupedSequence) walked enumerator MoveNext
        // loops with no QueryCancellation.ThrowIfCancellationRequested check inside. A
        // ZeroOrMore property path with both subject and object unbound forces a whole-graph
        // scan; the cancellation token would fire but the inner MoveNext loop didn't sample
        // it, leaving the query to run for hours past its timeout.
        //
        // This test wires the cancellation through a transitive-path query whose execution
        // path goes through every patched site (InitializeTransitive's discoveryEnumerator
        // and allTriplesEnumerator on the ZeroOrMore branch, plus the BFS inner loop). It
        // is a smoke test for the wiring, not a stress test for hang detection — the deep
        // validation is re-running WDBench paths + c2rpqs against wiki-21b-ref. With ~500
        // triples and a pre-cancelled token, an unfixed code path would still complete the
        // discovery loops without checking the token; with the fix, the first MoveNext
        // body samples the token and unwinds.
        var dir = Path.Combine(_testDir, "ct_transitive");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);

        for (int i = 0; i < 500; i++)
        {
            store.AddCurrent($"<http://ex/e{i}>", "<http://ex/p>", $"<http://ex/e{i + 1}>");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        var result = SparqlEngine.Query(store,
            "SELECT * WHERE { ?x <http://ex/p>* ?y }",
            cts.Token);
        sw.Stop();

        // The query should report failure with the cancellation reason, not a successful result.
        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Cancelled query returned in {sw.Elapsed.TotalMilliseconds:F0} ms (expected < 1000)");
    }

    /// <summary>
    /// Property-path grammar shapes surfaced by the WDBench cold baseline (2026-04-27 + rerun
    /// 2026-04-29) that were originally parse-failures in 1.7.45. The 1.7.46 parser refactor
    /// (compositional <c>ParsePathExpr</c>) closes all three shapes documented in
    /// <c>docs/limits/property-path-grammar-gaps.md</c>:
    ///   Shape 1: <c>^(P){q}</c>          — inverse-quantified single predicate
    ///   Shape 2: <c>^((A|B)){q}</c>      — inverse-quantified alternative
    ///   Shape 3: <c>(^A/B)</c>, <c>((^A/B)){q}</c> — sequence with non-trivial first element
    /// Each test verifies BOTH parse success AND correct execution against a small fixture.
    /// </summary>
    [Fact]
    public void PropertyPathShapes_ParseAndExecuteCorrectly()
    {
        var dir = Path.Combine(_testDir, "shapes");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);

        // Linear chain: A -P-> B -P-> C -P-> D
        store.AddCurrent("<http://ex/A>", "<http://ex/P>", "<http://ex/B>");
        store.AddCurrent("<http://ex/B>", "<http://ex/P>", "<http://ex/C>");
        store.AddCurrent("<http://ex/C>", "<http://ex/P>", "<http://ex/D>");
        // Family graph for Shape 2: GA-P22->A-P22->Q, GB-P25->B-P25->Q
        store.AddCurrent("<http://ex/A>", "<http://ex/P22>", "<http://ex/Q>");
        store.AddCurrent("<http://ex/B>", "<http://ex/P25>", "<http://ex/Q>");
        store.AddCurrent("<http://ex/GA>", "<http://ex/P22>", "<http://ex/A>");
        store.AddCurrent("<http://ex/GB>", "<http://ex/P25>", "<http://ex/B>");
        // Sibling graph for nested Shape 3: M-P->X, M-P->Y
        store.AddCurrent("<http://ex/M>", "<http://ex/P>", "<http://ex/X>");
        store.AddCurrent("<http://ex/M>", "<http://ex/P>", "<http://ex/Y>");

        // Shape 1 — ^(P)* from bound subject: reflexive + zero-or-more inverse steps.
        AssertBindings(store,
            "SELECT ?x WHERE { <http://ex/D> ^(<http://ex/P>)* ?x }",
            "x", "<http://ex/A>", "<http://ex/B>", "<http://ex/C>", "<http://ex/D>");

        // Shape 1 — ^(P)+ : strictly one-or-more inverse steps (no reflexive).
        AssertBindings(store,
            "SELECT ?x WHERE { <http://ex/D> ^(<http://ex/P>)+ ?x }",
            "x", "<http://ex/A>", "<http://ex/B>", "<http://ex/C>");

        // Shape 1 — ^(P)? : zero or one inverse step.
        AssertBindings(store,
            "SELECT ?x WHERE { <http://ex/D> ^(<http://ex/P>)? ?x }",
            "x", "<http://ex/C>", "<http://ex/D>");

        // Shape 2 — ^((A|B))+ from bound subject Q: ancestors via either P22 or P25, 1+ steps.
        AssertBindings(store,
            "SELECT ?x WHERE { <http://ex/Q> ^((<http://ex/P22>|<http://ex/P25>))+ ?x }",
            "x", "<http://ex/A>", "<http://ex/B>", "<http://ex/GA>", "<http://ex/GB>");

        // Shape 2 forward — ((A|B))+ from GA: descendants via P22 or P25.
        AssertBindings(store,
            "SELECT ?x WHERE { <http://ex/GA> ((<http://ex/P22>|<http://ex/P25>))+ ?x }",
            "x", "<http://ex/A>", "<http://ex/Q>");

        // Shape 3 — (^A/B): pairs (?x1, ?x2) where ?x1 is sibling of ?x2 via the chain.
        // Restrict to the linear-chain subjects {B,C,D} so the parallel sibling fixture (M->X,M->Y)
        // doesn't pollute the assertion.
        AssertBindingsPair(store,
            "SELECT ?x1 ?x2 WHERE { ?x1 (^<http://ex/P>/<http://ex/P>) ?x2 . FILTER(?x1 = <http://ex/B> || ?x1 = <http://ex/C> || ?x1 = <http://ex/D>) }",
            "x1", "x2",
            ("<http://ex/B>", "<http://ex/B>"),
            ("<http://ex/C>", "<http://ex/C>"),
            ("<http://ex/D>", "<http://ex/D>"));

        // Nested Shape 3 — ((^P/P))+ for non-degenerate sibling fixture: X and Y reach each
        // other via the shared ancestor M. Filter to the X/Y subjects to scope the assertion.
        AssertBindingsPair(store,
            "SELECT ?x1 ?x2 WHERE { ?x1 ((^<http://ex/P>/<http://ex/P>))+ ?x2 . FILTER(?x1 = <http://ex/X> || ?x1 = <http://ex/Y>) }",
            "x1", "x2",
            ("<http://ex/X>", "<http://ex/Y>"),
            ("<http://ex/Y>", "<http://ex/X>"));

        // ----- Case 2 (object-bound) — surfaced by paths/00656-00659 + c2rpqs/00504 + 00332 -----
        // The walker correctly finds ancestors of a bound object; MoveNextTransitive's binding
        // direction was flipped (always Subject=_startNode, Object=targetNode) which produced
        // 0 rows for any non-reflexive ancestor. The Case 2 binding fix swaps these when the
        // BFS started from the object position.

        // Shape 1 inverse, bound object: ?x ^(P)+ <D>.
        // Semantically: ?x ^P D ≡ D P ?x. So ?x ^(P)+ D ≡ D (P)+ ?x — descendants of D via P+.
        // For chain A→B→C→D, D has no outgoing P → no descendants. Empty.
        AssertBindings(store,
            "SELECT ?x WHERE { ?x ^(<http://ex/P>)+ <http://ex/D> }",
            "x" /* expected: empty */);

        // Symmetric counterpart: ?x ^(P)+ <A> — descendants of A via P+ → {B, C, D}.
        AssertBindings(store,
            "SELECT ?x WHERE { ?x ^(<http://ex/P>)+ <http://ex/A> }",
            "x", "<http://ex/B>", "<http://ex/C>", "<http://ex/D>");

        // Shape 1 forward, bound object: ?x (P)+ <D> — ancestors of D via P+.
        // For chain A→B→C→D, walking BACKWARD from D reaches {C, B, A}.
        AssertBindings(store,
            "SELECT ?x WHERE { ?x (<http://ex/P>)+ <http://ex/D> }",
            "x", "<http://ex/A>", "<http://ex/B>", "<http://ex/C>");

        // Shape 2 forward alternative, bound object: ?x ((P22|P25))+ <Q> — descendants reaching Q.
        // For our family fixture, all four ancestors {A, B, GA, GB} reach Q via 1+ P22|P25 steps.
        AssertBindings(store,
            "SELECT ?x WHERE { ?x ((<http://ex/P22>|<http://ex/P25>))+ <http://ex/Q> }",
            "x", "<http://ex/A>", "<http://ex/B>", "<http://ex/GA>", "<http://ex/GB>");

        // Shape 2 inverse, bound object: ?x ^((P22|P25))+ <GA> — entities that ^((P22|P25))+ reach GA,
        // i.e., entities X where GA reaches X via forward (P22|P25)+. From GA, descendants are {A, Q}.
        AssertBindings(store,
            "SELECT ?x WHERE { ?x ^((<http://ex/P22>|<http://ex/P25>))+ <http://ex/GA> }",
            "x", "<http://ex/A>", "<http://ex/Q>");
    }

    private static void AssertBindings(QuadStore store, string sparql, string variable, params string[] expected)
    {
        var r = SparqlEngine.Query(store, sparql);
        Assert.True(r.Success, $"Query failed: {r.ErrorMessage}");
        Assert.NotNull(r.Rows);
        var actual = r.Rows!.Select(row => row[variable]).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        var expectedSorted = expected.OrderBy(v => v, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedSorted, actual);
    }

    private static void AssertBindingsPair(QuadStore store, string sparql, string v1, string v2,
        params (string, string)[] expected)
    {
        var r = SparqlEngine.Query(store, sparql);
        Assert.True(r.Success, $"Query failed: {r.ErrorMessage}");
        Assert.NotNull(r.Rows);
        var actual = r.Rows!
            .Select(row => (row[v1], row[v2]))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal)
            .ToArray();
        var expectedSorted = expected
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedSorted, actual);
    }
}
