// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Provides context for W3C test execution including path resolution,
/// skip lists, and test discovery utilities.
/// </summary>
public static class W3CTestContext
{
    /// <summary>
    /// Gets the root directory of the W3C RDF tests submodule.
    /// </summary>
    public static string TestsRoot { get; } = FindTestsRoot();

    /// <summary>
    /// Gets whether the W3C test suite is available (submodule initialized).
    /// </summary>
    public static bool IsAvailable => Directory.Exists(TestsRoot) &&
                                       File.Exists(Path.Combine(TestsRoot, "README.md"));

    /// <summary>
    /// Tests to skip with documented reasons.
    /// Key format: "suite/relative/path/to/test" or test ID.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SkipList { get; } = new Dictionary<string, string>
    {
        // RDF 1.2 features not yet implemented
        ["turtle12-version"] = "VERSION directive not implemented (RDF 1.2 feature)",
        ["turtle12-ann"] = "Annotation syntax not implemented (RDF 1.2 feature)",
        ["nt-ttl12-langdir"] = "Base direction (--ltr/--rtl) not implemented (RDF 1.2 feature)",

        // SPARQL 1.1 features not supported
        ["entailment"] = "Entailment regimes not supported",
        ["http-rdf-update"] = "Graph Store HTTP Protocol not implemented",
        ["protocol"] = "SPARQL Protocol tests require HTTP server",
        ["service"] = "SERVICE tests require network access",

        // Known limitations
        ["nfc"] = "NFC normalization not enforced on IRIs",
    };

    /// <summary>
    /// Known slow tests that may exceed standard timeout.
    /// Tests listed here are known to take >10 seconds due to complexity.
    /// </summary>
    public static IReadOnlyDictionary<string, int> SlowTests { get; } = new Dictionary<string, int>
    {
        // Property path tests with transitive closure
        ["pp37"] = 60000, // 60 seconds - transitive property paths on large graphs
        ["pp38"] = 60000, // 60 seconds - complex property path expressions

        // Subquery tests with cartesian products
        ["sq14"] = 45000, // 45 seconds - nested subqueries with multiple joins

        // Large dataset query tests
        ["aggregates/agg-groupconcat-02"] = 30000, // 30 seconds - large string concatenation

        // Complex UNION queries
        ["union/dawg-union-002"] = 30000, // 30 seconds - many UNION branches
    };

