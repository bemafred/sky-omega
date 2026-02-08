# ADR-018: CLI Library Extraction for Test Environment Independence

## Status

Accepted

## Context

Mercury includes two CLI utilities that have grown substantially after ADR-016’s upgrades:

|CLI                 |Lines|Functionality                                                   |
|---------------------|-----|----------------------------------------------------------------|
|`Mercury.Cli.Sparql`|~1100|SPARQL query execution, RDF loading, REPL, result formatting    |
|`Mercury.Cli.Turtle`|~660 |Turtle parsing, validation, conversion, statistics, benchmarking|

ADR-017 addressed test runner portability by copying CLI DLLs to the test output directory, enabling NCrunch and other isolated test runners to locate the binaries. However, this approach has fundamental limitations:

### Current Problems

**1. Process boundary testing is fragile:**

- Tests spawn `dotnet exec` subprocesses
- Process startup overhead (~200ms) slows test execution
- stdout/stderr capture introduces timing dependencies
- Exit code semantics vary across platforms

**2. NCrunch compatibility remains problematic:**

- Build order dependencies between test and CLI projects
- `ProjectReference` with `ReferenceOutputAssembly=false` confuses some runners
- Custom MSBuild targets (`CopyCliTools`) may not execute in expected order

**3. Architectural smell:**

- Testing through a process boundary when we don’t need to
- The CLIs are not external tools — they’re our code
- Integration testing should verify CLI argument parsing, not terminal I/O

### The Insight

As discussed in the ideation conversation that led to this ADR:

> “There really is no point in testing if terminal apps work — we know they do, if the CLI hosts compile. We should test the CLI functionality, not the console wrapper.”

The CLI projects conflate two concerns:

1. **Business logic:** Parsing, execution, formatting, validation
1. **Terminal interaction:** Argument parsing, stdout/stderr, exit codes

Only the business logic needs testing. Terminal apps are trivial shims — if they compile and call the library, they work.

## Decision

Extract CLI functionality into testable class library projects. The CLI console applications become minimal entry points that delegate to these libraries.

### New Project Structure

```
src/
├── Mercury.Cli.Sparql/           # Thin console shim (~50 lines)
│   └── Program.cs                # Parses args, calls library, handles exit
├── Mercury.Cli.Turtle/           # Thin console shim (~50 lines)
│   └── Program.cs                # Parses args, calls library, handles exit
├── Mercury.Sparql.Tool/          # New library with all Sparql CLI logic
│   ├── SparqlTool.cs             # Main entry: Run(args) -> Result
│   ├── SparqlToolOptions.cs      # Strongly-typed options
│   ├── QueryRunner.cs            # Query execution logic
│   ├── ResultFormatter.cs        # JSON/CSV/TSV/XML output
│   └── ReplSession.cs            # Interactive REPL logic
├── Mercury.Turtle.Tool/          # New library with all Turtle CLI logic
│   ├── TurtleTool.cs             # Main entry: Run(args) -> Result
│   ├── TurtleToolOptions.cs      # Strongly-typed options
│   ├── TurtleValidator.cs        # Validation logic
│   ├── FormatConverter.cs        # RDF format conversion
│   └── StatisticsCollector.cs    # Triple counting, predicate stats
```

### Naming Rationale

The library names follow Mercury’s semantic naming principles:

- **`Mercury.Sparql.Tool`**: A tool (capability) for SPARQL operations
- **`Mercury.Turtle.Tool`**: A tool (capability) for Turtle operations

Alternatives considered and rejected:

- `Mercury.Cli.Sparql.Core` — “Core” is a non-semantic placeholder
- `Mercury.Sparql.Cli.Library` — “Library” is tautological for a library project
- `Mercury.Sparql.Command` — Implies imperative structure, not capability

### API Design

Each library exposes a clean programmatic interface:

```csharp
// Mercury.Sparql.Tool/SparqlTool.cs
namespace SkyOmega.Mercury.Sparql.Tool;

public sealed class SparqlTool
{
    /// <summary>
    /// Execute SPARQL tool operations programmatically.
    /// </summary>
    /// <param name=“options”>Strongly-typed options (no string parsing)</param>
    /// <param name=“output”>TextWriter for results (enables testing)</param>
    /// <param name=“error”>TextWriter for errors (enables testing)</param>
    /// <returns>Result with exit code and any error information</returns>
    public static async Task<ToolResult> RunAsync(
        SparqlToolOptions options,
        TextWriter output,
        TextWriter error)
    {
        // All current Program.cs logic moves here
    }
}

public sealed class SparqlToolOptions
{
    public string? LoadFile { get; init; }
    public string? Query { get; init; }
    public string? QueryFile { get; init; }
    public string? StorePath { get; init; }
    public string? Explain { get; init; }
    public OutputFormat Format { get; init; } = OutputFormat.Json;
    public RdfFormat RdfOutputFormat { get; init; } = RdfFormat.NTriples;
    public bool Repl { get; init; }
}

public readonly struct ToolResult
{
    public int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Success => ExitCode == 0;
}
```

### CLI Shim Pattern

The console application becomes trivial:

```csharp
// Mercury.Cli.Sparql/Program.cs
namespace SkyOmega.Mercury.Cli.Sparql;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Parse arguments (this part stays in CLI)
        var options = ParseArgs(args);
        
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }
        
        if (options.Error != null)
        {
            Console.Error.WriteLine($”Error: {options.Error}”);
            return 1;
        }
        
        // Delegate to library
        var result = await SparqlTool.RunAsync(
            options.ToToolOptions(),
            Console.Out,
            Console.Error);
        
        return result.ExitCode;
    }
    
    // Argument parsing remains CLI-specific
    private static CliOptions ParseArgs(string[] args) { ... }
    private static void PrintHelp() { ... }
}
```

### Test Refactoring

Tests reference the library directly — no process spawning, no file path resolution:

```csharp
// Mercury.Tests/SparqlTool/SparqlToolTests.cs
namespace SkyOmega.Mercury.Tests.SparqlTool;

public class SparqlToolTests : IDisposable
{
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly string _tempDir;
    
    [Fact]
    public async Task LoadAndQuery_ReturnsResults()
    {
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = “SELECT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }”
        };
        
        var result = await SparqlTool.RunAsync(options, _output, _error);
        
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(“Alice”, _output.ToString());
        Assert.Contains(“Bob”, _output.ToString());
    }
    
    [Fact]
    public async Task OutputFormat_Json_ProducesValidJson()
    {
        var options = new SparqlToolOptions
        {
            LoadFile = _testTurtleFile,
            Query = “SELECT * WHERE { ?s ?p ?o } LIMIT 1”,
            Format = OutputFormat.Json
        };
        
        var result = await SparqlTool.RunAsync(options, _output, _error);
        
        Assert.Equal(0, result.ExitCode);
        var json = _output.ToString();
        Assert.Contains(“\”head\”:”, json);
        Assert.Contains(“\”bindings\”:”, json);
    }
}
```

### Benefits Over Current Approach

|Aspect               |Current (Process Spawn)         |Proposed (Library)            |
|----------------------|--------------------------------|------------------------------|
|Test speed           |~200ms overhead per test        |Direct method call (~1ms)     |
|NCrunch compatibility|Fragile build order             |Standard ProjectReference     |
|Debugging            |Separate process, no breakpoints|Single process, full debugging|
|Code coverage        |Not measurable                  |Full coverage reporting       |
|Error messages       |Must parse stderr strings       |Structured ToolResult         |
|IDE support          |Limited IntelliSense            |Full IntelliSense, refactoring|

## Implementation Plan

### Phase 1: Create Library Projects

|Task|Description                                                        |
|----|-------------------------------------------------------------------|
|1.1 |Create `Mercury.Sparql.Tool` project                               |
|1.2 |Create `Mercury.Turtle.Tool` project                               |
|1.3 |Define `SparqlToolOptions`, `TurtleToolOptions`, `ToolResult` types|
|1.4 |Add projects to solution                                           |

