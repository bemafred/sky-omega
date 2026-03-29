> 📖 **New here?** Read [AI.md](AI.md) first for project context.

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Initialize submodules (W3C test data — required for dotnet test)
./tools/update-submodules.sh   # or .\tools\update-submodules.ps1

# Build entire solution
dotnet build SkyOmega.sln

# Build specific project
dotnet build src/Mercury/Mercury.csproj

# Release build (enables optimizations)
dotnet build -c Release

# Run tests (xUnit)
dotnet test

# Run specific test
dotnet test --filter "FullyQualifiedName~BasicSelect"

# Run benchmarks (BenchmarkDotNet)
dotnet run --project benchmarks/Mercury.Benchmarks -c Release

# Run specific benchmark class
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*Storage*"

# List available benchmarks
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --list

# Run examples
dotnet run --project examples/Mercury.Examples
dotnet run --project examples/Mercury.Examples -- storage
dotnet run --project examples/Mercury.Examples -- temporal
dotnet run --project examples/Mercury.Examples -- demo
```

## File-Based Apps (.NET 10)

For throwaway scripts, one-off debugging, test data generation, or quick repro cases, use file-based apps instead of creating a full project. Write a single `.cs` file and run it directly:

```csharp
#!/usr/bin/env -S dotnet
#:project ../src/Mercury/Mercury.csproj

// your code here
```

```bash
chmod +x script.cs
./script.cs          # or: dotnet script.cs
```

Use `#:package Name@version` for NuGet references, `#:project path` for project references, `#:sdk Microsoft.NET.Sdk.Web` for web apps. Do not add file-based scripts to the solution — they are standalone by design.

## Global Tools

Sky Omega tools are packaged as .NET global tools for use from any directory.

```bash
# Install all tools from local source
./tools/install-tools.sh

# Or install individually from local nupkg
dotnet pack SkyOmega.sln -c Release -o ./nupkg
dotnet tool install -g SkyOmega.Mercury.Mcp --add-source ./nupkg
```

| Command | Description |
|---------|-------------|
| `mercury` | SPARQL CLI with persistent store at `~/Library/SkyOmega/stores/cli/` |
| `mercury-mcp` | MCP server for Claude with persistent store at `~/Library/SkyOmega/stores/mcp/` |
| `mercury-sparql` | SPARQL query engine demo |
| `mercury-turtle` | Turtle parser demo |
| `drhook-mcp` | MCP server for .NET runtime inspection (EventPipe + DAP) |

All tools support `-v`/`--version`.

### MCP Integration

**Dev-time** (this repo): `.mcp.json` at repo root auto-configures Claude Code.

**Production** (any repo):
```bash
claude mcp add --transport stdio --scope user mercury -- mercury-mcp
claude mcp add --transport stdio --scope user drhook -- drhook-mcp
```

### Semantic Memory

Mercury MCP provides persistent semantic memory across sessions. The store at `~/Library/SkyOmega/stores/mcp/` survives between sessions — what you write, future sessions can query.

**At session start:** check what's in memory. If `mercury_stats` reports zero quads, follow the bootstrap procedure in [MERCURY.md](MERCURY.md) to load foundational knowledge from `docs/knowledge/bootstrap.ttl`. **At session end:** consider what's worth remembering.

See **[MERCURY.md](MERCURY.md)** for when, why, and how to use semantic memory — including EEE discipline, provenance conventions, and consolidation patterns.

## In-Flight Work: ADRs

Architecture Decision Records track planning and progress for complex features:

```bash
ls docs/adrs/             # Cross-cutting ADRs (e.g., ADR-000 repo structure)
ls docs/adrs/mercury/     # Mercury ADRs
ls docs/adrs/minerva/     # Minerva ADRs
ls docs/adrs/drhook/      # DrHook ADRs
```

**ADR workflow:** Plan in ADR → implement → check off success criteria → update status to "Accepted".

See individual ADRs for current implementation status. Don't duplicate progress tracking in CLAUDE.md.

## Codebase Statistics

See **[STATISTICS.md](STATISTICS.md)** for line counts, benchmark summaries, and growth tracking. Update after significant changes.

## Project Overview

Sky Omega is a semantic-aware cognitive assistant with zero-GC performance design. The codebase targets .NET 10 with C# 14. The core library (Mercury) has **no external dependencies** (BCL only). Mercury exposes **21 public types** (3 facades, 2 protocol, 11 storage, 3 diagnostics, 2 delegates); all ~140 other types are internal.

