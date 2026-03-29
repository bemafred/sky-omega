# Production Hardening

Infrastructure abstractions, query optimization, full-text search, test infrastructure, and benchmarking.

## Infrastructure Abstractions

**ILogger** (`SkyOmega.Mercury.Diagnostics.ILogger`):
- BCL-only logging abstraction with zero-allocation hot path
- Levels: Trace, Debug, Info, Warning, Error, Critical
- `NullLogger.Instance` for production (no overhead)
- `ConsoleLogger` for development/debugging

**IBufferManager** (`SkyOmega.Mercury.Buffers.IBufferManager`):
- Unified buffer allocation strategy across all components
- `PooledBufferManager.Shared` uses `ArrayPool<T>` internally
- `BufferLease<T>` ref struct for RAII-style automatic cleanup

## Query Optimization

Statistics-based join reordering for 10-100x performance improvement on multi-pattern queries.

**Components:**

| Component | File | Purpose |
|-----------|------|---------|
| `PredicateStats` | `Storage/PredicateStatistics.cs` | Per-predicate cardinality statistics |
| `StatisticsStore` | `Storage/PredicateStatistics.cs` | Thread-safe statistics storage |
| `QueryPlanner` | `Sparql/Execution/QueryPlanner.cs` | Cardinality estimation and pattern reordering |
| `QueryPlanCache` | `Sparql/Execution/QueryPlanCache.cs` | LRU cache for execution plans |

**How it works:**
1. Statistics Collection: During `Checkpoint()`, scans GPOS index for per-predicate triple count, distinct subjects, distinct objects
2. Cardinality Estimation: Estimates result count based on bound/unbound variables
3. Join Reordering: Sorts patterns by estimated cardinality (lowest first)
4. Plan Caching: Caches reordered pattern order, invalidates when statistics change

| Optimization | Impact | Status |
|--------------|--------|--------|
| Join Reordering | 10-100x | Implemented |
| Statistics Collection | 2-10x | Implemented |
| Plan Caching | 2-5x | Implemented |
| Predicate Pushdown | 5-50x | Implemented - FilterAnalyzer + MultiPatternScan |

## SERVICE Clause Architecture

SERVICE clauses (federated queries) require special handling due to fundamentally different access semantics vs local patterns. See **[ADR-004-service-scan-interface.md](../../adrs/mercury/ADR-004-service-scan-interface.md)** for the architectural decision record.

**Key principle:** SERVICE is a materialization boundary, not an iterator. Implementation uses:
- `IScan` interface for uniform operator handling
- `ServiceStore` with `TempPath` lifecycle (crash-safe temp QuadStore)
- `ServicePatternScan` wrapping `TriplePatternScan` against temp store

**Implementation phases:**
1. Extract `IScan` interface (mechanical refactor, tests must pass unchanged)
2. Implement temp store pattern (SERVICE results become local triples)

## Full-Text Search

BCL-only trigram index for SPARQL text search. Always enabled — every QuadStore instance creates a trigram index.

**Components:**

| Component | File | Purpose |
|-----------|------|---------|
| `TrigramIndex` | `Storage/TrigramIndex.cs` | UTF-8 trigram extraction and inverted index |
| `text:match` | `Sparql/Execution/FilterEvaluator.Functions.cs` | SPARQL FILTER function |

**Usage:**
```sparql
SELECT ?city ?name WHERE {
    ?city <http://ex.org/name> ?name .
    FILTER(text:match(?name, "göteborg"))
}
```

**Features:**
- Case-insensitive matching with Unicode case-folding (supports Swedish å, ä, ö)
- ADR-024: Trigram pre-filtering at scan level — `MultiPatternScan` restricts enumerator to candidate object atoms, reducing text:match from O(N) to O(k × log N)
- Selectivity-based fallback: candidate sets > 10,000 revert to brute-force scan
- Memory-mapped two-file architecture (trigram.hash + trigram.posts)
- FNV-1a hashing with quadratic probing
- Alternative `match()` syntax supported
- Works with variables, literals, negation, boolean combinations

## Test Coverage

