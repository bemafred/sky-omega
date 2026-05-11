# ADR-040: Readahead memory adaptive sizing — substrate adapts to host RAM

## Status

**Status:** Proposed — 2026-05-10 (revised 2026-05-11 with measured MPHF evidence + scope clarification)

## Scope

This ADR covers **readahead memory only** — the per-chunk double-buffered `ChunkReadAheadBuffer` allocation pattern in `SortedAtomStoreExternalBuilder.MergeAndWrite`. It does **not** cover MPHF construction memory, which the 2026-05-11 `rebuild-mphf` measurement (see "Measured baseline" below) revealed as the substrate's *actual* peak memory consumer — roughly 3× the readahead's peak. ADR-040's readahead-adaptive design is necessary but not sufficient for substrate-host-portability; a sibling ADR (proposed: **ADR-042 — MPHF construction memory adaptive sizing**) will address the dominant memory phase.

The two memory phases are sequential, not concurrent:

| Phase | Lifetime | Measured peak working set (4 B atoms, 128 GB host) |
|---|---|---:|
| Merge (readahead active) | MergeAndWrite enter → atoms.atoms sealed | ~6 GB parser + 31 GB readahead + page cache (projected; not yet measured with `ReadAheadFootprintSampleEvent`) |
| MPHF construction | BuildMphfFiles enter → atoms.idx written | **89 GB measured** (rebuild-mphf, 2026-05-11, level 0 peak) |

ADR-040 addresses the merge phase. ADR-042 will address the MPHF phase. Each must independently scale to the host; together they bound the substrate's host-portability surface.

## Context

ADR-038 Part 2 introduced `ChunkReadAheadBuffer` — a per-chunk double-buffered (`_front` + `_back`) user-space frontier cache, refilled asynchronously by `ChunkReadAheadDispatcher`. The architectural intent was sound (turn 3,923 random-access streams into 3,923 truly-sequential streams), but its memory accounting was incomplete:

- ADR-038 §Part 2 stated the budget as `3,923 chunks × 4 MB = ~15 GB`. That undercounts by 2× — the buffer is double-buffered, so the real per-chunk-reader footprint is 8 MiB, not 4 MiB. **True peak ≈ 31 GiB** at 21.3 B Wikidata scale.
- The 31 GiB figure was caught by external review on 2026-05-10 ([`docs/reviews/sky-omega-latest-version-review-2026-05-10.md`](../../reviews/sky-omega-latest-version-review-2026-05-10.md) §7) and now characterized in [`docs/limits/readahead-buffer-memory-budget.md`](../../limits/readahead-buffer-memory-budget.md).
- On the 128 GB target host the projection is acceptable (~24 % of RAM). On a 64 GB host the budget would consume nearly half of RAM and crowd out the kernel page cache. On a 32 GB host the substrate would collapse under its own readahead.

The substrate currently treats 8 MiB-per-chunk × N as a hard architectural commitment, regardless of host. That is *brittle*: it works on the substrate-target host and silently degrades elsewhere. ADR-040 makes the substrate adaptive — it senses its host at merge start and scales its readahead budget accordingly.

This is a substrate-discipline call, not a performance optimization. The current behavior is: **"the substrate assumes 128 GB; on smaller hosts, behavior is undefined / undocumented."** That is unacceptable for an MIT-licensed substrate that others may run.

## Hypothesis (falsifiable)

**H1 — A budget-bounded readahead achieves equivalent merge throughput to today's eager 8-MiB-per-chunk allocation, on the 128 GB target host.** If the substrate caps total readahead at, say, 30 GiB (slightly above the eager projection), it should select `bufferSize = DefaultBufferSize` (4 MiB per side) for cycle-9-shape inputs. Merge phase performance is unchanged.

**Falsified if:** measured merge phase throughput regresses > 5 % vs the eager-allocation cycle 10 baseline.

**H2 — On a memory-constrained host (e.g., 32 GB), the budget-bounded readahead halves bufferSize automatically and merge completes successfully — slower than on the 128 GB host but stable.** Today the same workload would either OOM the host or thrash the kernel page cache.

