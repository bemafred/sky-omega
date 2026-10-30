// Licensed under the MIT License.

using SkyOmega.Mercury.NQuads;
using Xunit;

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

    [Theory]
    [MemberData(nameof(GetNQuadsPositiveSyntaxTests))]
    public async Task NQuads_PositiveSyntax(W3CTestCase test)
    {
        Assert.SkipUnless(W3CTestContext.IsAvailable, "W3C test suite not available");
        if (W3CTestContext.IsKnownFailure(test.Id, out var cause))
            Assert.SkipWhen(true, cause);
        Assert.SkipUnless(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Positive syntax test: should parse without error
        var quads = new List<(string s, string p, string o, string g)>();

        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new NQuadsStreamParser(stream);

        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        }, TestContext.Current.CancellationToken);

        _output.WriteLine($"Parsed {quads.Count} quads");
    }

    [Theory]
    [MemberData(nameof(GetNQuadsNegativeSyntaxTests))]
    public async Task NQuads_NegativeSyntax(W3CTestCase test)
    {
        Assert.SkipUnless(W3CTestContext.IsAvailable, "W3C test suite not available");
        if (W3CTestContext.IsKnownFailure(test.Id, out var cause))
            Assert.SkipWhen(true, cause);
        Assert.SkipUnless(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Negative syntax test: should throw an exception
        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new NQuadsStreamParser(stream);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await parser.ParseAsync((s, p, o, g) => { }, TestContext.Current.CancellationToken);
        });

        _output.WriteLine($"Expected error: {exception.Message}");
    }

    [Theory]
    [MemberData(nameof(GetNQuadsCanonicalFormTests))]
    public async Task NQuads_CanonicalForm(W3CTestCase test)
    {
        Assert.SkipUnless(W3CTestContext.IsAvailable, "W3C test suite not available");
        if (W3CTestContext.IsKnownFailure(test.Id, out var cause))
            Assert.SkipWhen(true, cause);
        Assert.SkipUnless(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");
        Assert.SkipWhen(test.ResultPath == null, "No expected result file");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Canonical-form test (RDF 1.2): the input, parsed and written back by Mercury's writer, is the
        // expected file character for character.
        var output = new StringWriter();
        using (var writer = new NQuadsStreamWriter(output))
        {
            await using var stream = File.OpenRead(test.ActionPath);
            using var parser = new NQuadsStreamParser(stream);
            await parser.ParseAsync((s, p, o, g) => writer.WriteQuad(s, p, o, g), TestContext.Current.CancellationToken);
        }
        string expected = await File.ReadAllTextAsync(test.ResultPath!, TestContext.Current.CancellationToken);

        Assert.Equal(expected, output.ToString());
    }

    public static IEnumerable<object[]> GetNQuadsCanonicalFormTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{W3CTestContext.TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(W3CTestContext.TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NQuads12);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.CanonicalForm))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetNQuadsPositiveSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{W3CTestContext.TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(W3CTestContext.TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NQuads12);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetNQuadsNegativeSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{W3CTestContext.TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(W3CTestContext.TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NQuads12);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.NegativeSyntax))
        {
            yield return new object[] { test };
        }
    }
}
