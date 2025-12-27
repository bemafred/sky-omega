# Sky Omega Mercury: RDF Core Data Model

## Overview

This directory contains the foundational data structures for the **Mercury** RDF engine. These structures are designed for high-performance, zero-allocation processing of RDF data in C# 14 and .NET 10.

The core philosophy of this module is to provide representations of RDF terms and triples that can be used across parsers (Turtle, N-Triples) and storage layers (Temporal Store, Lucy) without triggering Garbage Collection.

## Core Data Structures

### 1. `TripleRef` (Zero-Allocation)
A `readonly ref struct` designed for transient use during parsing. It utilizes `ReadOnlySpan<char>` to point directly into input buffers, ensuring no strings are allocated while navigating a stream.

### 2. `Triple` (Compact Storage)
A memory-efficient representation using 64-bit interned IDs (`SubjectId`, `PredicateId`, `ObjectId`). This is the primary structure used for high-density storage in memory-mapped files and B+Trees, enabling TB-scale capability.

### 3. `RdfTriple` (General Purpose)
An immutable `record struct` that uses standard strings. This is used for API boundaries and general-purpose consumption where the extreme performance of spans is not required.

### 4. `RdfLiteral` (Structured Terms)
Provides formal support for RDF Literals, including:
- Plain strings
- Language tags (e.g., `@en`, `@en-ltr`)
- Datatypes (e.g., `^^xsd:dateTime`)

## Design Principles

- **Zero-GC Path**: High-frequency operations use `ref struct` and `Span<T>` to keep the heap clean.
- **TB-Scale Ready**: Atom interning via 64-bit identifiers allows for datasets exceeding billions of triples.
- **W3C RDF 1.2 Support**: Designed to handle the latest RDF 1.2 specifications, including triple terms (quoted triples) and annotations.

## Usage

### Converting to N-Triples
The `RdfTriple` struct provides a canonical serialization method:
```csharp
var triple = new RdfTriple("http://ex.org/s", "http://ex.org/p", "literal");
Console.WriteLine(triple.ToNTriples()); // <http://ex.org/s> <http://ex.org/p> "literal" .
```

### High-Performance Parsing
Parsers yield objects to streaming consumers to maintain zero-allocation throughput. 
TripleRef` is used to navigate the input stream.

## Integration
This module is the "bridge" between:
1. **Parsers**: , `SkyOmega.Mercury.Rdf.Turtle``SkyOmega.Mercury.Sparql`
2. **Storage**: , `SkyOmega.Mercury.Sparql.Temporal``SkyOmega.Mercury.Sparql.Storage`