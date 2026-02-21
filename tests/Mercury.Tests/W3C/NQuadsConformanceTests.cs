// Licensed under the MIT License.

using SkyOmega.Mercury.NQuads;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// W3C N-Quads conformance tests.
/// Runs the official W3C test suite against our N-Quads parser.
/// </summary>
public class NQuadsConformanceTests
{
    private readonly ITestOutputHelper _output;

    public NQuadsConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableTheory]
    [MemberData(nameof(GetNQuads11PositiveSyntaxTests))]
    public async Task NQuads11_PositiveSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Positive syntax test: should parse without error
        var quads = new List<(string s, string p, string o, string g)>();

        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new NQuadsStreamParser(stream);

        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        _output.WriteLine($"Parsed {quads.Count} quads");
    }

    [SkippableTheory]
    [MemberData(nameof(GetNQuads11NegativeSyntaxTests))]
    public async Task NQuads11_NegativeSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Negative syntax test: should throw an exception
        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new NQuadsStreamParser(stream);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await parser.ParseAsync((s, p, o, g) => { });
        });

        _output.WriteLine($"Expected error: {exception.Message}");
    }

    public static IEnumerable<object[]> GetNQuads11PositiveSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                "W3C RDF test suite not found. Run ./tools/update-submodules.sh to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NQuads11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetNQuads11NegativeSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                "W3C RDF test suite not found. Run ./tools/update-submodules.sh to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NQuads11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.NegativeSyntax))
        {
            yield return new object[] { test };
        }
    }
}