| Component | Status | Priority |
|-----------|--------|----------|
| REPL system | ✓ 147 tests | Done |
| HttpSparqlServiceExecutor | ✓ 44 tests | Done |
| LoadExecutor | ✓ tests | Done |
| Concurrent access stress | ✓ 15 tests | Done |
| PatternSlot/QueryBuffer | ✓ tests | Done |

## Benchmarks

| Component | Status | Priority |
|-----------|--------|----------|
| SPARQL parsing | ✓ SparqlParserBenchmarks | Done |
| SPARQL execution | ✓ SparqlExecutionBenchmarks | Done |
| JOIN operators | ✓ JoinBenchmarks | Done |
| FILTER evaluation | ✓ FilterBenchmarks | Done |
| Storage (batch/query) | ✓ BatchWrite/QueryBenchmarks | Done |
| Temporal queries | ✓ TemporalQueryBenchmarks | Done |
| Concurrent access | ✓ ConcurrentBenchmarks | Done |
| RDF parser throughput | ✓ NTriples/Turtle/FormatComparison | Done |
| Filter pushdown | ✓ FilterPushdownBenchmarks | Done |

### Running Benchmarks via Claude Code

BenchmarkDotNet produces verbose output that exceeds context limits. Use this file-based workflow:

**Strategy: Run targeted benchmarks, read artifact files**

```bash
# 1. Run a single benchmark class (generates markdown report)
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- \
  --filter "*QueryBenchmarks*"

# 2. Results are written to: BenchmarkDotNet.Artifacts/results/ (repo root)
# Read the generated markdown summary (compact, ~18 lines per class)
```

**Available benchmark classes:**

| Class | Filter | Purpose |
|-------|--------|---------|
| `BatchWriteBenchmarks` | `*BatchWrite*` | Single vs batch write throughput |
| `QueryBenchmarks` | `*QueryBenchmarks*` | Query operations on pre-populated store |
| `IndexSelectionBenchmarks` | `*IndexSelection*` | Index selection impact (SPO/POS/OSP) |
| `SparqlParserBenchmarks` | `*ParserBenchmarks*` | SPARQL parsing throughput |
| `SparqlExecutionBenchmarks` | `*ExecutionBenchmarks*` | SPARQL query execution |
| `JoinBenchmarks` | `*JoinBenchmarks*` | JOIN operator scaling (2/5/8 patterns) |
| `FilterBenchmarks` | `*FilterBenchmarks*` | FILTER expression overhead |
| `TemporalWriteBenchmarks` | `*TemporalWrite*` | Temporal triple write performance |
| `TemporalQueryBenchmarks` | `*TemporalQuery*` | Temporal query operations |
| `NTriplesParserBenchmarks` | `*NTriples*` | N-Triples parsing (zero-GC vs allocating) |
| `TurtleParserBenchmarks` | `*Turtle*` | Turtle parsing (zero-GC vs allocating) |
| `RdfFormatComparisonBenchmarks` | `*FormatComparison*` | N-Triples vs Turtle format comparison |

**Workflow for Claude Code:**

1. Run benchmark with `--filter` (one class at a time)
2. Wait for completion (may take 1-5 minutes per class)
3. Read the markdown report from `BenchmarkDotNet.Artifacts/results/`
4. Compare results, identify regressions

**Example - investigating query performance:**

```bash
# Run storage query benchmarks (use precise filter to avoid matching TemporalQueryBenchmarks)
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*.QueryBenchmarks.*"

# Read the compact markdown report
cat BenchmarkDotNet.Artifacts/results/SkyOmega.Mercury.Benchmarks.QueryBenchmarks-report-github.md
```

**Filter tips:**
- `*.ClassName.*` is more precise than `*ClassName*`
- `*QueryBenchmarks*` matches both `QueryBenchmarks` and `TemporalQueryBenchmarks`
- `*.QueryBenchmarks.*` matches only `QueryBenchmarks`

**Note:** Artifacts directory is gitignored. Results persist locally between runs for comparison.

## NCrunch Configuration (Parallel Test Execution)

NCrunch runs tests in **separate processes** (never concurrent threads within the same process). Each process creates its own `QuadStorePool`, so disk usage scales with process count.

