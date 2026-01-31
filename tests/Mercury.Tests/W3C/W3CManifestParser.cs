// Licensed under the MIT License.

using SkyOmega.Mercury.Rdf.Turtle;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Parses W3C test manifest files (Turtle format) into test case descriptors.
/// Handles nested manifests via mf:include and resolves relative paths.
/// </summary>
public sealed class W3CManifestParser
{
    // W3C manifest vocabulary prefixes
    private const string MF = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
    private const string RDFT = "http://www.w3.org/ns/rdftest#";
    private const string RDF = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string RDFS = "http://www.w3.org/2000/01/rdf-schema#";
    private const string QT = "http://www.w3.org/2001/sw/DataAccess/tests/test-query#";
    private const string UT = "http://www.w3.org/2009/sparql/tests/test-update#";

    // Common predicates
    private static readonly string RdfType = $"<{RDF}type>";
    private static readonly string RdfFirst = $"<{RDF}first>";
    private static readonly string RdfRest = $"<{RDF}rest>";
    private static readonly string RdfNil = $"<{RDF}nil>";
    private static readonly string MfEntries = $"<{MF}entries>";
    private static readonly string MfInclude = $"<{MF}include>";
    private static readonly string MfName = $"<{MF}name>";
    private static readonly string MfAction = $"<{MF}action>";
    private static readonly string MfResult = $"<{MF}result>";
    private static readonly string RdfsComment = $"<{RDFS}comment>";
    private static readonly string RdfsLabel = $"<{RDFS}label>";
    private static readonly string QtQuery = $"<{QT}query>";
    private static readonly string QtData = $"<{QT}data>";
    private static readonly string QtGraphData = $"<{QT}graphData>";
    private static readonly string UtRequest = $"<{UT}request>";
    private static readonly string UtData = $"<{UT}data>";
    private static readonly string UtGraphData = $"<{UT}graphData>";
    private static readonly string UtGraph = $"<{UT}graph>";
    private static readonly string UtResult = $"<{UT}result>";
    private static readonly string UtSuccess = $"<{UT}success>";

