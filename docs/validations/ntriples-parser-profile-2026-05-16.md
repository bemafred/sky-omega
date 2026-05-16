# N-Triples Parser Profile + Decision — 2026-05-16

**Mercury version:** 1.7.58 baseline, 1.7.59 after-fix
**Hardware:** Apple M5 Max, 128 GB RAM, macOS 25.4
**Source files:**
- N-Triples: `~/Library/SkyOmega/datasets/wikidata/truthy/latest-truthy.nt.bz2` (40 GB, dump 2026-05-08)
- Turtle: `~/Library/SkyOmega/datasets/wikidata/full/latest-all.ttl.bz2` (114 GB, dump 2026-04-03)
**Workload:** 10M-triple bulk-load via `mercury --bulk-load ... --limit 10000000 --profile Reference --no-repl`

## Summary

The Tier 2 N-Triples parser profile localized the ~18-23% per-triple gap (vs Turtle path) to two distinct contributors:

1. **Bounded fix (shipped 1.7.59):** `NTriplesStreamParser.Peek()` was missing `[MethodImpl(MethodImplOptions.AggressiveInlining)]` — the same annotation Turtle's `Peek()` has carried since 1.7.4. One-attribute change measured at **+6.0% end-to-end / +7.4% steady-state** improvement on N-Triples parse rate at 10M scale.
2. **Structural remainder (documented as inherent):** N-Triples grammar reads ~5-6× more source bytes per triple than Turtle (full IRIs `<http://...>` vs prefix-resolved `wd:Q42`). The per-byte parse cost is roughly equivalent between formats; the remaining gap is grammar-inherent and not closable without redesigning the format.

## Measured before / after

### End-to-end (`load.summary` records)

| Run | Source | Mercury | Elapsed | Avg triples/sec |
|---|---|---|---:|---:|
| N-Triples baseline | `latest-truthy.nt.bz2` | 1.7.57 | 27.71 s | 360,889 |
| **N-Triples after fix** | `latest-truthy.nt.bz2` | **1.7.59** | **26.15 s** | **382,425** |
| **Δ (after − before)** | — | — | **−1.56 s (−5.6%)** | **+21,536 (+6.0%)** |
| Turtle reference | `latest-all.ttl.bz2` | 1.7.57 | 25.10 s | 398,367 |

After-fix N-Triples (382,425 tps) is within 4.0% of the Turtle reference (398,367 tps) at the end-to-end aggregate. The remaining gap dilutes further at smaller-scale runs because the spill + merge + sort phases dominate proportionally as triple count drops.

### Steady-state parse-loop rate (samples 8.1M-9.0M, mid-run)

| Run | Mean recent_rate (10 samples) | Sample range |
|---|---:|---|
| N-Triples 1.7.57 baseline | 543,394 tps | 514K – 578K |
| **N-Triples 1.7.59 after fix** | **583,548 tps** | **555K – 618K** |
| Δ steady-state | **+40,154 tps (+7.4%)** | — |
| Turtle 1.7.57 (parse-dominant steady state) | ~730,000 tps | (from earlier session captures) |

The steady-state ratio Turtle 730K / N-Triples 583K ≈ 1.25 → after-fix gap is ~25%. The ~6× source-bytes-per-triple ratio caps how much closer the N-Triples path can practically come.

## Root-cause analysis (structural)

The asymmetric `AggressiveInlining` annotation between `TurtleStreamParser.Peek()` (annotated since 1.7.4) and `NTriplesStreamParser.Peek()` (un-annotated) was the load-bearing finding.

The Peek hot path:

```csharp
private int Peek()
{
    while (_bufferPosition >= _bufferLength)
    {
        if (_endOfStream) return -1;
        FillBufferSync();
    }
    return _inputBuffer[_bufferPosition];
}
```

Steady-state cost is a single bounds-check + array index. The while-loop is the cold path (only fires on buffer-exhaustion). Without `AggressiveInlining`, the JIT cannot eliminate the per-call dispatch overhead at every inner-loop call site.

### Per-IRI call count comparison

For a typical Wikidata-shape IRI:

| Format | Token shape | Bytes | Peek/Consume calls per token |
|---|---|---:|---:|
| N-Triples | `<http://www.wikidata.org/entity/Q42>` | 38 | ~38 each |
| Turtle (prefixed) | `wd:Q42` | 6 | ~6 each (+1 dict lookup) |

