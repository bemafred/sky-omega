# Mercury

*Powered by Mercury.*

——

## What Mercury Is

Mercury is an embeddable triple store engine—zero-GC, bitemporal, key/value efficient at its core, with full RDF conformance available when needed.

It’s a library, not a product. Mercury provides the storage foundation; you decide what to build on top.

## What Mercury Isn’t

Mercury is not a stand-alone SPARQL server. It has no network protocol, no authentication layer, no deployment opinions. If you need those things, you build them—or use something that already has.

It’s open source (MIT). Build what you need.

——

## Core Identity

Mercury is a **mechanical** triple store.

Three values. Multiple indices. That’s it.

The term “RDF” carries decades of semantic web baggage—ontologies, OWL reasoning, linked data idealism, XML serialization complexity. People immediately jump to the *conceptual framework* and miss the *mechanical simplicity*.

Mercury inverts that relationship:

|Conventional framing          |Accurate framing                                 |
|——————————|-————————————————|
|“An RDF store”                |“A triple store with optional RDF expressiveness”|
|Implies semantic web stack    |Implies data structure efficiency                |
|Suggests ontology requirements|Suggests key/value simplicity                    |
|W3C compliance burden         |W3C conformance when useful                      |

The indices (SPO, POS, OSP, etc.) give you O(1) access from any entry point—subject, predicate, or object. That’s key/value retrieval. Store file paths as objects, store JSON blobs, store whatever—retrieval is index lookup.

The semantic expressiveness is there when useful. But it’s not load-bearing for the core efficiency claim.

——

## Performance Model

For single-pattern queries—“give me the object where subject=X and predicate=Y”—you’re hitting an index directly. That’s key/value performance.

Mercury can serve many simultaneous users at key/value efficiency for key/value-shaped queries, with graph query capability available when needed. The performance ceiling is hardware and memory, not architectural overhead.

**Where Mercury excels:**

- Pattern matching over triple indices (highly parallelizable)
- Concurrent reads (immutable/append-style temporal model)
- Predictable latency under load (no GC pauses)
- Compositional queries when you *need* joins

**The overhead only appears when:**

- SPARQL queries with multiple join patterns
- Complex graph traversals
- Full-text search across literals

Even then, the zero-GC design means no stop-the-world pauses disrupting execution.

——

## Architectural Position

Mercury is infrastructure for *others to build on*, not a product that competes with Blazegraph, Stardog, or GraphDB. Those are stand-alone servers with operational concerns baked in. Mercury is what you might put *inside* such a thing—or inside something entirely different.

**Mercury provides:**

- Embeddable engine
- Managed pool of QuadStores
- Flexible assembly
- Pruning at consumer’s discretion
- 100% RDF conformance available

**Mercury does not provide:**

- Network protocol
- Authentication
- Query endpoint configuration
- Deployment topology
- Operational opinions

Want a stand-alone SPARQL server? Build it. It’s open source.  
Want a document store with triple-based indexing? Build it.  
Want a cognitive architecture with semantic memory? That’s [Sky Omega](../../README.md).

——

## Use Cases

- Embed in a cognitive architecture (Sky Omega)
- Build a SPARQL endpoint
- Use as a versioned document store with triple-based indexing
- Power a knowledge graph application
- Build a temporal audit system
- Create a provenance tracking system
- Something we haven’t imagined

——

## Technical Specifications

|Aspect      |Detail                         |
|————|-——————————|
|Core engine |~46K lines                     |
|Language    |C# 14                          |
|Platform    |.NET 10                        |
|Dependencies|BCL-only (no external packages)|
|Storage     |mmap’d B+Trees                 |
|Scale       |TB-capable                     |
|GC profile  |Zero-allocation hot paths      |

### Data Structures

- **`Triple`** — Compact storage using 64-bit interned IDs. Primary structure for memory-mapped storage.
- **`TripleRef`** — Zero-allocation `ref struct` for transient parsing. Points directly into input buffers.
- **`RdfTriple`** — Immutable `record struct` for API boundaries.
- **`RdfLiteral`** — Formal RDF literal support (plain strings, language tags, datatypes).

### Storage Layer

- **QuadStore** — Core storage unit with multiple indices
- **QuadStorePool** — Managed pool for concurrent access
- **AtomStore** — String interning for 64-bit ID efficiency
- **WriteAheadLog** — Durability guarantee
- **PageCache** — Buffer management for mmap’d access

### Format Support

|Format   |Parser|Writer|
|———|——|——|
|Turtle   |✓     |✓     |
|N-Triples|✓     |✓     |
|N-Quads  |✓     |✓     |
|TriG     |✓     |✓     |
|RDF/XML  |✓     |✓     |
|JSON-LD  |✓     |✓     |

### SPARQL

Full SPARQL 1.1 query support with streaming execution model.

——

## Getting Started

```csharp
// Create a store
using var store = new QuadStore(options);

// Assert triples
store.Assert(subject, predicate, obj);

// Query via pattern
foreach (var triple in store.Match(subject, null, null))
{
    // Process matches
}

// Or use SPARQL
var results = store.Query(“SELECT ?s ?p ?o WHERE { ?s ?p ?o }”);
```

For pooled access:

```csharp
using var pool = new QuadStorePool(poolOptions);

// Acquire store from pool
using var lease = pool.Acquire(“my-store”);
var store = lease.Store;

// Use store...
// Returns to pool on dispose
```

——

## Documentation

- [Canonical Definition](../../docs/architecture/concepts/canonical/mercury.md) — Identity and scope
- [Architecture Decision Records](../../docs/adrs/mercury/README.md) — Design decisions
- [Temporal RDF](../../docs/architecture/technical/temporal-rdf.md) — Bitemporal model
- [RDF Core Types](Rdf/README.md) — Data structure documentation

——

## License

MIT

——

*Powered by Mercury.*