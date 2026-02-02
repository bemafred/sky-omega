# ADR-016: Mercury CLI Tool Upgrade

## Status

Accepted

## Context

Mercury includes two CLI demo applications that showcase parser capabilities but lack meaningful functionality:

| CLI | Current State | Lines | Issues |
|-----|--------------|-------|--------|
| `Mercury.Cli.Sparql` | Parses 5 hardcoded queries, prints metadata | 66 | No storage, no execution, no results |
| `Mercury.Cli.Turtle` | Parses sample Turtle, runs perf test | 252 | Uses legacy allocating API, dead code |

Both projects reference Mercury correctly but only exercise parsers. None of Mercury's powerful features (QuadStore, QueryExecutor, batch loading, result formatting) are wired up.

### Current Problems

**Mercury.Cli.Sparql:**
- Parses queries but cannot execute them (no QuadStore)
- No way to load RDF data for querying
- No result output in any format (JSON/CSV/TSV/XML)
- No EXPLAIN support for query plans
- Hardcoded query list, no interactive mode

**Mercury.Cli.Turtle:**
- Uses legacy `IAsyncEnumerable` API which allocates (defeats zero-GC design)
- Does not persist parsed triples anywhere
- Contains dead code: `ILucyRdfStore` interface and `LucyTurtleImporter` class (lines 219-251)
- GC measurement is unreliable (`GC.GetTotalMemory(true)` between examples)
- Creates side-effect files without proper cleanup on exception

### Desired Outcome

Transform these demos into useful CLI tools that:
1. Actually exercise Mercury's full capabilities
2. Demonstrate best practices for integration
3. Provide practical utility for testing/development

## Decision

Upgrade both CLIs to be functional tools with proper Mercury integration:

### Unified CLI Architecture

Both CLIs will share a common pattern:

```
                    ┌─────────────────┐
                    │   CLI Entry     │
                    │  (Program.cs)   │
                    └────────┬────────┘
                             │
              ┌──────────────┼──────────────┐
              │              │              │
              ▼              ▼              ▼
        ┌──────────┐  ┌──────────┐  ┌──────────┐
        │  LOAD    │  │  QUERY   │  │  EXPORT  │
        │ RDF data │  │ SPARQL   │  │ Results  │
        └────┬─────┘  └────┬─────┘  └────┬─────┘
             │             │             │
             └──────┬──────┴─────────────┘
                    │
                    ▼
              ┌──────────┐
              │QuadStore │
              │(temp/named)│
              └──────────┘
```

### Store Modes

Both CLIs support two storage modes:

| Mode | Flag | Behavior |
|------|------|----------|
| **Temp** | (default) | Creates temp store, auto-deleted on exit |
| **Named** | `--store <path>` | Uses persistent store at path, creates if needed |

**Named store benefits:**
- Data persists between CLI invocations
- Can build up knowledge base incrementally
- Query existing stores without reloading data
- Share stores between tools

**Auto-creation:** When `--store` path doesn't exist, the CLI creates a new QuadStore at that location. This enables workflows like:
```bash
# First run: create store and load data
mercury-sparql --store ./mydata --load data.ttl

# Later: query existing store (no reload needed)
mercury-sparql --store ./mydata --query "SELECT * WHERE { ?s ?p ?o }"

# Add more data incrementally
mercury-sparql --store ./mydata --load more-data.nt
```

### Mercury.Cli.Sparql Upgrade

**New capabilities:**
1. Load RDF data from file (Turtle, N-Triples, RDF/XML, N-Quads, TriG, JSON-LD)
2. Execute SPARQL queries against loaded data
3. Output results in JSON, CSV, TSV, or XML format
4. Show query execution plan (EXPLAIN)
5. Interactive REPL mode for ad-hoc queries

**Usage:**
```bash
# Load data and run query (temp store, auto-deleted)
mercury-sparql --load data.ttl --query "SELECT * WHERE { ?s ?p ?o } LIMIT 10"

# Use persistent named store (created if doesn't exist)
mercury-sparql --store ./mydb --load data.ttl --query "SELECT * WHERE { ?s ?p ?o }"

# Query existing store (no load needed)
mercury-sparql --store ./mydb --query "SELECT ?name WHERE { ?s <name> ?name }"

# Add more data to existing store
mercury-sparql --store ./mydb --load more-data.nt

# Output in different formats
mercury-sparql --load data.nt --query "SELECT ?name WHERE { ?s <name> ?name }" --format csv

# Show execution plan
mercury-sparql --load data.ttl --explain "SELECT * WHERE { ?s <knows> ?o . ?o <name> ?n }"

# Interactive REPL with persistent store
mercury-sparql --store ./mydb --repl

# Read query from file
mercury-sparql --store ./mydb --query-file query.rq --format json
```