### Solution Structure

**IDE Views:** Visual Studio, Rider, and VS Code support both *Solution View* (virtual folders defined in `.sln`) and *Filesystem View* (actual directory structure). This solution uses virtual folders to provide logical grouping for developers:

- **Solution View**: ADRs appear under their substrate (Mercury/Minerva), architecture docs under Documentation
- **Filesystem View**: All docs live in `docs/` with consistent paths for linking

Both views are valid and useful. Solution View is optimized for browsing by role (architect, developer), while Filesystem View reflects the actual repository structure.

```
SkyOmega.sln
├── docs/
│   ├── adrs/                # Architecture Decision Records
│   │   ├── mercury/         # Mercury-specific ADRs
│   │   ├── minerva/         # Minerva-specific ADRs
│   │   └── drhook/          # DrHook-specific ADRs
│   ├── specs/               # External format specifications
│   │   ├── rdf/             # RDF specs (future: SPARQL, Turtle, etc.)
│   │   └── llm/             # LLM specs (GGUF, SafeTensors, Tokenizers)
│   ├── architecture/        # Conceptual documentation
│   ├── knowledge/           # Shared semantic knowledge (Turtle files, see MERCURY.md)
│   └── api/                 # API documentation
├── src/
│   ├── Mercury/             # Knowledge substrate - RDF storage and SPARQL (BCL only)
│   │   ├── NTriples/        # Streaming N-Triples parser
│   │   ├── Rdf/             # Triple data structures
│   │   ├── RdfXml/          # Streaming RDF/XML parser
│   │   ├── Sparql/          # SPARQL parser and query execution
│   │   │   ├── Execution/   # Query executor, results, query planning
│   │   │   │   ├── Expressions/ # Filter/BIND evaluation, filter analysis
│   │   │   │   ├── Federated/   # SERVICE clause, LOAD, remote execution
│   │   │   │   └── Operators/   # One file per scan operator (ref structs)
│   │   │   ├── Parsing/     # SparqlParser, RdfParser (zero-GC parsing)
│   │   │   ├── Patterns/    # PatternSlot, QueryBuffer (Buffer+View pattern)
│   │   │   └── Types/       # One file per SPARQL type (Query, GraphPattern, etc.)
│   │   ├── Storage/         # B+Tree indexes, atom storage, WAL
│   │   └── Turtle/          # Streaming RDF Turtle parser
│   ├── Mercury.Abstractions/ # Shared interfaces and types (RdfFormat, Results)
│   ├── Mercury.Runtime/     # Runtime utilities (CrossProcessStoreGate, TempPath, buffers)
│   ├── Mercury.Cli/         # Mercury CLI tool (persistent store)
│   ├── Mercury.Cli.Sparql/  # SPARQL engine CLI (thin shim over Mercury.Sparql.Tool)
│   ├── Mercury.Cli.Turtle/  # Turtle parser CLI (thin shim over Mercury.Turtle.Tool)
│   ├── Mercury.Sparql.Tool/ # SPARQL CLI logic as testable library
│   ├── Mercury.Turtle.Tool/ # Turtle CLI logic as testable library
│   ├── Mercury.Mcp/         # MCP server for Claude
│   ├── Mercury.Pruning/     # Dual-instance pruning with copy-and-switch
│   ├── Mercury.Solid/       # Solid protocol server (authentication, access control, N3)
│   │
│   ├── Minerva.Core/        # Thought substrate - tensor inference (BCL only)
│   │   ├── Weights/         # GGUF and SafeTensors readers
│   │   ├── Tokenizers/      # BPE, SentencePiece tokenizers
│   │   ├── Tensors/         # Tensor operations
│   │   └── Inference/       # Model inference
│   ├── Minerva.Cli/         # Minerva CLI
│   ├── Minerva.Mcp/         # Minerva MCP server
│   │
│   ├── DrHook/              # Runtime observation substrate (EventPipe + DAP)
│   │   ├── Diagnostics/     # ProcessAttacher, StackInspector (EventPipe)
│   │   └── Stepping/        # DapClient, SteppingSessionManager, NetCoreDbgLocator
│   └── DrHook.Mcp/          # MCP server for .NET runtime inspection
├── tests/
│   ├── Mercury.Tests/       # Mercury xUnit tests
│   │   ├── Diagnostics/     # Diagnostic system tests
│   │   ├── Fixtures/        # Test fixtures and helpers
│   │   ├── Infrastructure/  # Cross-cutting tests (allocation, concurrency, buffers)
│   │   ├── Owl/             # OWL/RDFS reasoning tests
│   │   ├── Rdf/             # RDF format parser/writer tests
│   │   ├── Repl/            # REPL session tests
│   │   ├── Sparql/          # SPARQL parser, executor, protocol tests
│   │   ├── Storage/         # Storage layer tests (QuadStore, AtomStore, WAL)
│   │   └── W3C/             # W3C conformance test suites
│   ├── Mercury.Solid.Tests/ # Mercury Solid protocol tests
│   ├── DrHook.Tests/        # DrHook xUnit tests
│   ├── Minerva.Tests/       # Minerva xUnit tests
│   ├── w3c-json-ld-api/     # W3C JSON-LD conformance test suite data
│   └── w3c-rdf-tests/       # W3C RDF conformance test suite data
├── benchmarks/
│   ├── Mercury.Benchmarks/  # Mercury BenchmarkDotNet tests
│   └── Minerva.Benchmarks/  # Minerva benchmarks (future)
└── examples/
    ├── Mercury.Examples/    # Mercury usage examples
    └── Minerva.Examples/    # Minerva usage examples (future)
```