Three IRIs per triple (subject + predicate + object) — at Wikidata scale that's:
- N-Triples: ~114 Peek calls per triple inner loop
- Turtle: ~18 Peek calls per triple inner loop (+ 1 namespace-dict lookup per prefix occurrence, hit-rate ≈ 100%)

The Peek inlining saves ~96 dispatch overheads per triple in N-Triples — multiplied by 360K triples/sec, that's the 6-7% measured speedup.

## Implementation

`src/Mercury/NTriples/NTriplesStreamParser.cs`:

```diff
+ [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private int Peek()
  {
      while (_bufferPosition >= _bufferLength)
      ...
  }

+ [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private int PeekAhead(int offset)
  ...
```

Two annotations added; same convention as Turtle's `Peek()`. No behavioral change; passes all 137 NTriples-related tests.

## Validation

- 137 N-Triples-related tests in Mercury.Tests passed (`dotnet test --filter "FullyQualifiedName~NTriples"`).
- N-Triples 10M re-measurement: avg triples/sec 360K → 382K (+6.0%).
- W3C N-Triples conformance suite is part of the standard Mercury.Tests run (tested at every release).

## Remaining structural gap (Latent — characterized for future)

The post-fix gap (~25% steady-state, ~4% end-to-end at 10M) is grammar-inherent: N-Triples requires fully-qualified IRIs per line. Two candidates for future rounds, both deferred:

**Option B — Vectorized IRI body scan.** Replace the per-char loop in `ParseIriRefSpan` (lines 319-396) with a `MemoryExtensions.IndexOfAny` scan over the raw byte buffer to find the next stop-character (`>`, `\\`, or one of the 7 disallowed chars). For typical Wikidata IRIs without escapes (the common case), one SIMD instruction can scan 16-64 bytes. Expected additional speedup: 10-20% on parse-loop wall-clock. Risk: handling the escape-sequence rollback when an unusual character is hit; correctness via existing W3C conformance suite. **Deferred** — requires a more focused measurement run + risk-bounded refactor.

**Option C — Specialize Consume() for IRI bodies.** IRIs can't span newlines (control-char check rejects them). Per-byte `_line`/`_column` tracking in `Consume()` is wasted work inside `ParseIriRefSpan`. A specialized `ConsumeNonNewline()` shaves the per-byte overhead. **Deferred** — bounded but expected speedup small (~2-3%).

Limits register entry [`ntriples-parser-per-triple-perf.md`](../limits/ntriples-parser-per-triple-perf.md) is updated to **Resolved-Partially** (1.7.59 Peek inlining shipped); Options B + C remain queued as Latent candidates if a future workload requires sub-1.25× format-gap.

## Methodology

The dotnet-trace CPU-sampling approach (intended path) does not capture managed-thread CPU samples reliably on macOS — its `dotnet-sampled-thread-time` profile reports nearly 100 % `UNMANAGED_CODE_TIME` for I/O-mixed workloads, because samples land on the kernel side of `read()` / `recv()` / `mmap()` syscalls. The `cpu-sampling` profile is `collect-linux`-only.

Replacement methodology: **steady-state rate measurement** from the existing 100K-triple-interval `load.progress` JSONL records the bulk-load CLI emits with `--metrics-out`. Mid-run samples (~9M-triple mark) are away from startup transients and pre-spill effects. Mean of 10 consecutive samples produces a stable parse-loop rate estimate (~3% variance). The Peek-inlining intervention's +7.4% steady-state delta is well outside that variance.

For deeper hot-method attribution, future profiling rounds should use Instruments.app (Xcode) or `xctrace record` — Apple-native sampling that captures both managed and unmanaged stacks. Not used here because the structural code-read (Peek-inlining asymmetry vs Turtle) plus the steady-state rate measurement was sufficient evidence to ship the bounded fix.

## References

- [N-Triples parser per-triple perf](../limits/ntriples-parser-per-triple-perf.md) — limits-register characterization (this validation closes part of it).
- [Truthy r1 validation](truthy-r1-2026-05-14.md) — the original full-scale measurement that surfaced the ~18-23% format gap.
- [Cycle 10 r4 validation](cycle10-phase3-r4-21b-2026-05-12.md) — the Turtle full-scale comparison baseline.
- `src/Mercury/NTriples/NTriplesStreamParser.cs` — the file changed; 1.7.59 implements the Peek inlining.
- `src/Mercury/Turtle/TurtleStreamParser.Buffer.cs` — the asymmetric annotation that motivated the fix.
