using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace SkyOmega.Mercury.Tests.Cli;

/// <summary>
/// Integration tests for Mercury CLI tools.
/// These tests run the actual CLI executables and verify their output.
/// </summary>
/// <remarks>
/// CLI DLLs are copied to the test output directory at build time, so they
/// travel with the test assembly. This makes tests runner-agnostic - works
/// with dotnet test, VS, NCrunch, etc. See ADR-017 for the rationale.
/// </remarks>
public class CliIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testTurtleFile;
    private readonly string _invalidTurtleFile;
    private readonly string _turtleCliPath;
    private readonly string _sparqlCliPath;

    public CliIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mercury-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // CLI DLLs are in the same directory as the test assembly
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot determine test assembly location");

        _turtleCliPath = Path.Combine(assemblyDir, "Mercury.Cli.Turtle.dll");
        _sparqlCliPath = Path.Combine(assemblyDir, "Mercury.Cli.Sparql.dll");

        if (!File.Exists(_turtleCliPath))
            throw new InvalidOperationException($"Turtle CLI not found at {_turtleCliPath}. Ensure the build copies CLI DLLs to test output.");
        if (!File.Exists(_sparqlCliPath))
            throw new InvalidOperationException($"SPARQL CLI not found at {_sparqlCliPath}. Ensure the build copies CLI DLLs to test output.");

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

    #region Mercury.Cli.Turtle Tests

    [Fact]
    public async Task TurtleCli_Help_ShowsUsage()
    {
        var (exitCode, stdout, _) = await RunTurtleCliAsync("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Mercury Turtle CLI", stdout);
        Assert.Contains("--validate", stdout);
        Assert.Contains("--stats", stdout);
        Assert.Contains("--benchmark", stdout);
    }

    [Fact]
    public async Task TurtleCli_Validate_ValidFile_ReturnsSuccess()
    {
        var (exitCode, stdout, _) = await RunTurtleCliAsync($"--validate {_testTurtleFile}");

        Assert.Equal(0, exitCode);
        Assert.Contains("Valid Turtle:", stdout);
        Assert.Contains("triples", stdout);
    }

    [Fact]
    public async Task TurtleCli_Validate_InvalidFile_ReturnsError()
    {
        var (exitCode, _, stderr) = await RunTurtleCliAsync($"--validate {_invalidTurtleFile}");

        Assert.Equal(1, exitCode);
        Assert.Contains("Syntax error", stderr);
    }

    [Fact]
    public async Task TurtleCli_Stats_ShowsStatistics()
    {
        var (exitCode, stdout, _) = await RunTurtleCliAsync($"--stats {_testTurtleFile}");

        Assert.Equal(0, exitCode);
        Assert.Contains("Total triples:", stdout);
        Assert.Contains("Unique subjects:", stdout);
        Assert.Contains("Predicate distribution:", stdout);
    }

    [Fact]
    public async Task TurtleCli_Convert_ToNTriples()
    {
        var outputFile = Path.Combine(_tempDir, "output.nt");
        var (exitCode, _, stderr) = await RunTurtleCliAsync($"--input {_testTurtleFile} --output {outputFile}");

        Assert.Equal(0, exitCode);
        Assert.Contains("Converted", stderr); // Status message goes to stderr
        Assert.True(File.Exists(outputFile));

        var content = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("<http://example.org/alice>", content);
        Assert.Contains("<http://xmlns.com/foaf/0.1/name>", content);
    }

    [Fact]
    public async Task TurtleCli_Convert_ToStdout()
    {
        var (exitCode, stdout, _) = await RunTurtleCliAsync($"--input {_testTurtleFile} --output-format nt");

        Assert.Equal(0, exitCode);
        Assert.Contains("<http://example.org/alice>", stdout);
        Assert.Contains("<http://xmlns.com/foaf/0.1/name>", stdout);
    }

    [Fact]
    public async Task TurtleCli_Store_LoadsIntoQuadStore()
    {
        var storePath = Path.Combine(_tempDir, "store");
        var (exitCode, stdout, _) = await RunTurtleCliAsync($"--input {_testTurtleFile} --store {storePath}");

        Assert.Equal(0, exitCode);
        Assert.Contains("Loaded", stdout);
        Assert.Contains("triples", stdout);
        Assert.True(Directory.Exists(storePath));
    }

    [Fact]
    public async Task TurtleCli_Benchmark_ShowsMetrics()
    {
        var (exitCode, stdout, _) = await RunTurtleCliAsync("--benchmark --count 1000");

        Assert.Equal(0, exitCode);
        Assert.Contains("Benchmark", stdout);
        Assert.Contains("triples/sec", stdout);
        Assert.Contains("GC Collections", stdout);
    }

    [Fact]
    public async Task TurtleCli_Demo_RunsWithoutArgs()
    {
        var (exitCode, stdout, _) = await RunTurtleCliAsync("");

        Assert.Equal(0, exitCode);
        Assert.Contains("Mercury Turtle Parser Demo", stdout);
        Assert.Contains("Parsed", stdout);
    }

    #endregion

    #region Mercury.Cli.Sparql Tests

    [Fact]
    public async Task SparqlCli_Help_ShowsUsage()
    {
        var (exitCode, stdout, _) = await RunSparqlCliAsync("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Mercury SPARQL CLI", stdout);
        Assert.Contains("--load", stdout);
        Assert.Contains("--query", stdout);
        Assert.Contains("--repl", stdout);
    }

    [Fact]
    public async Task SparqlCli_LoadAndQuery_ReturnsResults()
    {
        var (exitCode, stdout, _) = await RunSparqlCliAsync(
            $"--load {_testTurtleFile} --query \"SELECT ?name WHERE {{ ?s <http://xmlns.com/foaf/0.1/name> ?name }}\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("Alice", stdout);
        Assert.Contains("Bob", stdout);
    }

    [Fact]
    public async Task SparqlCli_OutputFormat_Json()
    {
        var (exitCode, stdout, _) = await RunSparqlCliAsync(
            $"--load {_testTurtleFile} -q \"SELECT ?name WHERE {{ ?s <http://xmlns.com/foaf/0.1/name> ?name }}\" --format json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"head\":", stdout);
        Assert.Contains("\"bindings\":", stdout);
    }

    [Fact]
    public async Task SparqlCli_OutputFormat_Csv()
    {
        var (exitCode, stdout, _) = await RunSparqlCliAsync(
            $"--load {_testTurtleFile} -q \"SELECT ?name WHERE {{ ?s <http://xmlns.com/foaf/0.1/name> ?name }}\" --format csv");

        Assert.Equal(0, exitCode);
        Assert.Contains("name", stdout); // Header
    }

    [Fact]
    public async Task SparqlCli_OutputFormat_Xml()
    {
        var (exitCode, stdout, _) = await RunSparqlCliAsync(
            $"--load {_testTurtleFile} -q \"SELECT ?name WHERE {{ ?s <http://xmlns.com/foaf/0.1/name> ?name }}\" --format xml");

        Assert.Equal(0, exitCode);
        Assert.Contains("<?xml", stdout);
        Assert.Contains("<sparql", stdout);
    }

    [Fact]
    public async Task SparqlCli_Explain_ShowsPlan()
    {
        var (exitCode, stdout, _) = await RunSparqlCliAsync(
            "--explain \"SELECT * WHERE { ?s ?p ?o }\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("QUERY PLAN", stdout);
    }

    [Fact]
    public async Task SparqlCli_PersistentStore_Persists()
    {
        var storePath = Path.Combine(_tempDir, "sparql-store");

        // Load data into store
        var (loadExit, loadOut, _) = await RunSparqlCliAsync(
            $"--store {storePath} --load {_testTurtleFile}");
        Assert.Equal(0, loadExit);
        Assert.Contains("Loaded", loadOut);

        // Query same store without loading
        var (queryExit, queryOut, _) = await RunSparqlCliAsync(
            $"--store {storePath} --query \"SELECT ?name WHERE {{ ?s <http://xmlns.com/foaf/0.1/name> ?name }}\"");
        Assert.Equal(0, queryExit);
        Assert.Contains("Alice", queryOut);
    }

    [Fact]
    public async Task SparqlCli_QueryFile_ExecutesFromFile()
    {
        var queryFile = Path.Combine(_tempDir, "query.rq");
        await File.WriteAllTextAsync(queryFile,
            "SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }");

        var (exitCode, stdout, _) = await RunSparqlCliAsync(
            $"--load {_testTurtleFile} --query-file {queryFile}");

        Assert.Equal(0, exitCode);
        Assert.Contains("Alice", stdout);
    }

    [Fact]
    public async Task SparqlCli_AskQuery_ReturnsBooleanResult()
    {
        var (exitCode, stdout, _) = await RunSparqlCliAsync(
            $"--load {_testTurtleFile} --query \"ASK WHERE {{ ?s <http://xmlns.com/foaf/0.1/name> ?name }}\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("true", stdout.ToLowerInvariant());
    }

    [Fact]
    public async Task SparqlCli_NoAction_ReturnsError()
    {
        var (exitCode, _, stderr) = await RunSparqlCliAsync("");

        Assert.Equal(1, exitCode);
        Assert.Contains("No action specified", stderr);
    }

    #endregion

    #region Cross-CLI Tests

    [Fact]
    public async Task CrossCli_TurtleStoreCanBeQueriedBySparql()
    {
        var storePath = Path.Combine(_tempDir, "shared-store");

        // Load with Turtle CLI
        var (loadExit, _, _) = await RunTurtleCliAsync(
            $"--input {_testTurtleFile} --store {storePath}");
        Assert.Equal(0, loadExit);

        // Query with SPARQL CLI
        var (queryExit, queryOut, _) = await RunSparqlCliAsync(
            $"--store {storePath} --query \"SELECT ?name WHERE {{ ?s <http://xmlns.com/foaf/0.1/name> ?name }}\"");
        Assert.Equal(0, queryExit);
        Assert.Contains("Alice", queryOut);
        Assert.Contains("Bob", queryOut);
    }

    #endregion

    #region Helper Methods

    private async Task<(int exitCode, string stdout, string stderr)> RunTurtleCliAsync(string args)
    {
        return await RunCliAsync(_turtleCliPath, args);
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunSparqlCliAsync(string args)
    {
        return await RunCliAsync(_sparqlCliPath, args);
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunCliAsync(string dllPath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{dllPath}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start process");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return (process.ExitCode, stdout, stderr);
    }

    #endregion
}
