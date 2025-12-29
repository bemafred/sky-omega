# Temporal RDF for Sky Omega

## Overview

Sky Omega's temporal RDF substrate implements a **bitemporal data model** supporting both valid-time and transaction-time semantics. This enables time-travel queries, version tracking, historical reconstruction, and audit trails.

## Bitemporal Model

### Two Time Dimensions

**Valid Time (VT)**: When a fact is true in the real world
- "Alice worked at Anthropic from 2020-01-01 to 2023-06-30"
- Represents reality, independent of database

**Transaction Time (TT)**: When a fact was recorded in the database
- "This fact was recorded on 2023-07-01 at 14:30:00"
- Database's knowledge timeline
- Immutable audit trail

### Why Bitemporal?

```
Scenario: Employment fact correction

Original record (TT: 2023-01-15):
  Alice worksFor Acme
  VT: 2020-01-01 to 2023-01-01
  
Correction (TT: 2023-06-20):
  Alice worksFor Acme  
  VT: 2020-01-01 to 2022-12-31  // Actually left earlier

Bitemporal allows:
  • Query: "What did we KNOW on 2023-03-01?" → First version
  • Query: "What is TRUE on 2022-06-01?" → Works at Acme (both versions agree)
  • Query: "When did our knowledge change?" → 2023-06-20
```

## Temporal Key Structure

```
TemporalKey (56 bytes):
┌────────┬──────────┬────────────┬──────────┬──────────┬─────────┬───────────────┐
│Graph(8)│Subject(8)│Predicate(8)│Object(8) │ValidFrom │ValidTo  │TransactionTime│
│        │          │            │          │  (8)     │  (8)    │     (8)       │
└────────┴──────────┴────────────┴──────────┴──────────┴─────────┴───────────────┘

Sorted lexicographically (GSPO ordering):
  1. Graph (0 = default graph, enables named graph isolation)
  2. Subject, Predicate, Object (spatial)
  3. ValidFrom, ValidTo (temporal)
  4. TransactionTime (versioning)
```

### Named Graphs (Quads)

Each triple can belong to a named graph or the default graph:

```csharp
// Add to named graph
store.AddCurrent(subject, predicate, obj, "<http://example.org/graph1>");

// Add to default graph (no graph parameter)
store.AddCurrent(subject, predicate, obj);

// Query specific named graph
var results = store.QueryCurrent(subject, predicate, obj, "<http://example.org/graph1>");

// Enumerate all named graphs
foreach (var graphIri in store.GetNamedGraphs())
{
    // Process each distinct named graph
}
```

**Graph isolation**: Default graph (atom 0) and named graphs are fully isolated. Querying without a graph parameter queries only the default graph.

## Persistent Storage Architecture

**Memory-Mapped B+Trees**
- Single B+Tree index with GSPO ordering
- Pages: 16KB (optimal for SSDs)
- Entries per page: 185 temporal quads (with graph atom)
- Zero-copy access via memory mapping

**File Organization**
```
mydb/
├── gspo.tdb           # Graph-Subject-Predicate-Object-Time index
├── atoms.atoms        # Atom data (UTF-8 strings)
├── atoms.atomidx      # Hash table for interning
├── atoms.offsets      # AtomId→offset index
└── wal.log            # Write-ahead log for crash recovery
```

**Note**: Atoms are stored once and referenced by 64-bit IDs. Graph atom 0 represents the default graph.

**Persistence Guarantees**
- All temporal data persisted to disk
- Automatic recovery on restart
- No data loss on process termination
- ACID semantics (simplified)

## GSPO Index

The single B+Tree index uses **GSPO ordering** (Graph-Subject-Predicate-Object-Time):

```
Order: G → S → P → O → ValidFrom → ValidTo → TransactionTime
```

### Query Patterns

| Pattern | Efficiency | Example |
|---------|------------|---------|
| Graph + Subject bound | Optimal | "All facts about Alice in graph G" |
| Graph bound | Good | "All facts in named graph G" |
| Subject bound (default graph) | Optimal | "All facts about Alice" |
| No bounds | Full scan | "All facts everywhere" |

### Named Graph Queries

```csharp
// Query specific named graph
var results = store.QueryCurrent(
    "<http://ex.org/alice>",
    ReadOnlySpan<char>.Empty,
    ReadOnlySpan<char>.Empty,
    "<http://example.org/graph1>"  // Named graph
);

// Query default graph (omit graph parameter)
var results = store.QueryCurrent(
    "<http://ex.org/alice>",
    ReadOnlySpan<char>.Empty,
    ReadOnlySpan<char>.Empty
);
```

## Query Types

