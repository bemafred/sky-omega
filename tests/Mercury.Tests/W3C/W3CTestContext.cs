// Licensed under the MIT License.

using System.Reflection;
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

        // CONSTRUCT/DESCRIBE features not yet fully implemented
        // ["agg-empty-group-count-graph"] = "COUNT without GROUP BY inside GRAPH not implemented",  // FIXED: Now supported
        // ["bindings/manifest#graph"] = "VALUES inside GRAPH binding same variable as graph name not implemented",  // Testing...
        // ["constructlist"] = "RDF collection construction in CONSTRUCT not implemented",  // FIXED: Now supported
    };

    /// <summary>
    /// The SPARQL 1.0 (DAWG) tests that failed when the suite was first run, 2026-10-28, by cause. Each entry is a
    /// known failure to fix and remove, not an exclusion: see the Mercury finding mercury-w3c-sparql10-not-run.
    /// </summary>
    private static readonly Dictionary<string, string> Sparql10Triage = new()
    {
        ["data-r2/basic/manifest#base-prefix-1"] = "SPARQL 1.0 triage: relative IRIs are not resolved against BASE",
        ["data-r2/basic/manifest#base-prefix-2"] = "SPARQL 1.0 triage: relative IRIs are not resolved against BASE",
        ["data-r2/basic/manifest#base-prefix-4"] = "SPARQL 1.0 triage: relative IRIs are not resolved against BASE",
        ["data-r2/basic/manifest#base-prefix-5"] = "SPARQL 1.0 triage: relative IRIs are not resolved against BASE",
        ["data-r2/basic/manifest#quotes-1"] = "SPARQL 1.0 triage: long string literals in a pattern",
        ["data-r2/basic/manifest#quotes-2"] = "SPARQL 1.0 triage: long string literals in a pattern",
        ["data-r2/basic/manifest#quotes-3"] = "SPARQL 1.0 triage: long string literals in a pattern",
        ["data-r2/basic/manifest#quotes-4"] = "SPARQL 1.0 triage: long string literals in a pattern",
        ["data-r2/basic/manifest#term-6"] = "SPARQL 1.0 triage: numeric literal forms in a pattern (\"456.\"^^xsd:decimal, +5)",
        ["data-r2/basic/manifest#term-7"] = "SPARQL 1.0 triage: numeric literal forms in a pattern (\"456.\"^^xsd:decimal, +5)",
        ["data-r2/basic/manifest#term-8"] = "SPARQL 1.0 triage: numeric literal forms in a pattern (\"456.\"^^xsd:decimal, +5)",
        ["data-r2/distinct/manifest#distinct-2"] = "SPARQL 1.0 triage: a simple literal and its xsd:string form are stored as two atoms (ADR-053 D1.5)",
        ["data-r2/distinct/manifest#distinct-9"] = "SPARQL 1.0 triage: a simple literal and its xsd:string form are stored as two atoms (ADR-053 D1.5)",
        ["data-r2/expr-builtin/manifest#dawg-lang-3"] = "SPARQL 1.0 triage: a pattern constant matches a language tag by case (ADR-053, language tags)",
        ["data-r2/open-world/manifest#open-eq-02"] = "SPARQL 1.0 triage: a prefixed datatype in a pattern constant is not expanded (ADR-053 D1)",
        ["data-r2/regex/manifest#dawg-regex-003"] = "SPARQL 1.0 triage: REGEX escapes and the q flag",
        ["data-r2/regex/manifest#regex-no-metacharacters"] = "SPARQL 1.0 triage: REGEX escapes and the q flag",
        ["data-r2/regex/manifest#regex-no-metacharacters-case-insensitive"] = "SPARQL 1.0 triage: REGEX escapes and the q flag",
        ["data-r2/syntax-sparql3/manifest#bnode-dot"] = "SPARQL 1.0 triage: invalid syntax accepted: a blank-node property list without a property ([] alone, [,], [;])",
        ["data-r2/syntax-sparql3/manifest#bnodes-missing-pvalues-01"] = "SPARQL 1.0 triage: invalid syntax accepted: a blank-node property list without a property ([] alone, [,], [;])",
        ["data-r2/syntax-sparql3/manifest#syn-bad-26"] = "SPARQL 1.0 triage: invalid syntax accepted: an expression the grammar does not read (a blank node in FILTER; ?x<?a&&?b>?y)",
        ["data-r2/syntax-sparql3/manifest#syn-bad-29"] = "SPARQL 1.0 triage: invalid syntax accepted: an expression the grammar does not read (a blank node in FILTER; ?x<?a&&?b>?y)",
        ["data-r2/syntax-sparql3/manifest#syn-bad-30"] = "SPARQL 1.0 triage: invalid syntax accepted: an expression the grammar does not read (a blank node in FILTER; ?x<?a&&?b>?y)",
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
    /// Optional: Include only tests matching this pattern.
    /// Set via W3C_INCLUDE_ONLY environment variable.
    /// Example: W3C_INCLUDE_ONLY=bind/ to only run bind tests.
    /// </summary>
    public static string? IncludeOnly { get; } = Environment.GetEnvironmentVariable("W3C_INCLUDE_ONLY");

    /// <summary>
    /// True when the known failures (the SPARQL 1.0 triage) run as ordinary tests rather than skipped ones, to see what
    /// each one does while fixing its cause. Set with the W3C_RUN_KNOWN_FAILURES=1 environment variable.
    /// </summary>
    public static bool RunKnownFailures { get; } = Environment.GetEnvironmentVariable("W3C_RUN_KNOWN_FAILURES") == "1";

    /// <summary>
    /// True when a test is out of scope (<see cref="SkipList"/>): a feature Mercury does not provide. Such tests are
    /// left out of a run. A known failure (the SPARQL 1.0 triage) is not: it runs as a skipped test, counted and with its
    /// cause, so the gap stays visible.
    /// </summary>
    public static bool IsOutOfScope(string testId)
    {
        if (SkipList.ContainsKey(testId))
            return true;
        foreach (var pattern in SkipList.Keys)
        {
            if (testId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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

        // If IncludeOnly is set, skip tests that DON'T match
        if (!string.IsNullOrEmpty(IncludeOnly))
        {
            if (!testId.Contains(IncludeOnly, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Not in include filter: {IncludeOnly}";
                return true;
            }
        }

        // Check for exact match
        if (SkipList.TryGetValue(testId, out reason))
            return true;

        // A SPARQL 1.0 test that is a known failure, by cause (run anyway with W3C_RUN_KNOWN_FAILURES=1)
        foreach (var (failure, cause) in Sparql10Triage)
        {
            if (RunKnownFailures)
                break;
            if (testId.Contains(failure, StringComparison.Ordinal))
            {
                reason = cause;
                return true;
            }
        }

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
        W3CTestSuite.Sparql10Query => Path.Combine(TestsRoot, "sparql", "sparql10", "manifest.ttl"),
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
        W3CTestSuite.Sparql10Query or W3CTestSuite.Sparql11Query => W3CTestFormat.SparqlQuery,
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
        // Strategy 1: MSBuild-embedded path (computed from .csproj location at build time).
        // Under NCrunch, uses $(NCrunchOriginalProjectDir) to resolve back to the real
        // source tree where git submodules live (NCrunch workspaces don't copy submodules).
        var assembly = typeof(W3CTestContext).Assembly;
        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key == "W3CRdfTestsRoot" && !string.IsNullOrEmpty(attr.Value))
                return attr.Value;
        }

        // Strategy 2: Walk up from [CallerFilePath] (fallback if metadata not available)
        var dir = callerPath != null
            ? Path.GetDirectoryName(callerPath)
            : Directory.GetCurrentDirectory();

        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "w3c-rdf-tests");
            if (Directory.Exists(candidate))
                return candidate;

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

    // SPARQL 1.0 Tests (the DAWG suite: core features the 1.1 suite does not test again)
    Sparql10Query,

    // SPARQL 1.1 Tests
    Sparql11Query,
    Sparql11Update,
}
