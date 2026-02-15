# Running Benchmarks

Mercury includes a BenchmarkDotNet project covering storage, SPARQL, parsing,
temporal, concurrent, and federated query performance. This tutorial explains
how to run benchmarks, read the results, and use them for performance
validation.

> **Prerequisites:** The repository cloned and building. See
> [Getting Started](getting-started.md) for setup.

---

## Quick Start

Run all benchmarks (takes 15-30 minutes):

```bash
dotnet run --project benchmarks/Mercury.Benchmarks -c Release
```

Run a single benchmark class:

```bash
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*.QueryBenchmarks.*"
```

List available benchmarks without running them:

```bash
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --list
```

---

## Available Benchmark Classes

### Storage

| Class | Filter | What it measures |
|-------|--------|-----------------|
| `BatchWriteBenchmarks` | `*.BatchWriteBenchmarks.*` | Single vs. batch write throughput |
| `QueryBenchmarks` | `*.QueryBenchmarks.*` | Query operations on pre-populated store |
| `IndexSelectionBenchmarks` | `*.IndexSelectionBenchmarks.*` | Impact of index selection (SPO/POS/OSP) |

### SPARQL

| Class | Filter | What it measures |
|-------|--------|-----------------|
| `SparqlParserBenchmarks` | `*.SparqlParserBenchmarks.*` | SPARQL parsing throughput |
| `SparqlExecutionBenchmarks` | `*.SparqlExecutionBenchmarks.*` | End-to-end query execution |
| `JoinBenchmarks` | `*.JoinBenchmarks.*` | JOIN operator scaling (2/5/8 patterns) |
| `FilterBenchmarks` | `*.FilterBenchmarks.*` | FILTER expression overhead |
| `FilterPushdownBenchmarks` | `*.FilterPushdownBenchmarks.*` | Filter pushdown optimization impact |

### Temporal

| Class | Filter | What it measures |
|-------|--------|-----------------|
| `TemporalWriteBenchmarks` | `*.TemporalWriteBenchmarks.*` | Temporal triple write performance |
| `TemporalQueryBenchmarks` | `*.TemporalQueryBenchmarks.*` | AS OF, DURING, ALL VERSIONS queries |

### RDF Parsing

| Class | Filter | What it measures |
|-------|--------|-----------------|
| `NTriplesParserBenchmarks` | `*.NTriplesParserBenchmarks.*` | N-Triples zero-GC vs. allocating |
| `TurtleParserBenchmarks` | `*.TurtleParserBenchmarks.*` | Turtle zero-GC vs. allocating |
| `RdfFormatComparisonBenchmarks` | `*.RdfFormatComparisonBenchmarks.*` | N-Triples vs. Turtle throughput |

### Concurrent Access

| Class | Filter | What it measures |
|-------|--------|-----------------|
| `ConcurrentReadBenchmarks` | `*.ConcurrentReadBenchmarks.*` | Parallel read scaling |
| `ConcurrentWriteBenchmarks` | `*.ConcurrentWriteBenchmarks.*` | Write throughput under contention |
| `MixedWorkloadBenchmarks` | `*.MixedWorkloadBenchmarks.*` | Mixed read/write workloads |
| `LockContentionBenchmarks` | `*.LockContentionBenchmarks.*` | Lock contention measurement |
| `ConcurrentBatchBenchmarks` | `*.ConcurrentBatchBenchmarks.*` | Concurrent batch operations |

### Federated

| Class | Filter | What it measures |
|-------|--------|-----------------|
| `ServiceBenchmarks` | `*.ServiceBenchmarks.*` | SERVICE clause execution |
| `ServiceThresholdBenchmarks` | `*.ServiceThresholdBenchmarks.*` | SERVICE temp store thresholds |

---

## Reading Results

BenchmarkDotNet writes results to `BenchmarkDotNet.Artifacts/results/` at the
repository root (gitignored).

### Markdown reports

Each benchmark class produces a GitHub-flavored markdown report:

```bash
# Run a benchmark
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- \
  --filter "*.QueryBenchmarks.*"

# Read the report (~18 lines per class)
cat BenchmarkDotNet.Artifacts/results/SkyOmega.Mercury.Benchmarks.QueryBenchmarks-report-github.md
```

### Key columns

| Column | Meaning |
|--------|---------|
| Mean | Average execution time |
| Error | Half-width of the 99.9% confidence interval |
| StdDev | Standard deviation across iterations |
| Gen0/Gen1/Gen2 | GC collections per operation (0 = zero-GC) |
| Allocated | Bytes allocated per operation |

### What "good" looks like

| Metric | Target | Why |
|--------|--------|-----|
| Gen0 = 0 | Zero GC on hot paths | Predictable latency |
| Allocated = 0 B | No heap allocations | Zero-GC design verified |
| Mean stable across runs | Consistent performance | No measurement noise |

---

## Filter Tips

BenchmarkDotNet uses glob patterns for `--filter`:

```bash
# Exact class match (avoids matching TemporalQueryBenchmarks)
--filter "*.QueryBenchmarks.*"

# All benchmarks with "Temporal" in the name
--filter "*Temporal*"

# Specific method in a class
--filter "*.QueryBenchmarks.PointQuery"

# Multiple classes
--filter "*.BatchWriteBenchmarks.*" --filter "*.QueryBenchmarks.*"
```

The `*ClassName*` pattern is looser than `*.ClassName.*` -- the latter avoids
matching classes with similar names (e.g., `QueryBenchmarks` vs.
`TemporalQueryBenchmarks`).

---

## Performance Regression Testing

Compare results between runs:

1. Run the benchmark on the current commit and save the report
2. Make your changes
3. Run the same benchmark again
4. Compare the Mean and Allocated columns

```bash
# Before changes
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- \
  --filter "*.QueryBenchmarks.*"
cp BenchmarkDotNet.Artifacts/results/*QueryBenchmarks* /tmp/before.md

# After changes
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- \
  --filter "*.QueryBenchmarks.*"

# Compare
diff /tmp/before.md BenchmarkDotNet.Artifacts/results/*QueryBenchmarks*
```

### Reference performance

| Operation | Throughput | Notes |
|-----------|------------|-------|
| Single write | ~250-300/sec | fsync per write |
| Batch 1K | ~25,000+/sec | 1 fsync per batch |
| Batch 10K | ~100,000+/sec | Amortized fsync |
| Point-in-time query (cached) | ~100K queries/sec | Hot page cache |
| Point-in-time query (cold) | ~5K queries/sec | Disk access |
| Range scan | ~200K triples/sec | Sequential read |
| Evolution scan | ~500K triples/sec | Full history |

---

## Tips

- Always use `-c Release` -- Debug builds disable optimizations and produce
  misleading numbers
- Close other applications during benchmark runs to reduce noise
- The first run after a build may be slower due to JIT compilation --
  BenchmarkDotNet handles warmup automatically
- Results are in `BenchmarkDotNet.Artifacts/results/` which is gitignored --
  they persist locally between runs for comparison but are not committed

---

## See Also

- [Embedding Mercury](embedding-mercury.md) -- using Mercury as a library
- [Getting Started](getting-started.md) -- build and setup
- [STATISTICS.md](../../STATISTICS.md) -- reference performance numbers
