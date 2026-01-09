// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using SkyOmega.Mercury.JsonLd;
using SkyOmega.Mercury.NQuads;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// W3C JSON-LD conformance tests.
/// Runs the official W3C JSON-LD API test suite against our JSON-LD parser.
/// Tests the toRdf algorithm (JSON-LD → RDF conversion).
/// </summary>
public class JsonLdConformanceTests
{
    private readonly ITestOutputHelper _output;

    public JsonLdConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableTheory]
    [MemberData(nameof(GetToRdfPositiveTests))]
    public async Task ToRdf_PositiveEval(JsonLdTestCase test)
    {
        Skip.IfNot(JsonLdTestContext.IsAvailable, "JSON-LD test suite not available");
        Skip.IfNot(File.Exists(test.InputPath), $"Input file not found: {test.InputPath}");
        // PositiveSyntaxTest has no expected output file - just tests parsing succeeds
        Skip.If(test.ExpectPath == null && !test.IsSyntaxTest, "No expected result file");
        Skip.If(!test.IsSyntaxTest && !File.Exists(test.ExpectPath), $"Expected file not found: {test.ExpectPath}");

        // Skip tests requiring features not yet implemented
        if (test.Option?.SpecVersion == "json-ld-1.0")
        {
            Skip.If(true, "JSON-LD 1.0 specific tests not supported");
        }
        if (test.Option?.ProduceGeneralizedRdf == true)
        {
            Skip.If(true, "Generalized RDF not supported");
        }

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Input: {test.InputPath}");
        _output.WriteLine($"Expected: {test.ExpectPath}");
        if (test.IsSyntaxTest)
            _output.WriteLine("(Syntax test - parsing should succeed, no output comparison)");

        // Parse the JSON-LD input
        var actualQuads = new List<(string s, string p, string o, string g)>();
        var baseUri = test.Option?.Base ?? test.BaseIri;

        _output.WriteLine($"Base URI: {baseUri}");

        await using (var stream = File.OpenRead(test.InputPath))
        using (var parser = new JsonLdStreamParser(stream, baseUri))
        {
            await parser.ParseAsync((s, p, o, g) =>
            {
                actualQuads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
            });
        }

        // For syntax tests, we just verify parsing succeeded (no exception)
        if (test.IsSyntaxTest)
        {
            _output.WriteLine($"Syntax test passed - parsed {actualQuads.Count} quads without error");
            return;
        }

        // Parse the expected N-Quads output
        var expectedQuads = new List<(string s, string p, string o, string g)>();

        await using (var stream = File.OpenRead(test.ExpectPath!))
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

    [SkippableTheory]
    [MemberData(nameof(GetToRdfNegativeTests))]
    public async Task ToRdf_NegativeEval(JsonLdTestCase test)
    {
        Skip.IfNot(JsonLdTestContext.IsAvailable, "JSON-LD test suite not available");
        Skip.IfNot(File.Exists(test.InputPath), $"Input file not found: {test.InputPath}");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Input: {test.InputPath}");
        _output.WriteLine($"Expected error: {test.ExpectErrorCode}");

        var baseUri = test.Option?.Base ?? test.BaseIri;

        // Negative test: should throw an exception
        await using var stream = File.OpenRead(test.InputPath);
        using var parser = new JsonLdStreamParser(stream, baseUri);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await parser.ParseAsync((s, p, o, g) => { });
        });

        _output.WriteLine($"Actual error: {exception.Message}");
    }

    /// <summary>
    /// Canonicalize blank node IDs for graph isomorphism comparison.
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
                .Select(q => $"+{q.p}:{(q.o.StartsWith("_:") ? "B" : q.o)}")
                .OrderBy(x => x);

            var incoming = quads
                .Where(q => q.o == bnode)
                .Select(q => $"-{q.p}:{(q.s.StartsWith("_:") ? "B" : q.s)}")
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

        return quads.Select(q => (
            Canonicalize(q.s),
            q.p,
            Canonicalize(q.o),
            Canonicalize(q.g)
        )).ToList();
    }

    public static IEnumerable<object[]> GetToRdfPositiveTests()
    {
        if (!JsonLdTestContext.IsAvailable)
            yield break;

        var tests = JsonLdTestContext.LoadToRdfTests().GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => t.IsPositive))
        {
            yield return new object[] { test };
        }
    }

    public static IEnumerable<object[]> GetToRdfNegativeTests()
    {
        if (!JsonLdTestContext.IsAvailable)
            yield break;

        var tests = JsonLdTestContext.LoadToRdfTests().GetAwaiter().GetResult();

        foreach (var test in tests.Where(t => !t.IsPositive))
        {
            yield return new object[] { test };
        }
    }
}

/// <summary>
/// Context for JSON-LD W3C tests.
/// </summary>
public static class JsonLdTestContext
{
    public static string TestsRoot { get; } = FindTestsRoot();

    public static bool IsAvailable => Directory.Exists(TestsRoot) &&
                                       File.Exists(Path.Combine(TestsRoot, "tests", "toRdf-manifest.jsonld"));

    public static string GetManifestPath(string manifest) =>
        Path.Combine(TestsRoot, "tests", manifest);