    // Test type IRIs mapped to enum values
    private static readonly Dictionary<string, W3CTestType> TestTypes = new()
    {
        // RDF parser tests (N-Triples)
        [$"<{RDFT}TestNTriplesPositiveSyntax>"] = W3CTestType.PositiveSyntax,
        [$"<{RDFT}TestNTriplesNegativeSyntax>"] = W3CTestType.NegativeSyntax,
        [$"<{RDFT}TestNTriplesEval>"] = W3CTestType.PositiveEval,

        // RDF parser tests (N-Quads)
        [$"<{RDFT}TestNQuadsPositiveSyntax>"] = W3CTestType.PositiveSyntax,
        [$"<{RDFT}TestNQuadsNegativeSyntax>"] = W3CTestType.NegativeSyntax,
        [$"<{RDFT}TestNQuadsEval>"] = W3CTestType.PositiveEval,

        // RDF parser tests (Turtle)
        [$"<{RDFT}TestTurtlePositiveSyntax>"] = W3CTestType.PositiveSyntax,
        [$"<{RDFT}TestTurtleNegativeSyntax>"] = W3CTestType.NegativeSyntax,
        [$"<{RDFT}TestTurtleEval>"] = W3CTestType.PositiveEval,
        [$"<{RDFT}TestTurtleNegativeEval>"] = W3CTestType.NegativeEval,

        // RDF parser tests (TriG) - both casing variants exist in different manifests
        [$"<{RDFT}TestTriGPositiveSyntax>"] = W3CTestType.PositiveSyntax,
        [$"<{RDFT}TestTriGNegativeSyntax>"] = W3CTestType.NegativeSyntax,
        [$"<{RDFT}TestTriGEval>"] = W3CTestType.PositiveEval,
        [$"<{RDFT}TestTriGNegativeEval>"] = W3CTestType.NegativeEval,
        [$"<{RDFT}TestTrigPositiveSyntax>"] = W3CTestType.PositiveSyntax,
        [$"<{RDFT}TestTrigNegativeSyntax>"] = W3CTestType.NegativeSyntax,
        [$"<{RDFT}TestTrigEval>"] = W3CTestType.PositiveEval,
        [$"<{RDFT}TestTrigNegativeEval>"] = W3CTestType.NegativeEval,

        // RDF/XML tests
        [$"<{RDFT}TestXMLPositiveSyntax>"] = W3CTestType.PositiveSyntax,
        [$"<{RDFT}TestXMLNegativeSyntax>"] = W3CTestType.NegativeSyntax,
        [$"<{RDFT}TestXMLEval>"] = W3CTestType.PositiveEval,

        // SPARQL 1.0 tests
        [$"<{MF}QueryEvaluationTest>"] = W3CTestType.QueryEval,
        [$"<{MF}PositiveSyntaxTest>"] = W3CTestType.PositiveSyntax,
        [$"<{MF}NegativeSyntaxTest>"] = W3CTestType.NegativeSyntax,
        [$"<{MF}PositiveUpdateSyntaxTest>"] = W3CTestType.PositiveSyntax,
        [$"<{MF}NegativeUpdateSyntaxTest>"] = W3CTestType.NegativeSyntax,

        // SPARQL 1.1 tests
        [$"<{MF}PositiveSyntaxTest11>"] = W3CTestType.PositiveSyntax,
        [$"<{MF}NegativeSyntaxTest11>"] = W3CTestType.NegativeSyntax,
        [$"<{MF}PositiveUpdateSyntaxTest11>"] = W3CTestType.PositiveSyntax,
        [$"<{MF}NegativeUpdateSyntaxTest11>"] = W3CTestType.NegativeSyntax,
        [$"<{MF}UpdateEvaluationTest>"] = W3CTestType.UpdateEval,
        [$"<{MF}CSVResultFormatTest>"] = W3CTestType.QueryEval,
        [$"<{MF}ServiceDescriptionTest>"] = W3CTestType.QueryEval,
        [$"<{MF}ProtocolTest>"] = W3CTestType.QueryEval,
        [$"<{MF}GraphStoreProtocolTest>"] = W3CTestType.UpdateEval,
    };

    private readonly HashSet<string> _visitedManifests = new();