**Implementation outline:**
```csharp
// Program.cs - simplified structure
public static async Task Main(string[] args)
{
    var options = ParseArgs(args);

    // Determine store path: named (persistent) or temp (auto-deleted)
    var isTemp = options.StorePath == null;
    var storePath = options.StorePath
        ?? Path.Combine(Path.GetTempPath(), $"mercury-cli-{Guid.NewGuid():N}");

    try
    {
        // Create store if it doesn't exist (works for both temp and named)
        using var store = new QuadStore(storePath);

        // Load RDF data using zero-GC callback API
        if (options.LoadFile != null)
        {
            var count = await LoadRdfFile(store, options.LoadFile);
            Console.WriteLine($"Loaded {count} triples from {options.LoadFile}");
        }

        // Execute query or enter REPL
        if (options.Query != null)
        {
            await ExecuteQuery(store, options.Query, options.Format);
        }
        else if (options.Explain != null)
        {
            ShowExplainPlan(options.Explain);
        }
        else if (options.Repl)
        {
            await RunRepl(store, options.Format);
        }
    }
    finally
    {
        // Only cleanup temp stores, preserve named stores
        if (isTemp && Directory.Exists(storePath))
            Directory.Delete(storePath, recursive: true);
    }
}
```

### Mercury.Cli.Turtle Upgrade

**New capabilities:**
1. Load Turtle using zero-GC callback API (not legacy IAsyncEnumerable)
2. Convert between RDF formats (Turtle → N-Triples, RDF/XML, etc.)
3. Validate Turtle syntax with detailed error reporting
4. Show statistics (triple count, predicate distribution)
5. Optionally persist to QuadStore for verification

**Usage:**
```bash
# Validate Turtle syntax
mercury-turtle --validate input.ttl

# Convert Turtle to N-Triples
mercury-turtle --input data.ttl --output data.nt

# Convert with format detection
mercury-turtle --input data.ttl --output-format nquads > data.nq

# Show statistics
mercury-turtle --stats data.ttl

# Performance benchmark (zero-GC verification)
mercury-turtle --benchmark data.ttl

# Load into persistent QuadStore (created if doesn't exist)
mercury-turtle --input data.ttl --store ./mydb

# Load multiple files into same store
mercury-turtle --input file1.ttl --store ./mydb
mercury-turtle --input file2.ttl --store ./mydb
```

**Implementation outline:**
```csharp
// Program.cs - simplified structure
public static async Task Main(string[] args)
{
    var options = ParseArgs(args);

    if (options.Validate)
    {
        await ValidateTurtle(options.Input);
    }
    else if (options.Output != null || options.OutputFormat != null)
    {
        await ConvertFormat(options.Input, options.Output, options.OutputFormat);
    }
    else if (options.Stats)
    {
        await ShowStatistics(options.Input);
    }
    else if (options.Benchmark)
    {
        await RunBenchmark(options.Input);
    }
    else if (options.StorePath != null)
    {
        // Load into persistent QuadStore (created if doesn't exist)
        using var store = new QuadStore(options.StorePath);
        var count = await LoadWithZeroGc(options.Input, store);
        Console.WriteLine($"Loaded {count} triples into {options.StorePath}");
    }
}

// Use zero-GC callback API
static async Task<long> LoadWithZeroGc(string path, QuadStore store)
{
    await using var stream = File.OpenRead(path);
    using var parser = new TurtleStreamParser(stream);

    var batch = store.BeginBatch();
    long count = 0;

    // Zero-GC callback API - no IAsyncEnumerable allocation
    await parser.ParseAsync((subject, predicate, obj) =>
    {
        batch.AddCurrent(subject.ToString(), predicate.ToString(), obj.ToString());
        count++;
    });

    batch.Commit();
    return count;
}
```

### Code to Remove

**Mercury.Cli.Turtle dead code (lines 219-251):**
```csharp
// DELETE: Unused interface
public interface ILucyRdfStore { ... }

// DELETE: Unused class
public class LucyTurtleImporter { ... }
```

### Shared Components

Both CLIs will use:

| Component | Purpose |
|-----------|---------|
| `RdfFormatNegotiator` | Detect RDF format from file extension/content |
| `QuadStore` | Storage for loaded data (temp or persistent) |
| `ContentNegotiator` | Map Accept headers to result format |
| Zero-GC callback APIs | `ParseAsync((s,p,o) => {...})` pattern |

**Store creation logic** (shared between CLIs):
```csharp
// QuadStore constructor creates store if path doesn't exist
// This works for both new named stores and temp stores
using var store = new QuadStore(storePath);
```