**Falsified if:** the smaller-buffer path produces wrong output (correctness regression), OR a 32 GB host run still exhausts RAM despite the cap.

**H3 — Lazy back-buffer allocation reduces the standing footprint without changing peak.** Many chunks consume their entire `_front` quickly at merge start before any refill is needed; deferring `_back` allocation until `RequestRefill` fires saves transient memory.

**Falsified if:** measured peak readahead memory does not fall vs the eager-allocation baseline (i.e., enough chunks immediately demand refill that lazy allocation never wins).

## Decision

### Part 1 — Adaptive sizing at MergeAndWrite start

`SortedAtomStoreExternalBuilder.MergeAndWrite` computes an effective readahead budget at start, before constructing any `ChunkReadAheadBuffer`:

```
availableMemoryBytes = ProcessMemoryProbe.AvailablePhysicalBytes()  // see Part 4
budgetFraction       = MERCURY_READAHEAD_BUDGET_FRACTION env var, default 0.25
maxReadAheadBytes    = (long)(availableMemoryBytes * budgetFraction)

projectedTotal       = chunkCount * 2L * DefaultBufferSize   // front + back per chunk
effectiveBufferSize  = DefaultBufferSize

while (chunkCount * 2L * effectiveBufferSize > maxReadAheadBytes
       && effectiveBufferSize > MinBufferSize)
{
    effectiveBufferSize /= 2
}

if (chunkCount * 2L * effectiveBufferSize > maxReadAheadBytes)
{
    // Budget cannot be met even at MinBufferSize — disable readahead entirely.
    readAheadEnabled = false
}
```

- `DefaultBufferSize = 4 * 1024 * 1024` (current value, unchanged).
- `MinBufferSize = 256 * 1024` (256 KiB — still ~4,000× larger than any single record).
- Decision logged via the `ReadAheadBudgetEvent` (Part 4).

The selected `effectiveBufferSize` is passed to every `ChunkReadAheadBuffer` constructor. The dispatcher's `streamBufferSize` (separate concern: FileStream-internal buffer) is unchanged.

### Part 2 — Lazy back-buffer allocation

`ChunkReadAheadBuffer` constructor allocates `_front` only:

```csharp
public ChunkReadAheadBuffer(long fileLength, int bufferSize, Action? onBackEmpty)
{
    _front = new byte[bufferSize];
    _back  = null;                         // lazy
    _backBufferSize = bufferSize;
    ...
}
```

`FillBack(FileStream fs)` allocates `_back` on first invocation:

```csharp
public void FillBack(FileStream fs)
{
    ...
    _back ??= new byte[_backBufferSize];
    ...
}
```

`Swap` continues as today (atomic reference swap). After the first refill, behavior is identical to the eager-allocation path. For chunks that finish their `_front` content without ever needing a refill (small chunks, or chunks at the tail of a long-skewed merge), `_back` is never allocated.

### Part 3 — Eager teardown on chunk exhaustion