    /// <summary>
    /// Parses a manifest file and returns all test cases, including those from included manifests.
    /// </summary>
    /// <param name="manifestPath">Absolute path to the manifest.ttl file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Enumerable of test cases.</returns>
    public async Task<IReadOnlyList<W3CTestCase>> ParseAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var results = new List<W3CTestCase>();
        await ParseManifestRecursiveAsync(manifestPath, results, cancellationToken).ConfigureAwait(false);
        return results;
    }

    private async Task ParseManifestRecursiveAsync(
        string manifestPath,
        List<W3CTestCase> results,
        CancellationToken cancellationToken)
    {
        // Normalize path for duplicate detection
        var normalizedPath = Path.GetFullPath(manifestPath);
        if (!_visitedManifests.Add(normalizedPath))
            return; // Already processed

        if (!File.Exists(normalizedPath))
        {
            // Skip missing manifests silently (some includes may be optional)
            return;
        }

        var manifestDir = Path.GetDirectoryName(normalizedPath) ?? ".";

        // Parse the manifest file into an in-memory graph
        var graph = await ParseManifestFileAsync(normalizedPath, cancellationToken).ConfigureAwait(false);

        // Process mf:include directives first (recursive)
        foreach (var includePath in GetIncludedManifests(graph, manifestDir))
        {
            await ParseManifestRecursiveAsync(includePath, results, cancellationToken).ConfigureAwait(false);
        }

        // Extract test cases from this manifest
        var testCases = ExtractTestCases(graph, manifestDir, normalizedPath);
        results.AddRange(testCases);
    }

    // Use large buffer to handle manifests with long collections
    private const int ManifestBufferSize = 131072; // 128KB

    private async Task<ManifestGraph> ParseManifestFileAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var graph = new ManifestGraph();
        var baseUri = new Uri(manifestPath).AbsoluteUri;

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            using var parser = new TurtleStreamParser(stream, ManifestBufferSize);

            await parser.ParseAsync((subject, predicate, obj) =>
            {
                // Normalize IRIs - resolve relative IRIs against base
                var s = NormalizeIri(subject.ToString(), baseUri);
                var p = NormalizeIri(predicate.ToString(), baseUri);
                var o = obj.ToString();

                // Only normalize object if it looks like an IRI
                if (o.StartsWith('<') && o.EndsWith('>'))
                    o = NormalizeIri(o, baseUri);

                graph.Add(s, p, o);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log but continue - some manifests may have syntax we don't support
            Console.Error.WriteLine($"Warning: Failed to parse manifest {manifestPath}: {ex.Message}");
        }

        return graph;
    }

    private static string NormalizeIri(string iri, string baseUri)
    {
        // Handle empty IRI <> - resolves to base
        if (iri == "<>")
            return $"<{baseUri}>";

        // Handle fragment IRIs <#...>
        if (iri.StartsWith("<#"))
            return $"<{baseUri}{iri[1..^1]}>";

        return iri;
    }

    private IEnumerable<string> GetIncludedManifests(ManifestGraph graph, string manifestDir)
    {
        // Find all subjects that have mf:include
        foreach (var (subject, predicateObjects) in graph.Subjects)
        {
            if (!predicateObjects.TryGetValue(MfInclude, out var includes))
                continue;

            // mf:include points to an RDF list of manifest IRIs
            foreach (var listHead in includes)
            {
                foreach (var iri in TraverseRdfList(graph, listHead))
                {
                    var resolvedPath = ResolveIriToPath(iri, manifestDir);
                    if (resolvedPath != null)
                        yield return resolvedPath;
                }
            }
        }
    }

    private IEnumerable<W3CTestCase> ExtractTestCases(
        ManifestGraph graph,
        string manifestDir,
        string manifestPath)
    {
        // Find the manifest subject (has mf:entries)
        foreach (var (subject, predicateObjects) in graph.Subjects)
        {
            if (!predicateObjects.TryGetValue(MfEntries, out var entriesLists))
                continue;

            // mf:entries points to an RDF list of test IRIs
            foreach (var listHead in entriesLists)
            {
                foreach (var testIri in TraverseRdfList(graph, listHead))
                {
                    var testCase = ExtractSingleTestCase(graph, testIri, manifestDir, manifestPath);
                    if (testCase != null)
                        yield return testCase;
                }
            }
        }
    }

    private W3CTestCase? ExtractSingleTestCase(
        ManifestGraph graph,
        string testIri,
        string manifestDir,
        string manifestPath)
    {
        if (!graph.Subjects.TryGetValue(testIri, out var props))
            return null;

        // Get test type
        var testType = W3CTestType.Unknown;
        if (props.TryGetValue(RdfType, out var types))
        {
            foreach (var type in types)
            {
                if (TestTypes.TryGetValue(type, out var mappedType))
                {
                    testType = mappedType;
                    break;
                }
            }
        }

        // Get test name
        var name = props.TryGetValue(MfName, out var names) && names.Count > 0
            ? UnquoteLiteral(names[0])
            : testIri;

        // Get comment
        string? comment = null;
        if (props.TryGetValue(RdfsComment, out var comments) && comments.Count > 0)
            comment = UnquoteLiteral(comments[0]);

        // Get action (input file or blank node with query/data)
        string? actionPath = null;
        string? dataPath = null;
        string[]? graphDataPaths = null;
        (string Path, string GraphIri)[]? actionGraphData = null;

        if (props.TryGetValue(MfAction, out var actions) && actions.Count > 0)
        {
            var action = actions[0];

            if (action.StartsWith("_:"))
            {
                // Blank node - look up query/data predicates
                if (graph.Subjects.TryGetValue(action, out var actionProps))
                {
                    // Try qt:query first (SPARQL Query tests)
                    if (actionProps.TryGetValue(QtQuery, out var queries) && queries.Count > 0)
                        actionPath = ResolveIriToPath(queries[0], manifestDir);

                    // Try ut:request for SPARQL Update tests
                    if (actionPath == null && actionProps.TryGetValue(UtRequest, out var requests) && requests.Count > 0)
                        actionPath = ResolveIriToPath(requests[0], manifestDir);

                    // Try qt:data first (SPARQL Query tests)
                    if (actionProps.TryGetValue(QtData, out var dataFiles) && dataFiles.Count > 0)
                        dataPath = ResolveIriToPath(dataFiles[0], manifestDir);

                    // Try ut:data for SPARQL Update tests
                    if (dataPath == null && actionProps.TryGetValue(UtData, out var utDataFiles) && utDataFiles.Count > 0)
                        dataPath = ResolveIriToPath(utDataFiles[0], manifestDir);

                    // Extract qt:graphData for named graphs (can have multiple entries)
                    if (actionProps.TryGetValue(QtGraphData, out var graphDataFiles) && graphDataFiles.Count > 0)
                    {
                        graphDataPaths = graphDataFiles
                            .Select(f => ResolveIriToPath(f, manifestDir))
                            .Where(p => p != null)
                            .ToArray()!;
                    }

                    // Extract ut:graphData for Update tests (have nested blank nodes with ut:graph and rdfs:label)
                    if (actionProps.TryGetValue(UtGraphData, out var utGraphDataNodes) && utGraphDataNodes.Count > 0)
                    {
                        actionGraphData = ExtractGraphDataNodes(graph, utGraphDataNodes, manifestDir);
                    }
                }
            }
            else
            {
                // Direct IRI reference
                actionPath = ResolveIriToPath(action, manifestDir);
            }
        }

        // Get result (expected output file or blank node for Update tests)
        string? resultPath = null;
        string? expectedDefaultGraphPath = null;
        (string Path, string GraphIri)[]? expectedNamedGraphs = null;

        if (props.TryGetValue(MfResult, out var results) && results.Count > 0)
        {
            var result = results[0];

            if (result.StartsWith("_:") && testType == W3CTestType.UpdateEval)
            {
                // Update test: result is a blank node with ut:data and/or ut:graphData
                if (graph.Subjects.TryGetValue(result, out var resultProps))
                {
                    // Extract ut:data (expected default graph)
                    if (resultProps.TryGetValue(UtData, out var utDataFiles) && utDataFiles.Count > 0)
                    {
                        expectedDefaultGraphPath = ResolveIriToPath(utDataFiles[0], manifestDir);
                    }

                    // Extract ut:graphData (expected named graphs)
                    if (resultProps.TryGetValue(UtGraphData, out var utGraphDataNodes) && utGraphDataNodes.Count > 0)
                    {
                        expectedNamedGraphs = ExtractGraphDataNodes(graph, utGraphDataNodes, manifestDir);
                    }
                }
            }
            else
            {
                // Query test: result is a direct file reference
                resultPath = ResolveIriToPath(result, manifestDir);
            }
        }

        if (actionPath == null)
            return null; // No valid action, skip this test

        return new W3CTestCase(
            Id: testIri,
            Name: name,
            Comment: comment,
            Type: testType,
            ActionPath: actionPath,
            ResultPath: resultPath,
            DataPath: dataPath,
            GraphDataPaths: graphDataPaths?.Length > 0 ? graphDataPaths : null,
            ManifestPath: manifestPath,
            ActionGraphData: actionGraphData,
            ExpectedDefaultGraphPath: expectedDefaultGraphPath,
            ExpectedNamedGraphs: expectedNamedGraphs);
    }

    /// <summary>
    /// Extracts ut:graphData nodes which have nested blank nodes with ut:graph and rdfs:label.
    /// Format: ut:graphData [ ut:graph &lt;file.ttl&gt; ; rdfs:label "http://example.org/g1" ]
    /// </summary>
    private (string Path, string GraphIri)[]? ExtractGraphDataNodes(
        ManifestGraph graph,
        List<string> graphDataNodes,
        string manifestDir)
    {
        var results = new List<(string Path, string GraphIri)>();

        foreach (var node in graphDataNodes)
        {
            if (!node.StartsWith("_:") || !graph.Subjects.TryGetValue(node, out var nodeProps))
                continue;

            // Get ut:graph (the file path)
            string? filePath = null;
            if (nodeProps.TryGetValue(UtGraph, out var graphFiles) && graphFiles.Count > 0)
            {
                filePath = ResolveIriToPath(graphFiles[0], manifestDir);
            }

            // Get rdfs:label (the graph IRI)
            string? graphIri = null;
            if (nodeProps.TryGetValue(RdfsLabel, out var labels) && labels.Count > 0)
            {
                graphIri = UnquoteLiteral(labels[0]);
            }

            if (filePath != null && graphIri != null)
            {
                results.Add((filePath, graphIri));
            }
        }

        return results.Count > 0 ? results.ToArray() : null;
    }

    private IEnumerable<string> TraverseRdfList(ManifestGraph graph, string listHead)
    {
        var current = listHead;
        var visited = new HashSet<string>();

        while (current != RdfNil && visited.Add(current))
        {
            if (!graph.Subjects.TryGetValue(current, out var props))
                yield break;

            // Get rdf:first (the value)
            if (props.TryGetValue(RdfFirst, out var firsts) && firsts.Count > 0)
                yield return firsts[0];

            // Get rdf:rest (the tail)
            if (props.TryGetValue(RdfRest, out var rests) && rests.Count > 0)
                current = rests[0];
            else
                yield break;
        }
    }

    private static string? ResolveIriToPath(string iri, string baseDir)
    {
        // Handle angle-bracketed IRIs
        if (iri.StartsWith('<') && iri.EndsWith('>'))
            iri = iri[1..^1];

        // Handle file:// URIs
        if (iri.StartsWith("file://"))
        {
            iri = iri[7..];
            if (iri.StartsWith('/') && Path.DirectorySeparatorChar == '\\')
            {
                // Windows path normalization
                iri = iri[1..];
            }
        }

        // Handle http(s) URIs - convert to relative path if it's a W3C test URL
        if (iri.StartsWith("http://") || iri.StartsWith("https://"))
        {
            // Try to extract relative path from W3C test URLs
            var w3cPrefixes = new[]
            {
                "https://w3c.github.io/rdf-tests/",
                "http://w3c.github.io/rdf-tests/",
                "https://www.w3.org/2013/",
                "http://www.w3.org/2013/",
            };

            foreach (var prefix in w3cPrefixes)
            {
                if (iri.StartsWith(prefix))
                {
                    // Extract relative path, but we can't resolve it without knowing the repo root
                    // For now, try to resolve as a relative path
                    iri = iri[prefix.Length..];
                    break;
                }
            }

            // If still an absolute URL, we can't resolve it
            if (iri.StartsWith("http"))
                return null;
        }

        // Resolve relative path against base directory
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, iri));
        return fullPath;
    }

    private static string UnquoteLiteral(string literal)
    {
        if (string.IsNullOrEmpty(literal))
            return literal;

        // Handle quoted strings: "value" or "value"@lang or "value"^^datatype
        if (literal.StartsWith('"'))
        {
            var endQuote = literal.LastIndexOf('"');
            if (endQuote > 0)
                return literal[1..endQuote];
        }

        // Handle triple-quoted strings
        if (literal.StartsWith("\"\"\""))
        {
            var endQuote = literal.LastIndexOf("\"\"\"");
            if (endQuote > 3)
                return literal[3..endQuote];
        }

        return literal;
    }

    /// <summary>
    /// Simple in-memory RDF graph for manifest parsing.
    /// </summary>
    private sealed class ManifestGraph
    {
        public Dictionary<string, Dictionary<string, List<string>>> Subjects { get; } = new();

        public void Add(string subject, string predicate, string obj)
        {
            if (!Subjects.TryGetValue(subject, out var predicateObjects))
            {
                predicateObjects = new Dictionary<string, List<string>>();
                Subjects[subject] = predicateObjects;
            }

            if (!predicateObjects.TryGetValue(predicate, out var objects))
            {
                objects = new List<string>();
                predicateObjects[predicate] = objects;
            }

            objects.Add(obj);
        }
    }
}