    public static async Task<List<JsonLdTestCase>> LoadToRdfTests()
    {
        var manifestPath = GetManifestPath("toRdf-manifest.jsonld");
        if (!File.Exists(manifestPath))
            return new List<JsonLdTestCase>();

        var json = await File.ReadAllTextAsync(manifestPath);
        var doc = JsonDocument.Parse(json);

        var tests = new List<JsonLdTestCase>();
        var baseIri = "https://w3c.github.io/json-ld-api/tests/";

        if (doc.RootElement.TryGetProperty("baseIri", out var baseIriElement))
            baseIri = baseIriElement.GetString() ?? baseIri;

        if (doc.RootElement.TryGetProperty("sequence", out var sequence))
        {
            foreach (var testElement in sequence.EnumerateArray())
            {
                var test = ParseTestCase(testElement, baseIri);
                if (test != null)
                    tests.Add(test);
            }
        }

        return tests;
    }

    private static JsonLdTestCase? ParseTestCase(JsonElement element, string baseIri)
    {
        var id = element.TryGetProperty("@id", out var idProp) ? idProp.GetString() : null;
        var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var purpose = element.TryGetProperty("purpose", out var purposeProp) ? purposeProp.GetString() : null;

        if (id == null || name == null)
            return null;

        // Determine test type
        var isPositive = false;
        var isSyntaxTest = false;
        var isToRdf = false;

        if (element.TryGetProperty("@type", out var typeProp))
        {
            if (typeProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in typeProp.EnumerateArray())
                {
                    var typeStr = t.GetString();
                    if (typeStr == "jld:PositiveEvaluationTest") isPositive = true;
                    // PositiveSyntaxTest tests that parsing succeeds (no expected output)
                    if (typeStr == "jld:PositiveSyntaxTest") { isPositive = true; isSyntaxTest = true; }
                    if (typeStr == "jld:ToRDFTest") isToRdf = true;
                }
            }
            else if (typeProp.ValueKind == JsonValueKind.String)
            {
                var typeStr = typeProp.GetString();
                if (typeStr == "jld:PositiveEvaluationTest") isPositive = true;
                if (typeStr == "jld:PositiveSyntaxTest") { isPositive = true; isSyntaxTest = true; }
                if (typeStr == "jld:ToRDFTest") isToRdf = true;
            }
        }

        if (!isToRdf)
            return null;

        // Get input and expected files
        var input = element.TryGetProperty("input", out var inputProp) ? inputProp.GetString() : null;
        var expect = element.TryGetProperty("expect", out var expectProp) ? expectProp.GetString() : null;
        var expectErrorCode = element.TryGetProperty("expectErrorCode", out var errorProp) ? errorProp.GetString() : null;

        if (input == null)
            return null;

        // Parse options
        JsonLdTestOption? option = null;
        if (element.TryGetProperty("option", out var optionProp))
        {
            option = new JsonLdTestOption
            {
                Base = optionProp.TryGetProperty("base", out var baseProp) ? baseProp.GetString() : null,
                SpecVersion = optionProp.TryGetProperty("specVersion", out var specProp) ? specProp.GetString() : null,
                ProduceGeneralizedRdf = optionProp.TryGetProperty("produceGeneralizedRdf", out var genProp) && genProp.GetBoolean(),
                ProcessingMode = optionProp.TryGetProperty("processingMode", out var modeProp) ? modeProp.GetString() : null,
            };
        }

        var testsDir = Path.Combine(TestsRoot, "tests");

        return new JsonLdTestCase
        {
            Id = id,
            Name = name,
            Purpose = purpose,
            IsPositive = isPositive,
            IsSyntaxTest = isSyntaxTest,
            InputPath = Path.Combine(testsDir, input),
            ExpectPath = expect != null ? Path.Combine(testsDir, expect) : null,
            ExpectErrorCode = expectErrorCode,
            BaseIri = baseIri + input,
            Option = option
        };
    }

    private static string FindTestsRoot([CallerFilePath] string? callerPath = null)
    {
        var dir = callerPath != null
            ? Path.GetDirectoryName(callerPath)
            : Directory.GetCurrentDirectory();

        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "w3c-json-ld-api");
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir, "w3c-json-ld-api");
            if (Directory.Exists(candidate))
                return candidate;

            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "tests", "w3c-json-ld-api");
    }
}

/// <summary>
/// Represents a JSON-LD test case.
/// </summary>
public class JsonLdTestCase
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Purpose { get; init; }
    public bool IsPositive { get; init; }
    public bool IsSyntaxTest { get; init; } // PositiveSyntaxTest - just tests parsing succeeds
    public required string InputPath { get; init; }
    public string? ExpectPath { get; init; }
    public string? ExpectErrorCode { get; init; }
    public required string BaseIri { get; init; }
    public JsonLdTestOption? Option { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Test options from JSON-LD manifest.
/// </summary>
public class JsonLdTestOption
{
    public string? Base { get; init; }
    public string? SpecVersion { get; init; }
    public bool ProduceGeneralizedRdf { get; init; }
    public string? ProcessingMode { get; init; }
}