    /// <summary>
    /// Gets the recommended timeout for a test in milliseconds.
    /// Returns null if the test should use the default timeout.
    /// </summary>
    public static int? GetRecommendedTimeout(string testId)
    {
        // Check for exact match
        if (SlowTests.TryGetValue(testId, out var timeout))
            return timeout;

        // Check for partial match (test ID contains key)
        foreach (var (pattern, recommendedTimeout) in SlowTests)
        {
            if (testId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return recommendedTimeout;
        }

        return null; // Use default timeout
    }

    /// <summary>
    /// Checks if a test should be skipped.
    /// </summary>
    /// <param name="testId">The test identifier or path.</param>
    /// <param name="reason">The reason for skipping, if applicable.</param>
    /// <returns>True if the test should be skipped.</returns>
    public static bool ShouldSkip(string testId, out string? reason)
    {
        reason = null;

        // Check for exact match
        if (SkipList.TryGetValue(testId, out reason))
            return true;

        // Check for partial match (e.g., skip entire category)
        foreach (var (pattern, skipReason) in SkipList)
        {
            if (testId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                reason = skipReason;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the path to a specific test suite manifest.
    /// </summary>
    public static string GetManifestPath(W3CTestSuite suite) => suite switch
    {
        W3CTestSuite.NTriples11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-n-triples", "manifest.ttl"),
        W3CTestSuite.NTriples12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-n-triples", "manifest.ttl"),
        W3CTestSuite.NQuads11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-n-quads", "manifest.ttl"),
        W3CTestSuite.NQuads12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-n-quads", "manifest.ttl"),
        W3CTestSuite.Turtle11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-turtle", "manifest.ttl"),
        W3CTestSuite.Turtle12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-turtle", "manifest.ttl"),
        W3CTestSuite.TriG11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-trig", "manifest.ttl"),
        W3CTestSuite.TriG12 => Path.Combine(TestsRoot, "rdf", "rdf12", "rdf-trig", "manifest.ttl"),
        W3CTestSuite.RdfXml11 => Path.Combine(TestsRoot, "rdf", "rdf11", "rdf-xml", "manifest.ttl"),
        W3CTestSuite.Sparql11Query => Path.Combine(TestsRoot, "sparql", "sparql11", "manifest-sparql11-query.ttl"),
        W3CTestSuite.Sparql11Update => Path.Combine(TestsRoot, "sparql", "sparql11", "manifest-sparql11-update.ttl"),
        _ => throw new ArgumentException($"Unknown test suite: {suite}", nameof(suite))
    };

    /// <summary>
    /// Gets the format being tested by a test suite.
    /// </summary>
    public static W3CTestFormat GetTestFormat(W3CTestSuite suite) => suite switch
    {
        W3CTestSuite.NTriples11 or W3CTestSuite.NTriples12 => W3CTestFormat.NTriples,
        W3CTestSuite.NQuads11 or W3CTestSuite.NQuads12 => W3CTestFormat.NQuads,
        W3CTestSuite.Turtle11 or W3CTestSuite.Turtle12 => W3CTestFormat.Turtle,
        W3CTestSuite.TriG11 or W3CTestSuite.TriG12 => W3CTestFormat.TriG,
        W3CTestSuite.RdfXml11 => W3CTestFormat.RdfXml,
        W3CTestSuite.Sparql11Query => W3CTestFormat.SparqlQuery,
        W3CTestSuite.Sparql11Update => W3CTestFormat.SparqlUpdate,
        _ => W3CTestFormat.Unknown
    };

    /// <summary>
    /// Loads test cases from a manifest, filtering by type.
    /// </summary>
    public static async Task<IReadOnlyList<W3CTestCase>> LoadTestCasesAsync(
        W3CTestSuite suite,
        W3CTestType? filterType = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Array.Empty<W3CTestCase>();

        var manifestPath = GetManifestPath(suite);
        if (!File.Exists(manifestPath))
            return Array.Empty<W3CTestCase>();

        var parser = new W3CManifestParser();
        var tests = await parser.ParseAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        if (filterType.HasValue)
            tests = tests.Where(t => t.Type == filterType.Value).ToList();

        return tests;
    }

    /// <summary>
    /// Gets test cases suitable for xUnit Theory data.
    /// Returns: [displayName, testCase]
    /// </summary>
    public static async Task<IEnumerable<object[]>> GetTheoryDataAsync(
        W3CTestSuite suite,
        W3CTestType? filterType = null,
        CancellationToken cancellationToken = default)
    {
        var tests = await LoadTestCasesAsync(suite, filterType, cancellationToken);
        return tests.Select(t => new object[] { t.DisplayName, t });
    }

    private static string FindTestsRoot([CallerFilePath] string? callerPath = null)
    {
        // Start from the test file location and walk up to find the submodule
        var dir = callerPath != null
            ? Path.GetDirectoryName(callerPath)
            : Directory.GetCurrentDirectory();

        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "w3c-rdf-tests");
            if (Directory.Exists(candidate))
                return candidate;

            // Also check for direct w3c-rdf-tests folder (when running from tests directory)
            candidate = Path.Combine(dir, "w3c-rdf-tests");
            if (Directory.Exists(candidate))
                return candidate;

            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: relative to current directory
        return Path.Combine(Directory.GetCurrentDirectory(), "tests", "w3c-rdf-tests");
    }
}

/// <summary>
/// Available W3C test suites.
/// </summary>
public enum W3CTestSuite
{
    // RDF 1.1 Parser Tests
    NTriples11,
    NQuads11,
    Turtle11,
    TriG11,
    RdfXml11,

    // RDF 1.2 Parser Tests (includes 1.1)
    NTriples12,
    NQuads12,
    Turtle12,
    TriG12,

    // SPARQL 1.1 Tests
    Sparql11Query,
    Sparql11Update,
}