### 1. Point-in-Time (As-Of Query)

**Query**: "What was true at time T?"

```csharp
var results = store.QueryAsOf(
    "<http://ex.org/alice>",
    "<http://ex.org/worksFor>",
    ReadOnlySpan<char>.Empty,
    new DateTimeOffset(2021, 6, 1, 0, 0, 0, TimeSpan.Zero)
);
```

**Semantics**: Return triples where `ValidFrom <= T < ValidTo`

### 2. Temporal Range Query

**Query**: "What changed during period [T1, T2]?"

```csharp
var results = store.QueryChanges(
    periodStart: new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
    periodEnd: new DateTimeOffset(2023, 12, 31, 0, 0, 0, TimeSpan.Zero),
    subject: "<http://ex.org/alice>",
    predicate: ReadOnlySpan<char>.Empty,
    obj: ReadOnlySpan<char>.Empty
);
```

**Semantics**: Return triples where `ValidFrom < T2 AND ValidTo > T1`

### 3. Evolution Query

**Query**: "Show all versions ever"

```csharp
var results = store.QueryEvolution(
    "<http://ex.org/alice>",
    "<http://ex.org/salary>",
    ReadOnlySpan<char>.Empty
);
```

**Semantics**: Return all versions, ordered by ValidFrom

### 4. Current State Query

**Query**: "What is true now?"

```csharp
var results = store.QueryCurrent(
    "<http://ex.org/alice>",
    ReadOnlySpan<char>.Empty,
    ReadOnlySpan<char>.Empty
);
```

**Semantics**: Point-in-time query at `DateTimeOffset.UtcNow`

## Usage Patterns

### Pattern 1: Version Tracking

```csharp
// Track salary over time
store.Add("<http://ex.org/employee1>", "<http://ex.org/salary>", "\"80000\"",
    new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
    new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

store.Add("<http://ex.org/employee1>", "<http://ex.org/salary>", "\"90000\"",
    new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero),
    new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero));

// Query: What was salary in June 2021?
var results = store.QueryAsOf(
    "<http://ex.org/employee1>",
    "<http://ex.org/salary>",
    ReadOnlySpan<char>.Empty,
    new DateTimeOffset(2021, 6, 1, 0, 0, 0, TimeSpan.Zero)
);
// Returns: "90000"
```

### Pattern 2: Audit Trail

```csharp
// Every insert has transaction time
store.Add(subject, predicate, obj, validFrom, validTo);
// Transaction time automatically recorded

// Later: Who changed what and when?
var evolution = store.QueryEvolution(subject, predicate, obj);
while (evolution.MoveNext())
{
    var triple = evolution.Current;
    Console.WriteLine($"Changed to {triple.Object}");
    Console.WriteLine($"  Valid: {triple.ValidFrom} to {triple.ValidTo}");
    Console.WriteLine($"  Recorded: {triple.TransactionTime}");
}
```

### Pattern 3: Historical Snapshot

```csharp
// Reconstruct complete state at specific time
var snapshotTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

var snapshot = store.TimeTravelTo(
    snapshotTime,
    "<http://ex.org/organization>",
    ReadOnlySpan<char>.Empty,  // All predicates
    ReadOnlySpan<char>.Empty   // All objects
);

// Build complete picture of organization as of 2020-01-01
while (snapshot.MoveNext())
{
    var triple = snapshot.Current;
    // Process all facts valid at that time
}
```

### Pattern 4: Temporal Joins

```csharp
// Find: Who worked where at same time?
var employees = store.QueryAsOf(
    ReadOnlySpan<char>.Empty,
    "<http://ex.org/worksFor>",
    "<http://ex.org/Anthropic>",
    targetTime
);

while (employees.MoveNext())
{
    var emp = employees.Current;
    // emp.Subject = employee who worked at Anthropic at targetTime
    // emp.ValidFrom/ValidTo = their employment period
}
```

### Pattern 5: Temporal Aggregation

```csharp
// Count employees over time
var timeSeries = new Dictionary<DateTimeOffset, int>();

// Sample every month for 5 years
for (var date = startDate; date < endDate; date = date.AddMonths(1))
{
    var results = store.QueryAsOf(
        ReadOnlySpan<char>.Empty,
        "<http://ex.org/worksFor>",
        "<http://ex.org/company>",
        date
    );
    
    int count = 0;
    while (results.MoveNext()) count++;
    
    timeSeries[date] = count;
}

// Now have employee count time series
```

## Performance Characteristics

### Storage

