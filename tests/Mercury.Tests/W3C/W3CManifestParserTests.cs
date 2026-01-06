// Licensed under the MIT License.

using Xunit;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Tests for W3CManifestParser functionality.
/// </summary>
public class W3CManifestParserTests
{
    [SkippableFact]
    public async Task ParseNTriples11Manifest_ReturnsTests()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NTriples11);

        var tests = await parser.ParseAsync(manifestPath);

        Assert.NotEmpty(tests);
        Assert.All(tests, t => Assert.NotNull(t.ActionPath));
        Assert.Contains(tests, t => t.Type == W3CTestType.PositiveSyntax);
        Assert.Contains(tests, t => t.Type == W3CTestType.NegativeSyntax);
    }

    [SkippableFact]
    public async Task ParseTurtle11Manifest_ReturnsEvalTests()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.Turtle11);

        var tests = await parser.ParseAsync(manifestPath);

        Assert.NotEmpty(tests);

        var evalTests = tests.Where(t => t.Type == W3CTestType.PositiveEval).ToList();
        Assert.NotEmpty(evalTests);

        // Eval tests should have result paths
        Assert.All(evalTests, t => Assert.NotNull(t.ResultPath));
    }

    [SkippableFact]
    public async Task ParseTurtle12Manifest_IncludesTurtle11Tests()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var turtle12Path = W3CTestContext.GetManifestPath(W3CTestSuite.Turtle12);

        var tests = await parser.ParseAsync(turtle12Path);

        // Turtle 1.2 manifest includes Turtle 1.1 tests via mf:include
        Assert.NotEmpty(tests);

        // Should have tests from multiple manifest files
        var manifestPaths = tests.Select(t => t.ManifestPath).Distinct().ToList();
        Assert.True(manifestPaths.Count >= 2, "Expected tests from multiple manifests due to mf:include");
    }

    [SkippableFact]
    public async Task ParseNQuads11Manifest_ReturnsTests()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NQuads11);

        var tests = await parser.ParseAsync(manifestPath);

        Assert.NotEmpty(tests);
        Assert.Contains(tests, t => t.Type == W3CTestType.PositiveSyntax);
    }

    [SkippableFact]
    public async Task ParseTriG11Manifest_ReturnsTests()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.TriG11);

        var tests = await parser.ParseAsync(manifestPath);

        Assert.NotEmpty(tests);
        Assert.Contains(tests, t => t.Type == W3CTestType.PositiveSyntax);
    }

    [SkippableFact]
    public async Task ParseSparql11Manifest_ReturnsQueryTests()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        // Check if the SPARQL manifest exists (may be in a different location)
        var aggregatesManifest = Path.Combine(W3CTestContext.TestsRoot, "sparql", "sparql11", "aggregates", "manifest.ttl");
        Skip.IfNot(File.Exists(aggregatesManifest), "SPARQL aggregates manifest not found");

        var parser = new W3CManifestParser();
        var tests = await parser.ParseAsync(aggregatesManifest);

        Assert.NotEmpty(tests);
        Assert.Contains(tests, t => t.Type == W3CTestType.QueryEval);

        // SPARQL query tests should have DataPath for qt:data
        var evalTests = tests.Where(t => t.Type == W3CTestType.QueryEval).ToList();
        Assert.NotEmpty(evalTests);
    }

    [SkippableFact]
    public async Task TestCase_HasCorrectProperties()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NTriples11);

        var tests = await parser.ParseAsync(manifestPath);
        var test = tests.FirstOrDefault(t => !string.IsNullOrEmpty(t.Name));

        Assert.NotNull(test);
        Assert.NotEmpty(test.Id);
        Assert.NotEmpty(test.Name);
        Assert.NotEmpty(test.ActionPath);
        Assert.NotEmpty(test.ManifestPath);
        Assert.NotEqual(W3CTestType.Unknown, test.Type);
    }

    [SkippableFact]
    public async Task TestCase_IsPositive_ReturnsCorrectly()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NTriples11);

        var tests = await parser.ParseAsync(manifestPath);

        var positiveTests = tests.Where(t => t.IsPositive).ToList();
        var negativeTests = tests.Where(t => t.IsNegative).ToList();

        Assert.NotEmpty(positiveTests);
        Assert.NotEmpty(negativeTests);
        Assert.All(positiveTests, t => Assert.False(t.IsNegative));
        Assert.All(negativeTests, t => Assert.False(t.IsPositive));
    }

    [SkippableFact]
    public async Task ActionPaths_AreResolved()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NTriples11);

        var tests = await parser.ParseAsync(manifestPath);
        var firstTest = tests.FirstOrDefault();

        Assert.NotNull(firstTest);
        Assert.True(Path.IsPathRooted(firstTest.ActionPath), "Action path should be absolute");

        // The file should exist
        Assert.True(File.Exists(firstTest.ActionPath),
            $"Test file should exist: {firstTest.ActionPath}");
    }

    [SkippableFact]
    public async Task ResultPaths_AreResolved_ForEvalTests()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.Turtle11);

        var tests = await parser.ParseAsync(manifestPath);
        var evalTest = tests.FirstOrDefault(t => t.Type == W3CTestType.PositiveEval);

        Assert.NotNull(evalTest);
        Assert.NotNull(evalTest.ResultPath);
        Assert.True(Path.IsPathRooted(evalTest.ResultPath), "Result path should be absolute");
        Assert.True(File.Exists(evalTest.ResultPath),
            $"Result file should exist: {evalTest.ResultPath}");
    }

    [Fact]
    public void W3CTestContext_SkipList_Contains_KnownPatterns()
    {
        Assert.True(W3CTestContext.ShouldSkip("entailment/test1", out var reason));
        Assert.NotNull(reason);

        Assert.True(W3CTestContext.ShouldSkip("protocol/test1", out _));
        Assert.True(W3CTestContext.ShouldSkip("http-rdf-update/test1", out _));
    }

    [Fact]
    public void W3CTestContext_ShouldSkip_ReturnsFalse_ForNormalTests()
    {
        Assert.False(W3CTestContext.ShouldSkip("turtle-syntax-test-01", out _));
        Assert.False(W3CTestContext.ShouldSkip("nt-syntax-uri-01", out _));
    }

    [SkippableFact]
    public async Task LoadTestCasesAsync_FiltersbyType()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var positiveTests = await W3CTestContext.LoadTestCasesAsync(
            W3CTestSuite.NTriples11,
            W3CTestType.PositiveSyntax);

        Assert.NotEmpty(positiveTests);
        Assert.All(positiveTests, t => Assert.Equal(W3CTestType.PositiveSyntax, t.Type));
    }

    [SkippableFact]
    public async Task TestCountsAreReasonable()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var ntTests = await W3CTestContext.LoadTestCasesAsync(W3CTestSuite.NTriples11);
        var turtleTests = await W3CTestContext.LoadTestCasesAsync(W3CTestSuite.Turtle11);

        // Based on W3C test suite sizes, we expect:
        // N-Triples: ~100+ tests
        // Turtle: ~200+ tests
        Assert.True(ntTests.Count >= 50, $"Expected at least 50 N-Triples tests, got {ntTests.Count}");
        Assert.True(turtleTests.Count >= 100, $"Expected at least 100 Turtle tests, got {turtleTests.Count}");
    }
}
