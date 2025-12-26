# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build entire solution
dotnet build SkyOmega.sln

# Build specific project
dotnet build src/TurtleParser/TurtleParser.csproj
dotnet build src/SparqlEngine/SparqlEngine.csproj

# Release build (enables optimizations)
dotnet build -c Release

# Run a project
dotnet run --project src/TurtleParser/TurtleParser.csproj
dotnet run --project src/SparqlEngine/SparqlEngine.csproj
```

## Project Overview

Sky Omega is a semantic-aware cognitive assistant with zero-GC performance design. The codebase targets .NET 10 with C# 14 and has **no external dependencies** (BCL only).

### Solution Structure

- **TurtleParser** (`src/TurtleParser/`) - Zero-allocation streaming RDF Turtle parser implementing W3C RDF 1.2 EBNF grammar
- **SparqlEngine** (`src/SparqlEngine/`) - Zero-GC SPARQL 1.1 query engine with streaming execution

## Architecture

### Zero-GC Design Principles

Both parsers use aggressive zero-allocation techniques:
- `ref struct` parsers that live entirely on the stack
- `ArrayPool<T>` for all buffer allocations
- `ReadOnlySpan<char>` for string operations
- String interning pools to avoid duplicate allocations
- Streaming enumerators that yield results without materializing collections

### TurtleParser

`TurtleStreamParser` is a `partial class` split across files:
- `TurtleStreamParser.cs` - Main parser logic and `ParseAsync()` entry point
- `TurtleStreamParser.Buffer.cs` - Buffer management
- `TurtleStreamParser.Structures.cs` - RDF structure parsing (blank nodes, collections)
- `TurtleStreamParser.Terminals.cs` - Terminal parsing (IRIs, literals, prefixed names)

API: `IAsyncEnumerable<RdfTriple>` streaming interface.

### SparqlEngine

`SparqlParser` is a `ref struct` that parses SPARQL queries from `ReadOnlySpan<char>`.

Key components:
- `StreamingTripleStore` - In-memory triple store with SPO/POS/OSP indexes
- `BPlusTreeStore` - Memory-mapped B+Tree for TB-scale persistence
- `MultiIndexStore` - Multi-index wrapper for file-based storage
- `AtomStore` - String deduplication with memory-mapped storage
- `QueryExecutor` - Streaming query execution
- `JoinAlgorithms` - Hash joins, sort-merge joins, nested loop joins
- `PropertyPaths` - SPARQL 1.1 property path evaluation
- `TemporalTripleStore` / `MultiTemporalStore` - Bitemporal RDF support

### Component Layers

```
Sky (Agent Layer) → Lucy (Semantic Memory) → Mercury (Storage Substrate)
```

- **Lucy** - RDF triple store with SPARQL queries
- **Mercury** - B+Tree indexes, append-only stores, memory-mapped files

## Code Conventions

- All parsing methods follow W3C EBNF grammar productions (comments reference production numbers)
- Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for hot paths
- Prefer `ReadOnlySpan<char>` over `string` for parsing operations
- Use `unsafe fixed` buffers for small inline storage when needed
