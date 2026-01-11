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
        var expectedRows = await ParseExpectedResultsAsync(test.ResultPath!);
        _output.WriteLine($"Expected {expectedRows.Count} results");

        // Execute the query (no async after lock acquisition)
        int actualCount = 0;
        store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(store, query.AsSpan(), parsed);
            var results = executor.Execute();

            // Count results
            while (results.MoveNext())
            {
                actualCount++;
            }
            results.Dispose();

            _output.WriteLine($"Got {actualCount} results");
        }
        finally
        {
            store.ReleaseReadLock();
        }

        // Compare results
        // Note: This is a simplified comparison - a full implementation would need
        // to handle blank node isomorphism, unordered result sets, etc.
        Assert.Equal(expectedRows.Count, actualCount);
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

    private async Task<List<Dictionary<string, string>>> ParseExpectedResultsAsync(string path)
    {
        var results = new List<Dictionary<string, string>>();
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".srx" || extension == ".xml")
        {
            // Parse SPARQL Results XML format
            results = await ParseSparqlResultsXmlAsync(path);
        }
        else if (extension == ".srj" || extension == ".json")
        {
            // Parse SPARQL Results JSON format
            results = await ParseSparqlResultsJsonAsync(path);
        }
        else if (extension == ".ttl" || extension == ".nt")
        {
            // CONSTRUCT results - parse as triples
            // For now, just return empty (we'd need triple comparison)
            _output.WriteLine($"CONSTRUCT result format not fully implemented");
        }

        return results;
    }

    private async Task<List<Dictionary<string, string>>> ParseSparqlResultsXmlAsync(string path)
    {
        var results = new List<Dictionary<string, string>>();
        var xml = await File.ReadAllTextAsync(path);

        // Simple XML parsing for SPARQL results
        // A full implementation would use proper XML parsing
        // This is a placeholder that just counts results

        var resultMatches = System.Text.RegularExpressions.Regex.Matches(xml, @"<result>");
        foreach (System.Text.RegularExpressions.Match match in resultMatches)
        {
            results.Add(new Dictionary<string, string>());
        }

        return results;
    }

    private async Task<List<Dictionary<string, string>>> ParseSparqlResultsJsonAsync(string path)
    {
        var results = new List<Dictionary<string, string>>();
        var json = await File.ReadAllTextAsync(path);

        // Simple JSON parsing for SPARQL results
        // A full implementation would use proper JSON parsing
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("results", out var resultsElement) &&
            resultsElement.TryGetProperty("bindings", out var bindings))
        {
            foreach (var binding in bindings.EnumerateArray())
            {
                var row = new Dictionary<string, string>();
                foreach (var prop in binding.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("value", out var value))
                    {
                        row[prop.Name] = value.GetString() ?? "";
                    }
                }
                results.Add(row);
            }
        }

        return results;
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
            yield return new object[] { test.DisplayName, test };
        }
    }
}
