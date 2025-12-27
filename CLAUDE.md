# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build entire solution
dotnet build SkyOmega.sln

# Build specific project
dotnet build src/Mercury/Mercury.csproj

# Release build (enables optimizations)
dotnet build -c Release

# Run tests and examples
dotnet run --project src/Mercury.Tests/Mercury.Tests.csproj
dotnet run --project src/Mercury.Tests/Mercury.Tests.csproj -- tests
dotnet run --project src/Mercury.Tests/Mercury.Tests.csproj -- storage
dotnet run --project src/Mercury.Tests/Mercury.Tests.csproj -- temporal
```

## Project Overview

Sky Omega is a semantic-aware cognitive assistant with zero-GC performance design. The codebase targets .NET 10 with C# 14 and has **no external dependencies** (BCL only).

### Solution Structure

```
SkyOmega.sln
├── Mercury              # Core library - storage and query engine
│   ├── Rdf/             # Triple data structures
│   ├── Sparql/          # SPARQL parser and query execution
│   ├── Storage/         # B+Tree indexes, atom storage, WAL
│   └── Turtle/          # Streaming RDF Turtle parser
├── Mercury.Cli.Turtle   # Turtle parser CLI demo
├── Mercury.Cli.Sparql   # SPARQL engine CLI demo
└── Mercury.Tests        # Tests and benchmarks
```

## Architecture

### Component Layers

```
Sky (Agent Layer) → Lucy (Semantic Memory) → Mercury (Storage Substrate)
```

- **Sky** - Cognitive agent with reasoning and reflection
- **Lucy** - RDF triple store with SPARQL queries
- **Mercury** - B+Tree indexes, append-only stores, memory-mapped files

### Storage Layer (`SkyOmega.Mercury.Storage`)

| Component | Purpose |
|-----------|---------|
| `TripleStore` | Multi-index RDF store (SPOT/POST/OSPT/TSPO) |
| `TripleIndex` | Single B+Tree index with bitemporal support |
| `AtomStore` | String interning with memory-mapped storage |
| `PageCache` | LRU cache for B+Tree pages (clock algorithm) |

### Durability Design

Sky Omega uses Write-Ahead Logging (WAL) for crash safety:

1. **Write path**: WAL append → fsync → apply to indexes
2. **Recovery**: Replay uncommitted WAL entries after last checkpoint
3. **Checkpointing**: Hybrid trigger (size OR time, whichever first)

**Design decisions and rationale:**

- **AtomStore has no separate WAL**: It's append-only by design. On recovery, validate tail and rebuild hash index. Simpler than double-WAL.
- **WAL stores atom IDs, not strings**: Atoms are persisted before WAL write (we need IDs to write the record). Natural ordering solves the dependency.
- **Batch-first design**: TxId in WAL records enables batching. Single writes are batch-of-one. Amortizing fsync across N triples is critical for performance.
- **Hybrid checkpoint trigger**: Size-based (16MB) adapts to bursts; time-based (60s) bounds recovery during idle.

### Zero-GC Design Principles

All parsers use aggressive zero-allocation techniques:
- `ref struct` parsers that live entirely on the stack
- `ArrayPool<T>` for all buffer allocations
- `ReadOnlySpan<char>` for string operations
- String interning via AtomStore to avoid duplicate allocations
- Streaming enumerators that yield results without materializing collections

### Turtle Parser (`SkyOmega.Mercury.Turtle`)

`TurtleStreamParser` is a `partial class` split across files:
- `TurtleStreamParser.cs` - Main parser logic and `ParseAsync()` entry point
- `TurtleStreamParser.Buffer.cs` - Buffer management
- `TurtleStreamParser.Structures.cs` - RDF structure parsing (blank nodes, collections)
- `TurtleStreamParser.Terminals.cs` - Terminal parsing (IRIs, literals, prefixed names)

API: `IAsyncEnumerable<RdfTriple>` streaming interface.

### SPARQL Engine (`SkyOmega.Mercury.Sparql`)

`SparqlParser` is a `ref struct` that parses SPARQL queries from `ReadOnlySpan<char>`.

Key components:
- `SparqlParser` - Zero-GC query parser
- `FilterEvaluator` - SPARQL FILTER expression evaluation
- `RdfParser` - N-Triples parsing utilities

## Code Conventions

- All parsing methods follow W3C EBNF grammar productions (comments reference production numbers)
- Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for hot paths
- Prefer `ReadOnlySpan<char>` over `string` for parsing operations
- Use `unsafe fixed` buffers for small inline storage when needed
- Temporal semantics are implicit - all triples have valid-time bounds

## Design Philosophy

Sky Omega values:
- **Simplicity over flexibility** - fewer moving parts, less to break
- **Append-only where possible** - naturally crash-safe, simpler recovery
- **Zero external dependencies** - BCL only, no surprises
- **Zero-GC on hot paths** - predictable latency for cognitive operations
