// Licensed under the MIT License.

using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.NTriples;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// W3C Turtle conformance tests.
/// Runs the official W3C test suite against our Turtle parser.
/// </summary>
public class TurtleConformanceTests
{
    private readonly ITestOutputHelper _output;

    public TurtleConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableTheory]
    [MemberData(nameof(GetTurtle11PositiveSyntaxTests))]
    public async Task Turtle11_PositiveSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Positive syntax test: should parse without error
        var triples = new List<(string s, string p, string o)>();

        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new TurtleStreamParser(stream);

        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        _output.WriteLine($"Parsed {triples.Count} triples");
    }

    [SkippableTheory]
    [MemberData(nameof(GetTurtle11NegativeSyntaxTests))]
    public async Task Turtle11_NegativeSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Negative syntax test: should throw an exception
        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new TurtleStreamParser(stream);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await parser.ParseAsync((s, p, o) => { });
        });

        _output.WriteLine($"Expected error: {exception.Message}");
    }

    [SkippableTheory]
    [MemberData(nameof(GetTurtle11EvalTests))]
    public async Task Turtle11_Eval(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");
        Skip.If(test.ResultPath == null, "No expected result file");
        Skip.IfNot(File.Exists(test.ResultPath), $"Result file not found: {test.ResultPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Input: {test.ActionPath}");
        _output.WriteLine($"Expected: {test.ResultPath}");

        // Parse the Turtle input
        var actualTriples = new HashSet<(string s, string p, string o)>();

        await using (var stream = File.OpenRead(test.ActionPath))
        using (var parser = new TurtleStreamParser(stream))
        {
            await parser.ParseAsync((s, p, o) =>
            {
                actualTriples.Add((s.ToString(), p.ToString(), o.ToString()));
            });
        }

        // Parse the expected N-Triples output
        var expectedTriples = new HashSet<(string s, string p, string o)>();

        await using (var stream = File.OpenRead(test.ResultPath))
        using (var parser = new NTriplesStreamParser(stream))
        {
            await parser.ParseAsync((s, p, o) =>
            {
                expectedTriples.Add((s.ToString(), p.ToString(), o.ToString()));
            });
        }

        _output.WriteLine($"Actual: {actualTriples.Count} triples");
        _output.WriteLine($"Expected: {expectedTriples.Count} triples");

        // Compare sets
        var missing = expectedTriples.Except(actualTriples).ToList();
        var extra = actualTriples.Except(expectedTriples).ToList();

        if (missing.Count > 0)
        {
            _output.WriteLine($"Missing {missing.Count} triples:");
            foreach (var t in missing.Take(10))
                _output.WriteLine($"  {t.s} {t.p} {t.o}");
        }

        if (extra.Count > 0)
        {
            _output.WriteLine($"Extra {extra.Count} triples:");
            foreach (var t in extra.Take(10))
                _output.WriteLine($"  {t.s} {t.p} {t.o}");
        }

        Assert.Empty(missing);
        Assert.Empty(extra);
    }

    public static IEnumerable<object[]> GetTurtle11PositiveSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.Turtle11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetTurtle11NegativeSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.Turtle11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.NegativeSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetTurtle11EvalTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.Turtle11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveEval))
        {
            yield return new object[] { test };
        }
    }
}
