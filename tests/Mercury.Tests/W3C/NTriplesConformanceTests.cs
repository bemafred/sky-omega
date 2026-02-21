// Licensed under the MIT License.

using SkyOmega.Mercury.NTriples;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// W3C N-Triples conformance tests.
/// Runs the official W3C test suite against our N-Triples parser.
/// </summary>
public class NTriplesConformanceTests
{
    private readonly ITestOutputHelper _output;

    public NTriplesConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableTheory]
    [MemberData(nameof(GetNTriples11PositiveSyntaxTests))]
    public async Task NTriples11_PositiveSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Positive syntax test: should parse without error
        var triples = new List<(string s, string p, string o)>();

        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new NTriplesStreamParser(stream);

        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        _output.WriteLine($"Parsed {triples.Count} triples");
    }

    [SkippableTheory]
    [MemberData(nameof(GetNTriples11NegativeSyntaxTests))]
    public async Task NTriples11_NegativeSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Negative syntax test: should throw an exception
        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new NTriplesStreamParser(stream);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await parser.ParseAsync((s, p, o) => { });
        });

        _output.WriteLine($"Expected error: {exception.Message}");
    }

    public static IEnumerable<object[]> GetNTriples11PositiveSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                "W3C RDF test suite not found. Run ./tools/update-submodules.sh to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NTriples11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetNTriples11NegativeSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                "W3C RDF test suite not found. Run ./tools/update-submodules.sh to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NTriples11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.NegativeSyntax))
        {
            yield return new object[] { test };
        }
    }
}