`ChunkReader` (or its caller in `MergeAndWrite`) disposes the `ChunkReadAheadBuffer` when `IsExhausted` becomes true. `Dispose` nulls both `_front` and `_back` references:

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    try { _backEmpty.Release(); } catch { }
    try { _backFilled.Release(); } catch { }
    _backEmpty.Dispose();
    _backFilled.Dispose();
    _front = null!;        // release for GC
    _back = null;
}
```

Verification: search for `ChunkReadAheadBuffer.Dispose` callers in the merge pipeline. If `MergeAndWrite` only disposes via the outer `using var readers = ...` pattern at end-of-merge, that is too late — chunks exhaust at very different times under k-way merge. A per-chunk dispose-on-exhaustion is the structural requirement.

### Part 4 — Observability: `ReadAheadBudgetEvent`

A one-shot structured event emitted at MergeAndWrite start, before constructing any buffer:

```csharp
public sealed class ReadAheadBudgetEvent : MetricEvent
{
    public string Phase => "merge_readahead_budget";
    public int ChunkCount;
    public long AvailableMemoryBytes;
    public long MaxReadAheadBytes;       // = AvailableMemoryBytes × budgetFraction
    public int RequestedBufferSize;       // = DefaultBufferSize
    public int EffectiveBufferSize;       // post-scaling
    public long ProjectedTotalBytes;      // chunkCount × 2 × EffectiveBufferSize
    public bool ReadAheadEnabled;
    public string DecisionLog;            // human-readable scaling sequence
}
```

Plus `ReadAheadFootprintSampleEvent` emitted periodically during merge (every `metrics-state-interval` seconds, alongside RSS): tracks live `_front` + `_back` allocations across all active `ChunkReadAheadBuffer` instances. Gives measured peak vs projected.

### Part 5 — Cross-process memory probe

`ProcessMemoryProbe.AvailablePhysicalBytes()` — a new helper in `Mercury.Runtime`:

- macOS: `host_statistics64(HOST_VM_INFO64, ...)` via P/Invoke. Returns `free_count + inactive_count` × `page_size` (the "available, can be reclaimed" view, not just `free_count`).
- Linux: parse `/proc/meminfo`'s `MemAvailable` field.
- Windows: `GlobalMemoryStatusEx` `ullAvailPhys` field.

Substrate-independence consistent: pure P/Invoke, no NuGet dependency. Probe runs once per merge — overhead is irrelevant.

## Consequences

### Positive

- **Substrate becomes host-portable.** The same `mercury` binary runs correctness-preserved on 32 GB / 64 GB / 128 GB hosts, scaling its readahead aggressiveness to fit. Required for MIT-licensed substrate hygiene.
- **Documentation matches reality.** ADR-038 §Part 2's 15 GB figure is corrected to the actual 31 GiB; future readers see honest projections.
- **Memory shape becomes data, not docstring.** `ReadAheadBudgetEvent` makes the substrate's memory decisions queryable (in JSONL) and replayable (per-cycle attribution).
- **Lazy back-buffer + eager teardown reduce standing footprint** without changing peak behavior on the target host.

### Negative / risks

- **Adaptive sizing adds one layer of decision logic.** A bug in the scaling math could under- or over-allocate. Falsifiable via H1 (target host throughput unchanged) + unit tests covering the `availableMemoryBytes`/budget edge cases (host with 16 GB available, host with 256 GB, etc.).
- **`ProcessMemoryProbe` is platform-specific code.** P/Invoke for three OSes adds surface area. Mitigated: probe is one function, ~30 lines per platform.
- **Lazy back-buffer changes timing slightly** — the first refill of a chunk now includes an allocation. At ~4 MiB allocation in a runtime that pretouches large arrays, this is sub-millisecond per chunk × 3,923 chunks = a few seconds of cumulative latency, masked by I/O.
- **Per-chunk eager dispose requires plumbing.** The merge loop knows when a `ChunkReader` is `IsExhausted`; it must explicitly dispose its buffer at that point rather than leaving disposal to the outer `using`.

### Neutral

- The 30 % budget fraction is a default, overridable via env var. Users running other workloads on the same host can tune it down.
- The "disable readahead entirely" branch (Part 1, when budget can't be met even at `MinBufferSize`) preserves correctness via the existing direct-fs-read fallback path.

## Validation plan

Cycle 11 (or a dedicated Phase post-cycle-10):

1. **Target host (128 GB), eager-vs-adaptive A/B at 100 M / 1 B / 21.3 B.** Merge throughput must be within ±5 % of the eager-allocation baseline (H1).
2. **Memory-constrained host (32 GB cloud VM), 100 M Wikidata Reference.** Substrate selects 1 MiB or 512 KiB buffer, completes successfully (H2).
3. **Lazy-back A/B at 21.3 B.** `ReadAheadFootprintSampleEvent`'s peak should be ≤ eager projection; ideally lower depending on chunk-finish-time distribution (H3).

Validation document: `docs/validations/adr-040-adaptive-readahead-{date}.md`.

## Measured baseline (rebuild-mphf, 2026-05-11)

The 1.7.55 `mercury --rebuild-mphf wiki-21b-ref-r3` run against the existing sealed atoms.atoms (99 GB, 4,005,235,528 atoms) was the first opportunity to measure the substrate's MPHF-phase memory footprint in isolation (no readahead active, no parser running — just BBHashBuilder reading atoms via mmap and building levels in memory).

| Observation point | Elapsed (m:s) | RSS | %CPU |
|---|---:|---:|---:|
| Process start (BBHashBuilder allocates `remaining` + `translation` ChunkedArrays) | 02:00 | 31 GB | 87.6 % |
| Level 0 bit-vector hashing + position capture peak | 34:42 | **89 GB** | 71.6 % |

**Memory consumers at level 0 (4 B atoms):**

| Component | Allocation | Lifetime | Approx size |
|---|---|---|---:|
| `translation` | `ChunkedArray<long>(N)` | All levels (persistent) | **32 GB** |
| `keyPositions` | `ChunkedArray<long>(N)` | This level only | **32 GB** |
| `remaining` (start of level 0) | `ChunkedList<long>` of length N | This level only | ~32 GB |
| `bv` + `seen` + `collided` BitVectors | 3 × γ N bits = 3 GB level 0 | This level only | ~3 GB |
| `bumped` (level 0 → level 1 input) | `ChunkedList<long>` of length ~0.39 N | Cross-level transient | ~12 GB |
| Per-atom byte[] allocations from `GetAtomSpan().ToArray()` | N × ~60 B with GC churn | Continuous | hidden in GC |

The peak 89 GB observed at level 0 represents ~70 % of available physical RAM on the 128 GB substrate target host. **This is the substrate's binding memory constraint, not readahead.** Subsequent levels see geometric decay (each level processes ~0.39× the previous), so the level-0 peak is the all-phase peak.

For a 32 GB host, this workload is currently impossible — the substrate would OOM during MPHF construction long before readahead became the issue. ADR-042 will need to address chunked `translation` (mmap-backed instead of in-memory), elimination of `keyPositions` via re-hashing in the second pass, and a streaming `remaining`/`bumped` model.

## Alternatives considered

- **Hard-code a smaller buffer (e.g., 2 MiB) globally.** Halves the budget at the cost of merge throughput on hosts where the budget is fine. Doesn't address the host-portability concern; just shifts the assumed-host point.
- **`ArrayPool<byte>` for the buffers.** Pool reuse helps if buffers complete at staggered times. But during k-way merge most buffers are concurrently alive — pool gains are marginal vs the complexity. Also conflicts with substrate-independence if `ArrayPool` semantics diverge across BCL versions.
- **Per-chunk size hint based on chunk-file size.** For very small chunks (< 50 MB), a 4 MiB buffer is wasteful. Adds complexity and partial overlap with Part 1's adaptive sizing. Out of scope for this ADR; could compose with it later.
- **Disable readahead by default, opt-in via env var.** Loses ADR-038 §Part 2's measured wins. Wrong tradeoff — readahead is the right architecture; the substrate just needs to scale it.

## References

- ADR-038 §Part 2 — original readahead architecture (this ADR refines its memory accounting and adds host-adaptivity)
- ADR-042 (forward reference, to be drafted) — MPHF construction memory adaptive sizing; the *dominant* memory-phase substrate-host-adaptation. ADR-040 alone does not deliver substrate host-portability — both are required.
- [`docs/limits/readahead-buffer-memory-budget.md`](../../limits/readahead-buffer-memory-budget.md) — limits-register characterization that ADR-040 makes Resolved
- [`docs/reviews/sky-omega-latest-version-review-2026-05-10.md`](../../reviews/sky-omega-latest-version-review-2026-05-10.md) §7 — external review that surfaced the 2× undercount in ADR-038's stated budget
- 1.7.55 `rebuild-mphf` measurement (2026-05-11) — log: `docs/validations/cycle10-phase3-21b-resume-2026-05-11.log` — first direct measurement of MPHF-phase RSS at 4 B-atom scale, motivating the scope clarification above
- [feedback_resource_limit_class_audit](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_resource_limit_class_audit.md) — discipline that motivates this ADR (memory is a resource class; treating it as host-independent is the same anti-pattern as treating FDs as host-independent)
