# Building and Testing

## Prerequisites

- .NET 10 SDK (or .NET 8/9 - update TargetFramework in .csproj)
- C# 14 compiler support
- 64-bit system (for memory-mapped files)

## Quick Start

```bash
# Build
dotnet build -c Release

# Run (includes all tests and examples)
dotnet run -c Release

# Run specific example
# (Edit Program.cs to comment out others for faster iteration)
```

## Known Limitations

### 1. B+Tree Page Splitting
**Status**: ✅ Fixed

The B+Tree now properly handles:
- Leaf page splits with key promotion to parent
- Internal node splits with recursive propagation
- Root splitting (creating new root when root is full)
- Correct child pointer maintenance via `NextLeaf` (repurposed for internal nodes)

Implementation:
- `SplitResult` struct propagates split info up the tree
- `InsertRecursive` handles bottom-up split propagation
- `CreateNewRoot` handles root splits
- `InsertIntoInternal` / `SplitInternalPage` handle internal nodes
- Thread-safe page allocation via `Interlocked.Increment`

### 2. Thread Safety
**Status**: Not thread-safe

All stores are single-threaded only:
```csharp
_nextPageId++;  // Race condition!
_tripleCount++; // Race condition!
```

**Fix needed**: Add locks or use lock-free data structures:
```csharp
Interlocked.Increment(ref _nextPageId);
```

### 3. Memory-Mapped Handle Leaks
**Status**: Possible resource leaks

```csharp
byte* ptr = null;
_accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
// Never released!
```

**Fix needed**: Proper handle lifecycle management in page cache.

### 4. Temporal Query Edge Cases
**Status**: Basic implementation

Boundary conditions for temporal ranges may be incorrect:
```csharp
// Edge case: What if ValidFrom == RangeEnd?
ValidFrom < RangeEnd && ValidTo > RangeStart
```

**Fix needed**: Comprehensive temporal algebra tests.

### 5. AtomStore Offset Index
**Status**: ✅ Fixed

`GetAtomOffset()` now uses O(1) direct lookup via memory-mapped offset index file.

Blob storage limits:
- Storage format: 64-bit length prefix (unlimited blob size)
- Retrieval: Limited to 2GB per span (.NET Span<T> limit)
- For larger blobs: Chunked access required

### 6. No Write-Ahead Log
**Status**: Not crash-safe

Partial writes can corrupt database:
- No transaction log
- No crash recovery
- No ACID guarantees

**Fix needed**: Implement WAL like SQLite.

## Testing Strategy

### Unit Tests
```bash
# Currently in Tests.cs
dotnet test  # (if we add xUnit/NUnit)
```

### Integration Tests
Run examples and verify:
1. In-memory store works
2. File-based store persists
3. Temporal queries return correct results
4. Zero-GC verified (check GC.CollectionCount)

### Stress Tests
```csharp
// Test B+Tree splitting
for (int i = 0; i < 100_000; i++) {
    store.Add(...);  // Will this crash?
}

// Test concurrent access
Parallel.For(0, 100, i => {
    store.Add(...);  // Will this corrupt?
});
```

## Performance Tuning

### Page Cache Size
```csharp
// Default: 10,000 pages (~160MB)
var cache = new PageCache(capacity: 10_000);

// For large datasets:
var cache = new PageCache(capacity: 100_000);  // ~1.6GB
```

### File Growth Strategy
```csharp
// Current: Double on overflow
_fileStream.SetLength(_fileStream.Length * 2);

// Alternative: Fixed increments
_fileStream.SetLength(_fileStream.Length + (1L << 30));  // +1GB
```

### Hash Table Size
```csharp
// AtomStore hash table
const int HashTableSize = 1 << 20;  // 1M buckets

// For more unique strings:
const int HashTableSize = 1 << 24;  // 16M buckets
```

## Debugging Tips

### Enable GC Logging
```bash
DOTNET_GCName=1 dotnet run -c Release
```

### Memory-Mapped Files
```bash
# Linux: Check mapped regions
cat /proc/$(pidof SparqlEngine)/maps

# Windows: Use RAMMap or Process Explorer
```

### B+Tree Visualization
Add debug method:
```csharp
private void DumpTree(long pageId, int level) {
    var page = GetPage(pageId);
    Console.WriteLine($"{new string(' ', level)}Page {pageId}: {page->EntryCount} entries");
    if (!page->IsLeaf) {
        for (int i = 0; i < page->EntryCount; i++) {
            DumpTree(page->GetEntry(i).ChildOrValue, level + 1);
        }
    }
}
```

## Production Readiness Checklist

- [x] Fix B+Tree page splitting recursion
- [ ] Add proper locking or lock-free atomics
- [ ] Implement WAL for crash recovery
- [ ] Add comprehensive error handling
- [ ] Memory-mapped handle lifecycle
- [ ] Temporal query edge case tests
- [x] AtomStore offset index (O(1) lookup)
- [x] 64-bit blob size support
- [ ] Compaction/defragmentation
- [ ] Backup/restore utilities
- [ ] Monitoring and metrics
- [ ] Documentation for all public APIs
- [ ] Benchmark suite
- [ ] Fuzz testing

## What's Production-Ready Now

✅ Memory-mapped I/O architecture
✅ Zero-GC allocation patterns
✅ Atom storage and deduplication (O(1) lookup)
✅ Complete B+Tree operations (including splits)
✅ SPARQL 1.1 parsing
✅ Query execution framework
✅ Temporal data model
✅ Multi-index architecture
✅ 64-bit blob storage support

## What Needs Work

⚠️ Thread safety (partial - some Interlocked ops added)
⚠️ Crash recovery (no WAL)
⚠️ Production error handling
⚠️ Comprehensive test coverage
⚠️ Memory-mapped handle lifecycle  

## Contributing

This is a proof-of-concept implementation demonstrating zero-GC systems programming in C#. 

For production use:
1. Add proper error handling
2. Implement missing B+Tree operations
3. Add thread safety
4. Implement WAL
5. Extensive testing

## License

See main README.md
