# Mercury

## Status

Canonical

## Canonical Definition

**Mercury** is an embeddable triple store engine—zero-GC, bitemporal, key/value efficient at its core, with full RDF conformance available when needed.

Mercury is a library, not a product. It provides the storage foundation; consumers decide what to build on top.

## Core Identity

Mercury is a **mechanical** triple store. Three values. Multiple indices. That’s it.

The indices (SPO, POS, OSP, etc.) give O(1) access from any entry point—subject, predicate, or object. The bitemporal extension adds two dimensions for versioning. The zero-GC design ensures predictable performance under concurrent load.

RDF compatibility is a **feature**, not the **foundation**. The semantic web baggage—ontologies, OWL reasoning, linked data idealism—is opt-in, not load-bearing.

## What Mercury Provides

- Embeddable engine (library)
- Managed pool of QuadStores
- Flexible assembly and pruning at consumer’s discretion
- Multiple index access paths built-in
- Bitemporal versioning intrinsic to storage
- 100% RDF conformance when needed
- Zero-GC concurrent access

## What Mercury Does Not Provide

- Network protocol
- Authentication layer
- Query endpoint configuration
- Deployment topology
- Operational opinions

If you need a stand-alone SPARQL server, you build the server part. Mercury is the engine inside.

## Use Cases

- Embed in a cognitive architecture (Sky Omega)
- Build a SPARQL endpoint
- Use as a versioned document store with triple-based indexing
- Power a knowledge graph application
- Something not yet imagined

## Technical Foundation

- ~46K lines core engine
- C# 14 / .NET 10
- BCL-only (no external dependencies)
- mmap’d B+Trees for TB-scale capability
- Zero-allocation hot paths via `ref struct` and `Span<T>`

## Non-Goals

- Being a stand-alone server product
- Competing with Blazegraph, Stardog, or GraphDB
- Requiring semantic web knowledge for basic usage
- Imposing operational opinions on consumers

## See Also

- [Lucy](lucy.md) - Long-term memory layer built on Mercury
- [Temporal RDF](../../technical/temporal-rdf.md) - Bitemporal model documentation
- [Mercury ADRs](../../../adrs/mercury/README.md) - Architecture decision records

——

*Powered by Mercury.*