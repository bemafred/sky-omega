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
}
