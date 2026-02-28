// Licensed under the MIT License.

using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// W3C SPARQL 1.1 conformance tests.
/// Runs the official W3C test suite against our SPARQL parser and executor.
/// </summary>
[Collection("QuadStore")]
public class SparqlConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly QuadStorePoolFixture _fixture;

    public SparqlConformanceTests(ITestOutputHelper output, QuadStorePoolFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [SkippableTheory]
    [MemberData(nameof(GetPositiveSyntaxTests))]
    public void Sparql11_PositiveSyntax(string _, W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        if (W3CTestContext.ShouldSkip(test.Id, out var reason))
            Skip.If(true, reason);

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Read the query
        var query = File.ReadAllText(test.ActionPath);
        _output.WriteLine($"Query:\n{query}");

        // Positive syntax test: should parse without error
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        _output.WriteLine($"Parsed successfully: {parsed.Type}");
    }

    [SkippableTheory]
    [MemberData(nameof(GetNegativeSyntaxTests))]
    public void Sparql11_NegativeSyntax(string _, W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");
        Skip.IfNot(File.Exists(test.ActionPath), $"Test file not found: {test.ActionPath}");

        if (W3CTestContext.ShouldSkip(test.Id, out var reason))
            Skip.If(true, reason);

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"File: {test.ActionPath}");

        // Read the query
        var query = File.ReadAllText(test.ActionPath);
        _output.WriteLine($"Query:\n{query}");

        // Negative syntax test: should throw an exception
        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            var parser = new SparqlParser(query.AsSpan());
            parser.ParseQuery();
        });

        _output.WriteLine($"Expected error: {exception.Message}");
    }

    [SkippableTheory]
    [MemberData(nameof(GetQueryEvalTests))]
    public async Task Sparql11_QueryEval(string _, W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        if (W3CTestContext.ShouldSkip(test.Id, out var reason))
            Skip.If(true, reason);

        // Skip tests that require features we haven't implemented yet
        if (test.Id.Contains("entailment", StringComparison.OrdinalIgnoreCase))
            Skip.If(true, "Entailment tests not supported");
        if (test.Id.Contains("service", StringComparison.OrdinalIgnoreCase))
            Skip.If(true, "SERVICE tests require network access");

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Query: {test.ActionPath}");
        _output.WriteLine($"Data: {test.DataPath}");
        _output.WriteLine($"Result: {test.ResultPath}");

        Skip.IfNot(File.Exists(test.ActionPath), $"Query file not found: {test.ActionPath}");
        Skip.If(test.ResultPath == null, "No result file");
        Skip.IfNot(File.Exists(test.ResultPath), $"Result file not found: {test.ResultPath}");

        // Read the query
        var query = await File.ReadAllTextAsync(test.ActionPath);
        _output.WriteLine($"Query:\n{query}");

        // Create a temporary store and load data
        using var lease = _fixture.Pool.RentScoped();
        var store = lease.Store;

        // Load named graph data (qt:graphData)
        // W3C tests use filename-based graph IRIs like <exists02.ttl>
        // Use urn:w3c: scheme to create valid absolute URI for base resolution
        // e.g., "urn:w3c:exists02.ttl" so <> resolves to <urn:w3c:exists02.ttl>
        _output.WriteLine($"GraphDataPaths: {(test.GraphDataPaths == null ? "null" : $"[{string.Join(", ", test.GraphDataPaths)}]")}");
        var namedGraphIris = new List<string>();
        if (test.GraphDataPaths != null)
        {
            foreach (var graphPath in test.GraphDataPaths)
            {
                _output.WriteLine($"  Checking graphPath: {graphPath}, exists: {File.Exists(graphPath)}");
                if (File.Exists(graphPath))
                {
                    var filename = Path.GetFileName(graphPath);
                    // Use urn:w3c: scheme with filename as both graph IRI and base URI
                    // This creates a valid absolute URI that the Turtle parser can use for <> resolution
                    var graphBaseUri = $"urn:w3c:{filename}";
                    var graphIri = $"<{graphBaseUri}>";
                    var tripleCount = await LoadDataToNamedGraphAsync(store, graphPath, graphIri, baseUri: graphBaseUri);

                    // Track the graph IRI for FROM NAMED injection
                    // This ensures GRAPH ?g will iterate over declared graphs even if empty
                    namedGraphIris.Add(graphBaseUri);

                    _output.WriteLine($"Loaded graph data from {graphPath} into {graphIri} ({tripleCount} triples)");
                }
            }
        }

        // Load default graph data
        // Use urn:w3c: scheme with filename as base URI so <> resolves correctly
        // (consistent with named graph IRIs which also use urn:w3c: scheme)
        if (test.DataPath != null && File.Exists(test.DataPath))
        {
            var dataFilename = Path.GetFileName(test.DataPath);
            var dataBaseUri = $"urn:w3c:{dataFilename}";
            await LoadDataAsync(store, test.DataPath, baseUri: dataBaseUri);
            _output.WriteLine($"Loaded data from {test.DataPath}");
        }

        // Inject FROM NAMED clauses for declared named graphs
        // This ensures GRAPH ?g iteration includes empty graphs (W3C semantics)
        if (namedGraphIris.Count > 0)
        {
            query = InjectFromNamedClauses(query, namedGraphIris);
            _output.WriteLine($"Injected FROM NAMED for: {string.Join(", ", namedGraphIris)}");
        }

        // Preprocess query to resolve relative GRAPH IRIs
        // W3C tests use relative IRIs like GRAPH <exists02.ttl> which need to match our file: scheme
        query = ResolveRelativeGraphIris(query);
        _output.WriteLine($"Transformed query:\n{query}");

        // Parse the query
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();
        _output.WriteLine($"Query type: {parsed.Type}");

        // Debug: Log SelectClause info for cast tests
        if (test.Id.Contains("cast", StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine($"SelectClause.HasAggregates: {parsed.SelectClause.HasAggregates}");
            _output.WriteLine($"SelectClause.AggregateCount: {parsed.SelectClause.AggregateCount}");
            for (int i = 0; i < parsed.SelectClause.AggregateCount; i++)
            {
                var agg = parsed.SelectClause.GetAggregate(i);
                var expr = query.AsSpan(agg.VariableStart, agg.VariableLength).ToString();
                var alias = agg.AliasLength > 0 ? query.AsSpan(agg.AliasStart, agg.AliasLength).ToString() : "(none)";
                _output.WriteLine($"  Aggregate {i}: Function={agg.Function}, Expr='{expr}', Alias='{alias}'");
            }
        }

        // Check for CONSTRUCT/DESCRIBE results (RDF graph format)
        var resultExt = Path.GetExtension(test.ResultPath!).ToLowerInvariant();
        if (resultExt == ".ttl" || resultExt == ".nt" || resultExt == ".rdf")
        {
            // Check if this is a RDF-encoded SPARQL result set (rs:ResultSet)
            // This is used by some W3C tests to encode SELECT results as RDF
            if (resultExt == ".ttl" && await IsRdfResultSetAsync(test.ResultPath!))
            {
                // SELECT query with RDF-encoded result set - parse and compare as result set
                var expectedRdf = await SparqlResultParser.ParseRdfResultSetAsync(test.ResultPath!);
                _output.WriteLine($"Expected {expectedRdf.Count} results (from RDF result set)");

                var actualRdf = await RunSelectQueryAsync(store, query, parsed, expectedRdf);
                _output.WriteLine($"Got {actualRdf.Count} results");

                var rdfError = SparqlResultComparer.Compare(expectedRdf, actualRdf);
                if (rdfError != null)
                {
                    _output.WriteLine($"Result comparison failed:");
                    _output.WriteLine(rdfError);
                }
                Assert.Null(rdfError);
                return;
            }

            // CONSTRUCT/DESCRIBE query - use graph comparison
            var expectedGraph = await ParseExpectedGraphAsync(test.ResultPath!);
            _output.WriteLine($"Expected {expectedGraph.Count} triples");

            var actualGraph = RunOnLargeStack(() =>
            {
                var graph = new SparqlGraphResult();

                store.AcquireReadLock();
                try
                {
                    using var executor = new QueryExecutor(store, query.AsSpan(), parsed);

                    if (parsed.Type == QueryType.Construct)
                    {
                        var results = executor.ExecuteConstruct();
                        try
                        {
                            while (results.MoveNext())
                            {
                                var triple = results.Current;
                                graph.AddTriple(
                                    ParseTermToBinding(triple.Subject.ToString()),
                                    ParseTermToBinding(triple.Predicate.ToString()),
                                    ParseTermToBinding(triple.Object.ToString()));
                            }
                        }
                        finally
                        {
                            results.Dispose();
                        }
                    }
                    else if (parsed.Type == QueryType.Describe)
                    {
                        var results = executor.ExecuteDescribe();
                        try
                        {
                            while (results.MoveNext())
                            {
                                var triple = results.Current;
                                graph.AddTriple(
                                    ParseTermToBinding(triple.Subject.ToString()),
                                    ParseTermToBinding(triple.Predicate.ToString()),
                                    ParseTermToBinding(triple.Object.ToString()));
                            }
                        }
                        finally
                        {
                            results.Dispose();
                        }
                    }

                    return graph;
                }
                finally
                {
                    store.ReleaseReadLock();
                }
            });

            _output.WriteLine($"Got {actualGraph.Count} triples");

            var graphError = SparqlResultComparer.CompareGraphs(expectedGraph, actualGraph);
            if (graphError != null)
            {
                _output.WriteLine("Graph comparison failed:");
                _output.WriteLine(graphError);
            }

            Assert.Null(graphError);
            return;
        }

        // Parse expected results BEFORE acquiring lock to avoid thread-affinity issues
        // (ReaderWriterLockSlim requires release on same thread as acquire)
        var expected = await ParseExpectedResultSetAsync(test.ResultPath!);
        _output.WriteLine($"Expected {expected.Count} results");

        // Check if this is an ORDER BY query (results must match in order)
        var hasOrderBy = query.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase);

        // Execute the query and collect actual results
        // Run on dedicated thread with 8MB stack to avoid stack overflow from QueryResults (~22KB)
        var actual = RunOnLargeStack(() =>
        {
            var resultSet = new SparqlResultSet();
            foreach (var variable in expected.Variables)
                resultSet.AddVariable(variable);

            store.AcquireReadLock();
            try
            {
                using var executor = new QueryExecutor(store, query.AsSpan(), parsed);

                // Handle ASK queries
                if (parsed.Type == QueryType.Ask)
                {
                    var askResult = executor.ExecuteAsk();
                    return SparqlResultSet.Boolean(askResult);
                }

                // SELECT query - execute and collect results
                var results = executor.Execute();
                int rowNum = 0;
                try
                {
                    while (results.MoveNext())
                    {
                        rowNum++;
                        var row = new SparqlResultRow();
                        var current = results.Current;

                        // Debug: for cast tests, log binding details
                        if (test.Id.Contains("cast-int"))
                        {
                            var diagSb = new System.Text.StringBuilder();
                            diagSb.AppendLine($"Row {rowNum}: BindingCount={current.Count}");
                            for (int i = 0; i < current.Count; i++)
                            {
                                diagSb.AppendLine($"  [{i}]: Type={current.GetType(i)}, Value='{current.GetString(i).ToString()}'");
                            }
                            // Try to find ?integer
                            var intIdx = current.FindBinding("?integer".AsSpan());
                            diagSb.AppendLine($"  FindBinding('?integer')={intIdx}");
                            if (intIdx >= 0)
                            {
                                diagSb.AppendLine($"  ?integer Type={current.GetType(intIdx)}, Value='{current.GetString(intIdx).ToString()}'");
                            }
                            _output.WriteLine(diagSb.ToString());
                        }

                        foreach (var varName in expected.Variables)
                        {
                            var binding = ExtractBinding(current, varName);
                            row.Set(varName, binding);
                        }
                        resultSet.AddRow(row);
                    }
                }
                finally
                {
                    results.Dispose();
                }
                return resultSet;
            }
            finally
            {
                store.ReleaseReadLock();
            }
        });
        _output.WriteLine($"Got {actual.Count} results");

        // Compare results using full validation with blank node isomorphism
        var comparisonError = SparqlResultComparer.Compare(expected, actual, ordered: hasOrderBy);

        if (comparisonError != null)
        {
            _output.WriteLine("Comparison failed:");
            _output.WriteLine(comparisonError);

            // Log first few rows for debugging
            _output.WriteLine("\nExpected rows:");
            foreach (var row in expected.Rows.Take(3))
            {
                _output.WriteLine($"  {row}");
            }
            if (expected.Count > 3)
                _output.WriteLine($"  ... ({expected.Count - 3} more)");

            _output.WriteLine("\nActual rows:");
            foreach (var row in actual.Rows.Take(3))
            {
                _output.WriteLine($"  {row}");
            }
            if (actual.Count > 3)
                _output.WriteLine($"  ... ({actual.Count - 3} more)");
        }

        Assert.Null(comparisonError);
    }

    /// <summary>
    /// Extracts a binding from the current result row.
    /// </summary>
    private static SparqlBinding ExtractBinding(BindingTable current, string varName)
    {
        // Try with '?' prefix first (aggregates and BIND store aliases with '?')
        var prefixedName = "?" + varName;
        var idx = current.FindBinding(prefixedName.AsSpan());

        // Fall back to without prefix (triple pattern variables)
        if (idx < 0)
            idx = current.FindBinding(varName.AsSpan());

        if (idx < 0)
            return SparqlBinding.Unbound;

        var type = current.GetType(idx);
        var value = current.GetString(idx).ToString();

        return type switch
        {
            // Strip angle brackets from URIs (internal format includes them, W3C format doesn't)
            BindingValueType.Uri => SparqlBinding.Uri(
                value.Length >= 2 && value[0] == '<' && value[^1] == '>'
                    ? value[1..^1]
                    : value),
            BindingValueType.String => ParseLiteralBinding(value),
            BindingValueType.Integer => SparqlBinding.TypedLiteral(value, "http://www.w3.org/2001/XMLSchema#integer"),
            BindingValueType.Double => SparqlBinding.TypedLiteral(value, "http://www.w3.org/2001/XMLSchema#double"),
            BindingValueType.Boolean => SparqlBinding.TypedLiteral(value, "http://www.w3.org/2001/XMLSchema#boolean"),
            _ => SparqlBinding.Unbound
        };
    }

    /// <summary>
    /// Parses a literal value that may contain datatype or language tag.
    /// Handles formats like: "value", "value"@lang, "value"^^<datatype>, <uri>, _:bnode
    /// Note: Empty string "" is a valid literal, not unbound.
    /// </summary>
    private static SparqlBinding ParseLiteralBinding(string value)
    {
        // null means unbound, but empty string is a valid literal (e.g., CONCAT() returns "")
        if (value is null)
            return SparqlBinding.Unbound;

        // Empty string is a valid plain literal
        if (value.Length == 0)
            return SparqlBinding.Literal("");

        // Check for URI (stored with angle brackets)
        if (value.StartsWith('<') && value.EndsWith('>'))
            return SparqlBinding.Uri(value[1..^1]);

        // Check for blank node
        if (value.StartsWith("_:"))
            return SparqlBinding.BNode(value[2..]);

        // Check for typed literal: "value"^^<datatype>
        if (value.StartsWith('"'))
        {
            var closeQuote = FindClosingQuote(value);
            if (closeQuote > 0)
            {
                var literalValue = UnescapeLiteral(value[1..closeQuote]);
                var suffix = value[(closeQuote + 1)..];

                if (suffix.StartsWith("^^"))
                {
                    var datatype = suffix[2..];
                    if (datatype.StartsWith('<') && datatype.EndsWith('>'))
                        datatype = datatype[1..^1];
                    return SparqlBinding.TypedLiteral(literalValue, datatype);
                }

                if (suffix.StartsWith('@'))
                {
                    return SparqlBinding.LangLiteral(literalValue, suffix[1..]);
                }

                return SparqlBinding.Literal(literalValue);
            }
        }

        // Plain value - treat as literal
        return SparqlBinding.Literal(value);
    }

    private static int FindClosingQuote(string value)
    {
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] == '"' && (i == 1 || value[i - 1] != '\\'))
                return i;
        }
        return -1;
    }

    private static string UnescapeLiteral(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                switch (next)
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    default: sb.Append(value[i]); break;
                }
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses expected results using the full SPARQL result format parser.
    /// </summary>
    private static async Task<SparqlResultSet> ParseExpectedResultSetAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        // For RDF graph results (CONSTRUCT/DESCRIBE), return empty - handled separately
        if (extension is ".ttl" or ".nt" or ".rdf")
        {
            return SparqlResultSet.Empty();
        }

        return await SparqlResultParser.ParseFileAsync(path);
    }

    /// <summary>
    /// Parses expected RDF graph from .ttl, .nt, or .rdf file.
    /// </summary>
    private static async Task<SparqlGraphResult> ParseExpectedGraphAsync(string path)
    {
        var graph = new SparqlGraphResult();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var baseUri = new Uri(path).AbsoluteUri;

        if (extension == ".ttl" || extension == ".turtle")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new TurtleStreamParser(stream, baseUri: baseUri);

            await parser.ParseAsync((s, p, o) =>
            {
                graph.AddTriple(
                    ParseTermToBinding(s.ToString()),
                    ParseTermToBinding(p.ToString()),
                    ParseTermToBinding(o.ToString()));
            });
        }
        else if (extension == ".nt" || extension == ".ntriples")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new Mercury.NTriples.NTriplesStreamParser(stream);

            await parser.ParseAsync((s, p, o) =>
            {
                graph.AddTriple(
                    ParseTermToBinding(s.ToString()),
                    ParseTermToBinding(p.ToString()),
                    ParseTermToBinding(o.ToString()));
            });
        }
        else if (extension == ".rdf" || extension == ".xml" || extension == ".rdfxml")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new Mercury.RdfXml.RdfXmlStreamParser(stream, baseUri: baseUri);

            await parser.ParseAsync((s, p, o) =>
            {
                graph.AddTriple(
                    ParseTermToBinding(s.ToString()),
                    ParseTermToBinding(p.ToString()),
                    ParseTermToBinding(o.ToString()));
            });
        }

        return graph;
    }

    /// <summary>
    /// Parses an RDF term string into a SparqlBinding.
    /// Handles IRIs (<...>), blank nodes (_:...), and literals ("..."^^datatype, "..."@lang).
    /// </summary>
    private static SparqlBinding ParseTermToBinding(string term)
    {
        if (string.IsNullOrEmpty(term))
            return SparqlBinding.Unbound;

        // IRI - strip angle brackets
        if (term.StartsWith('<') && term.EndsWith('>'))
            return SparqlBinding.Uri(term[1..^1]);

        // Blank node
        if (term.StartsWith("_:"))
            return SparqlBinding.BNode(term[2..]);

        // Literal with possible datatype or language tag
        if (term.StartsWith('"'))
        {
            var closeQuote = FindClosingQuote(term);
            if (closeQuote > 0)
            {
                var value = UnescapeLiteral(term[1..closeQuote]);
                var suffix = term[(closeQuote + 1)..];

                if (suffix.StartsWith("^^"))
                {
                    var datatype = suffix[2..];
                    if (datatype.StartsWith('<') && datatype.EndsWith('>'))
                        datatype = datatype[1..^1];
                    return SparqlBinding.TypedLiteral(value, datatype);
                }

                if (suffix.StartsWith('@'))
                {
                    return SparqlBinding.LangLiteral(value, suffix[1..]);
                }

                return SparqlBinding.Literal(value);
            }
        }

        // Plain value - treat as literal
        return SparqlBinding.Literal(term);
    }

    /// <summary>
    /// Inject FROM NAMED clauses into a query for declared named graphs.
    /// This ensures GRAPH ?g iteration includes all declared graphs, even empty ones.
    /// W3C tests declare named graphs via qt:graphData entries in the manifest.
    ///
    /// SPARQL grammar: SelectQuery ::= SelectClause DatasetClause* WhereClause
    /// So FROM NAMED must come AFTER SELECT clause but BEFORE WHERE.
    /// </summary>
    private static string InjectFromNamedClauses(string query, List<string> namedGraphIris)
    {
        if (namedGraphIris.Count == 0)
            return query;

        // Build FROM NAMED clauses (with leading newline for formatting)
        var fromNamedClauses = new System.Text.StringBuilder();
        fromNamedClauses.AppendLine();
        foreach (var graphIri in namedGraphIris)
        {
            fromNamedClauses.Append("FROM NAMED <");
            fromNamedClauses.Append(graphIri);
            fromNamedClauses.AppendLine(">");
        }

        // Find insertion point: after SELECT clause (including variable list and DISTINCT/REDUCED)
        // but before WHERE/FROM keywords.
        //
        // Pattern for SELECT clause:
        //   SELECT (DISTINCT|REDUCED)? (variables | * | expressions)+
        //
        // The safest approach: find WHERE and insert just before it
        var whereMatch = System.Text.RegularExpressions.Regex.Match(
            query,
            @"\bWHERE\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (whereMatch.Success)
        {
            var insertPos = whereMatch.Index;

            // Check if FROM clauses already exist before WHERE
            var beforeWhere = query.Substring(0, insertPos);
            var fromMatch = System.Text.RegularExpressions.Regex.Match(
                beforeWhere,
                @"(FROM\s+(NAMED\s+)?<[^>]+>\s*)+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (fromMatch.Success)
            {
                // Already has FROM clauses - insert after them
                insertPos = fromMatch.Index + fromMatch.Length;
            }

            return query.Substring(0, insertPos) + fromNamedClauses.ToString() + query.Substring(insertPos);
        }

        // ASK/CONSTRUCT/DESCRIBE without WHERE - insert before opening brace
        var braceMatch = System.Text.RegularExpressions.Regex.Match(query, @"\{");
        if (braceMatch.Success)
        {
            return query.Substring(0, braceMatch.Index) + fromNamedClauses.ToString() + query.Substring(braceMatch.Index);
        }

        return query; // Can't find insertion point - return unchanged
    }

    /// <summary>
    /// Resolve relative GRAPH IRIs in SPARQL queries to use urn:w3c: scheme.
    /// W3C tests use relative IRIs like GRAPH &lt;exists02.ttl&gt; or in FILTER clauses
    /// which need to match our named graph loading (urn:w3c:exists02.ttl).
    /// </summary>
    private static string ResolveRelativeGraphIris(string query)
    {
        // Match any <filename.ext> where the IRI has no scheme (no ://)
        // This handles both GRAPH <file.ttl> and FILTER (?g = <file.ttl>) patterns
        // Pattern: < followed by content without :// that ends with .extension >
        return System.Text.RegularExpressions.Regex.Replace(
            query,
            @"<([^<>:]+\.[a-zA-Z]+)>",
            "<urn:w3c:$1>");
    }

    /// <summary>
    /// Checks if a Turtle file contains an rs:ResultSet (RDF-encoded SPARQL result set).
    /// </summary>
    private static async Task<bool> IsRdfResultSetAsync(string path)
    {
        var content = await File.ReadAllTextAsync(path);
        // Quick check for rs:ResultSet pattern
        return content.Contains("rs:ResultSet") ||
               content.Contains("<http://www.w3.org/2001/sw/DataAccess/tests/result-set#ResultSet>");
    }

    /// <summary>
    /// Runs a SELECT query and returns the results as a SparqlResultSet.
    /// </summary>
    private async Task<SparqlResultSet> RunSelectQueryAsync(QuadStore store, string query, Query parsed, SparqlResultSet expectedForVariables)
    {
        return await Task.Run(() => RunOnLargeStack(() =>
        {
            var resultSet = new SparqlResultSet();

            // Copy variable names from expected result set
            foreach (var variable in expectedForVariables.Variables)
                resultSet.AddVariable(variable);

            store.AcquireReadLock();
            try
            {
                using var executor = new QueryExecutor(store, query.AsSpan(), parsed);
                var results = executor.Execute();

                try
                {
                    while (results.MoveNext())
                    {
                        var current = results.Current;
                        var resultRow = new SparqlResultRow();

                        foreach (var varName in expectedForVariables.Variables)
                        {
                            var binding = ExtractBinding(current, varName);
                            resultRow.Set(varName, binding);
                        }

                        resultSet.AddRow(resultRow);
                    }
                }
                finally
                {
                    results.Dispose();
                }

                return resultSet;
            }
            finally
            {
                store.ReleaseReadLock();
            }
        }));
    }

    private async Task LoadDataAsync(QuadStore store, string path, string? baseUri = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var effectiveBaseUri = baseUri ?? new Uri(path).AbsoluteUri;

        if (extension == ".ttl" || extension == ".turtle")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new TurtleStreamParser(stream, baseUri: effectiveBaseUri);

            await parser.ParseAsync((s, p, o) =>
            {
                store.AddCurrent(s.ToString(), p.ToString(), o.ToString());
            });
        }
        else if (extension == ".nt" || extension == ".ntriples")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new Mercury.NTriples.NTriplesStreamParser(stream);

            await parser.ParseAsync((s, p, o) =>
            {
                store.AddCurrent(s.ToString(), p.ToString(), o.ToString());
            });
        }
        else if (extension == ".rdf" || extension == ".xml" || extension == ".rdfxml")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new Mercury.RdfXml.RdfXmlStreamParser(stream, baseUri: effectiveBaseUri);

            await parser.ParseAsync((s, p, o) =>
            {
                store.AddCurrent(s.ToString(), p.ToString(), o.ToString());
            });
        }
        // Add more formats as needed
    }

    private async Task<int> LoadDataToNamedGraphAsync(QuadStore store, string path, string graphIri, string? baseUri = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        // Use provided base URI, or default to full file path
        var effectiveBaseUri = baseUri ?? new Uri(path).AbsoluteUri;
        int tripleCount = 0;

        if (extension == ".ttl" || extension == ".turtle")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new TurtleStreamParser(stream, baseUri: effectiveBaseUri);

            await parser.ParseAsync((s, p, o) =>
            {
                store.AddCurrent(s.ToString(), p.ToString(), o.ToString(), graphIri);
                tripleCount++;
            });
        }
        else if (extension == ".nt" || extension == ".ntriples")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new Mercury.NTriples.NTriplesStreamParser(stream);

            await parser.ParseAsync((s, p, o) =>
            {
                store.AddCurrent(s.ToString(), p.ToString(), o.ToString(), graphIri);
                tripleCount++;
            });
        }
        else if (extension == ".rdf" || extension == ".xml" || extension == ".rdfxml")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new Mercury.RdfXml.RdfXmlStreamParser(stream, baseUri: effectiveBaseUri);

            await parser.ParseAsync((s, p, o) =>
            {
                store.AddCurrent(s.ToString(), p.ToString(), o.ToString(), graphIri);
                tripleCount++;
            });
        }

        return tripleCount;
    }

    public static IEnumerable<object[]> GetPositiveSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{W3CTestContext.TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(W3CTestContext.TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var tests = W3CTestContext.LoadTestCasesAsync(
            W3CTestSuite.Sparql11Query,
            W3CTestType.PositiveSyntax).GetAwaiter().GetResult();

        foreach (var test in tests)
        {
            yield return new object[] { test.DisplayName, test };
        }
    }

    public static IEnumerable<object[]> GetNegativeSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{W3CTestContext.TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(W3CTestContext.TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var tests = W3CTestContext.LoadTestCasesAsync(
            W3CTestSuite.Sparql11Query,
            W3CTestType.NegativeSyntax).GetAwaiter().GetResult();

        foreach (var test in tests)
        {
            yield return new object[] { test.DisplayName, test };
        }
    }

    public static IEnumerable<object[]> GetQueryEvalTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{W3CTestContext.TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(W3CTestContext.TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var tests = W3CTestContext.LoadTestCasesAsync(
            W3CTestSuite.Sparql11Query,
            W3CTestType.QueryEval).GetAwaiter().GetResult();

        foreach (var test in tests)
        {
            // Skip tests at data generation level to prevent stack overflow during test execution
            if (W3CTestContext.ShouldSkip(test.Id, out _))
                continue;

            yield return new object[] { test.DisplayName, test };
        }
    }

    [SkippableTheory]
    [MemberData(nameof(GetUpdateEvalTests))]
    public async Task Sparql11_UpdateEval(string _, W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        if (W3CTestContext.ShouldSkip(test.Id, out var reason))
            Skip.If(true, reason);

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Update: {test.ActionPath}");
        _output.WriteLine($"Data: {test.DataPath}");
        _output.WriteLine($"ExpectedDefaultGraph: {test.ExpectedDefaultGraphPath}");
        _output.WriteLine($"ExpectedNamedGraphs: {test.ExpectedNamedGraphs?.Length ?? 0}");

        Skip.IfNot(File.Exists(test.ActionPath), $"Update file not found: {test.ActionPath}");

        // Read the update query (.ru file)
        var update = await File.ReadAllTextAsync(test.ActionPath);
        _output.WriteLine($"Update:\n{update}");

        // Create a temporary store and load initial data
        using var lease = _fixture.Pool.RentScoped();
        var store = lease.Store;

        // Load default graph data
        if (test.DataPath != null && File.Exists(test.DataPath))
        {
            await LoadDataAsync(store, test.DataPath);
            _output.WriteLine($"Loaded initial default graph from {test.DataPath}");
        }

        // Load named graph data (from ActionGraphData for Update tests)
        if (test.ActionGraphData != null)
        {
            foreach (var (path, graphIri) in test.ActionGraphData)
            {
                if (File.Exists(path))
                {
                    await LoadDataToNamedGraphAsync(store, path, $"<{graphIri}>");
                    _output.WriteLine($"Loaded initial named graph <{graphIri}> from {path}");
                }
            }
        }

        // Parse and execute the update sequence (may contain multiple operations separated by ;)
        // Run on dedicated thread with 8MB stack to avoid stack overflow from large ref structs
        var parser = new SparqlParser(update.AsSpan());
        var operations = parser.ParseUpdateSequence();
        _output.WriteLine($"Update operations: {operations.Length}");
        foreach (var op in operations)
        {
            _output.WriteLine($"  - {op.Type}");
            if (op.Type == SkyOmega.Mercury.Sparql.Types.QueryType.Modify && op.WhereClause.Pattern.SubQueryCount > 0)
            {
                _output.WriteLine($"    WHERE has {op.WhereClause.Pattern.SubQueryCount} subquery(ies)");
            }
        }

        var result = RunOnLargeStack(() =>
        {
            return UpdateExecutor.ExecuteSequence(store, update.AsSpan(), operations);
        });

        _output.WriteLine($"Update result: Success={result.Success}, Affected={result.AffectedCount}");

        if (!result.Success)
        {
            _output.WriteLine($"Error: {result.ErrorMessage}");
        }

        // Verify execution succeeded
        Assert.True(result.Success, result.ErrorMessage ?? "Update failed");

        // Skip graph state comparison if no expected graphs specified
        // (some tests only verify execution success)
        if (test.ExpectedDefaultGraphPath == null && test.ExpectedNamedGraphs == null)
        {
            _output.WriteLine("No expected graph state specified - verifying execution success only");
            return;
        }

        // Compare default graph state
        if (test.ExpectedDefaultGraphPath != null)
        {
            Skip.IfNot(File.Exists(test.ExpectedDefaultGraphPath),
                $"Expected default graph file not found: {test.ExpectedDefaultGraphPath}");

            var expectedDefault = await ParseExpectedGraphAsync(test.ExpectedDefaultGraphPath);
            var actualDefault = ExtractGraphFromStore(store, null);

            _output.WriteLine($"Default graph: expected {expectedDefault.Count}, actual {actualDefault.Count}");

            var defaultError = SparqlResultComparer.CompareGraphs(expectedDefault, actualDefault);
            if (defaultError != null)
            {
                _output.WriteLine("Default graph comparison failed:");
                _output.WriteLine(defaultError);
                LogGraphContents("Expected default", expectedDefault);
                LogGraphContents("Actual default", actualDefault);
            }
            Assert.Null(defaultError);
        }

        // Compare named graph states
        if (test.ExpectedNamedGraphs != null)
        {
            foreach (var (path, graphIri) in test.ExpectedNamedGraphs)
            {
                Skip.IfNot(File.Exists(path),
                    $"Expected named graph file not found: {path}");

                var expectedNamed = await ParseExpectedGraphAsync(path);
                var actualNamed = ExtractGraphFromStore(store, $"<{graphIri}>");

                _output.WriteLine($"Named graph <{graphIri}>: expected {expectedNamed.Count}, actual {actualNamed.Count}");

                var namedError = SparqlResultComparer.CompareGraphs(expectedNamed, actualNamed);
                if (namedError != null)
                {
                    _output.WriteLine($"Named graph <{graphIri}> comparison failed:");
                    _output.WriteLine(namedError);
                    LogGraphContents($"Expected <{graphIri}>", expectedNamed);
                    LogGraphContents($"Actual <{graphIri}>", actualNamed);
                }
                Assert.Null(namedError);
            }
        }
    }

    /// <summary>
    /// Extracts all triples from a specific graph in the store.
    /// </summary>
    /// <param name="store">The quad store.</param>
    /// <param name="graphIri">The graph IRI (null for default graph, or &lt;iri&gt; for named graph).</param>
    private SparqlGraphResult ExtractGraphFromStore(SkyOmega.Mercury.Storage.QuadStore store, string? graphIri)
    {
        var graph = new SparqlGraphResult();

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(null, null, null, graphIri);
            try
            {
                while (results.MoveNext())
                {
                    var quad = results.Current;
                    graph.AddTriple(
                        ParseTermToBinding(quad.Subject.ToString()),
                        ParseTermToBinding(quad.Predicate.ToString()),
                        ParseTermToBinding(quad.Object.ToString()));
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }

        return graph;
    }

    /// <summary>
    /// Logs the contents of a graph for debugging.
    /// </summary>
    private void LogGraphContents(string label, SparqlGraphResult graph)
    {
        _output.WriteLine($"{label} ({graph.Count} triples):");
        foreach (var triple in graph.Triples.Take(10))
        {
            _output.WriteLine($"  {triple.Subject} {triple.Predicate} {triple.Object}");
        }
        if (graph.Count > 10)
            _output.WriteLine($"  ... ({graph.Count - 10} more)");
    }

    public static IEnumerable<object[]> GetUpdateEvalTests()
    {
        if (!W3CTestContext.IsAvailable)
            throw new InvalidOperationException(
                $"W3C RDF test suite not found at '{W3CTestContext.TestsRoot}'. " +
                $"Directory exists: {Directory.Exists(W3CTestContext.TestsRoot)}. " +
                "Run ./tools/update-submodules.sh (or .\\tools\\update-submodules.ps1) to initialize.");

        var tests = W3CTestContext.LoadTestCasesAsync(
            W3CTestSuite.Sparql11Update,
            W3CTestType.UpdateEval).GetAwaiter().GetResult();

        foreach (var test in tests)
        {
            if (W3CTestContext.ShouldSkip(test.Id, out _))
                continue;

            yield return new object[] { test.DisplayName, test };
        }
    }

    /// <summary>
    /// Run a function on a dedicated thread with 8MB stack to avoid stack overflow
    /// from large ref structs like QueryResults (~22KB).
    /// Thread pool threads have 1MB stacks which is insufficient for complex queries.
    /// </summary>
    private static T RunOnLargeStack<T>(Func<T> func)
    {
        const int stackSize = 8 * 1024 * 1024; // 8MB
        T? result = default;
        Exception? exception = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        }, stackSize);

        thread.Start();
        thread.Join();

        if (exception != null)
            throw exception;

        return result!;
    }
}
