using System;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Turtle.Tool;
using Xunit;
using TurtleToolLib = SkyOmega.Mercury.Turtle.Tool.TurtleTool;

namespace SkyOmega.Mercury.Tests.TurtleTool;

/// <summary>
/// Tests for the TurtleTool library.
/// These tests call the library directly instead of spawning CLI processes.
/// </summary>
public class TurtleToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testTurtleFile;
    private readonly string _invalidTurtleFile;

    public TurtleToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"turtle-tool-tests-{Guid.NewGuid():N}");
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
    public async Task TurtleTool_Validate_ValidFile()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile,
            Validate = true
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("Valid Turtle:", stdout);
        Assert.Contains("triples", stdout);
    }

    [Fact]
    public async Task TurtleTool_Validate_InvalidFile()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _invalidTurtleFile,
            Validate = true
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Syntax error", error.ToString());
    }

    [Fact]
    public async Task TurtleTool_Statistics_ShowsStats()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile,
            Stats = true
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("Total triples:", stdout);
        Assert.Contains("Unique subjects:", stdout);
        Assert.Contains("Predicate distribution:", stdout);
    }

    [Fact]
    public async Task TurtleTool_Convert_ToNTriples()
    {
        var outputFile = Path.Combine(_tempDir, "output.nt");
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile,
            OutputFile = outputFile
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Converted", error.ToString()); // Status message goes to stderr
        Assert.True(File.Exists(outputFile));

        var content = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("<http://example.org/alice>", content);
        Assert.Contains("<http://xmlns.com/foaf/0.1/name>", content);
    }

    [Fact]
    public async Task TurtleTool_Convert_ToStdout()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile,
            OutputFormat = RdfFormat.NTriples
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("<http://example.org/alice>", stdout);
        Assert.Contains("<http://xmlns.com/foaf/0.1/name>", stdout);
    }

    [Fact]
    public async Task TurtleTool_Load_CreatesStore()
    {
        var storePath = Path.Combine(_tempDir, "store");
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile,
            StorePath = storePath
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("Loaded", stdout);
        Assert.Contains("triples", stdout);
        Assert.True(Directory.Exists(storePath));
    }

    [Fact]
    public async Task TurtleTool_Benchmark_ShowsMetrics()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            Benchmark = true,
            TripleCount = 1000
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("Benchmark", stdout);
        Assert.Contains("triples/sec", stdout);
        Assert.Contains("GC Collections", stdout);
    }

    [Fact]
    public async Task TurtleTool_Demo_RunsWithNoInput()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions();

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("Mercury Turtle Parser Demo", stdout);
        Assert.Contains("Parsed", stdout);
    }

    [Fact]
    public async Task TurtleTool_Parse_PrintsTriples()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        var stdout = output.ToString();
        Assert.Contains("<http://example.org/alice>", stdout);
        Assert.Contains(".", stdout);
    }

    [Fact]
    public async Task TurtleTool_Convert_ToNQuads()
    {
        var outputFile = Path.Combine(_tempDir, "output.nq");
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile,
            OutputFile = outputFile,
            OutputFormat = RdfFormat.NQuads
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile));

        var content = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("<http://example.org/alice>", content);
    }

    [Fact]
    public async Task TurtleTool_Convert_ToTriG()
    {
        var outputFile = Path.Combine(_tempDir, "output.trig");
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new TurtleToolOptions
        {
            InputFile = _testTurtleFile,
            OutputFile = outputFile,
            OutputFormat = RdfFormat.TriG
        };

        var result = await TurtleToolLib.RunAsync(options, output, error);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile));
    }
}
