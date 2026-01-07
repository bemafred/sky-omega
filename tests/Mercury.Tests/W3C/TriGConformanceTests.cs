// Licensed under the MIT License.

using SkyOmega.Mercury.TriG;
using SkyOmega.Mercury.NQuads;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// W3C TriG conformance tests.
/// Runs the official W3C test suite against our TriG parser.
/// </summary>
public class TriGConformanceTests
{
    private readonly ITestOutputHelper _output;

    public TriGConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableTheory]
    [MemberData(nameof(GetTriG11PositiveSyntaxTests))]
    public async Task TriG11_PositiveSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Compute document base URI from test ID
        var documentBaseUri = GetDocumentBaseUri(test);

        // Positive syntax test: should parse without error
        var quads = new List<(string s, string p, string o, string g)>();

        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new TriGStreamParser(stream, documentBaseUri);

        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        _output.WriteLine($"Parsed {quads.Count} quads");
    }

    [SkippableTheory]
    [MemberData(nameof(GetTriG11NegativeSyntaxTests))]
    public async Task TriG11_NegativeSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Compute document base URI from test ID
        var documentBaseUri = GetDocumentBaseUri(test);

        // Negative syntax test: should throw an exception
        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new TriGStreamParser(stream, documentBaseUri);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await parser.ParseAsync((s, p, o, g) => { });
        });

        _output.WriteLine($"Expected error: {exception.Message}");
    }

    [SkippableTheory]
    [MemberData(nameof(GetTriG11EvalTests))]
    public async Task TriG11_Eval(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");
        Skip.If(test.ResultPath == null, "No expected result file");
        Skip.IfNot(File.Exists(test.ResultPath), $"Result file not found: {test.ResultPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Input: {test.ActionPath}");
        _output.WriteLine($"Expected: {test.ResultPath}");

        // Compute document base URI from test ID
        var documentBaseUri = GetDocumentBaseUri(test);
        _output.WriteLine($"Base URI: {documentBaseUri}");

        // Parse the TriG input
        var actualQuads = new List<(string s, string p, string o, string g)>();

        await using (var stream = File.OpenRead(test.ActionPath))
        using (var parser = new TriGStreamParser(stream, documentBaseUri))
        {
            await parser.ParseAsync((s, p, o, g) =>
            {
                actualQuads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
            });
        }

        // Parse the expected N-Quads output
        var expectedQuads = new List<(string s, string p, string o, string g)>();

        await using (var stream = File.OpenRead(test.ResultPath))
        using (var parser = new NQuadsStreamParser(stream))
        {
            await parser.ParseAsync((s, p, o, g) =>
            {
                expectedQuads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
            });
        }

        _output.WriteLine($"Actual: {actualQuads.Count} quads");
        _output.WriteLine($"Expected: {expectedQuads.Count} quads");

        // Canonicalize blank nodes for isomorphism comparison
        var actualCanonicalized = CanonicalizeBlankNodes(actualQuads);
        var expectedCanonicalized = CanonicalizeBlankNodes(expectedQuads);

        var actualSet = actualCanonicalized.ToHashSet();
        var expectedSet = expectedCanonicalized.ToHashSet();

        // Compare sets
        var missing = expectedSet.Except(actualSet).ToList();
        var extra = actualSet.Except(expectedSet).ToList();

        if (missing.Count > 0)
        {
            _output.WriteLine($"Missing {missing.Count} quads:");
            foreach (var q in missing.Take(10))
                _output.WriteLine($"  {q.s} {q.p} {q.o} {q.g}");
        }

        if (extra.Count > 0)
        {
            _output.WriteLine($"Extra {extra.Count} quads:");
            foreach (var q in extra.Take(10))
                _output.WriteLine($"  {q.s} {q.p} {q.o} {q.g}");
        }

        Assert.Empty(missing);
        Assert.Empty(extra);
    }

    /// <summary>
    /// Canonicalize blank node IDs for graph isomorphism comparison.
    /// Uses hash-based canonicalization: blank nodes get IDs based on their
    /// structural context (predicates and non-blank-node neighbors).
    /// </summary>
    private static List<(string s, string p, string o, string g)> CanonicalizeBlankNodes(
        List<(string s, string p, string o, string g)> quads)
    {
        // Collect all blank nodes
        var bnodes = new HashSet<string>();
        foreach (var q in quads)
        {
            if (q.s.StartsWith("_:")) bnodes.Add(q.s);
            if (q.o.StartsWith("_:")) bnodes.Add(q.o);
            if (q.g.StartsWith("_:")) bnodes.Add(q.g);
        }

        if (bnodes.Count == 0)
            return quads;

        // Build signature for each blank node based on its structural context
        var signatures = new Dictionary<string, string>();
        foreach (var bnode in bnodes)
        {
            var outgoing = quads
                .Where(q => q.s == bnode)
                .Select(q => $"+{q.p}:{(q.o.StartsWith("_:") ? "B" : q.o)}:{(q.g.StartsWith("_:") ? "G" : q.g)}")
                .OrderBy(x => x);

            var incoming = quads
                .Where(q => q.o == bnode)
                .Select(q => $"-{q.p}:{(q.s.StartsWith("_:") ? "B" : q.s)}:{(q.g.StartsWith("_:") ? "G" : q.g)}")
                .OrderBy(x => x);

            var asGraph = quads
                .Where(q => q.g == bnode)
                .Select(q => $"@{q.p}:{(q.s.StartsWith("_:") ? "B" : q.s)}:{(q.o.StartsWith("_:") ? "B" : q.o)}")
                .OrderBy(x => x);

            signatures[bnode] = string.Join("|", outgoing.Concat(incoming).Concat(asGraph));
        }

        // Sort blank nodes by signature, then by original ID for stability
        var sortedBnodes = bnodes
            .OrderBy(b => signatures[b])
            .ThenBy(b => b)
            .ToList();

        // Create canonical mapping
        var bnodeMap = new Dictionary<string, string>();
        for (int i = 0; i < sortedBnodes.Count; i++)
        {
            bnodeMap[sortedBnodes[i]] = $"_:c{i}";
        }

        string Canonicalize(string term)
        {
            if (term.StartsWith("_:") && bnodeMap.TryGetValue(term, out var canonical))
                return canonical;
            return term;
        }

        return quads.Select(q => (
            Canonicalize(q.s),
            q.p,
            Canonicalize(q.o),
            Canonicalize(q.g)
        )).ToList();
    }

    /// <summary>
    /// Computes the document base URI from the test case.
    /// The W3C test suite uses URIs like https://w3c.github.io/rdf-tests/rdf/rdf11/rdf-trig/file.trig
    /// </summary>
    private static string GetDocumentBaseUri(W3CTestCase test)
    {
        // The test ID contains the manifest URI path
        // e.g., file:///Users/.../manifest.ttl#test-name
        // We need to derive the W3C public URL from the action file name
        var fileName = Path.GetFileName(test.ActionPath);
        return $"https://w3c.github.io/rdf-tests/rdf/rdf11/rdf-trig/{fileName}";
    }

    public static IEnumerable<object[]> GetTriG11PositiveSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.TriG11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetTriG11NegativeSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.TriG11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.NegativeSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetTriG11EvalTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.TriG11);

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveEval))
        {
            yield return new object[] { test };
        }
    }
}