## Implementation Plan

### Phase 1: Mercury.Cli.Turtle Cleanup (Low Risk) - DONE

| Task | Description | Status |
|------|-------------|--------|
| 1.1 | Remove dead code (`ILucyRdfStore`, `LucyTurtleImporter`) | Done |
| 1.2 | Replace legacy `IAsyncEnumerable` with zero-GC callback API | Done |
| 1.3 | Add proper file cleanup with `try/finally` | Done |
| 1.4 | Improve GC measurement accuracy | Done |

### Phase 2: Mercury.Cli.Turtle Features - DONE

| Task | Description | Status |
|------|-------------|--------|
| 2.1 | Add argument parsing (input file, output file, format, store, options) | Done |
| 2.2 | Implement `--validate` mode with error line/column reporting | Done |
| 2.3 | Implement format conversion using appropriate writers | Done |
| 2.4 | Implement `--stats` mode (triple count, predicate histogram) | Done |
| 2.5 | Implement `--benchmark` mode with proper zero-GC verification | Done |
| 2.6 | Implement `--store` mode for persistent QuadStore loading | Done |

### Phase 3: Mercury.Cli.Sparql Foundation - DONE

| Task | Description | Status |
|------|-------------|--------|
| 3.1 | Add argument parsing (load, query, format, explain, repl, store) | Done |
| 3.2 | Implement store mode selection (temp vs named persistent) | Done |
| 3.3 | Create QuadStore at path, auto-create if doesn't exist | Done |
| 3.4 | Implement RDF loading using format detection | Done |
| 3.5 | Wire up `QueryExecutor` for query execution | Done |

### Phase 4: Mercury.Cli.Sparql Features - DONE

| Task | Description | Status |
|------|-------------|--------|
| 4.1 | Implement result formatting (JSON, CSV, TSV, XML) | Done |
| 4.2 | Implement `--explain` mode using `SparqlExplainer` | Done |
| 4.3 | Implement interactive REPL mode | Done |
| 4.4 | Add query file support (`--query-file`) | Done |

### Phase 5: Documentation and Testing - DONE

| Task | Description | Status |
|------|-------------|--------|
| 5.1 | Add help text (`--help`) for both CLIs | Done |
| 5.2 | Update api-usage.md with CLI usage examples | Done |
| 5.3 | Add integration tests for CLI commands | Done (20 tests) |

## Success Criteria

- [x] Mercury.Cli.Turtle uses zero-GC callback API (no `IAsyncEnumerable`)
- [x] Mercury.Cli.Turtle dead code removed
- [x] Mercury.Cli.Turtle can convert between RDF formats
- [x] Mercury.Cli.Turtle supports `--store` for persistent QuadStore loading
- [x] Mercury.Cli.Sparql can load RDF data into QuadStore
- [x] Mercury.Cli.Sparql supports `--store` for named persistent stores
- [x] Mercury.Cli.Sparql auto-creates store if path doesn't exist
- [x] Mercury.Cli.Sparql can execute queries and return results
- [x] Mercury.Cli.Sparql supports JSON, CSV, TSV, XML output formats
- [x] Mercury.Cli.Sparql has EXPLAIN mode for query plans
- [x] Both CLIs have `--help` documentation
- [x] Temp directories cleaned up on exit (named stores preserved)

## Consequences

### Benefits

- **Practical utility**: CLIs become useful for RDF/SPARQL development and testing
- **Persistent stores**: Named stores enable incremental data building and reuse
- **Integration example**: Demonstrates proper Mercury API usage patterns
- **Zero-GC showcase**: Turtle CLI demonstrates zero-allocation parsing
- **Developer tooling**: Developers can test queries without writing code
- **Workflow flexibility**: Same store usable from both CLIs

### Drawbacks

- **Increased code size**: CLIs grow from ~300 lines total to ~600-800 lines
- **Maintenance burden**: More functionality to maintain
- **Disk usage**: Temp QuadStore requires disk space during operation
- **Named store management**: Users responsible for cleanup of persistent stores

### Alternatives Considered

1. **Remove CLIs entirely**: Rejected - they provide value as examples and tools
2. **Keep as minimal demos**: Rejected - current state is misleading and not useful
3. **Create single unified CLI**: Rejected - separate concerns (parsing vs querying) warrant separate tools

## References

- [API Usage Documentation](../../api/api-usage.md) - Mercury API examples
- [ADR-003: Buffer Pattern](ADR-003-buffer-pattern.md) - Zero-GC design principles
- CLAUDE.md - Batch write API, zero-GC callback patterns
