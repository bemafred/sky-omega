// Licensed under the MIT License.

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Represents a single W3C test case extracted from a manifest file.
/// </summary>
/// <param name="Id">The test identifier (IRI or prefixed name).</param>
/// <param name="Name">The human-readable test name (mf:name).</param>
/// <param name="Comment">Optional description of what the test validates (rdfs:comment).</param>
/// <param name="Type">The type of test (syntax, evaluation, etc.).</param>
/// <param name="ActionPath">Path to the input file to be parsed/executed.</param>
/// <param name="ResultPath">Path to the expected output file (for evaluation tests).</param>
/// <param name="DataPath">Path to the data file (for SPARQL query tests).</param>
/// <param name="ManifestPath">Path to the manifest file this test came from.</param>
public sealed record W3CTestCase(
    string Id,
    string Name,
    string? Comment,
    W3CTestType Type,
    string ActionPath,
    string? ResultPath,
    string? DataPath,
    string ManifestPath)
{
    /// <summary>
    /// Gets a display name suitable for xUnit test output.
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

    /// <summary>
    /// Gets whether this is a positive test (should succeed).
    /// </summary>
    public bool IsPositive => Type is
        W3CTestType.PositiveSyntax or
        W3CTestType.PositiveEval or
        W3CTestType.QueryEval or
        W3CTestType.UpdateEval;

    /// <summary>
    /// Gets whether this is a negative test (should fail).
    /// </summary>
    public bool IsNegative => Type is
        W3CTestType.NegativeSyntax or
        W3CTestType.NegativeEval;

    /// <summary>
    /// Gets whether this test has an expected result to compare against.
    /// </summary>
    public bool HasExpectedResult => ResultPath is not null;
}

/// <summary>
/// Types of W3C tests.
/// </summary>
public enum W3CTestType
{
    /// <summary>Parser must accept the input without error.</summary>
    PositiveSyntax,

    /// <summary>Parser must reject the input with an error.</summary>
    NegativeSyntax,

    /// <summary>Parser must accept and produce output matching expected result.</summary>
    PositiveEval,

    /// <summary>Parser accepts but output must differ from (incorrect) result.</summary>
    NegativeEval,

    /// <summary>SPARQL query must execute and produce expected results.</summary>
    QueryEval,

    /// <summary>SPARQL update must execute and produce expected graph state.</summary>
    UpdateEval,

    /// <summary>Unknown or unsupported test type.</summary>
    Unknown
}

/// <summary>
/// The format being tested.
/// </summary>
public enum W3CTestFormat
{
    NTriples,
    NQuads,
    Turtle,
    TriG,
    RdfXml,
    JsonLd,
    SparqlQuery,
    SparqlUpdate,
    SparqlResults,
    Unknown
}
