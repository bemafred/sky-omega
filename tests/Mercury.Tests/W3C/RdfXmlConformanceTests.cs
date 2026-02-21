// Licensed under the MIT License.

using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.NTriples;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// W3C RDF/XML conformance tests.
/// Runs the official W3C test suite against our RDF/XML parser.
/// </summary>
public class RdfXmlConformanceTests
{
    private readonly ITestOutputHelper _output;

    public RdfXmlConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableTheory]
    [MemberData(nameof(GetRdfXml11NegativeSyntaxTests))]
    public async Task RdfXml11_NegativeSyntax(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Negative syntax test: should throw an exception
        await using var stream = File.OpenRead(test.ActionPath);
        using var parser = new RdfXmlStreamParser(stream);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await parser.ParseAsync((s, p, o) => { });
        });

        _output.WriteLine($"Expected error: {exception.Message}");
    }

    [SkippableTheory]
    [MemberData(nameof(GetRdfXml11EvalTests))]
    public async Task RdfXml11_Eval(W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");
        Skip.If(test.ResultPath == null, "No expected result file");
        Skip.IfNot(File.Exists(test.ResultPath), $"Result file not found: {test.ResultPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Input: {test.ActionPath}");
        _output.WriteLine($"Expected: {test.ResultPath}");

        // Parse the RDF/XML input
        var actualTriples = new List<(string s, string p, string o)>();

        // Determine base URI from test file location
        var baseUri = GetW3CBaseUri(test.ActionPath);
        _output.WriteLine($"Base URI: {baseUri}");

        await using (var stream = File.OpenRead(test.ActionPath))
        using (var parser = new RdfXmlStreamParser(stream, baseUri))
        {
            await parser.ParseAsync((s, p, o) =>
            {
                actualTriples.Add((s.ToString(), p.ToString(), o.ToString()));
            });
        }

        // Parse the expected N-Triples output
        var expectedTriples = new List<(string s, string p, string o)>();

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

        // Canonicalize blank nodes for isomorphism comparison
        var actualCanonicalized = CanonicalizeBlankNodes(actualTriples);
        var expectedCanonicalized = CanonicalizeBlankNodes(expectedTriples);

        var actualSet = actualCanonicalized.ToHashSet();
        var expectedSet = expectedCanonicalized.ToHashSet();

        // Compare sets
        var missing = expectedSet.Except(actualSet).ToList();
        var extra = actualSet.Except(expectedSet).ToList();

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

    /// <summary>
    /// Canonicalize blank node IDs for graph isomorphism comparison.
    /// Uses hash-based canonicalization: blank nodes get IDs based on their
    /// structural context (predicates and non-blank-node neighbors).
    /// </summary>
    private static List<(string s, string p, string o)> CanonicalizeBlankNodes(
        List<(string s, string p, string o)> triples)
    {
        // Collect all blank nodes
        var bnodes = new HashSet<string>();
        foreach (var t in triples)
        {
            if (t.s.StartsWith("_:")) bnodes.Add(t.s);
            if (t.o.StartsWith("_:")) bnodes.Add(t.o);
        }

        if (bnodes.Count == 0)
            return triples;

        // Build signature for each blank node based on its structural context
        var signatures = new Dictionary<string, string>();
        foreach (var bnode in bnodes)
        {
            var outgoing = triples
                .Where(t => t.s == bnode)
                .Select(t => $"+{t.p}:{(t.o.StartsWith("_:") ? "B" : t.o)}")
                .OrderBy(x => x);

            var incoming = triples
                .Where(t => t.o == bnode)
                .Select(t => $"-{t.p}:{(t.s.StartsWith("_:") ? "B" : t.s)}")
                .OrderBy(x => x);

            signatures[bnode] = string.Join("|", outgoing.Concat(incoming));
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

        return triples.Select(t => (
            Canonicalize(t.s),
            t.p,
            Canonicalize(t.o)
        )).ToList();
    }

    /// <summary>
    /// Get the W3C base URI from a local file path.
    /// Converts paths like /path/to/w3c-rdf-tests/rdf/rdf11/rdf-xml/test.rdf
    /// to https://w3c.github.io/rdf-tests/rdf/rdf11/rdf-xml/test.rdf
    /// </summary>
    private static string GetW3CBaseUri(string localPath)
    {
        const string marker = "w3c-rdf-tests";
        const string w3cBase = "https://w3c.github.io/rdf-tests";

        var idx = localPath.IndexOf(marker, StringComparison.Ordinal);
        if (idx == -1)
            return localPath; // Can't derive, return as-is

        var relativePath = localPath[(idx + marker.Length)..].Replace('\\', '/');
        return w3cBase + relativePath;
    }

    public static IEnumerable<object[]> GetRdfXml11NegativeSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                "W3C RDF test suite not found. Run ./tools/update-submodules.sh to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.RdfXml11);

        if (!File.Exists(manifestPath))
            yield break;

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.NegativeSyntax))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetRdfXml11EvalTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                "W3C RDF test suite not found. Run ./tools/update-submodules.sh to initialize.");

        var parser = new W3CManifestParser();
        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.RdfXml11);

        if (!File.Exists(manifestPath))
            yield break;

        var tests = parser.ParseAsync(manifestPath).GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.Type == W3CTestType.PositiveEval))
        {
            yield return new object[] { test };
        }
    }
}
