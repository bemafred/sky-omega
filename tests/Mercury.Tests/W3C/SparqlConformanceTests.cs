// Licensed under the MIT License.

using SkyOmega.Mercury.Sparql;
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
    public void Sparql11_PositiveSyntax(string name, W3CTestCase test)
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
    public void Sparql11_NegativeSyntax(string name, W3CTestCase test)
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
    public async Task Sparql11_QueryEval(string name, W3CTestCase test)
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

        if (test.DataPath != null && File.Exists(test.DataPath))
        {
            await LoadDataAsync(store, test.DataPath);
            _output.WriteLine($"Loaded data from {test.DataPath}");
        }

        // Note: Additional graph data (qt:graphData) not yet supported
        // Would need to extend W3CTestCase to include multiple graph paths

        // Parse the query
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();
        _output.WriteLine($"Query type: {parsed.Type}");

        // Parse expected results BEFORE acquiring lock to avoid thread-affinity issues
        // (ReaderWriterLockSlim requires release on same thread as acquire)
        var expected = await ParseExpectedResultSetAsync(test.ResultPath!);
        _output.WriteLine($"Expected {expected.Count} results");

        // Check for CONSTRUCT/DESCRIBE results (RDF graph format)
        var resultExt = Path.GetExtension(test.ResultPath!).ToLowerInvariant();
        if (resultExt == ".ttl" || resultExt == ".nt" || resultExt == ".rdf")
        {
            // CONSTRUCT/DESCRIBE results - skip for now (need graph comparison)
            Skip.If(true, "CONSTRUCT/DESCRIBE result validation not yet implemented");
        }

        // Check if this is an ORDER BY query (results must match in order)
        var hasOrderBy = query.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase);

        // Execute the query and collect actual results
        var actual = new SparqlResultSet();

        // Add expected variables to actual result set
        foreach (var variable in expected.Variables)
        {
            actual.AddVariable(variable);
        }

        // Get timeout for this test (default 30 seconds)
        var timeoutMs = W3CTestContext.GetRecommendedTimeout(test.Id) ?? 30000;
        using var cts = new CancellationTokenSource(timeoutMs);

        store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(store, query.AsSpan(), parsed);

            // Handle ASK queries
            if (parsed.Type == QueryType.Ask)
            {
                var askResult = executor.ExecuteAsk();
                actual = SparqlResultSet.Boolean(askResult);
                _output.WriteLine($"ASK result: {askResult}");
            }
            else
            {
                // SELECT query - execute and collect results with timeout
                var results = executor.Execute(cts.Token);
                results.SetCancellationToken(cts.Token); // Enable timeout in collection loops
                try
                {
                    while (results.MoveNext())
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var row = new SparqlResultRow();
                        var current = results.Current;

                        // Extract bindings for expected variables
                        foreach (var varName in expected.Variables)
                        {
                            var binding = ExtractBinding(current, varName);
                            row.Set(varName, binding);
                        }

                        actual.AddRow(row);
                    }
                }
                finally
                {
                    results.Dispose();
                }
                _output.WriteLine($"Got {actual.Count} results");
            }
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"Test timed out after {timeoutMs}ms");
        }
        finally
        {
            store.ReleaseReadLock();
        }

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
            BindingValueType.Uri => SparqlBinding.Uri(value),
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
    /// </summary>
    private static SparqlBinding ParseLiteralBinding(string value)
    {
        if (string.IsNullOrEmpty(value))
            return SparqlBinding.Unbound;

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

    private async Task LoadDataAsync(QuadStore store, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".ttl" || extension == ".turtle")
        {
            await using var stream = File.OpenRead(path);
            using var parser = new TurtleStreamParser(stream, baseUri: new Uri(path).AbsoluteUri);

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
        // Add more formats as needed
    }

    public static IEnumerable<object[]> GetPositiveSyntaxTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

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
            yield break;

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
            yield break;

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
    public async Task Sparql11_UpdateEval(string name, W3CTestCase test)
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        if (W3CTestContext.ShouldSkip(test.Id, out var reason))
            Skip.If(true, reason);

        _output.WriteLine($"Test: {test.Name}");
        _output.WriteLine($"Update: {test.ActionPath}");
        _output.WriteLine($"Data: {test.DataPath}");

        Skip.IfNot(File.Exists(test.ActionPath), $"Update file not found: {test.ActionPath}");

        // Read the update query (.ru file)
        var update = await File.ReadAllTextAsync(test.ActionPath);
        _output.WriteLine($"Update:\n{update}");

        // Create a temporary store and load initial data
        using var lease = _fixture.Pool.RentScoped();
        var store = lease.Store;

        if (test.DataPath != null && File.Exists(test.DataPath))
        {
            await LoadDataAsync(store, test.DataPath);
            _output.WriteLine($"Loaded initial data from {test.DataPath}");
        }

        // Parse and execute the update
        var parser = new SparqlParser(update.AsSpan());
        var parsed = parser.ParseUpdate();
        _output.WriteLine($"Update type: {parsed.Type}");

        var executor = new UpdateExecutor(store, update.AsSpan(), parsed);
        var result = executor.Execute();

        _output.WriteLine($"Update result: Success={result.Success}, Affected={result.AffectedCount}");

        if (!result.Success)
        {
            _output.WriteLine($"Error: {result.ErrorMessage}");
        }

        // Verify result - for now just check success
        // Full conformance would compare actual graph state against expected
        Assert.True(result.Success, result.ErrorMessage ?? "Update failed");
    }

    public static IEnumerable<object[]> GetUpdateEvalTests()
    {
        if (!W3CTestContext.IsAvailable)
            yield break;

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
}
