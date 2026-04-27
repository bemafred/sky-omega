using System;
using System.IO;
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
}