```
Temporal quad:     88 bytes
  Key:             56 bytes (GSPO + VT + TT)
  Child/Value:      8 bytes
  Metadata:        24 bytes (flags, padding)

Page capacity:     185 entries per 16KB page
Tree height (1B):  ~5 levels
Lookup latency:    ~5 disk seeks (or ~0 with cache)
```

### Query Performance

```
Point-in-time (cached):    ~100K queries/sec
Point-in-time (cold):      ~5K queries/sec
Range scan:                ~200K triples/sec
Evolution scan:            ~500K triples/sec
```

### Index Selection Impact

```
Query Pattern                    Performance
──────────────────────────────────────────────
Graph + Subject + Time range     Optimal (GSPO prefix)
Graph + Time range               Good (G prefix)
Subject + Time range             Optimal (default graph)
No bounds                        Full scan
```

## Comparison with Non-Temporal

```
Feature                Non-Temporal    Temporal + Graphs
───────────────────────────────────────────────────────
Quad size              32 bytes        56 bytes (key)
Entry size             ~40 bytes       88 bytes (with metadata)
Index                  1 (GSPO)        1 (GSPO + time)
Named graphs           Yes             Yes
History                No              Full
Corrections            Destructive     Non-destructive
Audit trail            No              Yes
Time-travel            No              Yes
Version tracking       No              Yes
Storage overhead       1x              ~2.2x
Query complexity       Lower           Higher
Use cases              Static data     Evolving data
```

## Best Practices

### 1. Choose Valid-Time Granularity

```csharp
// Too granular (milliseconds): Excessive versions
store.Add(subject, predicate, obj,
    DateTimeOffset.UtcNow,
    DateTimeOffset.MaxValue);

// Good (days): Reasonable versioning
store.Add(subject, predicate, obj,
    DateTimeOffset.UtcNow.Date,
    DateTimeOffset.MaxValue);
```

### 2. Use Appropriate Time Bounds

```csharp
// Current fact: valid from now to infinity
store.AddCurrent(subject, predicate, obj);
// Equivalent to:
store.Add(subject, predicate, obj, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

// Historical fact: specific period
store.AddHistorical(subject, predicate, obj, from, to);
```

### 3. Leverage Index Selection

```csharp
// BAD: No bounds (full scan)
var results = store.Query(
    ReadOnlySpan<char>.Empty,
    ReadOnlySpan<char>.Empty,
    ReadOnlySpan<char>.Empty,
    TemporalQueryType.AsOf
);

// GOOD: Bound at least one component
var results = store.Query(
    "<http://ex.org/alice>",  // Subject bound → SPOT index
    ReadOnlySpan<char>.Empty,
    ReadOnlySpan<char>.Empty,
    TemporalQueryType.AsOf
);
```

### 4. Batch Temporal Operations

```csharp
// Insert facts in chronological order for better page locality
var facts = GetFacts().OrderBy(f => f.ValidFrom);

foreach (var fact in facts)
{
    store.Add(fact.Subject, fact.Predicate, fact.Object,
        fact.ValidFrom, fact.ValidTo);
}
```

## SPARQL Extensions for Temporal Queries

### Proposed Syntax

```sparql
# Point-in-time query
SELECT ?person ?company
WHERE {
  ?person worksFor ?company
} 
AS OF "2021-06-01"^^xsd:date

# Range query
SELECT ?person ?company ?from ?to
WHERE {
  ?person worksFor ?company
}
DURING ["2023-01-01"^^xsd:date, "2023-12-31"^^xsd:date]

# Evolution query
SELECT ?person ?salary ?from ?to
WHERE {
  ?person hasSalary ?salary
}
ALL VERSIONS
ORDER BY ?from
```

## Sky Omega Integration

For Sky Omega's knowledge graph:

1. **Entity Evolution**: Track how entities change over time
2. **Relationship Dynamics**: Model temporal relationships
3. **Knowledge Provenance**: Record when facts were learned
4. **Temporal Reasoning**: Infer facts valid at specific times
5. **Historical Context**: Understand past states

## Future Enhancements

- [x] Named graph support (GSPO ordering)
- [x] SPARQL GRAPH clause queries
- [ ] Temporal property paths
- [ ] Temporal aggregations (COUNT at time T)
- [ ] Allen's interval algebra for temporal reasoning
- [ ] Coalescing of adjacent periods
- [ ] Incremental materialized views
- [ ] Temporal CONSTRUCT queries
- [ ] Periodic snapshots for fast historical queries

## References

- Snodgrass, R. T. (1999). *Developing Time-Oriented Database Applications in SQL*
- Jensen, C. S. (1996). *Temporal Database Management*
- Allen, J. F. (1983). *Maintaining Knowledge about Temporal Intervals*
