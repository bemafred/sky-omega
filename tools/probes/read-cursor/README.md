# Read cursor probes

[ADR-062](../../../docs/adrs/mercury/ADR-062-what-a-read-yields.md) Step 2 measures the read as it is before the
cursor of D3 is built, so that the cursor is compared with measurements and not with expectations (D7). Step 5 runs
the same benchmarks again on the cursor.

| What | Shows |
|---|---|
| `enumerator-sizes.cs` | the size in bytes of `TemporalResultEnumerator`, of each profile's backing enumerator, of `NamedGraphEnumerator` and `MergedGraphEnumerator`, and of the two result structs. It reads the types by reflection. It opens no store, writes no file and asserts nothing. |
| the benchmark baseline | the existing read-side benchmarks on the code as it is: `SparqlExecutionBenchmarks`, `JoinBenchmarks`, `FilterBenchmarks` (in `SparqlBenchmarks.cs`), `QueryBenchmarks`, `IndexSelectionBenchmarks` (in `StorageBenchmarks.cs`) and `TemporalQueryBenchmarks`. They make their own temporary stores. The parser and write benchmarks in the same files are left out: the cursor does not touch them. |

```bash
cd tools/probes/read-cursor
dotnet run --no-cache enumerator-sizes.cs

# from the repository root; BenchmarkDotNet, Release, 41 minutes on omega
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*SparqlExecutionBenchmarks*" "*JoinBenchmarks*" "*FilterBenchmarks*" "*.QueryBenchmarks.*" "*IndexSelectionBenchmarks*" "*TemporalQueryBenchmarks*"
```

## Results on omega, 2026-11-15 (`d4f11f2`, .NET 10.0.9, osx-arm64)

The machine ran nothing else but this session's own light commands.

### Sizes

| Type | Bytes |
|---|---:|
| `TemporalResultEnumerator` (what every profile's read returns) | 632 |
| `TemporalQuadEnumerator` (Cognitive) | 232 |
| `VersionedQuadEnumerator` (Graph) | 144 |
| `ReferenceQuadEnumerator` | 120 |
| `MinimalQuadEnumerator` | 96 |
| `NamedGraphEnumerator` (ref struct) | 408 |
| `MergedGraphEnumerator` | 640 |
| `ResolvedTemporalQuad` (ref struct) | 144 |
| `TemporalQuad` | 72 |

The four backing enumerators are 592 bytes together; the struct that holds them all is 632.

### Benchmark baseline

BenchmarkDotNet 0.14.0, Apple M5 Max, 10 iterations after 3 warmups. Mean, with the standard deviation in brackets.

| Class | Benchmark | Mean | Allocated |
|---|---|---:|---:|
| `QueryBenchmarks` | QueryBySubject | 488.4 ns (1.5) | 8.02 KB |
| | QueryByObject | 17,092.6 ns (75.6) | 8.02 KB |
| | QueryByPredicate | 204,405.1 ns (371.6) | 8.02 KB |
| | FullScan | 1,812,011.6 ns (6,362.7) | 8.02 KB |
| `IndexSelectionBenchmarks` | SPO Index (Subject bound) | 748.5 ns (2.5) | none |
| | POS Index (Predicate bound) | 834.1 ns (25.3) | none |
| | OSP Index (Object bound) | 334.9 ns (2.7) | none |
| `TemporalQueryBenchmarks` | Point-in-time query (as-of) | 778.1 ns (9.3) | 8,216 B |
| | Temporal range query | 795.7 ns (9.7) | none |
| | Evolution query (all versions) | 816.1 ns (2.4) | none |
| | Current state query | 902.6 ns (4.6) | 8,216 B |
| `SparqlExecutionBenchmarks` | Execute single pattern | 1,220.5 μs (2.8) | 2,989.1 KB |
| | Execute 3-pattern query | 1,173.0 μs (9.0) | 522.9 KB |
| | Execute query with FILTER | 7,681.2 μs (43.5) | 4,445.9 KB |
| | Execute ASK query (early termination) | 1.203 μs (0.008) | 4.13 KB |
| `JoinBenchmarks` | 2-pattern JOIN | 695.7 μs (1.2) | 421.1 KB |
| | 5-pattern JOIN | 1,760.7 μs (9.9) | 687.9 KB |
| | 8-pattern JOIN | 1,725.3 μs (4.2) | 922.1 KB |
| `FilterBenchmarks` | Numeric comparison FILTER | 2.407 ms (0.011) | 3.73 MB |
| | String CONTAINS FILTER | 2.986 ms (0.005) | 2.95 MB |
| | REGEX FILTER | 7.003 ms (0.024) | 24.92 MB |

`FullScan` and `QueryByPredicate` are the two that walk many rows, so they are where a call per row would show.
The store holds 50,000 quads: `FullScan` reads all of them, 36 ns a row, and `QueryByPredicate` reads 5,000, 41 ns a
row. A row includes decoding three terms into the buffer.
