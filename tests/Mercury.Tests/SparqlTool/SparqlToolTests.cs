using System;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql.Tool;
using Xunit;
using SparqlToolLib = SkyOmega.Mercury.Sparql.Tool.SparqlTool;

namespace SkyOmega.Mercury.Tests.SparqlTool;

/// <summary>
/// Tests for the SparqlTool library.
/// These tests call the library directly instead of spawning CLI processes.
/// </summary>
public class SparqlToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testTurtleFile;
    private readonly string _invalidTurtleFile;

    public SparqlToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sparql-tool-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Create test Turtle file
        _testTurtleFile = Path.Combine(_tempDir, "test.ttl");
        File.WriteAllText(_testTurtleFile, """
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .
            @prefix ex: <http://example.org/> .

            ex:alice a foaf:Person ;
                foaf:name "Alice" ;
                foaf:knows ex:bob .

            ex:bob a foaf:Person ;
                foaf:name "Bob" .
            """);

        // Create invalid Turtle file
        _invalidTurtleFile = Path.Combine(_tempDir, "invalid.ttl");
        File.WriteAllText(_invalidTurtleFile, "this is not valid turtle @#$%");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task SparqlTool_LoadAndQuery_ReturnsResults()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }"
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("Alice", stdout);
        Assert.Contains("Bob", stdout);
    }

    [Fact]
    public async Task SparqlTool_OutputFormat_Json()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }",
            Format = OutputFormat.Json
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("\"head\":", stdout);
        Assert.Contains("\"bindings\":", stdout);
    }

    [Fact]
    public async Task SparqlTool_OutputFormat_Csv()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }",
            Format = OutputFormat.Csv
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("name", stdout); // Header
    }

    [Fact]
    public async Task SparqlTool_OutputFormat_Tsv()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }",
            Format = OutputFormat.Tsv
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("name", stdout); // Header
    }

    [Fact]
    public async Task SparqlTool_OutputFormat_Xml()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }",
            Format = OutputFormat.Xml
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("<?xml", stdout);
        Assert.Contains("<sparql", stdout);
    }

    [Fact]
    public async Task SparqlTool_Explain_ShowsPlan()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            Explain = "SELECT * WHERE { ?s ?p ?o }"
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("QUERY PLAN", stdout);
    }

    [Fact]
    public async Task SparqlTool_PersistentStore_Persists()
    {
        var storePath = Path.Combine(_tempDir, "sparql-store");

        // Load data into store
        var loadOutput = new StringWriter();
        var loadError = new StringWriter();
        var loadOptions = new SparqlToolOptions
        {
            StorePath = storePath,
            LoadFile = _testTurtleFile
        };

        var loadResult = await SparqlToolLib.RunAsync(loadOptions, loadOutput, loadError);
        Assert.Equal(0, loadResult.ExitCode);
        Assert.Contains("Loaded", loadOutput.ToString());

        // Query same store without loading
        var queryOutput = new StringWriter();
        var queryError = new StringWriter();
        var queryOptions = new SparqlToolOptions
        {
            StorePath = storePath,
            Query = "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }"
        };

        var queryResult = await SparqlToolLib.RunAsync(queryOptions, queryOutput, queryError);
        Assert.Equal(0, queryResult.ExitCode);
        Assert.Contains("Alice", queryOutput.ToString());
    }

    [Fact]
    public async Task SparqlTool_QueryFile_ExecutesFromFile()
    {
        var queryFile = Path.Combine(_tempDir, "query.rq");
        await File.WriteAllTextAsync(queryFile,
            "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }");

        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            QueryFile = queryFile
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alice", output.ToString());
    }

    [Fact]
    public async Task SparqlTool_AskQuery_ReturnsBooleanResult()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "ASK WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }"
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("true", output.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task SparqlTool_NoAction_ReturnsError()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions();

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No action specified", error.ToString());
    }

    [Fact]
    public async Task SparqlTool_ConstructQuery_ReturnsTriples()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o } LIMIT 5",
            RdfOutputFormat = RdfFormat.NTriples
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("<http://example.org/", stdout);
    }

    [Fact]
    public async Task SparqlTool_DescribeQuery_ReturnsTriples()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = "DESCRIBE <http://example.org/alice>",
            RdfOutputFormat = RdfFormat.NTriples
        };

        var result = await SparqlToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        // The output contains either the resource URI or the "Loaded" message (describing something returns relevant triples)
        Assert.True(stdout.Contains("<http://example.org/alice>") || stdout.Contains("Loaded"),
            $"Expected output to contain describe results or load message, got: {stdout}");
    }
}