**Disk footprint per store:**

| Configuration | Per-Store Size | Notes |
|---------------|----------------|-------|
| Default (`StorageOptions.Default`) | ~5.5 GB | 4×1GB indexes + 1GB atoms + 512MB hash |
| Testing (`StorageOptions.ForTesting`) | ~320 MB | 4×64MB indexes + 64MB atoms + 512MB hash |

The test fixture uses `StorageOptions.ForTesting` automatically.

## Cross-Process Store Coordination

**Problem:** Multiple NCrunch test runner processes can each create stores, potentially exhausting disk space even with `StorageOptions.ForTesting`.

**Solution:** `CrossProcessStoreGate` provides machine-wide coordination using file-based slot locking. Numbered lock files (slot-0.lock through slot-N.lock) with exclusive file locks ensure the OS releases locks automatically on process death, so slots are never permanently lost when a test runner is killed.

**Components:**

| Component | File | Purpose |
|-----------|------|---------|
| `CrossProcessStoreGate` | `Mercury.Runtime/CrossProcessStoreGate.cs` | Global store slot coordination |
| `QuadStorePool` (updated) | `Mercury/Storage/QuadStorePool.cs` | Uses gate when `useCrossProcessGate: true` |
| `QuadStorePoolFixture` | `Mercury.Tests/Fixtures/QuadStorePoolFixture.cs` | Enables gate for all tests |

**How it works:**
```csharp
// Test fixture enables cross-process coordination
Pool = new QuadStorePool(
    storageOptions: StorageOptions.ForTesting,
    useCrossProcessGate: true);  // <-- Key parameter

// When Rent() creates a NEW store:
// 1. Acquires global slot via CrossProcessStoreGate.Instance
// 2. Creates store
// 3. Slot held until pool is disposed
```

**Global slot calculation:**
- Available disk × 33% ÷ 320MB per store
- Clamped to 2-12 slots
- Example: 10GB available → 10GB × 0.33 ÷ 320MB ≈ 10 slots max globally

**File-based locking details:**
- Lock directory: `/tmp/.sky-omega-pool-locks/` (or `%TEMP%` on Windows)
- Files: `slot-0.lock` through `slot-N.lock`
- Uses `FileShare.None` for exclusive locking
- `DeleteOnClose` for automatic cleanup
- Stale lock detection via PID check

**Solution-level settings** (`SkyOmega.v3.ncrunchsolution`):

| Setting | Value | Purpose |
|---------|-------|---------|
| `AllowParallelTestExecution` | True | Enable parallel execution |
| `AllowTestsInParallelWithThemselves` | False | Prevent same-test conflicts |
| `DefaultTestTimeout` | 120000 | 2-minute timeout for storage tests |

**Global settings** (optional - gate handles coordination, but can still be tuned):

| Setting | Recommended | Purpose |
|---------|-------------|---------|
| Max Number Of Processing Threads | 4-8 | Optional - gate provides safety regardless |
| Max Test Runners To Pool | 2-4 | Optional - reduces memory usage |

**Example with cross-process gate:**
```
Machine: 10GB available disk
Global slots: 10 (calculated from disk budget)

NCrunch Process A: Creates 3 stores → holds 3 global slots
NCrunch Process B: Creates 3 stores → holds 3 global slots
NCrunch Process C: Tries to create 5 stores → gets 4, blocks on 5th
NCrunch Process D: Blocks waiting for global slot

Total: 10 stores × 320MB = 3.2GB ✓ Safe (within 33% budget)
```

**Troubleshooting:**

1. **Timeout waiting for slot**: Check `CrossProcessStoreGate.Instance.MaxGlobalStores` - may need more disk space
2. **Lock files accumulating**: Check `/tmp/.sky-omega-pool-locks/` for stale files from crashed processes

## Production Hardening Checklist

- [x] Query timeout via CancellationToken
- [x] Max atom size validation (default 1MB)
- [x] Max query depth limits (parser)
- [x] Max join depth limits (executor)
- [x] try/finally for all operator disposal
- [x] Pointer leak fix in AtomStore
- [x] Thread-safety documentation for parsers
