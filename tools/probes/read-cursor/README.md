# Read cursor probes

[ADR-062](../../../docs/adrs/mercury/ADR-062-what-a-read-yields.md) Step 2 measures the read as it is before the
cursor of D3 is built, so that the cursor is compared with measurements and not with expectations (D7). The step
says the probe is reviewed before it runs.

**Not run yet.** Written 2026-11-15 for Martin's review.

| What | Shows |
|---|---|
| `enumerator-sizes.cs` | the size in bytes of `TemporalResultEnumerator`, of each profile's backing enumerator, of `NamedGraphEnumerator` and `MergedGraphEnumerator`, and of the two result structs. It reads the types by reflection. It opens no store, writes no file and asserts nothing. |
| the benchmark baseline | the existing query benchmarks on the code as it is: `SparqlBenchmarks`, `StorageBenchmarks`, `TemporalBenchmarks`. They make their own temporary stores. |

```bash
cd tools/probes/read-cursor
dotnet run --no-cache enumerator-sizes.cs

# from the repository root; BenchmarkDotNet, Release, takes a long time and all cores
dotnet run --project benchmarks/Mercury.Benchmarks -c Release -- --filter "*SparqlBenchmarks*" "*StorageBenchmarks*" "*TemporalBenchmarks*"
```

The results go here, with the commit, the runtime and the machine, when the probe has run.
