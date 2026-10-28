# Stack probes

ADR-055 Q3 measured how much stack Mercury's SPARQL parse and execution use; its validation step 4 re-runs the same
measurements after the change. `./run.sh` produces all three:

| Probe | Measures |
|---|---|
| `struct-sizes.cs` | `Unsafe.SizeOf` of each parse struct (`Query`, `SubSelect`, `ConstructTemplate`, …) |
| `jit-workload.cs` + `frames.py` | each method's frame, from the JIT's prologue under `DOTNET_JitDisasm='*'`, fully optimized and at Tier 0 |
| `min-stack.cs` + `min-stack.py` | the smallest thread stack each query shape completes on (binary search, one child process per trial, since a stack overflow kills the process) |

`frames.py` reads arm64 prologues (`stp …, [sp, #-N]!`, `sub sp, sp, …`). The 1.8.25 baseline is in
[ADR-055](../../../docs/adrs/mercury/ADR-055-query-where-one-parse.md), Q3.
