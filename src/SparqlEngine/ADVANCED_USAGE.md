# Advanced Usage Examples

This document demonstrates advanced features of the SPARQL Zero-GC Streaming Query Engine.

## Table of Contents

1. [Custom BGP Evaluation](#custom-bgp-evaluation)
2. [FILTER Expressions](#filter-expressions)
3. [Join Optimization](#join-optimization)
4. [Large Dataset Streaming](#large-dataset-streaming)
5. [Query Statistics](#query-statistics)
6. [Memory Profiling](#memory-profiling)

## Custom BGP Evaluation

### Example 1: Multi-Pattern BGP

```csharp
using var store = new StreamingTripleStore();

// Add triples
store.Add("<http://ex.org/alice>", "<http://ex.org/knows>", "<http://ex.org/bob>");
store.Add("<http://ex.org/bob>", "<http://ex.org/age>", "\"30\"");
store.Add("<http://ex.org/alice>", "<http://ex.org/age>", "\"28\"");

// Create BGP with multiple patterns
Span<TriplePattern> patterns = stackalloc TriplePattern[2];

patterns[0] = new TriplePattern
{
    Subject = new TermPattern { IsVariable = true, VariableId = 0 },  // ?person
    Predicate = new TermPattern { IsVariable = false, ConstantId = 1 }, // knows
    Object = new TermPattern { IsVariable = true, VariableId = 1 }   // ?friend
};

patterns[1] = new TriplePattern
{
    Subject = new TermPattern { IsVariable = true, VariableId = 1 },  // ?friend
    Predicate = new TermPattern { IsVariable = false, ConstantId = 2 }, // age
    Object = new TermPattern { IsVariable = true, VariableId = 2 }   // ?age
};

var matcher = new BgpMatcher(store, patterns);
matcher.AddPattern(patterns[0]);
matcher.AddPattern(patterns[1]);

var results = matcher.Execute();

while (results.MoveNext())
{
    var triple = results.Current;
    Console.WriteLine($"Friend: {triple.Subject}, Age: {triple.Object}");
}
```

## FILTER Expressions

### Example 2: Numeric Filters

```csharp
using var store = new StreamingTripleStore();

// Add data
for (int i = 0; i < 100; i++)
{
    store.Add(
        $"<http://ex.org/person{i}>",
        "<http://ex.org/age>",
        $"\"{i}\""
    );
}

// Query with filter: age > 25
var query = store.Query(
    ReadOnlySpan<char>.Empty,
    "<http://ex.org/age>",
    ReadOnlySpan<char>.Empty
);

Span<Binding> bindingStorage = stackalloc Binding[16];
var bindings = new BindingTable(bindingStorage);

while (query.MoveNext())
{
    var triple = query.Current;
    
    // Extract age value
    var ageStr = triple.Object;
    if (int.TryParse(ageStr.Trim('"'), out var age))
    {
        // Apply filter
        var filter = $"{age} > 25";
        var evaluator = new FilterEvaluator(filter.AsSpan());
        
        if (evaluator.Evaluate(bindings))
        {
            Console.WriteLine($"Person: {triple.Subject}, Age: {age}");
        }
    }
}
```

### Example 3: String Filters with Functions

```csharp
// FILTER with string functions
var filter = "isIRI(?x)";
var evaluator = new FilterEvaluator(filter.AsSpan());

Span<Binding> bindingStorage = stackalloc Binding[16];
var bindings = new BindingTable(bindingStorage);

// Bind variable ?x to a URI
bindings.Add(new Binding
{
    VariableId = 0,
    ValueId = 1,
    Type = BindingType.Uri
});

if (evaluator.Evaluate(bindings))
{
    Console.WriteLine("?x is bound to a URI");
}
```

## Join Optimization

### Example 4: Hash Join for Large Datasets

```csharp
using var store = new StreamingTripleStore();

// Populate with large dataset
for (int i = 0; i < 10_000; i++)
{
    store.Add($"<http://ex.org/s{i}>", "<http://ex.org/type>", "<http://ex.org/Person>");
    store.Add($"<http://ex.org/s{i}>", "<http://ex.org/name>", $"\"Person{i}\"");
}

// Query both patterns
var leftEnum = store.Query(
    ReadOnlySpan<char>.Empty,
    "<http://ex.org/type>",
    "<http://ex.org/Person>"
);

var rightEnum = store.Query(
    ReadOnlySpan<char>.Empty,
    "<http://ex.org/name>",
    ReadOnlySpan<char>.Empty
);

// Perform hash join on subject
var joinCondition = new JoinCondition
{
    LeftPosition = JoinPosition.Subject,
    RightPosition = JoinPosition.Subject
};

var joinResults = JoinAlgorithms.HashJoin(leftEnum, rightEnum, joinCondition);

int count = 0;
while (joinResults.MoveNext())
{
    var result = joinResults.Current;
    count++;
}

Console.WriteLine($"Hash join produced {count} results");
```

### Example 5: Automatic Join Optimization

```csharp
using var store = new StreamingTripleStore();
var optimizer = new QueryOptimizer(store);

// Define multiple patterns
Span<TriplePattern> patterns = stackalloc TriplePattern[3];

patterns[0] = new TriplePattern
{
    Subject = new TermPattern { IsVariable = true },
    Predicate = new TermPattern { IsVariable = true },
    Object = new TermPattern { IsVariable = true }
};

patterns[1] = new TriplePattern
{
    Subject = new TermPattern { IsVariable = true },
    Predicate = new TermPattern { IsVariable = false },
    Object = new TermPattern { IsVariable = true }
};

patterns[2] = new TriplePattern
{
    Subject = new TermPattern { IsVariable = false },
    Predicate = new TermPattern { IsVariable = false },
    Object = new TermPattern { IsVariable = true }
};

// Reorder patterns for optimal execution
optimizer.ReorderPatterns(patterns);

// Estimate cardinalities
for (int i = 0; i < patterns.Length; i++)
{
    var card = optimizer.EstimateCardinality(patterns[i]);
    Console.WriteLine($"Pattern {i} estimated cardinality: {card}");
}

// Select join algorithm
var leftCard = optimizer.EstimateCardinality(patterns[0]);
var rightCard = optimizer.EstimateCardinality(patterns[1]);
var joinType = optimizer.SelectJoinAlgorithm(leftCard, rightCard);

Console.WriteLine($"Selected join algorithm: {joinType}");
```

## Large Dataset Streaming

### Example 6: Streaming Large Files

```csharp
using var store = new StreamingTripleStore();
using var loader = new StreamingRdfLoader();

// Load large N-Triples file in streaming fashion
// File is processed in 64KB chunks without loading entire file into memory
loader.LoadNTriples("/path/to/large.nt", store);

Console.WriteLine("Large file loaded with zero-GC streaming");

// Query the loaded data
var results = store.Query(
    ReadOnlySpan<char>.Empty,
    "<http://xmlns.com/foaf/0.1/knows>",
    ReadOnlySpan<char>.Empty
);

int relationshipCount = 0;
while (results.MoveNext())
{
    relationshipCount++;
    
    // Process relationships without allocating
    if (relationshipCount % 10000 == 0)
    {
        Console.WriteLine($"Processed {relationshipCount} relationships...");
    }
}

Console.WriteLine($"Total relationships: {relationshipCount}");
```

### Example 7: Paginated Results

```csharp
using var store = new StreamingTripleStore();

// Add large dataset
for (int i = 0; i < 100_000; i++)
{
    store.Add($"<http://ex.org/s{i}>", "<http://ex.org/p>", $"<http://ex.org/o{i}>");
}

// Implement pagination manually
const int pageSize = 1000;
int currentPage = 0;
int pageCount = 0;

var results = store.Query(
    ReadOnlySpan<char>.Empty,
    "<http://ex.org/p>",
    ReadOnlySpan<char>.Empty
);

int totalCount = 0;
int inPageCount = 0;

Console.WriteLine($"=== Page {currentPage} ===");

while (results.MoveNext())
{
    totalCount++;
    inPageCount++;
    
    var triple = results.Current;
    Console.WriteLine($"{triple.Subject} -> {triple.Object}");
    
    if (inPageCount >= pageSize)
    {
        currentPage++;
        pageCount++;
        inPageCount = 0;
        
        Console.WriteLine($"
=== Page {currentPage} ===");
        
        if (pageCount >= 5) // Show only first 5 pages
            break;
    }
}

Console.WriteLine($"
Total results: {totalCount}");
```

## Query Statistics

### Example 8: Collecting Query Statistics

```csharp
using var store = new StreamingTripleStore();
var stats = new Statistics();

// Populate store and collect statistics
for (int i = 0; i < 10_000; i++)
{
    var subject = $"<http://ex.org/person{i}>";
    var predicate = $"<http://ex.org/prop{i % 10}>";
    var obj = $"<http://ex.org/value{i % 100}>";
    
    store.Add(subject, predicate, obj);
    stats.UpdateStatistics(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan());
}

// Estimate selectivity for queries
var selectivity = stats.EstimateSelectivity(
    "<http://ex.org/prop0>",
    TriplePosition.Predicate
);

Console.WriteLine($"Selectivity for prop0: {selectivity:P2}");

// Use statistics for query planning
var estimatedResults = (int)(10_000 * selectivity);
Console.WriteLine($"Estimated results: {estimatedResults}");
```

## Memory Profiling

### Example 9: Zero-GC Verification

```csharp
using var store = new StreamingTripleStore();

// Pre-populate
for (int i = 0; i < 1000; i++)
{
    store.Add($"<http://ex.org/s{i}>", "<http://ex.org/p>", $"<http://ex.org/o{i}>");
}

// Force GC to start clean
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);

var gen0Before = GC.CollectionCount(0);
var gen1Before = GC.CollectionCount(1);
var gen2Before = GC.CollectionCount(2);
var memoryBefore = GC.GetTotalMemory(false);

// Perform operations
for (int iteration = 0; iteration < 10_000; iteration++)
{
    var results = store.Query(
        ReadOnlySpan<char>.Empty,
        "<http://ex.org/p>",
        ReadOnlySpan<char>.Empty
    );
    
    while (results.MoveNext())
    {
        var triple = results.Current;
        
        // Access all fields to ensure they're not optimized away
        _ = triple.Subject.Length;
        _ = triple.Predicate.Length;
        _ = triple.Object.Length;
    }
}

var gen0After = GC.CollectionCount(0);
var gen1After = GC.CollectionCount(1);
var gen2After = GC.CollectionCount(2);
var memoryAfter = GC.GetTotalMemory(false);

Console.WriteLine("=== Zero-GC Verification ===");
Console.WriteLine($"Gen0 Collections: {gen0After - gen0Before}");
Console.WriteLine($"Gen1 Collections: {gen1After - gen1Before}");
Console.WriteLine($"Gen2 Collections: {gen2After - gen2Before}");
Console.WriteLine($"Memory Delta: {memoryAfter - memoryBefore:N0} bytes");

if (gen0After == gen0Before && gen1After == gen1Before && gen2After == gen2Before)
{
    Console.WriteLine("✓ Zero-GC operation verified!");
}
else
{
    Console.WriteLine("✗ GC allocations detected");
}
```

### Example 10: ArrayPool Usage Monitoring

```csharp
// The engine uses ArrayPool internally, which can be monitored

using var store = new StreamingTripleStore();

// Insert data to trigger pool rentals
Console.WriteLine("Inserting data...");
for (int i = 0; i < 100_000; i++)
{
    store.Add($"<http://ex.org/s{i}>", "<http://ex.org/p>", $"<http://ex.org/o>");
}

// Pool automatically handles growth without GC pressure
Console.WriteLine($"✓ Inserted 100,000 triples using pooled arrays");

// When store is disposed, arrays are returned to pool
store.Dispose();
Console.WriteLine("✓ Arrays returned to pool");
```

## Best Practices

### 1. Reuse Query Objects

```csharp
// BAD: Parse query repeatedly
for (int i = 0; i < 1000; i++)
{
    var parser = new SparqlParser("SELECT * WHERE { ?s ?p ?o }".AsSpan());
    var query = parser.ParseQuery();
    // execute query...
}

// GOOD: Parse once, reuse many times
var parser = new SparqlParser("SELECT * WHERE { ?s ?p ?o }".AsSpan());
var query = parser.ParseQuery();

for (int i = 0; i < 1000; i++)
{
    // execute same query...
}
```

### 2. Use Stack-Allocated Buffers

```csharp
// Use stackalloc for small, short-lived arrays
Span<Binding> bindings = stackalloc Binding[16];
Span<TriplePattern> patterns = stackalloc TriplePattern[8];
```

### 3. Dispose Resources

```csharp
// Always dispose stores to return pooled arrays
using var store = new StreamingTripleStore();
// ... use store ...
// Automatic disposal at end of scope
```

### 4. Warm Up Pools Before Benchmarking

```csharp
using var store = new StreamingTripleStore();

// Pre-populate to warm up pools
for (int i = 0; i < 1000; i++)
{
    store.Add($"<s{i}>", "<p>", $"<o{i}>");
}

// Force GC before benchmarking
GC.Collect(2, GCCollectionMode.Forced, true, true);

// NOW begin accurate benchmark
var sw = Stopwatch.StartNew();
// ... benchmark code ...
```

## Performance Tips

1. **Pattern Order**: Most selective patterns first
2. **Index Usage**: Query constant predicates when possible
3. **Batch Operations**: Insert multiple triples before querying
4. **String Interning**: Automatic, but benefits from repeated values
5. **Join Selection**: Hash joins for small-medium sizes, nested loop for small

## Conclusion

This engine demonstrates that high-performance SPARQL query execution is possible with zero GC pressure using modern .NET features like `Span<T>`, `ref struct`, and `ArrayPool<T>`.