### Phase 2: Extract Logic

|Task|Description                                                                               |
|----|-----------------------------------------------------------------------------------------------|
|2.1 |Move SPARQL execution logic from `Mercury.Cli.Sparql/Program.cs` to `Mercury.Sparql.Tool` |
|2.2 |Move Turtle processing logic from `Mercury.Cli.Turtle/Program.cs` to `Mercury.Turtle.Tool`|
|2.3 |Parameterize `TextWriter` for output (replace `Console.Out`/`Console.Error`)              |
|2.4 |Convert internal classes to public API surface                                            |

### Phase 3: Simplify CLIs

|Task|Description                                                            |
|----|-----------------------------------------------------------------------|
|3.1 |Reduce `Mercury.Cli.Sparql/Program.cs` to argument parsing + delegation|
|3.2 |Reduce `Mercury.Cli.Turtle/Program.cs` to argument parsing + delegation|
|3.3 |Add ProjectReference from CLI to corresponding Tool library            |

### Phase 4: Refactor Tests

|Task|Description                                                      |
|----|---------------------------------------------------------------------|
|4.1 |Create new test files: `SparqlToolTests.cs`, `TurtleToolTests.cs`|
|4.2 |Port existing integration tests to library-based tests           |
|4.3 |Remove process-spawning test infrastructure                      |
|4.4 |Remove `CopyCliTools` MSBuild target from `Mercury.Tests.csproj` |
|4.5 |Remove CLI ProjectReferences with `ReferenceOutputAssembly=false`|

### Phase 5: Cleanup

|Task|Description                                      |
|----|--------------------------------------------------|
|5.1 |Delete `Mercury.Tests/Cli/CliIntegrationTests.cs`|
|5.2 |Update ADR-017 status to “Superseded by ADR-018” |
|5.3 |Verify NCrunch runs all tests successfully       |
|5.4 |Update README and documentation                  |

## Success Criteria

- [x] All existing CLI functionality preserved
- [x] Tests run successfully in Visual Studio, Rider, `dotnet test`, and NCrunch
- [x] No process spawning in test code
- [x] Code coverage includes CLI library logic
- [x] CLI executables remain functional (manual verification)
- [x] Build time not significantly increased

## Consequences

### Positive

- **Test reliability:** No process spawn race conditions or timing issues
- **Test speed:** ~200x faster (method call vs process startup)
- **Debuggability:** Breakpoints work, stack traces are complete
- **Coverage:** Library code appears in coverage reports
- **NCrunch:** Standard ProjectReference “just works”
- **Cross-platform:** No platform-specific process handling

### Negative

- **More projects:** Solution gains two library projects
- **Migration effort:** Existing tests need rewriting
- **API surface:** Library APIs must be thoughtfully designed

### Neutral

- **CLI behavior unchanged:** End users see no difference
- **Argument parsing stays in CLI:** Intentional — this is terminal-specific

## Alternatives Considered

### Keep Process Spawning, Fix NCrunch Config

Configure NCrunch workspace settings to copy CLI DLLs correctly.

**Rejected:** Couples tests to specific tool configuration. Other runners may have the same issue.

### Test Only via CLI Integration Tests

Accept process spawn overhead as integration test cost.

**Rejected:** Violates EEE principle — tests should be fast and reliable. Process spawning introduces unnecessary failure modes.

### Merge CLIs into Single Tool

Create one CLI with subcommands (`mercury sparql ...`, `mercury turtle ...`).

**Rejected:** Orthogonal to this ADR. Could be done later if desired.

## References

- [ADR-016: Mercury CLI Tool Upgrade](ADR-016-cli-tool-upgrade.md) — Original CLI implementation
- [ADR-017: Test Environment Independence](ADR-017-test-environment-independence.md) — Superseded approach
- Mercury naming conventions (CLAUDE.md, “Naming Allergy” section)