## API Usage Examples

For detailed code examples of all APIs, see **[docs/api/api-usage.md](docs/api/api-usage.md)**.

## Architecture

### Component Layers

```
Sky (Agent) → James (Orchestration) → Lucy (Semantic Memory) → Mercury (Storage)
                                   ↘ Mira (Surfaces) ↙
```

- **Sky** - Cognitive agent with reasoning and reflection
- **James** - Orchestration layer with pedagogical guidance
- **Lucy** - RDF triple store with SPARQL queries
- **Mira** - Presentation surfaces (CLI, chat, IDE extensions)
- **Mercury** - B+Tree indexes, append-only stores, memory-mapped files
- **Minerva** - Tensor inference (BCL only), HW interop in C/C++

For the vision, methodology (EEE), and broader context, see [docs/architecture/sky-omega-convergence.md](docs/architecture/sky-omega-convergence.md).

### Technical Reference

Detailed subsystem documentation — read on demand when working on specific areas:

- **[Mercury Internals](docs/architecture/technical/mercury-internals.md)** — storage layer, durability/WAL design, concurrency, zero-GC patterns, pruning, parsers, writers
- **[SPARQL Reference](docs/architecture/technical/sparql-reference.md)** — supported features, operator pipeline, EXPLAIN symbols, result formats, content negotiation, temporal extensions, OWL reasoning, HTTP server
- **[Production Hardening](docs/architecture/technical/production-hardening.md)** — infrastructure abstractions, query optimization, full-text search, benchmarking workflow, NCrunch configuration, cross-process coordination

## Code Conventions

- All parsing methods follow W3C EBNF grammar productions (comments reference production numbers)
- Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for hot paths
- Prefer `ReadOnlySpan<char>` over `string` for parsing operations
- Use `unsafe fixed` buffers for small inline storage when needed
- Temporal semantics are implicit - all triples have valid-time bounds

### Culture Invariance

All numeric and date formatting in RDF/SPARQL code paths MUST use `CultureInfo.InvariantCulture` to ensure consistent output across all locales:

```csharp
// Integers
value.ToString(CultureInfo.InvariantCulture)

// Doubles/Floats
value.ToString("G", CultureInfo.InvariantCulture)

// DateTimes
dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
```

**Rationale:** Swedish locale uses `,` as decimal separator, but W3C RDF/SPARQL specifications require `.` for numeric literals. See [ADR-014](docs/adrs/mercury/ADR-014-culture-invariance.md) for details.

## Design Philosophy

Sky Omega values:
- **Simplicity over flexibility** - fewer moving parts, less to break
- **Append-only where possible** - naturally crash-safe, simpler recovery
- **Zero external dependencies for core library** - Mercury is BCL only; dev tooling (tests, benchmarks) can use standard packages
- **Zero-GC on hot paths** - predictable latency for cognitive operations
