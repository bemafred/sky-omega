# Changelog

All notable changes to Sky Omega will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Current release: [Mercury 1.7.63](#1763---2026-05-16)** — released 2026-05-16; ADR-040 Parts 1 + 4 + shared `ProcessMemoryProbe` land — **adaptive readahead-buffer sizing** at `MergeAndWrite` start. Substrate now reads `ProcessMemoryProbe.AvailablePhysicalBytes()` and halves the per-chunk readahead buffer from 4 MiB → 256 KiB to fit a budget fraction (default 25 %) of available host memory; if the minimum still won't fit, falls back to synchronous direct-fs reads. Decision emitted as `merge_readahead_budget` JSONL event. End-to-end verified on a 10 M-triple run: 2 chunks, 102 GiB available, 25.6 GiB budget, accepts default 4 MiB. Stacks on 1.7.62's ADR-042 Parts 2+3. Production substrate continues to be validated by 1.7.57's **three paired measurements on the same substrate generation**:
- [cycle 10 Phase 3 r4](docs/validations/cycle10-phase3-r4-21b-2026-05-12.md) at 21.3 B **full** Wikidata in 23 h 57 m end-to-end (2026-05-13)
- [truthy r1](docs/validations/truthy-r1-2026-05-14.md) at 8.17 B **truthy** Wikidata in 14 h 13 m end-to-end (2026-05-14)
- [WGPB step C](docs/validations/wgpb-step-c-2026-05-16.md) at ~150 M **2018 reduced-truthy** Wikidata in 4 m 30 s end-to-end + 849/850 WGPB queries in 4 m 43 s (2026-05-16) — the apples-to-apples measurement vs published WGPB/MillenniumDB numbers

Plus the **complete WDBench paired matrix** (5 categories × 2 substrates, 5,316 queries) — now synthesized into the [aggregate distribution table](docs/validations/wdbench-aggregate-distribution-2026-05-16.md) with publication-grade percentiles (median 69 ms full / 62 ms truthy; p99 ≈ 52 s; 52.2 % completion rate; **0 failed**). Cumulative **0-substrate-failure discipline across ~9,763 measured queries**. Run `mercury --version` to confirm.

## What's Next

**Sky Omega 1.7.x** — cycle 10 Phase 3 r4 (full) + truthy r1 + WGPB step C production validations **complete** as of 2026-05-16. Three paired measurements on the same 1.7.57 substrate generation across three Wikidata-derived datasets: full (2026-04-03), truthy (2026-05-08), and 2018-reduced-truthy (WGPB filtered). Plus the complete WDBench paired matrix (all 5 categories × full + truthy substrates = 5,316 queries) and WGPB's 850 queries on the 2018 substrate. **Cumulative substrate discipline: 0 substrate failures across ~9,763 measured queries on the 1.7.57 line** (1 query rejected as malformed SPARQL source data — correctly identified by the parser). The three-substrate paired record is publication-ready for external Wikidata-community comparison vs QLever, Virtuoso, Blazegraph, MillenniumDB published numbers. Two paired 21.3 B + 8.17 B measurements on the same 1.7.57 substrate generation. The full-Wikidata measurement is the substrate-capability claim; the truthy-subset measurement is the apples-to-apples companion vs published WDBench / QLever / Virtuoso numbers (which run truthy). Trigram-phase finding: entries ratio truthy/full = 90.7 % at triple-count ratio 38.3 % = ~2.4× more literal-density per triple in truthy → trigram-phase prediction needs literal-volume scaling, not triple-count scaling (dump-date confounder noted: truthy 2026-05-08, full 2026-04-03). See [truthy r1 validation](docs/validations/truthy-r1-2026-05-14.md).

**Sky Omega 1.7.x — original cycle 10 r4 narrative** — Substrate at 1.7.57: ADR-034 SortedAtomStore for Reference (since 1.7.30), ADR-037 pipelined spill (1.7.50), 1.7.49 cleanup hook, **ADR-038 merge-phase read-side** (B1 prefix-compress intermediate chunks 1.7.52 + B2 frontier readahead + sidecar 1.7.52/1.7.54), **ADR-039 BBHash MPHF** over the sealed atom set (1.7.55 with `MaxLevels`=40 + dense final-level fallback), **MPHF instrumentation** (per-level events + dense-fallback + start/complete summary, 1.7.56), and the **listener wire-through fix** at `QuadStore.RebuildMphf` (1.7.57). Cycle 10 r4 21.3 B Wikidata Reference + Sorted bulk-load + rebuild **23 h 56 m 50 s end-to-end** (vs cycle 9's 35 h 35 m on 1.7.50, **−11 h 38 m / −32.7 %**). Phase breakdown: parse 9 h 17 m (cycle 9 parity), merge 2 h 41 m, MPHF construction **54 m 29 s** (first instrumented production-scale measurement on 4.005 B atoms — 25 levels, 0 dense fallback, placement_ratio held at 0.6065 = BBHash theoretical for γ=2.0), GSPO drain ~1 h 38 m, GPOS rebuild 55 m 27 s, Trigram rebuild 8 h 30 m 30 s. Final substrate ~2.1 TB on disk; MPHF blob 1.75 GB + idx table 16.0 GB are the new ADR-039 line items.

Cumulative optimization story (Phase 6 → cycle 10 r4, **measured-vs-measured, four completed 21.3 B runs**): **85 h → 24 h, −71.8 %** wall-clock reduction across the substrate's trajectory. Algorithmic (ADR-034 Hash → Sorted), architectural (ADR-037 pipelined spill + ADR-038 readahead + ADR-039 MPHF), and avoiding-work (prefix compression, FD pool, cleanup hook, B1 chunk-side prefix compression) shapes compounded.

**Cycle 10 retrospective** — three abort/retry attempts before the clean r4 run: 1.7.52 int32 overflow in `BBHashBuilder` at ~4 B atoms (fixed 1.7.53 via `SkyOmega.Bcl` long-indexed structures), 1.7.54 BBHash non-convergence after 24 iterative levels (fixed 1.7.55 via MaxLevels 24→40 + dense fallback), 1.7.55 Claude deployed 1.7.56 mid-run causing `FileNotFoundException` at MPHF construction when lazy-loaded `SkyOmega.Bcl 1.7.55` vanished from disk (recovered via `--rebuild-mphf`, then full r4 retry on 1.7.57). The deploy-during-running-CLI mistake is now memorialized as a hard discipline rule in `feedback_no_deploy_during_long_running_process.md`. Three open follow-ups deferred to cycle 11: **ADR-041** cleanup-on-exception (third orphaning occurrence), **`ExternalSorter` FD pool integration** (trigram rebuild held 8,192 chunk FileStreams simultaneously; 17 % headroom to the launchd ~10K ceiling), **ADR-040** readahead memory adaptive sizing (Proposed).

Cycle 10 r4 production validation: [docs/validations/cycle10-phase3-r4-21b-2026-05-12.md](docs/validations/cycle10-phase3-r4-21b-2026-05-12.md).

**Sky Omega 1.8.0** — production hardening release per [docs/roadmap/production-hardening-1.8.md](docs/roadmap/production-hardening-1.8.md). All six phases of the original roadmap shipped (ADR-028 rehash, ADR-029 profiles, ADR-030 measurement + Decision 5, ADR-031 Dispose gate, ADR-032 radix external sort, ADR-033 bulk-load radix). 1.8.0 will roll up the Phase 7 round series once Round 2 (atom-ID bit packing, hash-function quality) lands.

**Sky Omega 2.0.0** will introduce cognitive components: Lucy (semantic memory), James (orchestration), Sky (LLM interaction), and Minerva (local inference).

---

## [1.7.63] - 2026-05-16

**Headline:** ADR-040 Parts 1 + 4 + the shared `ProcessMemoryProbe` infrastructure land. The substrate's merge-phase readahead now adapts its per-chunk buffer size to the host's available physical memory at run start, rather than committing unconditionally to the 4 MiB default. Sized at 21.3 B Wikidata scale on a 128 GB host the eager allocation is ~31 GiB (24 % of RAM); on a 64 GB or 32 GB host the same workload would have crowded out kernel page cache or OOM'd. Adaptive sizing closes this substrate-host-portability gap.

### Added

- **`SkyOmega.Mercury.Runtime.ProcessMemoryProbe`** — host-physical-memory probe. macOS path uses `host_statistics64(HOST_VM_INFO64)` + `sysctlbyname("hw.pagesize")` via P/Invoke; Linux parses `/proc/meminfo`'s `MemAvailable`; Windows uses `GlobalMemoryStatusEx`. Other platforms fall back to `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`. Pure BCL + P/Invoke, no NuGet. Shared infrastructure for ADR-040 and ADR-042 Part 5.
- **`ReadAheadBudgetEvent`** — one-shot event emitted at `MergeAndWrite` start. Fields: `ChunkCount`, `AvailableMemoryBytes`, `MaxReadAheadBytes`, `RequestedBufferSize`, `EffectiveBufferSize`, `ProjectedTotalBytes`, `ReadAheadEnabled`, `DecisionLog`. JSONL `phase: "merge_readahead_budget"`.
- **`IObservabilityListener.OnReadAheadBudget(in ReadAheadBudgetEvent ev)`** — no-op default; `JsonlMetricsListener` serializes to JSONL.
- **`ChunkReadAheadBuffer.MinBufferSize`** = 256 KiB constant. Adaptive sizing halves down to this floor; if even MinBufferSize won't fit the budget, readahead is disabled entirely.
- **`ChunkReadAheadDispatcher.ReadAheadBufferSize`** property — captures the per-side buffer size MergeAndWrite's adaptive sizing decided on, so `ChunkReader` constructs each chunk's buffer at that size.
- 3 smoke tests in `ProcessMemoryProbeTests.cs` (positive value, plausible range, ≥ 1 MB sanity floor).

### Changed

- **`MergeAndWrite`** computes `effectiveBufferSize` at run start via `ProcessMemoryProbe.AvailablePhysicalBytes() × budgetFraction` where `budgetFraction` defaults to 0.25 (overridable via `MERCURY_READAHEAD_BUDGET_FRACTION` env var, range 0–1). Iteratively halves from 4 MiB → 256 KiB until `chunkCount × 2 × bufferSize ≤ maxReadAheadBytes`. If the floor doesn't fit, readahead is disabled and the synchronous fallback path takes over. The decision is logged via `ReadAheadBudgetEvent` regardless of outcome.
- **`ChunkReadAheadDispatcher`** constructor signature extended with `readAheadBufferSize` parameter (defaulted to `ChunkReadAheadBuffer.DefaultBufferSize` for callers that don't care).
- **`ChunkReader` (inside `SortedAtomStoreExternalBuilder`)** constructs each chunk's `ChunkReadAheadBuffer` at `_dispatcher.ReadAheadBufferSize` instead of the hard-coded default.

### Validation

- 550 Storage tests green (547 + 3 new `ProcessMemoryProbe` tests).
- End-to-end verified on a 10M-triple bulk-load: substrate probes 102 GiB available, computes 25.6 GiB budget, accepts 4 MiB default (2 chunks × 2 × 4 MiB = 16 MB ≪ 25.6 GiB). JSONL `merge_readahead_budget` record emitted as specified.
- macOS `ProcessMemoryProbe` returns 106 GiB on a 128 GB host (correctly excluding 22 GB of in-use anonymous memory); BCL fallback returns the full 128 GB total, motivating the macOS-specific path.

### What's queued for next ADR-040 slice

- Part 2 — Lazy back-buffer allocation. `ChunkReadAheadBuffer` constructor allocates only `_front`; `_back` is lazily allocated on first `FillBack` call. Reduces standing footprint when chunks consume their `_front` content before any refill is needed.
- Part 3 — Eager per-chunk teardown. `ChunkReader` disposes its `ChunkReadAheadBuffer` when `IsExhausted` becomes true, rather than waiting for end-of-merge.
- Periodic `ReadAheadFootprintSampleEvent` emission during merge (tracks live buffer allocations).

### ADR-042 Part 5 (host-adaptive validation)

`ProcessMemoryProbe` shipped here unblocks ADR-042 Part 5 — the projected-peak vs available-bytes warning that the MPHF build can now emit before consuming 89-102 GB of RSS on a constrained host. Implementation will follow with the ADR-042 Completed milestone.

## [1.7.62] - 2026-05-16

**Headline:** ADR-042 Parts 2 + 3 — second slice of MPHF construction memory adaptive sizing. Both algorithmic restructures preserve byte-equivalent output (level-by-level placement and translation entries match pre-1.7.62 builds exactly) while eliminating the two largest in-memory allocations of MPHF construction.

Part 3 — Re-hash second pass:
- Pre-1.7.62 the per-level build allocated a `keyPositions = new ChunkedArray<long>(remainingCount)` to store hashed positions across three passes (collide detect → place → translate). At level 0 with N=4B keys this allocation alone was 32 GB.
- 1.7.62 shape: two passes over keys + one linear scan of bit vectors. Pass 1 populates `seen`+`collided` bit vectors only (no positions stored); the placement bit-vector `bv` is derived via bit-vector arithmetic (`bv[w] = seen[w] & ~collided[w]`) in one O(bitCount/64) scan; Pass 2 re-hashes each key and writes its translation entry or bumps it.
- Re-hash cost: ~3 ns/key/pass × N = ~12 s extra CPU at level 0 for 4B keys. Negligible vs the ~50 min level-0 wall-clock.

Part 2 — Mmap-backed streaming translation:
- Pre-1.7.62 the build allocated a `translation = new ChunkedArray<long>(keyCount)` (32 GB persistent at 4B atoms × 8 bytes/long) and returned it as `BuildResult.Translation`; `SortedAtomStoreExternalBuilder.BuildMphfFiles` then wrote it to `atoms.idx` via `MphfTranslationTable.WriteTo` in 1 MB batches.
- 1.7.62 production path: `MphfTranslationTable.CreateForWriting(path, atomCount)` pre-allocates the full `atoms.idx` file (16 byte header + 4 × N bytes data) via `FileStream.SetLength`, mmaps it as ReadWrite, and exposes an `IMphfTranslationSink`. The new `BBHashBuilder.Build(keyCount, maxKeyByteLength, getKey, sink, listener)` overload writes each `(mphf_pos, input_idx)` pair directly to the mmap as positions are assigned. The 32 GB anonymous-memory ChunkedArray is gone; the kernel page cache handles working-set residency under memory pressure.
- File format is unchanged. The pre-1.7.62 `MphfTranslationTable.WriteTo(path, ChunkedArray<long>)` is preserved for tests that use the BuildResult-returning Build overload.

### Added

- **`IMphfTranslationSink`** internal interface in `BBHashBuilder.cs`: pluggable translation-entry receiver, declared once per `(mphf_pos, input_idx)` pair.
- **`BBHashBuilder.Build(long, int, GetKeyDelegate, IMphfTranslationSink, IObservabilityListener?)`** — Span + sink overload that writes to a caller-provided sink and returns just `BBHash` (no `BuildResult.Translation`).
- **`MphfTranslationTable.CreateForWriting(string path, long entryCount)`** — pre-allocates the `atoms.idx` file at full size, mmaps it for ReadWrite, and returns an `MphfTranslationWriter`.
- **`MphfTranslationWriter`** — `IDisposable` wrapper exposing the mmap'd view as an `IMphfTranslationSink` via `AsSink()`. Dispose flushes the mmap.
- One new validation test in `BBHashTests.cs` — `Build_SinkApi_WritesSameEntriesAsChunkedArrayPath` pins that the sink overload writes the same translation entries as the legacy `BuildResult.Translation` path on the same key set + same seed.

### Changed

- **`BBHashBuilder.Build` internals** restructured: extracted private `BuildToSink(...)` worker that takes an `IMphfTranslationSink`; the existing `BuildResult`-returning overload now creates an internal `ChunkedArraySink(translation)` and delegates to `BuildToSink`. Behavior is byte-equivalent to pre-1.7.62 from the caller's perspective.
- **`SortedAtomStoreExternalBuilder.BuildMphfFiles`** switched to the sink-based path: creates `atoms.idx` via `MphfTranslationTable.CreateForWriting` before calling `BBHashBuilder.Build` with the writer's sink. No in-memory ChunkedArray translation; no separate `WriteTo` step after Build.

### Validation

- 16 BBHash/Mphf tests green (15 pre-existing + 1 new sink-based test).
- 547 Storage tests green.
- Output byte-equivalence preserved via the sink-vs-BuildResult equivalence test.

### ADR-042 cumulative impact (Parts 1 + 2 + 3 + 4 shipped 1.7.60 → 1.7.62)

Projected peak MPHF-phase RSS at 4 B atoms:

| Structure | pre-ADR-042 | post-ADR-042 |
|---|---:|---:|
| `remaining = ChunkedList<long>` (level 0 input) | 32 GB | 0 (range iterator) |
| `translation = ChunkedArray<long>` (persistent) | 32 GB | 0 (mmap'd `atoms.idx`) |
| `keyPositions = ChunkedArray<long>` (per-level peak) | 32 GB | 0 (re-hash) |
| Per-call `byte[]` allocations from `GetAtomSpan().ToArray()` | ~770 GB GC churn | 0 (Span API + 64 KB scratch) |
| BitVectors at level 0 (γ=2.0, ~bitCount/8 × 3) | ~3 GB | ~3 GB |
| `bumped` (level 0 → level 1 transient) | ~12 GB | ~12 GB |
| **Total in-memory peak** | **~100 GB** | **~15 GB** |

The `atoms.idx` file (16 GB on disk at 4B atoms) is mmap-backed — pages occupy RAM only when accessed. Under memory pressure the kernel can reclaim them.

Part 5 (`ProcessMemoryProbe` + host-adaptive validation) is shared infrastructure with ADR-040 and is queued for the ADR-040 implementation slice.

## [1.7.61] - 2026-05-16

**Headline:** Defensive scratch sizing for ADR-042 Part 4. The pre-push audit on 1.7.60 caught that the 4 KB scratch buffer sized for "typical Wikidata atoms" was below the practical worst-case bound: Mercury's parsers cap term-output buffers at 16 K chars × max 4 bytes/char UTF-8 = 64 KB. An outlier atom larger than 4 KB would have triggered `ArgumentException` on `CopyTo(scratch)`. 1.7.61 raises the scratch to 64 KB and adds a per-call fallback that returns the source span directly via `(ReadOnlySpan<byte>)keyBytes` when even 64 KB is exceeded (in-practice impossible at parser-bounded scales, defensive belt-and-braces).

### Fixed

- **`BBHashBuilder.Build(long, Func<long, byte[]>, ...)`** legacy wrapper — scratch sized 4 KB → 64 KB, plus a guarded fallback when `keyBytes.Length > scratch.Length` returns the source array directly. Preserves the historical Func<long, byte[]> contract (which accepted arbitrary-size byte[]) without surprise.
- **`SortedAtomStoreExternalBuilder.BuildMphfFiles`** — scratch sized 4 KB → 64 KB. Outlier-path returns `span.ToArray()` via implicit conversion to `ReadOnlySpan<byte>`. Matches Mercury's parser-bounded atom byte-length envelope.

### Audit notes

Pre-push audit verified for **int vs long correctness** across the ADR-042 Parts 1+4 surface:
- `keyCount`, `remainingCount`, `bitCount`, `globalOffset` — all `long`. ✓
- `remaining`, `bumped` — `ChunkedList<long>` (long-indexed). ✓
- `keyPositions`, `translation` — `ChunkedArray<long>` (long-indexed). ✓
- Loop counter `k` — `long`. ✓
- Branch-on-level `(k + 1)` arithmetic — `long`. ✓
- `(int)denseRemaining` cast — bounded by `MaxDenseKeys = 1024` checked before cast. ✓
- `maxKeyByteLength` parameter — `int`, bounded at 64 KB ≪ int.MaxValue. ✓

No int-vs-long bugs introduced by Parts 1+4. The cycle-10 r3 `OverflowException` class of bug (which motivated `SkyOmega.Bcl` long-indexed structures in 1.7.53) is structurally prevented by the existing long-indexed primitives.

### Validation

- 15 BBHash/Mphf tests green (including the two ADR-042 equivalence tests from 1.7.60).
- 546 Storage tests green.

## [1.7.60] - 2026-05-16

**Headline:** ADR-042 Parts 1 + 4 — first slice of MPHF construction memory adaptive sizing. The `BBHashBuilder.Build` algorithm now (1) iterates the level-0 input range [1..N] directly rather than materializing it into a `ChunkedList<long>` (saves 32 GB at N=4B atoms), and (2) accepts a Span-based `GetKeyDelegate` that fills a caller-owned scratch buffer instead of allocating a fresh `byte[]` per call (saves ~770 GB of GC churn across the multi-pass build at 4 B atoms). The remaining ADR-042 work — Part 2 (mmap-backed streaming translation) and Part 3 (re-hash second pass eliminating per-level `keyPositions`) — is queued for the next slice.

### Added

- **`GetKeyDelegate`** in `BBHashBuilder.cs`: `delegate ReadOnlySpan<byte> GetKeyDelegate(long inputIndex, Span<byte> scratch)`. The new key-access shape for high-volume MPHF construction.
- **`BBHashBuilder.Build(long, int, GetKeyDelegate, IObservabilityListener?)`** — Span-based overload taking a maximum key byte length so the builder can allocate one scratch buffer for the entire build. Used by the production path (`SortedAtomStoreExternalBuilder.BuildMphfFiles`).
- Two new validation tests in `BBHashTests.cs`:
  - `Build_SpanApi_ProducesIdenticalResultToFuncApi` — equivalence pin between the legacy Func-based path and the Span-based path on the same key set.
  - `Build_SpanApi_ScratchBufferReuseDoesNotCorruptHash` — defensive test that intentionally fills the scratch with garbage before each key copy, verifying the builder doesn't retain a span past a single call.

### Changed

- **`BBHashBuilder.Build`** internal structure: level 0 no longer materializes `remaining = new ChunkedList<long>` populated with [1..keyCount]. Instead, the level loop branches on `levelIdx == 0` and uses `(k + 1)` directly for input-index lookup; the `remaining` list is allocated lazily as the level-0 `bumped` set when entering level 1+. Branch is constant per level, perfectly predicted, zero per-iteration cost.
- **`BBHashBuilder.Build(long, Func<long, byte[]>, ...)`** is now a thin wrapper around the Span-based overload. Existing callers (tests, ad-hoc usage) continue to work unchanged; the wrapper copies the legacy byte[] into the scratch on each call. Behavior is byte-equivalent to the pre-1.7.60 implementation.
- **`SortedAtomStoreExternalBuilder.BuildMphfFiles`** switched to the Span-based `BBHashBuilder.Build` overload with `maxKeyByteLength: 4096`. Per call: `atoms.GetAtomSpan(sortedPos).CopyTo(scratch); return scratch.Slice(0, span.Length);` — no per-key allocation.

### Validation

- 14 BBHash tests green (12 pre-existing + 2 new explicit ADR-042 tests).
- 546 Storage tests green.
- Output byte-equivalence verified at test scale via the Func ⇄ Span equivalence test.

### What's queued for next ADR-042 slice

- Part 2 — Mmap-backed streaming `atoms.idx` translation (eliminates the 32 GB persistent `ChunkedArray<long>` translation).
- Part 3 — Re-hash second pass (eliminates the 32 GB-at-level-0-decaying `keyPositions` `ChunkedArray<long>`).
- Part 5 — `ProcessMemoryProbe` (shared with ADR-040) and host-adaptive validation.

After all four substrate parts ship, ADR-042 moves Accepted → Completed and the projected peak RSS drops from 100 GB at 4 B atoms to ~15 GB (BitVectors + `bumped` transient).

## [1.7.59] - 2026-05-16

**Headline:** Targeted N-Triples parser optimization. `NTriplesStreamParser.Peek()` was missing `[MethodImpl(MethodImplOptions.AggressiveInlining)]` — the same annotation `TurtleStreamParser.Peek()` has carried since 1.7.4. Adding it produced a measured **+6.0 % end-to-end / +7.4 % steady-state** improvement on 10M-triple bulk-load. The remaining ~25 % steady-state gap (Turtle 730K tps vs N-Triples 583K tps at Wikidata shape) is grammar-inherent: N-Triples reads ~6× more source bytes per triple than prefix-resolved Turtle.

### Changed

- **`NTriplesStreamParser.Peek()` and `PeekAhead()`** annotated with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`. The hot path through `ParseIriRefSpan` (lines 319-396) calls `Peek` once per IRI byte — ~38 calls for a typical Wikidata IRI vs ~6 for the prefix-resolved Turtle equivalent. Inlining the bounds-check + array index eliminates per-call dispatch overhead.

### Validation

- 137 N-Triples-related tests in Mercury.Tests green.
- 10M-triple bulk-load comparison: avg 360,889 → 382,425 triples/sec (+6.0 %). Steady-state mid-run samples 543,394 → 583,548 tps (+7.4 %). Limits register entry [ntriples-parser-per-triple-perf](docs/limits/ntriples-parser-per-triple-perf.md) moves Latent → **Resolved-Partially**.

### Methodology note

The Tier 2 profile (intended via `dotnet-trace` + `cpu-sampling`) revealed that `cpu-sampling` is `collect-linux`-only; the `dotnet-sampled-thread-time` profile on macOS reports nearly 100 % `UNMANAGED_CODE_TIME` on I/O-mixed workloads because samples land on the kernel side of `read()` / `recv()`. Replacement methodology: **steady-state rate measurement** from the existing 100K-triple-interval `load.progress` JSONL records, plus **structural code-read comparison** with the Turtle parser's `Peek()` annotation. Combined evidence was sufficient to ship the bounded fix; deeper hot-method attribution (Apple Instruments.app / `xctrace`) is queued for future rounds if/when Options B (vectorized `IndexOfAny` IRI scan) or C (`ConsumeNonNewline` specialization) become candidates.

### References

- [N-Triples parser profile + decision (2026-05-16)](docs/validations/ntriples-parser-profile-2026-05-16.md) — full measurement + structural analysis.
- [ntriples-parser-per-triple-perf](docs/limits/ntriples-parser-per-triple-perf.md) — limits register entry, now Resolved-Partially.

## [1.7.58] - 2026-05-16

**Headline:** Ships ADR-041 cleanup-on-exception for bulk-tmp intermediates. The cycle-10 r3 incident pattern (BBHash `OverflowException` 2026-05-10 1.7.52, MPHF non-convergence 2026-05-11 1.7.54) left ~1.2 TB of intermediate chunk files orphaned after a Finalize-time exception, requiring manual `rm -rf` before retry. 1.7.58 closes this operationally: cleanup fires uniformly on both success and exception paths, with a `MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1` env var preserving the existing behavior for diagnostic sessions.

### Added

- **`BulkTmpCleanupEvent`** structured event emitted once per `MergeAndWrite` invocation. Includes `Trigger` ("merge_success" | "merge_exception" | "manual_rebuild"), `ChunksDeleted`, `ChunkBytesReclaimed`, `ElapsedDuration`, `AnyDeleteFailures`, and `FirstFailureMessage`. JSONL records emit under `phase: "bulk_tmp_cleanup"` for per-cycle attribution after the substrate becomes uniform across success and exception paths.
- **`MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1` env var.** When set, chunk files are preserved on merge-phase exception (forensic mode). Default behavior cleans unconditionally — matches the pre-1.7.58 success-path contract uniformly extended.
- Four new validation tests in `Mercury.Tests.Storage.SortedAtomStoreExternalBuilderTests`:
  - `BulkTmpCleanup_SuccessPath_EmitsMergeSuccessTrigger`
  - `BulkTmpCleanup_MergeException_CleansAndEmitsMergeExceptionTrigger`
  - `BulkTmpCleanup_PreserveFlagSet_KeepsChunksOnException`
  - `BulkTmpCleanup_NonBulkTmpPath_FailsAssertion`

### Changed

- **`MergeAndWrite` cleanup loop now asserts** that every chunk file path contains `bulk-tmp` as a directory segment. Fail-fast `InvalidOperationException` defends against a future refactor accidentally passing output paths (`atoms.atoms`, `atoms.offsets`, `atoms.mphf`, `atoms.idx`) into `chunkFiles`. Per ADR-041 Part 2.
- **Default tempDir conventions updated.** `BuildExternal` default tempDir is now `<TempPath>/bulk-tmp/sorted-atom-build-<guid>` (was `<TempPath>/sorted-atom-build-<guid>`). `SortedAtomBulkBuilder` default tempDir is now `<TempPath>/bulk-tmp/sorted-atom-bulk-<guid>` (was `<TempPath>/sorted-atom-bulk-<guid>`). Both now satisfy the segment assertion by construction without requiring callers to wire the convention.
- **`QuadStore.FinalizeSortedAtomBulkIfPresent` now wraps `bulkBuilder.Finalize()` and the EnumerateResolved replay loop in try/finally so `bulkBuilder.Dispose()` always runs.** This closes the cycle-10-r3 actual root cause: on `bulkBuilder.Finalize()` exception (BBHash failure), the `bulkBuilder.Dispose()` call lived on the success path. The Dispose cleans the SortedAtomBulkBuilder's `_tempDir` (including the resolveSorter's `bulk-tmp/sorted-vocab/assigned-ids-resolver/` subdirectory) — without it, the ~1.2 TB resolver-spill chunks were orphaned even though the `MergeAndWrite` internal finally cleaned its chunkFiles.

### Fixed

- **Cycle 10 r3 incident pattern fully closed.** The combination of `MergeAndWrite` cleanup-on-exception (already in finally; this release adds the `BulkTmpCleanupEvent` for visibility) + the new `BulkBuilder.Dispose()` guarantee in `QuadStore.FinalizeSortedAtomBulkIfPresent` means a Finalize-time exception (BBHash overflow, non-convergence, or any other post-merge failure) reclaims all bulk-tmp residue without operator intervention. The `mercury --rebuild-mphf` recovery path's "after rebuild, operator still has to clean manually" footgun is gone.

### Validation

- All 4 new ADR-041 cleanup tests green.
- 540 Storage tests green after rename of test tempDir paths to use `bulk-tmp` as a path segment (mechanical, matches the production convention).

### Documentation

- **WDBench aggregate distribution table shipped** ([docs/validations/wdbench-aggregate-distribution-2026-05-16.md](docs/validations/wdbench-aggregate-distribution-2026-05-16.md)) — publication-grade percentile synthesis across the complete 5-category × 2-substrate paired matrix (5,316 queries, 1.7.57 substrate generation). Headline: median 69 ms (full) / 62 ms (truthy); p99 ≈ 52 s; 52.2 % completion rate; **0 failed**. Closes the Tier 2 publication-prep deliverable.
- `externalsorter-fd-pool-bypass.md` limits doc retracted as a false alarm (code review confirmed the FD pool IS engaged on the trigram-drain path via `ExternalSorter.ChunkReader.RefillBuffer → BoundedFileStreamPool.Get`).

### References

- [ADR-041](docs/adrs/mercury/ADR-041-cleanup-on-finalize-exception.md) — moved Proposed → **Completed (2026-05-16)**.

## [1.7.57] - 2026-05-11

**Headline:** Closes the listener wire-through gap that made 1.7.56's MPHF instrumentation silently no-op for `--rebuild-mphf`. Audit pass over every `IObservabilityListener? listener = null` method confirms this was the only category-error gap. Adds a regression test that pins the contract.

### Fixed

- **`QuadStore.RebuildMphf` was ignoring its own `_observabilityListener` instance field.** The CLI sets `pool.Active.ObservabilityListener = jsonlListener` once at startup (Program.cs:364), but `pool.Active.RebuildMphf()` at line 520 was passing no listener, and `RebuildMphf(listener = null)` did NOT fall back to the instance field — silently passing null down the chain. Result: zero MPHF events emitted during `--rebuild-mphf` despite the 1.7.56 instrumentation being correctly plumbed through `BBHashBuilder` and `BuildMphfFiles`. Two-part fix:
  - `QuadStore.RebuildMphf` now uses `listener ?? _observabilityListener` so the instance-set listener is honored when no explicit listener is supplied (matches the pattern at SortedAtomBulkBuilder construction).
  - `Program.cs:520` passes `jsonlListener` explicitly (belt-and-suspenders — clearer to future readers, robust against future API refactors).

### Added

- **Regression test** `RebuildMphf_NoExplicitListener_UsesInstanceObservabilityListener` (`tests/Mercury.Tests/Storage/SortedAtomStoreCliPlumbingTests.cs`). Builds a small Reference store, deletes `atoms.mphf` + `atoms.idx` to simulate the post-crash recovery scenario, reopens, sets the listener as an instance property, calls `RebuildMphf()` WITHOUT a listener argument, and asserts the listener receives `MphfBuildStartedEvent` + `MphfLevelCompletedEvent` + `MphfBuildCompletedEvent`. Pins the contract so a future refactor can't reintroduce the silent no-op.

### Audit

Exhaustive pass over every method accepting `IObservabilityListener? listener = null` confirms `QuadStore.RebuildMphf` was the only critical gap:

| Method | Caller | Listener flow | Status |
|---|---|---|---|
| `SortedAtomBulkBuilder` ctor | `QuadStore.cs:669` | `listener: ObservabilityListener` instance | ✅ |
| `MergeAndWrite` | `SortedAtomBulkBuilder.cs:391` | `_listener` field | ✅ |
| `MergeAndWrite` | `BuildExternal` (line 175) | NONE — non-production (tests only) | ⚠️ documented |
| `RebuildMphfFromSealedStore` | `QuadStore.cs:1226` | passes through param (gap was upstream) | ✅ |
| `QuadStore.RebuildMphf` | **`Program.cs:520`** | **was: ignored instance, now: falls back to it** | ✅ FIXED |
| `BBHashBuilder.Build` | `BuildMphfFiles` (line 686) | YES (1.7.56 wiring) | ✅ |

### Background

The 2026-05-11 cycle 10 Phase 3 incident: 1.7.56 added MPHF instrumentation to `BBHashBuilder` + `BuildMphfFiles`, but the `--rebuild-mphf` CLI entry point silently dropped the listener at `QuadStore.RebuildMphf`. The recovery run (after the deploy-during-running-process crash) consumed 48 minutes producing only state records (gc/loh/rss/disk_free) with zero MPHF-specific events. The 1.7.56 instrumentation surface was correct; the wire-through gap was at the CLI/QuadStore boundary. This release closes it and audits the broader pattern.

### Validation

- 4402 Mercury.Tests pass (was 4401; +1 regression test).
- Full solution builds clean with no warnings.
- Global tool installed at 1.7.57.

---

## [1.7.56] - 2026-05-11

**Headline:** MPHF construction instrumented end-to-end. ADR-039 attribution gap surfaced mid-flight by cycle 10 Phase 3 — the substrate had zero MPHF-specific metric emission, so per-level convergence, dense-fallback engagement, and per-level wall-clock were invisible. Four new JSONL event records close the gap. The in-flight cycle 10 Phase 3 process (loaded with 1.7.55) keeps its mapped binary, so 1.7.56's value materializes for `mercury --rebuild-mphf` post-cycle re-runs and all subsequent MPHF construction.

### Added

- **MPHF event surface in `IObservabilityListener`** (`src/Mercury.Abstractions/MphfMetrics.cs`, `IObservabilityListener.cs`). Four new event structs + listener methods:
  - `MphfBuildStartedEvent` — atom count, γ, MaxLevels, MaxDenseKeys, base seed.
  - `MphfLevelCompletedEvent` — per-level convergence: index, remaining at entry, bit count, placed, bumped, level duration. Closes the per-level opacity gap.
  - `MphfDenseFallbackEvent` — fires only when the iterative phase leaves keys un-placed; carries dense key count + levels used.
  - `MphfBuildCompletedEvent` — atom count, level count, dense count, file sizes, build + total durations.
- **JsonlMetricsListener handlers** for all four events (`src/Mercury/Diagnostics/JsonlMetricsListener.cs`). Each emits as a `phase=mphf_*` JSONL record with `record_kind=event`. `mphf_level` records carry a precomputed `placement_ratio` (`placed / remaining_at_entry`) — expected ≈ 1 − e^(−1/γ) ≈ 0.63 at γ=2.0; meaningful drift indicates hash-distribution problems on the input set.
- **`BBHashBuilder.Build` accepts an optional `IObservabilityListener`** (`src/Mercury/Storage/Mphf/BBHashBuilder.cs`). Per-level Stopwatch + listener callbacks at level completion + dense engagement. Default null preserves the original signature; existing call sites and tests need no changes.
- **`SortedAtomStoreExternalBuilder.BuildMphfFiles` threads the listener through** to the builder and emits the completed event with on-disk file sizes after writing `atoms.mphf` and `atoms.idx`. The existing stderr `[mphf]` summary line is kept for human-tail visibility; structured events go to JSONL.
- **Two new BBHash listener tests** (`tests/Mercury.Tests/Storage/Mphf/BBHashTests.cs`):
  - `Build_WithListener_EmitsStartLevelAndCompleteEvents` — confirms start + per-level events fire with correct shape (remaining strictly decreases, placed + bumped == remaining at each level).
  - `Build_WithListener_DenseFallback_EmitsDenseFallbackEvent` — confirms dense-fallback event fires when forced via low `MaxLevels`.

### Background

The cycle 10 plan attributes per-fix wins via cycle 9 baseline + ~55 min MPHF − any B1/B2/B3 wins. Without MPHF-specific timing, that "+~55 min" assumption was unfalsifiable from this run alone — total rebuild wall-clock alone would aggregate MPHF construction over secondary-index construction with no causal evidence. The gap surfaced when grepping the in-flight bulk JSONL for MPHF events returned zero hits: 1.7.55 had zero structured MPHF emission anywhere in `BBHashBuilder`, `BBHash`, or `MphfTranslationTable`. A previous TODO in `BuildMphfFiles` ("add a structured event in a follow-up if cycle 10 results warrant") deferred the work; cycle 10 results warrant.

### Validation

- 12 Mercury.Tests BBHash tests pass (was 10; +2 new listener-event tests).
- Full solution builds clean with no warnings.
- Global tool installed at 1.7.56; running cycle 10 Phase 3 process unaffected (binary loaded at process start, file overwrite doesn't reach loaded image).
- Post-cycle `mercury --store wiki-21b-ref-r3 --rebuild-mphf --no-repl --metrics-out <file>` will produce the full per-level breakdown against the production 21.3 B sealed atom store; in-flight cycle 10 Phase 3 retains the coarse `[mphf]` stderr signal (total wall-clock + level count) for cross-check.

---

## [1.7.55] - 2026-05-11

**Headline:** BBHash MPHF construction made deterministic at any N via dense final-level fallback + `MaxLevels` 24→40. Surfaced by cycle 10 Phase 3 second failure (2 keys still bumped after 24 iterative levels). Adds `mercury --rebuild-mphf` recovery surface so a 12-hour parser+merge investment isn't lost to MPHF-only failures. Plus ADR-040 / ADR-041 for the substrate-discipline follow-ups (Proposed).

### Fixed

- **`BBHashBuilder` non-convergence at production scale.** At γ=2.0, expected levels for N=4 × 10⁹ ≈ 23.7 — the prior `MaxLevels=24` sat at the *expected* value, not the worst case, so ~50 % of runs would land over the threshold. Cycle 10 Phase 3 (2026-05-10) reached parser-end + merge-end (atoms.atoms 99 GB byte-identical to cycle 9) only to fail in `BuildMphfFiles` with `"2 keys still bumped after 24 levels"`. Substrate documentation flagged the risk and deferred the fix — the deferral cost 12 hours of recoverable work.
- Two-part structural correction:
  - `MaxLevels` 24 → 40 (defense-in-depth: probability of needing 40+ levels at any reasonable N is effectively zero).
  - **Dense final-level fallback** per the BBHash paper. Any keys still un-placed after the iterative phase are stored in `BBHash.DenseKeys` (a flat `byte[][]` set, bounded by `MaxDenseKeys = 1024`). `Lookup` chains: iterative levels → dense linear-scan (exact byte compare, no false positives) → -1 (definitively not in set). Convergence is now deterministic at any N — the substrate's own predicted "pathological seed" failure mode is structurally closed.

### Added

- **`mercury --rebuild-mphf` CLI subcommand** (`src/Mercury.Cli/Program.cs`). Opens an existing sealed `SortedAtomStore` and reruns `BuildMphfFiles` standalone. Saves the 12+ hours of parser + merge work that would otherwise be re-done after an MPHF-only failure. Recovery flow: kill failed bulk-load → `mercury --store X --rebuild-mphf --no-repl` → MPHF + idx written → `mercury --store X --rebuild-indexes --no-repl` → Phase B complete.
- **`QuadStore.RebuildMphf(IObservabilityListener?)`** (`src/Mercury/Storage/QuadStore.cs`). Public method that delegates to a new `internal static SortedAtomStoreExternalBuilder.RebuildMphfFromSealedStore`. Asserts SortedAtomStore-backed (Reference profile); throws clearly for Hash-backed stores.
- **`BBHash` file format Version 2.** Adds `[u64 dense_offset][u32 dense_count][per dense key: u32 length + bytes]` tail after the iterative level data. Version 1 readers explicitly rejected (v1 files never reached production — the 1.7.52 v1 codepath only crashed before any v1 MPHF file persisted).
- **Three new BBHash tests** (`tests/Mercury.Tests/Storage/Mphf/BBHashTests.cs`):
  - `Build_DenseFinalLevel_HandlesUnconvergedKeys` — forces dense path via `MaxLevels=4`, validates correctness + uniqueness.
  - `Build_DenseFallback_RoundTripsThroughSerialization` — write/read round-trip preserves dense keys + lookup behavior.
  - `Build_DenseFallback_ExceedsMaxDenseKeys_ThrowsWithDiagnostic` — fail-fast when dense set overflows `MaxDenseKeys`.
- **ADR-040 — Readahead Memory Adaptive Sizing** (Proposed). Substrate-adaptive readahead budget at MergeAndWrite start; lazy back-buffer allocation; eager teardown on chunk exhaustion; `ReadAheadBudgetEvent` + `ReadAheadFootprintSampleEvent` observability; `ProcessMemoryProbe.AvailablePhysicalBytes()` via P/Invoke. Implementation deferred to cycle 11.
- **ADR-041 — Cleanup-on-Exception for Bulk-Tmp Intermediates** (Proposed). Try/finally around bulk-tmp consumers in `MergeAndWrite` so cleanup fires on both success and exception paths. Resolves the 1.2 TB orphaning observed in both cycle 10 Phase 3 failures (1.7.52, 1.7.54). `MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1` opt-out for diagnostic sessions. Implementation deferred to cycle 11.

### Validation

- 4399 Mercury.Tests pass (was 4396; +3 new dense-fallback tests).
- 12 SkyOmega.Bcl.Tests pass.
- Cycle 10 Phase 3 recovery: planned via `mercury --rebuild-mphf wiki-21b-ref-r3` against the existing 99 GB sealed atoms — runs MPHF construction only, expected ~15-30 min vs the 12+ hours that would be required to restart from latest-all.ttl.bz2.

### Notes

- The substrate's own predicted convergence-boundary risk in `BBHashBuilder.cs:23-28` (pre-1.7.55) is now structurally resolved. The deferred-debt pattern — flagging a risk but not building the structural mitigation — cost a 12-hour production run. Lesson documented in the Phase 3 retrospective.
- File-format Version bumped 1→2. The v1 path shipped in 1.7.52 / 1.7.53 / 1.7.54 but never produced a persisted MPHF file at production scale (all three failed before writing). v1 readers are not required.

---

## [1.7.54] - 2026-05-10

**Headline:** Fix B2 readahead concurrency-contract violation surfaced by external review during cycle 10 Phase 3. `ChunkReadAheadDispatcher` was sharing `BoundedFileStreamPool` across N workers in violation of the pool's single-threaded contract. Phase 3 run killed at ~28 % parser before merge could exercise the racy path; substrate corrected before resuming.

### Fixed

- **`ChunkReadAheadDispatcher` worker-local short-lived FileStreams** (`src/Mercury/Storage/ChunkReadAheadDispatcher.cs`). Workers now open `using var fs = new FileStream(req.Path, ...)` per refill instead of calling `_pool.Get(req.Path)`. `FillBack` already seeks to its tracked `_filePosition` (`ChunkReadAheadBuffer.cs:148`), so a fresh stream per refill is always at the correct offset. Per-refill `open() + seek() ≈ 12 µs`, negligible against the ~10 ms cold / ~0.3 ms cached 4 MB-buffer read. FD pressure unchanged (peak ≈ `poolSize + workerCount` ≈ 64 + 8 = 72).
- **Constructor signature change**: `ChunkReadAheadDispatcher(BoundedFileStreamPool pool, int? workerCount)` → `ChunkReadAheadDispatcher(int streamBufferSize, int? workerCount)`. Caller in `SortedAtomStoreExternalBuilder` passes `MergeFileStreamBufferSize` (8 KB).

### Added

- **`BoundedFileStreamPool` thread-affinity contract guard** (`src/Mercury/Storage/BoundedFileStreamPool.cs`). First `Get`/`Drop` claims `_ownerThreadId` via `Interlocked.CompareExchange`; any subsequent call from a different managed-thread-id throws `InvalidOperationException` naming the violator. Cost: one volatile read + int compare per call after the first. Catches the failure mode at first cross-thread access rather than at runtime corruption.
- **`BoundedFileStreamPoolTests.Get_CrossThreadAccess_ThrowsContractViolation`** — regression guard for the contract.
- **`docs/limits/readahead-pool-concurrency-contract.md`** — limits-register entry capturing this contract violation as a *resolved* substrate concern. Sister entry to `collection-element-count.md` and `runtime-fd-detection.md` — same family of "documented contract that the type system cannot enforce; needs a runtime guard at the abstraction boundary."

### Notes

- Reviewer's short-term workaround (`MERCURY_MERGE_READAHEAD=0`) was rejected per substrate-discipline call ("no shortcuts"). Structural fix shipped instead.
- Phase 3 attribution rerun against 1.7.54: B1 + B2 + B3 + ADR-037 + cleanup hook all measured cleanly. The B2 attribution is preserved (not deferred) because the fix preserves B2's architectural intent (per-chunk frontier readahead) while removing the unsafe shared mutation.

---

## [1.7.53] - 2026-05-10

**Headline:** New `SkyOmega.Bcl` project (substrate-level BCL extensions) + `BBHashBuilder` int32-overflow fix surfaced by cycle 10 Phase 3 production-run crash at ~4 B atoms. Phase 3 unblocked.

### Added

- **`SkyOmega.Bcl` project** (`src/SkyOmega.Bcl/`). Substrate-level home for BCL extensions Sky Omega needs but cannot NuGet-ify due to the substrate-independence discipline. Mercury now references it; future migrations queued for `BZip2*` decompression streams and `Varint` encoding helpers.
- **`ChunkedList<T>`** and **`ChunkedArray<T>`** (`src/SkyOmega.Bcl/Collections/`). Long-indexed, chunked storage (default 1 M elements per chunk) supporting > `int.MaxValue` element collections. No doubling-on-growth element copy (only the top-level pointer array doubles). 12 unit tests covering basic round-trip, chunk-boundary crossings, and indexing past the int32 limit.
- **`BitVector`** relocated from `SkyOmega.Mercury.Storage.Mphf` → `SkyOmega.Bcl.DataStructures` (already long-indexed, qualifies as substrate-level BCL extension).
- **`SplitMix64Hash`** in `SkyOmega.Bcl.Hashing` (relocated and renamed from `SkyOmega.Mercury.Storage.Mphf.MphfHash`). Same algorithm, broader home.
- **`docs/limits/collection-element-count.md`** — limits-register entry capturing the int32-cap class as a *resolved* substrate-level concern. Sister entry to `runtime-fd-detection.md` and `bulk-load-memory-pressure.md` — same family of "hard OS/BCL ceiling that the application surface obscures."

### Fixed

- **`BBHashBuilder` int32 overflow at production scale** (`src/Mercury/Storage/Mphf/BBHashBuilder.cs`). Cycle 10 Phase 3 21.3 B production run on 1.7.52 crashed during MPHF construction at chunk-004045 (~4 B atoms) with `OverflowException` from `new List<long>(checked((int)keyCount))`. Five distinct int32 traps in one method:
  - `new List<long>(checked((int)keyCount))` — capacity arg
  - `new long[keyCount]` translation array — int32 element cap
  - `new Dictionary<long,int>(remaining.Count)` — capacity + bucket count
  - `new long[remaining.Count]` keyPositions — same
  - `new List<long>()` bumped collector — hits cap on next bump past 2.15 B
- Rewrite uses `ChunkedList<long>` for `remaining` / `bumped`, `ChunkedArray<long>` for `translation` / `keyPositions`, and a **bit-vector pair (seen + collided)** in place of the `Dictionary<long,int>` collision counter. Bit-vector pair is bounded by `~bitCount × 2 / 8` bytes — at 4 B atoms × γ=2.0 = ~2 GB per level vs the `Dictionary<long,int>`'s estimated > 96 GB at the same scale (which would have OOM'd before reaching the int32 trap). All loop counters widened to `long`.
- `BBHashBuilder.BuildResult.Translation` signature changed `long[]` → `ChunkedArray<long>`. `MphfTranslationTable.WriteTo` signature updated correspondingly. All callers and tests updated.

### Validated

- All 4395 Mercury.Tests pass (37 BBHash/MPHF/SortedAtomStore-specific).
- All 12 SkyOmega.Bcl.Tests pass.
- Phase 3 production restart unblocked: same substrate, same algorithms, same lookup semantics; only the construction-time data structures changed.

### Notes

- This fix triggered the **collection-element-count** limits-register entry. Per `feedback_resource_limit_class_audit`, the discipline is: *every* interaction with an int32-bounded BCL collection where the count is input-derived must use the chunked equivalents. The substrate forbids "safe-N" thinking.

---

## Cycle 9 — 21.3 B Production Validation (2026-05-09)

Not a release tag, but a substantive validation milestone. Substrate `wiki-21b-ref-r2` (since deleted to free disk for cycle 10) loaded full Wikidata 21.3 B Reference + Sorted on **1.7.50** in **35 h 35 m wall-clock** (vs cycle 8's 46 h with intervention).

### Validated

- **ADR-037 → Completed.** `parser_blocked_on_spill_ms = 78.9` across 9 h 18 m parser wall-clock = **0.000236 % blocked** vs cycle 8's projected ~5 h (38 %) at sequential. Parser wall-clock 14 h 15 m → 9 h 18 m = **−4 h 57 m measured saving at 21.3 B**, matching the falsifiable hypothesis stated by [ADR-037](docs/adrs/mercury/ADR-037-pipelined-spill-bulk-builder.md). Limit [`spill-blocks-parser.md`](docs/limits/spill-blocks-parser.md) Resolved at production scale.
- **1.7.49 cleanup hook → production-validated.** `chunks_deleted: 3,923`, `chunk_bytes_reclaimed: 3,955,458,913,128 bytes` (~3.96 TB) at end-of-merge instead of end-of-run. Manual `rm -rf` intervention requirement (cycle 8) eliminated structurally. Limit [`intermediate-cleanup-deferred-to-run-end.md`](docs/limits/intermediate-cleanup-deferred-to-run-end.md) Resolved at production scale.
- **Substrate identity preserved.** Cycle 9 produced byte-identical indices to cycle 8: atoms.atoms 99 GB, atoms.offsets 30 GB, gspo.tdb 1.0 TB, gpos.tdb 1.0 TB, trigram.posts 4.6 GB, total ~2.13 TB; 4,005,235,528 atoms; 17,029,283,265 GPOS entries; 7,472,855,623 trigram entries. Pipelined-spill + cleanup-hook changes were correctness-preserving.

### WDBench cold baseline against `wiki-21b-ref-r2`

1,199 queries (660 paths + 539 c2rpqs), per-query timeout 60 s, comparable to cycle 8's 2026-04-29 baseline:

- **588 completed (vs cycle 8's 544)** — 44 hard queries that timed out in cycle 8 completed under 60 s in cycle 9.
- **10 h 44 m total wall-clock (vs cycle 8's 11 h 30 m)** — 46 min faster (~7 %), driven by the 44 fewer timeouts × ~60 s each.
- Aggregated completed-only percentiles: p25 2.69 ms, p50 65 ms, p75 966 ms, p90 8.68 s, p95 23.13 s, p99 48.24 s, max 58.57 s. p50 +44 % vs cycle 8 is *composition shift* (44 newly-completed slow queries pull median up); p75–p99 all dropped 23–32 %.
- 0 query failures, 0 parser failures across 1,199 queries on 1.7.50 — cancellation contract (1.7.46) and property-path grammar (1.7.47) stable at production scale.

### Cumulative optimization story (Phase 6 → Cycle 9)

| Cycle pair | Wall-clock | Optimization shapes (see [feedback_optimization_taxonomy](.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_optimization_taxonomy.md)) |
|---|---:|---|
| Phase 6 → Cycle 8 | 85 h → 46 h, **−39 h** | Algorithmic (Hash → Sorted atom store, ADR-034), avoiding-work (prefix compression on output, FD pool), correctness-class fixes |
| Cycle 8 → Cycle 9 | 46 h → 35 h 35 m, **−10 h 25 m** | Architectural (ADR-037 pipelined spill, −4 h 57 m measured), avoiding-work (1.7.49 cleanup hook, ~5 h avoided intervention) |
| **Phase 6 → Cycle 9 (cumulative)** | **85 h → 35 h 35 m, −58.1 %** | All measured-vs-measured per [feedback_no_projection_baselines](.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_projection_baselines.md) |

Validation doc: [docs/validations/adr-037-cycle9-21b-2026-05-09.md](docs/validations/adr-037-cycle9-21b-2026-05-09.md). Mercury observation: `urn:sky-omega:incident:cycle9-21b-complete-2026-05-08`.

---

## [1.7.50] - 2026-05-06

**Headline:** [ADR-037](docs/adrs/mercury/ADR-037-pipelined-spill-bulk-builder.md) pipelined spill in `SortedAtomBulkBuilder`. 100 M Wikidata Reference + Sorted gradient: parser-blocked-on-spill drops from 95 s (33 % of parser wall-clock) to **0.44 ms (~0 %)** — a 216,000× reduction on the load-bearing metric. End-to-end wall-clock −25.6 % (285 s → 212 s). Queue depth steady-state 0 across all 18 handoffs (bound-1 sufficient). Merge phase unchanged within ±5 %.

### Added

- **`SortedAtomBulkBuilder` pipelined-spill architecture** (`src/Mercury/Storage/SortedAtomBulkBuilder.cs`). Single background worker thread drains a `BlockingCollection<SpillJob>` at `BoundedCapacity = 1`. Parser snapshots the full buffer, swaps in a fresh List, hands the snapshot to the worker via `Add` — after which the parser never touches the snapshot. Worker exclusively owns its in-flight buffer; runs sort + write on a separate core; accumulates output paths in `_workerChunkFiles`; merges those into the parser-owned `_chunkFiles` at `Finalize`. With cycle 8's parser-fill ~13 s/chunk vs sort+write ~7–8 s/chunk, queue depth stays at 0 — the parser never blocks, exactly the falsifiable hypothesis ADR-037 stated.
- **Worker-fault propagation via `CancellationTokenSource`** (Decision 4). Worker captures any sort/write/listener exception in `_workerException`, then cancels `_faultCts`. Parser's `Add(item, _faultCts.Token)` unblocks via `OperationCanceledException`; the catch path calls `ThrowIfWorkerFaulted` which throws `InvalidOperationException` with the original exception as `InnerException`. *Initial draft used `CompleteAdding` from the worker; that races with concurrent parser `Add` and throws `InvalidOperationException("CompleteAdding may not be used concurrently with additions to the collection")`* — caught immediately by the worker-exception unit test. Race fix recorded in Mercury as `pattern:cancellation-not-completeadding-for-fault-wake`.
- **`ThrowIfWorkerFaulted` on every `AddOneAtomOccurrence`** (Decision 4). The check is the load-bearing concurrency-correctness bit — a silent loss of a sort/write exception would corrupt the resulting store. Surfaced on the parser's stack with the worker's exception chained, never lost.
- **`Dispose` shutdown contract for cancellation paths** (Decision 5). When the bulk builder is disposed without `Finalize`, `Dispose` calls `CompleteAdding` on the queue (safe because Dispose runs on the parser/owner thread, never concurrently with Add) and waits up to 30 s for the worker to drain. Verified in unit test `PipelinedSpill_DisposeWithoutFinalize_CompletesPromptly` (in practice well under 1 s).
- **`SpillEvent.QueueDepthAtHandoff`** field (`src/Mercury.Abstractions/SortedAtomMetrics.cs`). Captured by the parser immediately before `_spillQueue.Add` — the load-bearing measurement for tuning the bound. Steady-state 0 → bound-1 is sufficient and parser is never blocked. Cycle 9 will tell whether 21.3 B-scale composition holds the same.
- **`BulkBuilderCompletedEvent`** (`src/Mercury.Abstractions/SortedAtomMetrics.cs`). One-shot end-of-bulk-builder summary emitted at `Finalize`, just before `MergeAndWrite` begins. Carries `TripleCount`, `AtomOccurrenceCount`, `SpillCount`, `ParserBlockedOnSpill` (cumulative wall-time across all `Add` calls), `TotalParserWallClock` (start of first `AddTriple` to start of `MergeAndWrite`). With pipelining successful, `ParserBlockedOnSpill` drops to ≈ 0 vs the sequential version where it equaled `Σ(SortDuration + WriteDuration)`.
- **`IObservabilityListener.OnBulkBuilderCompleted`** (default no-op). The interface contract docstring now states explicitly that `OnSpill` fires from the worker thread while other bulk methods fire from the parser thread — ADR-037 Decision 6. `JsonlMetricsListener` is thread-safe via its existing internal bounded channel.
- **`docs/validations/adr-037-pipelined-spill-gradient-2026-05-06.md`** — A/B at 1 M / 10 M / 100 M against the 1.7.49 baseline. Headline numbers, queue-depth distribution (`{0: 18}` at 100 M), per-spill cost A/B (sort total +43 %, write total +60 % under pipelining — explained as expected: per-spill cost rises slightly under cross-thread contention but is hidden behind parser-fill, so net wall-clock drops), memory cost (~2.5× working set from holding the worker's snapshot concurrent with the parser's accumulator).
- **Three pipelined-spill unit tests** (`tests/Mercury.Tests/Storage/SortedAtomBulkBuilderTests.cs`):
  - `PipelinedSpill_QueueSaturation_ParserBlocksAndProducesCorrectStore` — 50 ms-delayed listener forces the worker behind; asserts `parser_blocked > 0` and store correctness.
  - `PipelinedSpill_WorkerException_SurfacesOnParserThread` — injected fault on the 2nd `OnSpill`; asserts the original surfaces on the parser stack with `InnerException` chain intact. Regression guard for the `CompleteAdding`-race fix.
  - `PipelinedSpill_DisposeWithoutFinalize_CompletesPromptly` — disposes mid-load; asserts shutdown < 30 s budget (in practice well under 1 s).

### Documented

- **[`docs/limits/spill-blocks-parser.md`](docs/limits/spill-blocks-parser.md) Triggered → Resolved (gradient validated, 21.3 B pending).** ADR-037 closes the cycle 8-projected ~5 h parser wall-clock reduction at gradient scale; cycle 9 closes the production claim.
- **[`docs/limits/bz2-decompression-single-threaded.md`](docs/limits/bz2-decompression-single-threaded.md) — 2026-05-06 wiring experiment recorded.** After 1.7.50 landed, parser consumption rose to ~31 MB/s at 100 M — close to the 33 MB/s single-threaded bz2 ceiling. Hypothesis: wire `ParallelBZip2DecompressorStream` (workerCount=4) into `LoadFileAsync`'s bulk-load path to restore producer-side headroom. **Measured:** all four metrics regressed (end-to-end +11 % slower, parser_blocked 25,000× worse, merge phase +54 %, RSS 7.5×). Reverted before shipping. Likely cause: thread + memory-bandwidth contention with the spill worker; parallel decoder's output side appears un-back-pressured. Recorded in Mercury as `pattern:component-vs-system-optimization` — isolated-component speedups (ADR-036 Phase 2's clean 2.62× bz2 measurement) are not predictive of system-level effect when components share scarce host resources.

### Behavioral discipline (memory + Mercury)

- **`feedback_gradient_scope.md`** — gradient validates performance changes; correctness/disk-pressure/observability changes need unit test + production-scale validation, not gradient theatre. Caught 2026-05-06 after running the gradient on a `File.Delete`-in-finally cleanup hook with no perf signal to detect.
- Mercury observation `obs:adr037-pipelined-spill-shipped-2026-05-06`: 100 M gradient confirms the hypothesis (`teaches pattern:cancellation-not-completeadding-for-fault-wake`).
- Mercury observation `obs:parallel-bz2-bulk-load-regresses-2026-05-06` + `pattern:component-vs-system-optimization`: end-to-end measurement at composition scale is the load-bearing protocol.

---

## [1.7.49] - 2026-05-06

**Headline:** Round 2 #2 — phase-transition cleanup hook in `SortedAtomStoreExternalBuilder.MergeAndWrite`. Cycle 8 surfaced that consumed occurrence chunks were held until run end (3.6 TB at 21.3 B), putting the GSPO drain phase within ~600 GB of `MinimumFreeDiskSpace` abort on a 7.3 TB host. Smaller hosts would have aborted mid-flight. Fix: delete chunk files in the merge `finally` block, immediately after readers Dispose. `MergeCompletedEvent` extended with `ChunksDeleted` + `ChunkBytesReclaimed` for cycle 9+ observability.

### Added

- **Phase-transition cleanup hook** (`src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs`, `MergeAndWrite` finally block). After the merge loop completes and readers Dispose (each reader's Dispose calls `_pool.Drop(_path)` which closes the underlying FD), the chunk files are `File.Delete`'d. Best-effort try/catch — the caller's tempDir cleanup catches any stragglers. Releases disk pressure immediately at end-of-merge instead of waiting for end-of-run.
- **`MergeCompletedEvent.ChunksDeleted` + `ChunkBytesReclaimed`** (`src/Mercury.Abstractions/SortedAtomMetrics.cs`) — the cleanup is observable from JSONL without re-running with extra logging. JSONL emits `chunks_deleted` and `chunk_bytes_reclaimed` fields on `merge_completed` events.
- **Unit test `MergeAndWrite_DeletesConsumedChunks_AndReportsCounts`** (`tests/Mercury.Tests/Storage/SortedAtomStoreExternalBuilderTests.cs`) — uses a `CapturingMergeListener` to verify that `ChunkCount == ChunksDeleted` post-merge AND that the `occurrences/` filesystem directory is empty.
- **`docs/validations/intermediate-cleanup-gradient-2026-05-06.md`** — 1 M / 10 M / 100 M JSONL artefacts confirming counter wiring. Reframed as smoke + observability check, not perf gradient (the change is a `File.Delete` loop in a `finally`, off the hot path; running the gradient was over-scoped for the change class — surfaced the `feedback_gradient_scope` discipline).

### Documented

- **[`docs/limits/intermediate-cleanup-deferred-to-run-end.md`](docs/limits/intermediate-cleanup-deferred-to-run-end.md) Triggered → Resolved (in-flight; production validation = cycle 9).**

---

## [1.7.48] - 2026-05-06

**Headline:** First successful 21.3 B Reference + Sorted bulk-load **and** rebuild — substrate `wiki-21b-ref-r1` complete and verifiably queryable. ADR-034 Phase 1 closed at full Wikidata scale. Total wall-clock 46 h with intervention; ~32 h projected on a clean run with all fixes in place — **2.0× faster than Phase 6 baseline (85 h v1, retired)**.

### Added

- **`BoundedFileStreamPool`** (`src/Mercury/Storage/BoundedFileStreamPool.cs`) — bounded LRU cache of FileStream handles for the k-way merge phase. Replaces the prior architecture of "one FileStream per chunk reader, all open simultaneously" which hit the macOS launchd-applied ~10,240 FD soft limit at 21.3 B Wikidata scale (cycles 1, 4 crashed at chunk-010131 with EMFILE). Pool sized auto to `chunkCount` capped at `MergeFileStreamPoolHardCap` (8 K — chosen below macOS ~10 K effective limit, plenty of headroom for stdlib FDs). Below cap → 100% hit rate, zero evictions. Above cap → LRU eviction at the cap, completes cleanly with miss overhead. Wired into both merge paths: `SortedAtomStoreExternalBuilder.MergeAndWrite` (`b2d4e97`, `97d6ad6`) and `ExternalSorter<T>.Merge` (`dddfda3`). Cap lowered 32K → 8K in `880bfe1` after cycle 8 trigram drain crashed with the higher cap.
- **Profile-derived AtomStore dispatch** (`e8fa14f`). `StoreSchema.ForProfile(Reference)` now returns schema with `AtomStore = Sorted`; the prior code defaulted to Hash via the record-ctor default and required `--atom-store Sorted` flag to override. Cycles 1-3 silently used HashAtomStore for `--profile Reference` because the launch command dropped that flag. The `--atom-store` CLI flag is REMOVED entirely; profile is the single source of truth. Illegal combinations (Reference+Hash, Cognitive+Sorted) are unrepresentable.
- **Auto-pool-size the merge pool** (`97d6ad6`). Pool sized to `chunkFiles.Count` (zero evictions when chunks fit). Replaces fixed 64-stream cap which would have caused ~5h of miss overhead at 21.3B's 720-13K chunks. Cycle 8 atom-merge with 3,923 chunks: pool=3,923, peak_open=3,923, hits=63,949,590,286, misses=3,923 (one per chunk on initial open), **hit_rate=1.000000**.
- **Default chunk size 256 MB → 1 GB** (`4b7663c`). At 21.3 B: 17 K chunks → 4 K chunks. Single source of truth via `SortedAtomBulkBuilder.DefaultChunkBufferBytes = SortedAtomStoreExternalBuilder.DefaultChunkSizeBytes` constant reference (`07102cb`). The first attempt (`4b7663c`) only changed one of two duplicate constants — caught when cycle 5 chunk-rate matched cycle 4 exactly, fix in `07102cb` unifies them.
- **Bulk-load merge instrumentation infrastructure** (`c79e590`). Five new event types in `SkyOmega.Mercury.Abstractions`:
  - `RunConfigurationEvent` — one-shot at start of bulk-load. Discloses every load-bearing tuning + dispatch decision (profile, atomstore impl, chunk size, pool cap, resolver chunk size, disk-backed assigned ids, user pool override). Catches dispatch bugs in the second the run begins, not at the end.
  - `MergePoolState` — periodic state. Defined for future use by a periodic state producer.
  - `SpillEvent` — per-chunk spill in `SortedAtomBulkBuilder`. Carries chunk index, record count, bytes written, sort duration, write duration. Directly measures the parser-blocking spill cost rather than projecting it.
  - `MergeProgressEvent` — periodic mid-merge progress. Emitted every `MergeProgressEmissionInterval` (100 M) records during `MergeAndWrite`. Carries records_processed, atoms_emitted, resolver_records, pool stats, data_bytes_written. Closes the merge opacity gap surfaced by the cycle 6 21.3 B run (8+ h of unobservable merge progress).
  - `MergeCompletedEvent` — one-shot end-of-merge with full pool stats and totals. Replaces the ad-hoc `Console.WriteLine "[merge-pool]"` line with structured JSONL emission.
- **`docs/architecture/technical/bulk-load-flow.md`** — end-to-end ingest flow map (Reference profile, Sorted-backed). Eight stages from `.ttl.bz2` source to sealed Reference store with measured wall-clock numbers per phase. The structural map we hold visible while we work — every limits-register entry, every optimization, every "where is the wall-clock cost living" question lands somewhere on this diagram.
- **`docs/architecture/technical/observability-coverage.md`** — observability discipline. Inventories observable surfaces (Phase 7a parser/rebuild/process state) vs gaps (long-running internals, live state vs counters, startup configuration disclosure, decision/invariant approach states, failure-mode visibility). States the discipline: every operation projected to take >1 min in production must emit periodic progress before being considered shippable.
- **`docs/limits/cognitive-orchestrator-absent.md`** (`9181453`) — architectural deferral. ADR-005 names James as the cognitive orchestrator; until it exists in code, every Sky Omega session depends on a human reviewer applying EEE in real time to gate LLM expression through substrate measurement before action. Three caught-by-human cases in a single 24-hour window (atom-store dispatch bug, FD-cap raise reflex, wrong-constant chunk-size commit) demonstrate the structural risk.
- **`docs/limits/observability-coverage-gap.md`** (`39df7a0`) — register entry that gates promotion of new long-running work on the discipline.
- **`docs/limits/spill-blocks-parser.md`** (`993d207`) → promoted **Triggered** in `8864baa`. Cycle 8 measured 38% of parser wall-clock blocked on sort (3,923 spills × ~5 sec each = ~5.5 h of 14 h 15 m parser). Sort:write 12-16:1 cross-scale (10M / 100M / 1B / 21.3B). Mitigation: pipelined spill (worker thread + double buffer) — Round 2 candidate, evidence-justified.
- **`docs/limits/external-merge-intermediate-disk-pressure.md`** (`a0a6e91`) — peak intermediate disk volume (~5 TB at 21.3B). Mitigation: prefix-compress chunk records (extends ADR-034 Round 2's output-side compression to the intermediate layer).
- **`docs/limits/intermediate-cleanup-deferred-to-run-end.md`** (`7ebe8ad`) — **Triggered** during cycle 8 drain phase. 3.6 TB of post-merge occurrence chunks held until run end instead of merge end. Cycle 8 came within ~600 GB of the min-free-space abort during drain on a 7.3 TB host; smaller hosts would have aborted. Cause: `SortedAtomBulkBuilder._ownsTempDir = false` when `QuadStore` provides explicit tempDir; no phase-transition cleanup hook fires. Mitigation: phase-transition cleanup hooks at end of `MergeAndWrite` or incremental delete as `ChunkReader.MoveNext` returns false.
- **`docs/limits/trigram-drain-cap-eviction.md`** — Latent (Monitoring). Cycle 8 trigram drain processes 10,456 chunks vs 8K cap → ~23% miss rate, ~3-4 h overhead. Mitigation: larger trigram chunks (1 GB → ~2,000 chunks ≪ cap) or hierarchical merge.
- **`docs/limits/runtime-fd-detection.md`** — Latent. Hard-coded 8K cap is conservative outside macOS launchd context. Linux (typical FD limit 65K) leaves ~57K unused. Mitigation: `getrlimit(RLIMIT_NOFILE)` via P/Invoke at pool construction.
- **`docs/validations/adr-034-21b-2026-05-06.md`** (`ee265a6`) — cycle 8 validation report. All measured numbers across 8 phases. Compared to Phase 6 baseline (85 h v1 retired): 2.0× wall-clock improvement, 42% atom-store size reduction (99 GB vs ~170 GB) via prefix compression + sorted layout. Six Round 2 optimization candidates with evidence-justified estimated wins.
- **`tools/smoke-test-21b-r1.cs`** (`1ddb0ba`) — file-based dotnet script that verifies the cycle 8 substrate is queryable end-to-end. Five checks (open store, statistics, bounded SELECT, predicate-bound GPOS lookup, trigram FILTER text:match). Captures the dedup baseline: 21.32 B triples loaded → 20,935,927,828 quads stored (1.79% RDF set-semantics dedup). Future r2 ingest should land within 0.01% of this number.

### Fixed
- **Cycle 1 / cycle 4 / cycle 8 trigram-drain FD-ceiling crashes** — same crash mode at chunk-010131 across three different code paths. Fix layered in three commits:
  - `b2d4e97` — `BoundedFileStreamPool` wired into `SortedAtomStoreExternalBuilder.MergeAndWrite` (the atom-merge path).
  - `97d6ad6` — pool sized to chunk count instead of fixed 64.
  - `dddfda3` — same pool wired into `ExternalSorter<T>.Merge` (the trigram-drain path that the original fix didn't cover; surfaced as the cycle 8 trigram-drain crash).
  - `880bfe1` — cap lowered 32K → 8K. The 32K theoretical cap (based on `kern.maxfilesperproc`) is fictional in practice — macOS launchd applies ~10,240 effective FD soft limit to spawned children regardless of `ulimit -n` showing 1M+. 8K leaves comfortable headroom under that.
  - The full audit forced by Martin (`Are there more FD error risks? You obviously don't look for that? Why?`) revealed that the architectural pattern (k-way merge over many spilled chunks opening each via FileStream) was the load-bearing class, not the individual sites. Both surviving merge paths are now pool-wired with consistent sizing policy.
- **Profile-derived AtomStore — dispatch bug surfaced cycles 1-3** — `--profile Reference` silently used HashAtomStore for cycles 1, 2, 3 because the launch command lacked `--atom-store Sorted`. The atom-merge code path was running on the legacy hash-table architecture even though the docs, ADRs, and validation runs all assumed Sorted. Resolved by removing the `--atom-store` flag entirely (`e8fa14f`); Profile is now the single source of truth and illegal combinations are unrepresentable.
- **Constant duplication — chunk-size unification** (`07102cb`). The first chunk-size bump (`4b7663c` from 256 MB to 1 GB) only updated `SortedAtomStoreExternalBuilder.DefaultChunkSizeBytes`; the production bulk-load path goes through `SortedAtomBulkBuilder` which had its own duplicate constant. Cycle 5 chunk-rate matched cycle 4 exactly (911 chunks at 1.16 B → ~17 K projected) instead of the ~4 K projection — caught the bug. Fix: `SortedAtomBulkBuilder.DefaultChunkBufferBytes` is now a const-of-const reference to `SortedAtomStoreExternalBuilder.DefaultChunkSizeBytes`.

### Validated
- **First successful 21.3 B Reference + Sorted bulk-load + rebuild** (`docs/validations/adr-034-21b-2026-05-06.md`). Substrate at `~/Library/SkyOmega/stores/wiki-21b-ref-r1`: atoms.atoms 99 GB, atoms.offsets 30 GB, gspo.tdb 1.0 TB, gpos.tdb 1.0 TB, trigram.posts 4.6 GB, total ~2.13 TB. Index state Ready. 21,316,531,403 triples loaded, 20,935,927,828 quads stored after RDF set-semantics dedup (1.79%), 4,005,235,528 unique atoms.
- **Smoke test passed end-to-end:** open store 10 ms, statistics <1 ms, bounded SELECT 16 ms, predicate-bound GPOS query 1-6 ms, trigram FILTER `text:match("Stockholm")` 11 s cold returns 5 real Wikidata literal matches with proper `@nb`/`@sv`/`@pt` language tags.
- **Cross-scale sort:write ratio measured:** 12.2 : 1 (10M), 15.8 : 1 (100M), ~14:1 (1B), ~13:1 (21.3B). Sort dominates spill — pipelined-spill optimization is evidence-justified, not projection-justified.
- **Three-regime merge behavior at 21.3 B** (`urn:sky-omega:pattern:merge-three-regimes` in Mercury): warmup ~0.3 M/s, steady-state ~1.0-1.5 M/s, long-tail-cold-cache ~0.2-0.5 M/s. 1 B run merged at ~6 M/s sustained because 184 GB intermediate fit in 128 GB RAM; 21.3 B's 4 TB intermediate doesn't fit, hence the structural inflection. Pattern only emerges past the cache-fit boundary — the 1 B trace cannot surface it.
- **Auto-pool design validated at 21.3 B atom-merge:** zero evictions across 64 billion `pool.Get()` calls. Hit rate exactly 1.000000.
- **8 K cap fix validated at 21.3 B trigram drain:** 10,456 chunks > 8 K cap → ~23% miss rate, ~3-4 h overhead. No crash. Drain completes in 8 h 24 m at sustained 7.5 M/s.

### Documented
- **ADR-006: MCP Surface Discipline → Completed** (`79d6617`). Implementation verified shipped in `432f613`: `mercury_prune` removed from MCP tool surface; comment at `MercuryTools.cs:173` documents the deliberate exclusion.
- **ADR-007: Sealed Substrate Immutability → Completed** (`79d6617`). Implementation verified shipped in `432f613`: `PruneEngine.cs:44` rejects Reference profile at plan time with re-creation guidance message; `PruneEngineTests.cs:185` asserts ADR-007 reference appears in error.
- **`docs/limits/spill-blocks-parser.md` Latent (Monitoring) → Triggered** with cycle 8 measurements (38% sort-blocked, 12-16:1 sort:write cross-scale).
- **`docs/limits/intermediate-cleanup-deferred-to-run-end.md` Triggered** (cycle 8 came within ~600 GB of min-free-space abort during drain).
- **`docs/architecture/technical/bulk-load-flow.md` updated with measured-at-21.3B numbers** (Stage 5 atom-merge 15h20m, Stage 8a GPOS 60min32s, Stage 8c trigram drain 8h24m).

### Behavioral discipline (memory + Mercury)

Cycle 8 also surfaced behavioral discipline gaps that Mercury captured for future Sky Omega instances. Memory entries added:

- **`feedback_resource_limit_class_audit.md`** — every interaction with an OS-enforced resource is mandatory to mind. One occurrence is too many. Plurality is a symptom; the discipline is upstream. Three FD crashes at chunk-010131 cost days because each was treated as a local fix. Sharpened from "plurality is the danger signal" per Martin's correction 2026-05-05: the rule is mandatory minding of every interaction, not threshold-based detection of plurality.
- **`reference_resource_limits_checklist.md`** — catalog of OS-enforced ceilings (FDs, memory, threads, mmap, sockets, ephemeral ports, disk, inodes) with macOS-specific binding limits and the trust gap (`ulimit -n` 1M vs launchd-spawned process ~10K).
- **`feedback_force_third_option.md`** updated — sharpened with cycle 8's reinforcement: when offering options, force a third before deciding; two-option framings share a hidden axis; the third path requires stepping off it.

Mercury observations in graph `<urn:sky-omega:session:2026-05-05>`:
- `urn:sky-omega:pattern:resource-limit-class-audit` — the canonical pattern with three exemplar incidents (cycle 1, cycle 4, cycle 8 trigram).
- `urn:sky-omega:pattern:merge-three-regimes` — the architectural pattern surfaced by cycle 8.
- `urn:sky-omega:pattern:third-path-dimension-shift` — sibling cognitive pattern reinforced by today's audit.
- `urn:sky-omega:incident:cycle8-21b-instrumented-2026-05-04` + `urn:sky-omega:incident:cycle8-trigram-fd-crash-2026-05-05` — the run and the crash.
- Five observations cross-referencing the patterns to incidents and fixes.

## [1.7.47] - 2026-04-30

### Added
- **ADR-006: MCP Surface Discipline — Destructive Operations Excluded.** Top-level cross-cutting decision (not Mercury-specific): MCP-exposed tools must not include operations whose effects an AI shouldn't initiate autonomously. Classification framework: tools are scored along reversibility (reversible / recoverable-with-source / irreversible) × authority (informational / advisory / operative). Permitted on MCP iff `(reversible OR recoverable) AND (informational OR advisory)`. First implementation: `mercury_prune` removed from `MercuryTools.cs`. `mercury_update` retained — per-session-graph isolation makes it `recoverable / operative-with-isolation`, and the AI's reflexive-memory discipline is the substrate's reason for being. DrHook MCP surface to be audited per this ADR when the DrHook engine ADR ships. Operationalizes the governed-automation thesis at the tool-surface boundary. (`432f613`)
- **ADR-007: Sealed Substrate Immutability — Re-create, Don't Modify.** Top-level cross-cutting decision (not Mercury-specific): sealed substrates expose data via re-creation, not in-place modification. Mercury Reference is the first concrete instance. Generalizes ADR-029 Decision 7's session-API rejection to also reject pruning (which would mutate via the dual-instance copy-and-switch). To produce a filtered subset, bulk-load source data into a new Reference store with `--exclude-graphs / --exclude-predicates`; the original sealed snapshot remains queryable. ADR-034 Decision 7's single-bulk-load constraint was the structural blocker that made Reference pruning latent — `PruneEngine.Execute` now rejects at plan time with `PruneResult { Success = false, ErrorMessage = "...ADR-007..." }` pointing to the re-creation alternative. Future: applies to sealed Minerva model weights, possibly DrHook attached-process snapshots. (`432f613`)
- **PropertyPath grammar refactor — three shapes closed.** WDBench paths+c2rpqs surfaced 12 of 1,199 queries (1.0%) hitting property-path grammar combinations the W3C SPARQL 1.1 conformance suite did not exercise: Shape 1 `^(P){q}` (inverse-quantified single predicate), Shape 2 `^((A|B)){q}` (inverse-quantified alternative), Shape 3 `(^A/B)` and `((^A/B)){q}` (sequence with inverse-prefix first leg). `ParsePredicateOrPath` was structured so that only base-term primaries reached the trailing-modifier composition stage. Refactor extracts `ApplyPathExprModifiers` — every primary returns through a single composition stage that handles trailing quantifier (with proper `IsInverseGroup` flag for `^(X){q}`) plus sequence/alternative composition via the existing `CheckGroupedPathContinuation` helper. Inverse primaries now compose normally with anything that follows. New `PropertyPath.IsInverseGroup` field marks `Grouped*` AST nodes that originated from `^(X){q}` or `^iri{q}` — the runtime walker uses this to walk each inner step in inverse direction. (`1be7a4d`)
- **`ComposeQuantifiers` algebraic-collapse helper.** SPARQL transitive-closure idempotence reduces `((P){q1}){q2}` shapes at parse time so the runtime never sees nested quantifiers in property-path content. Reduction table: `any *X = *`, `+/?-mix = *`, `++ = +`, `?? = ?`. Justification per SPARQL semantics: `id ∪ P+ = P*`, `id ∪ P ∪ P² ∪ ... = P*`. Closed a W3C pp37 (Nested (*)*) regression introduced by an intermediate parser state during the refactor — caught by full SPARQL 1.1 conformance (423 tests) before commit. (`1be7a4d`)
- **PropertyPathRegressionTests — `PropertyPathShapes_ParseAndExecuteCorrectly`.** Eight assertions covering all three grammar shapes (bound-subject + Case 2 bound-object) against small fixtures. Verifies BOTH parse success AND execution correctness — protects against regressions of either kind. (`1be7a4d`)
- **PruneEngineTests — `Execute_ReferenceProfile_IsRejectedWithGuidance`.** Bulk-loads a tiny Reference store via `QuadStorePool` with `Profile=Reference`, invokes `PruneEngine.Execute`, asserts rejection with `Success=false`, asserts ADR-007 reference and `--bulk-load` re-creation alternative present in the error message, asserts `QuadsScanned=0 / QuadsWritten=0` (rejection happens before transfer begins). (`432f613`)

### Fixed
- **PropertyPath runtime walker — unified, zero-GC rewrite.** `ExecuteGroupedSequence` (forward), `ExecuteInverseGroupedSequence` (reverse), and `DiscoverGroupedSequenceStartNodes` were three near-duplicate methods that split content on `/` with only IRI-bracket depth tracking — none handled paren-wrapped content, top-level alternatives, or per-leg `^` prefixes. Replaced by a single `WalkPathContentInto` walker with recursive discrimination: top-level `|` → union over branches; top-level `/` → sequence chain (legs in reverse order for inverse-of-sequence, with each leg's direction also flipped); atomic predicate, optionally `^`-prefixed. Operator depth tracked across both `<>` (IRI brackets) AND `()` (group nesting). Zero-GC discipline restored: no `contentStr.ToString()`, all parsing on spans into `_source`; range tables in `stackalloc int[32]`; `HashSet<string>.GetAlternateLookup<ReadOnlySpan<char>>` avoids per-match `ToString()`; frontier sets (`_walkerCurrent`, `_walkerNext`) reuse fields between calls. `DiscoverGroupedSequenceStartNodes` paralleled the rewrite — `DiscoverContentStartNodes` recursively descends into path content honoring paren depth, top-level alternatives, per-leg `^`. (`1be7a4d`)
- **PropertyPath runtime — Case 2 (object-bound) binding silent failure.** `MoveNextTransitive` always emitted bindings as `(Subject = _startNode, Object = targetNode)` regardless of which end of the pattern was bound. Correct for Case 3 (`<subj> path ?x` — `_startNode` IS the subject). Wrong for Case 2 (`?x path <obj>` — `_startNode` is the bound OBJECT value, so the binding tries to match the literal object pattern against the WALKED node, which fails). Symptom on `wiki-21b-ref`: paths/00656-00659 + c2rpqs/00504 returned 0 rows in <1 ms — silent failures masquerading as completions. Real result is 39 K rows of ancestors-of-bound-object via the inverse-quantified alternative. Fix: `_startedFromObject` flag set in `InitializeTransitive` Case 2; binding swap in three sites (grouped branch + simple branch + simple-path enumerator direction); BFS frontier expansion direction flip. On `wiki-21b-ref` under 1.7.47: 00656=39,915 rows in 6.80s, 00657=39,272 in 2.20s, 00658=39,915 in 100ms, 00659=39,272 in 95ms — silent zero-row failures became real, correct, timed query work. Latent for unknown duration; W3C SPARQL 1.1 conformance suite did not exercise the shape. (`1be7a4d`)

### Validated
- **WDBench cold baseline against the hardened substrate (`wiki-21b-ref`, full Wikidata, 21.3 B triples).** 11h 30m total wall-clock (paths 5h 24m, c2rpqs 6h 6m). 1,199 queries (660 paths + 539 c2rpqs). **0 parser failures** (all 12 grammar gaps closed). 544 completed (45.4%), 655 timeouts (54.6%) — every one of 655 timeouts closed between 60.000 s and 63.620 s, cancellation contract honored at scale. Latency p25 = 4.09 ms, p50 = 45.05 ms, p75 = 1.39 s, p90 = 12.82 s, p95 = 29.85 s, p99 = 49.50 s, max = 59.82 s. Counterintuitive vs 1.7.46: completed count went DOWN (602 → 544) because 12 previously-parser-failed queries now actually execute, and 5 silent-zero-row Case 2 failures became real query work. Fidelity went up — each completion is now genuinely correct, each timeout honest. The "better" 1.7.46 numbers included silent failures masquerading as completions. Note: this is against full Wikidata, not the truthy subset that QLever / Virtuoso WDBench numbers use — see [memos/2026-04-30-latent-assumptions-from-qlever-comparison.md](memos/2026-04-30-latent-assumptions-from-qlever-comparison.md) for comparison framing. Sealed: `docs/validations/wdbench-paths-21b-2026-04-29-1747.jsonl` + `docs/validations/wdbench-c2rpqs-21b-2026-04-29-1747.jsonl`. (`1be7a4d`)

### Documented
- **`docs/limits/cancellable-executor-paths.md`** — Triggered → Resolved (1.7.46/1.7.47). Audit framework: cancellation-token coverage cannot rely on hand-curated site lists; future tooling (Roslyn analyzer or CI-time grep) should flag `while (...MoveNext())` over `TemporalResultEnumerator` without a `ThrowIfCancellationRequested()` check in scope.
- **`docs/limits/property-path-grammar-gaps.md`** — Triggered → Resolved (1.7.47). Inventory reconciled against the 1.7.47 rerun: 10 paths failures + 2 c2rpqs failures = 12 total (matches the original count, distribution shifted from the parse-only paths-only sweep).
- **Roadmap updated.** [docs/roadmap/production-hardening-1.8.md](docs/roadmap/production-hardening-1.8.md): 1.7.46 + 1.7.47 entries added to the Progress table; Phase 7c WDBench cold-baseline checkbox now references the sealed artifact.

## [1.7.46] - 2026-04-29

### Fixed
- **SPARQL property-path executor cancellation gap — phase 1: 8 sites.** WDBench cold-baseline observation: c2rpqs query 00137 reported `elapsed_us = 17,495,488,600` (4 h 51 m) for a 60 s timeout cap; the paths category lost ~547 of 660 events to the same hang shape. Static audit identified eight unbounded inner loops in property-path runtime evaluators that walked `TemporalResultEnumerator.MoveNext` without sampling the cancellation token — the token would fire, but the executor wouldn't yield until the loop completed naturally (hours, at 21.3 B triples). Sites patched: `TriplePatternScan.cs` — `ExecuteGroupedSequence` inner loop, `ExecuteInverseGroupedSequence` inner loop, `DiscoverGroupedSequenceStartNodes` whole-predicate scan, `InitializeTransitive` `discoveryEnumerator` loop, `InitializeTransitive` `allTriplesEnumerator` loop (worst offender at scale — whole-graph scan for `ZeroOrMore` reflexive bindings), `MoveNextTransitive` BFS inner loop. `SlotBasedOperators.cs` — `MoveNextSlot` main enumerator loop, `MoveNextTransitive` inner BFS loop, `TryAdvanceEnumerator` helper. Each loop body now opens with `QueryCancellation.ThrowIfCancellationRequested()` — a thread-static read with `[MethodImpl(AggressiveInlining)]`, essentially free per iteration. Bounds worst-case unbounded-hang to one B+Tree node walk plus the token check. New regression test `PropertyPathRegressionTests.TransitivePath_HonorsCancellationToken` exercises every patched site through a 500-node chain with a pre-cancelled token. (`527016f`)
- **SPARQL property-path executor cancellation gap — phase 2: 4 more sites.** First 1.7.46 rerun on the cold baseline hung indefinitely on paths/00120 despite the phase-1 fix. Static re-audit of all `while (...MoveNext())` loops in `src/Mercury/Sparql/Execution/` found four unguarded `TemporalResultEnumerator` iterations the eight-site sweep missed: `MultiPatternScan.TryAdvanceEnumerator` (sequence-decomposed property paths, primary culprit), `TriplePatternScan.ExecuteGroupedAlternativeFirstStep` (intermediates from `(A|B|...)` first leg of sequence path), `QueryResults.Patterns.cs.MaterializeAllSimple` and `MaterializeExistsQuerySimple` (FILTER EXISTS materialization). Same shape as phase 1; mechanical 1-line additions. Full SPARQL test suite green at 1,586 tests after both phases. (`963340c`)

### Added
- **WDBench rerun harness fixes.** `WdBenchRunner` correctly classifies harness-cancelled queries as `timeout` rather than `failed` (already in 1.7.45; carried into 1.7.46 reruns). Per-category JSONL output split (`wdbench-paths-21b-2026-04-29.jsonl` + `wdbench-c2rpqs-21b-2026-04-29.jsonl`) so filenames like `00001.sparql` overlapping between paths/ and c2rpqs/ don't lose category info.

### Validated
- **WDBench cold baseline against `wiki-21b-ref` — first WDBench run (pre-cancellation-fix).** 1,712 records emitted before the c2rpqs/00137 hang dominated the wall-clock. Surfaced both the cancellation gap (above) and 12 property-path parser grammar gaps (deferred to 1.7.47). Sealed as the pre-fix reference: `docs/validations/wdbench-cold-baseline-21b-2026-04-27.jsonl`. The 1.7.46 rerun (`wdbench-paths-21b-2026-04-29.jsonl` + `wdbench-c2rpqs-21b-2026-04-29.jsonl`) sits on top of the cancellation fix but BEFORE the grammar-gap closure and the Case 2 binding fix — completed: 602/1,199, parser-failures: 12, completion-rate inflated by the silent Case 2 zero-row failures (which 1.7.47 surfaces as either real completions or honest timeouts). (`527016f`, `963340c`)

### Documented
- **`docs/limits/cancellable-executor-paths.md`** — Triggered. Full root-cause analysis of the cancellation gap, observed instances on the 04-27 baseline, candidate mitigations.
- **`docs/limits/property-path-grammar-gaps.md`** — Triggered. Three shape combinations (`^(P){q}`, `^((A|B)){q}`, sequence-with-inverse-first), 12 affected queries, parse-only sweep methodology, structural-fix recommendation pointing toward a compositional `ParsePredicateOrPath` refactor.

---

## [1.7.45] - 2026-04-27

### Added
- **ADR-036 Phase 7b: BCL-only bzip2 streaming decompression.** `BZip2DecompressorStream` lands as a wholly new `src/Mercury/Compression/` subdirectory (1,453 lines). Implements CRC32, BitReader, RLE1, MTF, Huffman, and BWT inverse from the bzip2 spec — no third-party dependency. Validated end-to-end at 1 B Reference on 2026-04-27 (`docs/validations/adr-035-phase7a-1b-2026-04-27.md`): bz2 decompression at **33 MB/s steady-state** with 4× headroom over the parser's ~8 MB/s consumption. The CLI `--bulk-load` path now accepts `.ttl.bz2` directly — no upstream decompression step needed. Decompression is the streaming-source-decompression item in `docs/limits/streaming-source-decompression.md` graduating from Latent to Completed. Phase 7b Completed. (a062d86, ff46eed, 873034f, 9a9458e, 7bba720, 8e0c688)
- **ADR-034 Phase 1A through 1B-5c: SortedAtomStore for Reference.** Substrate work for QLever-style alphabetical-vocabulary atom store. Phase 1A extracts the `IAtomStore` interface and renames the existing implementation to `HashAtomStore` (`46adbf7`). Phase 1B-1+1B-2 ships the `SortedAtomStore` read-side (mmap-backed `{base}.atoms` + `{base}.offsets`, dense alphabetical IDs, binary-search lookup) plus the in-memory `SortedAtomStoreBuilder` (`2dbbcdd`). Phase 1B-3 wires profile dispatch in `QuadStore.Open` on the new `StoreSchema.AtomStore` field plus the Decision 7 single-bulk-load enforcement gate (`d217103`). Phase 1B-4 adds `SortedAtomStoreExternalBuilder` — chunked spill + k-way merge for past-RAM vocabularies (`6e20bf2`). Phase 1B-5a introduces `SortedAtomBulkBuilder` — the two-pass deferred-resolution orchestrator that buffers atoms during ingest, sorts at finalize, replays resolved (G,S,P,O) IDs into the GSPO external sorter (`1acd0ca`). Phase 1B-5b wires `SortedAtomBulkBuilder` into `QuadStore`'s `BeginBatch`/`AddCurrentBatched`/`CommitBatch` surface; CommitBatch finalizes the builder, disposes the placeholder atom store, reopens over fresh vocab files (`297a788`). Phase 1B-5c surfaces the CLI plumbing — `StorageOptions.AtomStore`, `mercury --atom-store <Hash|Sorted>` (`485acfa`). Phase 1B-5d (disk-backed AssignedIds for >100 M scale) and Phase 1B-6 (gradient validation 1 M / 10 M / 100 M against `HashAtomStore` baseline) remain.
- **ADR-035 Phase 7a Completed.** Closes the production-hardening Phase 7a metrics infrastructure ADR after the 1 B Reference end-to-end validation: 22,256 JSONL records emitted across all four metric channels (`LoadProgress`, `RebuildProgress`, atom-store events + state samplers, `ProcessState`) with correct schema and timing. (`3fa6409`)
- **WDBench cold-baseline harness** (`benchmarks/Mercury.Benchmarks/WdBenchRunner.cs`). Per-query timeout + cancellation discipline, captures elapsed time + result-row count, emits per-query and per-category JSONL summary records compatible with the existing metrics pipeline. Default 5-min timeout, configurable. (`9485d7d`)
- **`tools/fetch-wdbench.sh`** downloads the WDBench query suite from MillenniumDB (Buil-Aranda, Hernández, Hogan et al.) and splits per-query files. 2,658 queries across five categories. (`32df56d`)

### Fixed
- **SPARQL property-path planner crash on synthetic SequencePath terms.** `QueryPlanner.ComputeVariableHash` called `Fnv1a.Hash(source.Slice(term.Start, term.Length))` on every `Term` it received. SequencePath expansion synthesizes intermediate variables with `Term.Start = -(seqIndex + 200)` as a marker — there is no source-text slice to hash for those. Result: `ArgumentOutOfRangeException` 95% of the time on the WDBench `c2rpqs` category. Fix: detect `term.Start < 0` and use the negative Start as a stable hash; collisions across synthetic kinds are avoided by the +200/+400 offsets the parser already applies. Two-line fix; regression test `PropertyPathRegressionTests.SequenceWithZeroOrMore_DoesNotThrow` covers it. (`0c2f88b`)
- **`WdBenchRunner` misclassified harness-cancelled queries as failures.** When `cts.IsCancellationRequested`, the runner now emits `status: "timeout"` rather than `status: "failed"`. Aligns the metrics record with the operational truth: a 60-second cap is the timeout policy, not a query bug. (`0c2f88b`)
- **`RadixSort.SortInPlace` allocated 72 bytes per call from `stackalloc int[N] { ... }` initializer-list syntax.** ADR-032 contracts that the sort itself never allocates; rebuild paths sort millions of ~16 M-entry chunks, so 72 bytes × millions = GB-scale GC pressure that would have invalidated the ADR-032 latency story. Root cause: .NET 10 codegen for `stackalloc int[N] { initializer-list }` emits a per-call heap allocation regardless of N. Replaced with `stackalloc int[N];` + explicit indexed assignment at both ReferenceKey and TrigramEntry overload sites; allocation drops from 72 → 0 bytes/call across N ∈ {2, 10, 100, 1000}. Two zero-allocation regression tests now hold; full Mercury.Tests suite green at 4,331 passing. (`56f46be`)

### Documented
- **CHANGELOG backfill — 1.7.31 through 1.7.44 entries** for the production-hardening Phase 1-5 work, ADR-031 Dispose gate, ADR-032 radix external sort, ADR-033 bulk-load radix, and Phase 6 close-out (this commit).
- **README banner restructured** from a single dense paragraph to a scannable Phase 6 / Phase 7 hierarchy with bullet structure and a "Read more" link list. (`ae27226`)
- **STATISTICS.md and README.md refreshed to 1.7.45.** Mercury source 78,878 → 82,506 lines; Storage 9,456 → 10,949 (ADR-034 SortedAtomStore additions); Mercury.Abstractions 721 → 974 (`IAtomStore`, `AtomStoreImplementation`); +2,742 line `Diagnostics` row (Phase 7a metrics); +1,453 line `Compression` row (Phase 7b bz2). Tests 4,205 → 4,331. Removed `DrHook.Tests` row (project deleted 2026-04-06). Grand total 188,067 → 196,999. (`029c936`)
- **Article: "What Compounds — Notes on Sky Omega's First Four Months."** Peer to the 21.3 B Wikidata article. Where that one was the artifact, this one is the recipe — eight practices that produced substrate-grade output in four months of mostly-spare-time work. (`781a780`)
- **`docs/limits/per-index-subdirectory-layout.md`** entry: Mercury's flat store layout makes per-file symlinks fragile under file-replace patterns. Subdirectory-per-index (`gspo/data.tdb` instead of `gspo.tdb`) makes symlinks robust at the directory boundary. Latent until per-volume bandwidth becomes binding (WAL/data split, per-index placement, backup granularity). (`ec930c8`)

## [1.7.44] - 2026-04-22

### Added
- **ADR-035 Phase 7a: production-grade observability infrastructure.** Four metric channels under `src/Mercury/Diagnostics/` (~2,742 lines total). **Phase 7a.0** establishes the `IObservabilityListener` interface and `JsonlMetricsListener` writer wired through `QuadStore` (`1436239`). **Phase 7a.1 (Category A — rebuild progress)** emits `RebuildProgress` per-sub-phase records during `RebuildSecondaryIndexes`, with named sub-phases (`gpos.scan`, `gpos.sort`, `gpos.write`, `trigram.*`) so the silent middle hours of a 21 B rebuild become observable (`9395920`). **Phase 7a.2 (Category G — process-level state)** adds the `ProcessStateProducers` periodic sampler (GC heap, LOH, RSS, free disk space at the store path) on a configurable interval, default 0 (off) (`3ead8ed`). **Phase 7a.3 (Category B — atom-store metrics)** registers atom-store discrete events (`AtomStoreRehash`, `AtomStoreFileGrowth`) and state samplers (`AtomStoreState` — intern rate over interval, current load factor, mean probe distance) directly through `IAtomStore` so both `HashAtomStore` and the future `SortedAtomStore` can emit the same shape (`4ce1712`). The CLI `--metrics-out <file>` and `--metrics-state-interval <seconds>` flags expose all four channels uniformly. End-to-end validated at 1 B Reference: 22,256 JSONL records, schema-conformant across all channels, no observable runtime overhead.
- **ReferenceQuadIndex: BulkMode mmap floor 256 GB → 1 TB.** The 21.3 B Wikidata projection puts a single GPOS B+Tree at ~600-800 GB; the existing 256 GB initial mmap floor would have triggered ~3 grow-and-remap cycles during the bulk drain, each adding latency and disrupting steady-state I/O. Bumping the floor to 1 TB lets the 21.3 B run sail through without a remap. Cognitive profile and smaller Reference loads unaffected — the 1 TB is a mmap reservation in sparse files; physical disk usage is bounded by actual writes. (`aa35514`)

### Validated
- **Phase 6 — 21.3 B Wikidata Reference profile, end-to-end on a single laptop.** Completed 2026-04-25 22:32 at **85 h 35 m wall-clock**. 21,260,051,924 triples ingested + 17,029,283,265 GPOS entries + 7,457,242,193 trigram entries built. Bulk-load 73.93 h @ 80,091 triples/sec average; rebuild 11.65 h. Hardware: M5 Max, 18 cores, 128 GB unified memory, internal NVMe — no RAID, no add-in cards, consumer laptop. Software: .NET 10, Mercury 1.7.44, BCL-only core. Storage: ~2.5 TB physical / 4.1 TB logical mmap (sparse on APFS). Past Blazegraph WDQS reference ceiling (~12-13 B) by ~63%. Sealed-artifact query-side validation followed 2026-04-26 (`docs/validations/21b-query-validation-2026-04-26.md`): both GSPO and GPOS indexes correct at 21.3 B; cold-cache `LIMIT 10` queries in tens of milliseconds; `wdt:P31` instance-of bound queries return real Wikidata instances. **The capacity dimension of production hardening is empirical, not estimated.** Article: `docs/articles/2026-04-26-21b-wikidata-on-a-laptop.md`. Production-hardening Phase 6 milestone closed (`3628a86`).

### Documented
- **`docs/limits/` register established.** New documentation category for items past Emergence + Epistemics but pre-Engineering — surface latent issues by design rather than burying them in ADR Consequences sections that go invisible after the ADR is marked Completed. Initial seed entries: `predicate-statistics-memory.md`, `hash-function-quality.md`, `bit-packed-atom-ids.md`, `bulk-load-memory-pressure.md`, `streaming-source-decompression.md`, `rebuild-progress-observability.md`, `metrics-coverage-review.md`, `btree-mmap-remap.md`, `reference-readonly-mmap.md`, `sorted-atom-store-for-reference.md`. (`c11937a`, `f5babfd`, `09d39cc`, `b63ca7b`)
- **`docs/articles/2026-04-26-21b-wikidata-on-a-laptop.md`** — public framing of Phase 6, 169 lines, anchored on reproducible numbers. (`6ee6d3b`)

## [1.7.43] - 2026-04-22

### Added
- **ADR-033: bulk-load radix external sort.** Replaces the inline-secondary-write Reference bulk path with a radix-external-sort architecture analogous to ADR-032's rebuild path: the bulk loader buffers `(G, S, P, O)` records into chunked `ReferenceKey` arrays, spills via `RadixSort.SortInPlace` + `ExternalSorter<ReferenceKey, ReferenceKeySorter>` to `Path.GetTempPath()`, then drains the merged stream sequentially into `_gspoReference` via `AppendSorted`. Eliminates the page-cache thrash that the original GSPO+GPOS-inline path suffered from past 100 M triples (the ADR-029 gradient documented this collapse from 210 K → 31 K triples/sec). 1 B end-to-end validated: bulk + rebuild **~3h 57m baseline → 60m 36s** (3.92× combined). Three independent confirmations of the Phase 5.2 hypothesis across three code paths (GPOS rebuild, trigram rebuild, bulk load). `docs/validations/adr-033-phase5-bulk-radix-2026-04-22.md`. (`51c3776`)

## [1.7.42] - 2026-04-22

### Added
- **ADR-032 Phase 4: trigram rebuild via radix external sort.** Same shape as Phase 3 GPOS rebuild but with `TrigramEntry` (12-byte sort key: 4-byte uint Hash + 8-byte signed long AtomId) instead of `ReferenceKey`. The trigram portion of `RebuildSecondaryIndexes` previously did per-bucket allocations and write-amplified random posting-list writes; now it scans atoms once, emits `(Hash, AtomId)` records into a chunked sorter, then drains the merged stream into the trigram index in sequential order. Wall-clock 100 M Reference rebuild **457 s → 48.64 s** (9.4× faster); trigram portion 17× faster. Both indexes (GPOS via Phase 3, trigram via Phase 4) now hit NVMe sequential bandwidth — peak iostat 2,463 MB/s (7.5× the baseline 327 MB/s). Total rebuild speedup vs 1.7.38 baseline: **10.5×**. `docs/validations/adr-032-phase4-trigram-radix-2026-04-22.md`. (`49093df`)

## [1.7.41] - 2026-04-22

### Added
- **ADR-032 Phase 3: GPOS rebuild via radix external sort.** Replaces the comparator-based sort-insert (reverted in 1.7.38) with the new `RadixSort` + `ExternalSorter` chain. GPOS rebuild now scans `_gspoReference`, emits `ReferenceKey` records permuted to GPOS order, sorts via radix into chunks, k-way-merges the chunks, and drains into `_gposReference` via `AppendSorted` — all sequential I/O on the rebuild side. Wall-clock 100 M Reference rebuild **511 s → 457 s**; GPOS portion alone ~3× faster (76 s → 24 s). Peak iostat 2,463 MB/s (vs 327 MB/s baseline). Trigram portion still on the old path at this version; Phase 4 fixes that. `docs/validations/adr-032-phase3-gpos-radix-2026-04-22.md`. (`ff3af49`)

## [1.7.40] - 2026-04-21

### Added
- **ADR-032 Phase 2: `ExternalSorter<T, TSorter>` — chunked spill + k-way merge.** Generic external-sort primitive backing both rebuild and bulk-load radix paths. Buffers up to a per-chunk byte budget in memory (default 256 MB), sorts the chunk in place via `RadixSort.SortInPlace`, spills to a numbered temp file, repeats; on `Drain`, opens all chunks and merges via a `PriorityQueue<TElement, TPriority>` k-way merge. Caller-owned scratch buffer; zero allocations inside the merge loop. Used by Phase 3 (GPOS rebuild), Phase 4 (trigram rebuild), and ADR-033 (bulk-load) without modification. (`9c9fee2`)

## [1.7.39] - 2026-04-21

### Added
- **ADR-032 Phase 1: `RadixSort` primitive for `ReferenceKey` and `TrigramEntry`.** LSD radix sort with 8-bit digits, signed-long bias (XOR 0x80 on MSB bytes for sign-correct ordering), and skip-trivial-passes optimization (a single bucket holding all entries means the byte is constant — distribute pass becomes a no-op). Caller-owned scratch span, same length as data. 256-bucket histogram + prefix-sum offsets via `stackalloc uint[256]`. Two specialized internal entry points: `SortInPlace(Span<ReferenceKey>, Span<ReferenceKey> scratch)` and `SortInPlace(Span<TrigramEntry>, Span<TrigramEntry> scratch)`. New `TrigramEntry` struct under `Storage/` with explicit Pack=1 layout. (`5fd32e2`, `5fd32e2`)

## [1.7.38] - 2026-04-21

### Reverted
- **ADR-030 Phase 2 (parallel rebuild via broadcast channel) and Phase 3 (sort-insert via Array.Sort comparator).** Both shipped at wall-clock-neutral against the sequential baseline at Reference 100 M (524 s parallel vs 512 s sequential; sort-insert similarly neutral). Phase 5.2 dotnet-trace + iostat investigation (`docs/validations/adr-030-phase52-trace-2026-04-21.md`) revealed that wall-clock equality was hiding a structural cost shift: 1.7.37 had 453 s `GC.RunFinalizers` + 552 s `Monitor.Enter_Slowpath` that 1.7.34 did not. The architectural goal — sequential I/O via sort-insert — was right; the implementations (broadcast channel; comparator-sort + 3.2 GB monolithic buffer) traded compute for overhead. Reverts retired ~600 lines from `QuadStore` plus the `BroadcastChannel.cs` file. ADR-032 (radix external sort) replaced both, preserving the architectural goal without the implementation cost. (`5cf5d90`, `fb5f02f`, `625bd68`)

## [1.7.37] - 2026-04-21

### Added
- **ADR-030 Phase 3: sort-insert fast path for Reference GPOS rebuild.** GPOS rebuild scans `_gspoReference`, materializes a permuted `ReferenceKey[]`, calls `Array.Sort` with a comparator, then `AppendSorted` drains in sequential order. Wall-clock 100 M neutral against sequential baseline; Phase 5.2 trace later identified the comparator-sort + 3.2 GB monolithic buffer as the hidden cost driver. **Reverted** in 1.7.38; concept right (sort-insert), implementation wrong (comparator-sort). The radix external-sort path in ADR-032 Phase 3 is the production version. `docs/validations/adr-030-phase3-sort-insert-2026-04-21.md`. (`e29cc03`)

## [1.7.36] - 2026-04-21

### Added
- **ADR-030 Phase 2: parallel rebuild via broadcast channel.** GPOS and trigram rebuild run concurrently against a shared `_gspoReference` scan, broadcasting each `ReferenceKey` to both consumers via a custom `BroadcastChannel<T>` with single-producer / multiple-consumer semantics, bounded queue, and back-pressure. Wall-clock 100 M Reference rebuild neutral against sequential baseline (524 s vs 512 s). Phase 5.2 trace later showed Monitor.Enter_Slowpath dominating (~552 s) — the bounded-queue lock was the bottleneck, not the rebuild work itself. **Reverted** in 1.7.38. ADR-032 sequential-radix replaces this approach. `docs/validations/adr-030-phase2-parallel-rebuild-2026-04-21.md`. (`f320251`)

## [1.7.35] - 2026-04-21

### Fixed
- **Metrics single-writer contract pinned by concurrency test.** `JsonlMetricsListener` previously held its `StreamWriter` without coordination — under a mix of `LoadProgress` (per-chunk) and `RebuildProgress` (per-sub-phase) emissions, two threads writing simultaneously could interleave bytes and produce malformed JSONL. Fix wraps writes in a single `lock` per listener instance and adds a regression test that fires N concurrent emitters and asserts every line round-trips through `JsonDocument.Parse`. (`c9f5c41`)

## [1.7.34] - 2026-04-21

### Changed
- **ADR-030 Decision 5: Reference bulk-load refactored to GSPO-only inline + rebuild.** The 2026-04-20 Reference gradient (`docs/validations/adr-029-reference-gradient-2026-04-20.md`) measured Reference bulk rate collapsing from 210 K triples/sec at 1 M to 31 K/sec at 100 M — caused by `AddCurrentBatched` writing to two B+Trees in different sort orders per triple (GSPO and GPOS), thrashing the page cache once the working set passed RAM. Decision 5 amends ADR-030 to make the bulk/rebuild split profile-invariant: any profile with ≥2 indexes must split primary-inline from secondaries-via-rebuild. Reference now writes only `_gspoReference` during bulk; `RebuildSecondaryIndexes` populates `_gposReference` and trigram from a GSPO scan. CLI pipeline (`bulk-load → rebuild-indexes`) unchanged from the user's perspective. Reference 100 M end-to-end **4.7× faster wall-clock**, **20× faster bulk** alone (`docs/validations/adr-030-decision5-reference-refactor-2026-04-21.md`). (`be91cb2`, `ebde103`)

## [1.7.33] - 2026-04-21

### Fixed
- **`JsonlMetricsListener.AutoFlush=true`.** Without `AutoFlush`, JSONL records sat in the `StreamWriter` buffer until close; if the process crashed mid-run (or was killed by an out-of-memory signal during a heavy bulk-load), the most recent ~minutes of metrics were lost. `AutoFlush=true` makes every emit hit the OS buffer immediately — durable enough for diagnostic purposes, with negligible throughput impact at the chunk-flush emission cadence. (`bb54404`)

## [1.7.32] - 2026-04-21

### Fixed
- **ADR-031 Pieces 1+2: Dispose runtime collapsed from 14 minutes to 0.84 s on read-only sessions.** Phase 5.2 dispose profile (`docs/validations/dispose-profile-2026-04-20.md`) attributed the 14-minute Dispose at 1 B Cognitive to `CollectPredicateStatistics` running unconditionally inside `CheckpointInternal`. The work is meaningful only when statistics-relevant state has actually changed since the last checkpoint — a read-only session has nothing to collect. Piece 1 introduces a `_mutationsSinceCheckpoint` counter incremented by every mutation path (`Add`, `Delete`, batched variants, `Clear`, etc.). Piece 2 makes `CheckpointInternal` skip `CollectPredicateStatistics` when the counter is zero, which is the read-only-session case. 1 B Cognitive Dispose **14 min → 0.84 s** validated (`docs/validations/adr-031-dispose-gate-2026-04-21.md`). Cognitive write sessions still pay the full statistics cost. (`a918b80`, `3fda0d5`)

## [1.7.31] - 2026-04-21

### Added
- **ADR-030 Phase 1: measurement infrastructure for the Reference rebuild path.** Adds `JsonlMetricsListener` (the prototype that ADR-035 Phase 7a later subsumes) wired through the rebuild loop to emit per-sub-phase progress events. Also adds the validation harness pattern of separate JSONL files for separate runs (`docs/validations/<date>-<scope>.jsonl`). The infrastructure decision was an explicit gate on the parallel-rebuild and sort-insert work that follows: no Phase 2/3 shipping without the ability to measure what they cost. (`9052c37`)

---



## [1.7.30] - 2026-04-20

### Added
- **SPARQL queries route through Reference profile.** `QuadStore.Query` dispatches on `schema.Profile`: Reference + `AsOf` flows through new `QueryReferenceCurrent` (resolves atom IDs with wildcard semantics, picks `GSPO` vs `GPOS`, wraps the `ReferenceQuadEnumerator` in a `TemporalResultEnumerator` running in Reference mode). `TemporalResultEnumerator` augmented with an `_isReference` flag; `MoveNext`/`Current` branch accordingly; Reference rows synthesize temporal fields (`ValidFrom=MinValue`, `ValidTo=MaxValue`, `TransactionTime=MinValue`, `IsDeleted=false`) so downstream SPARQL code reads them as "always current, never deleted." The explicit time-travel methods (`QueryAsOf`, `QueryEvolution`, `TimeTravelTo`, `QueryChanges`) keep their `RequireTemporalProfile` guards — non-temporal profiles reject them at the API boundary. Trigram-candidate filtering carries through: `QueryCurrentWithCandidates` dispatches too. 7 new SPARQL-against-Reference tests (predicate-bound GPOS path, subject-bound GSPO path, `COUNT(*)`, ASK both cases, two-pattern join, reopen persistence). Session 6. Closes ADR-029 Phase 2 of the production-hardening roadmap functionally — Reference stores are now create-, load-, and query-capable through the standard CLI and API surfaces. (b3e2964)

## [1.7.29] - 2026-04-20

### Added
- **Reference-profile bulk-load works through the standard `RdfEngine.LoadStreamingAsync` path.** The batch API (`BeginBatch`/`AddBatched`/`AddCurrentBatched`/`CommitBatch`/`RollbackBatch`) now dispatches on `schema.Profile` per ADR-029 Decision 7 ("bulk-load path or equivalent programmatic interface is allowed against Reference"). Cognitive/Graph keep today's WAL-backed transactional semantics. Reference gets a direct path: no WAL, no batch transaction id, a single `_referenceBulkActive` flag enforces the "must be inside BeginBatch" contract; each `AddCurrentBatched` interns atoms and writes directly to both `_gspoReference` and `_gposReference`, plus trigram for literal objects. `CommitBatch` flushes; `RollbackBatch` releases the lock (per ADR-026 a failed Reference bulk-load means "delete the store and retry" — no WAL to rewind). Single-triple `Add`/`Delete`/`DeleteBatched` remain rejected for Reference — those are session-API per-triple writes that Decision 7 keeps immutable. `RebuildSecondaryIndexes` is a silent no-op for Reference; constructor skips the `PrimaryOnly` transition for Reference bulk opens. Common CLI pipeline (`bulk-load → rebuild-indexes`) now works uniformly across profiles. 5 new end-to-end tests through `RdfEngine` (NTriples, NQuads with named graphs, dedup by RDF uniqueness invariant, persistence across reopen, rebuild-after-load pipeline). Session 5. (5423027)

### Known issues
- **Inline secondary-index writes collapse throughput as the working set grows past RAM.** The 2026-04-20 Reference gradient (see `docs/validations/adr-029-reference-gradient-2026-04-20.md`) measured Reference bulk rate declining from 210 K triples/sec at 1 M to 31 K/sec at 100 M. Cause: `AddCurrentBatched` writes to two B+Trees in different sort orders per triple, thrashing the page cache. ADR-030 Decision 5 (2026-04-20 amendment, commit `e4b9b1b`) makes the bulk/rebuild split profile-invariant and specifies the Reference refactor — bulk writes only `_gspoReference` inline, `RebuildSecondaryIndexes` populates `_gposReference` and trigram from a GSPO scan. Refactor ships with ADR-030 Phase 3 alongside parallel rebuild and sort-insert.

## [1.7.28] - 2026-04-20

### Added
- **ADR-029 Phase 2d: QuadStore dispatches on `schema.Profile`.** Constructor branches to build the right index family — Cognitive/Graph produce four `TemporalQuadIndex` instances plus a WAL, Reference produces two `ReferenceQuadIndex` instances with no WAL (bulk-load durability is provided by `FlushToDisk` at load completion per ADR-026), Minimal throws `NotSupportedException` with an ADR pointer (accepted for schema write but QuadStore dispatch deferred). The four temporal-index fields become nullable; two nullable reference fields sit alongside. Two `[MemberNotNull]` guard helpers (`RequireWriteCapableProfile`, `RequireTemporalProfile`) thread the nullability through every call site without runtime cost; each public session-API mutation and each public temporal-query method calls its guard at the top. Reference callers raise `ProfileCapabilityException` (new type in Mercury.Abstractions) with a clear message rather than silent `NullReferenceException`. `FlushToDisk`, `Clear`, `Dispose`, `GetStatistics`, `GetWalStatistics`, `CheckpointInternal`, `CheckpointIfNeeded` are now profile-agnostic at the API level — each null-checks the fields it touches. `Recover` runs only when a WAL exists.
- **CLI `--profile <name>` flag** (case-insensitive). Reaches `StorageOptions.Profile`; honored only for brand-new stores, existing stores ignore the caller's preference in favor of the persisted `store-schema.json`. Startup banner prints the active profile when a store is opened for load/rebuild.
- 12 new `QuadStoreProfileDispatchTests`: Reference open + schema persistence, session-API / temporal-query rejection with the right exception type and message, Dispose safety without a WAL, reopen preserves profile, Minimal raises `NotSupportedException`, Cognitive default unchanged. Session 4. (86a8d91)

## [1.7.27] - 2026-04-20

### Added
- **ADR-029 Phase 2c: `ReferenceQuadIndex`.** Parallel B+Tree implementation to `TemporalQuadIndex`, aligned with ADR-029 Decision 3: 32-byte keys carrying only atom IDs — graph, primary, secondary, tertiary — no temporal dimension, no per-entry versioning, no soft-delete metadata. Uniqueness enforcement at insert per ADR-029 Decision 7 — an exact `(G, S, P, O)` match is a silent no-op. The two temporal cases (full-key duplicate, far-future-"currently valid" duplicate) that `TemporalQuadIndex` distinguishes collapse into one rule here because Reference has no temporal dimension for them to differ on — "RDF is a set of triples" is the whole invariant. Page layout is asymmetric: 32 B leaf entries (key only) with degree 511, 40 B internal entries (key + right-child pointer) with degree 408. Page header is 32 B; its `NextLeafOrLeftmostChild` slot is overloaded — next-leaf link on leaf pages, leftmost-child pointer on internal pages. A distinct magic number (`REFERENN`) in the file header so an attempt to open a Cognitive `.tdb` as a Reference index fails at `LoadMetadata` rather than silently misreading records. 13 new `ReferenceQuadIndexTests` cover basic add/query, uniqueness at both `Add` and `AddRaw` paths, cross-graph non-duplication, wildcard queries including the `-2` "graph unresolved" sentinel, bulk insert forcing leaf splits, Dispose/reopen persistence, rejection of a wrong-magic file. Session 3. No wiring into QuadStore yet — profile dispatch was Session 4. (3b8acac)

## [1.7.26] - 2026-04-20

### Changed
- **ADR-029 Phase 2a/2b: rename `QuadIndex` → `TemporalQuadIndex`, extract `IQuadIndex` interface.** Mechanical rename, behavior-preserving. The type that encodes bitemporal semantics, versioning, and soft-delete metadata now wears the name that describes what it actually is — so a parallel `ReferenceQuadIndex` with the 32-byte key layout can land alongside it in a later commit without either class being misnamed for its schema. File rename tracked as git-rename (98 % / 95 % similarity) so blame and history stay intact. `IQuadIndex` extracts only what `QuadStore` already invokes polymorphically across all four of its index fields today: `QuadCount`, `Flush`, `Clear`, `Dispose`. Temporal-specific methods (`QueryAsOf`, `QueryHistory`, `AddCurrent`, `AddHistorical`, `QueryRange`, `DeleteHistorical`) stay on the concrete `TemporalQuadIndex` — the interface grows in a later session when the second concrete implementation surfaces what is genuinely shared. `QuadStore` keeps its four fields typed as `TemporalQuadIndex` concretely in this commit; the profile-dispatch that switches index families based on `store.Schema.Profile` is a later change. Session 2. (724fb72)

## [1.7.25] - 2026-04-20

### Added
- **ADR-029 Phase 1: store-schema.json foundation.** New `StoreProfile` enum (Cognitive, Graph, Reference, Minimal) and `StoreSchema` record in `Mercury.Abstractions`. Schema carries the profile plus the capability flags it implies — `HasGraph`, `HasTemporal`, `HasVersioning` — and a `KeyLayoutVersion` discriminator for future incompatible schema evolutions. `ForProfile` builds the canonical shape per the ADR-029 matrix so callers never hand-assemble a schema for a known profile. Canonical JSON round-trip with byte-stable output (fields emitted in a fixed order so two stores with the same schema produce byte-identical files). `StorageOptions.Profile` property defaulting to Cognitive (ADR-029 Decision 6: opt-in, not opt-out). `QuadStore` constructor resolves the schema at open time — persisted `store-schema.json` wins when present, legacy stores (`gspo.tdb` exists but no schema file) get backfilled as Cognitive, brand-new stores write a schema matching `options.Profile`. Schema exposed on `QuadStore.Schema` for downstream consumers. Malformed schema, unknown profile name, or a `KeyLayoutVersion` higher than this build supports all raise `InvalidStoreSchemaException` at open — no silent degradation. 21 new `StoreSchemaTests` cover the canonical profile matrix, JSON round-trip, every failure mode, file I/O, and three QuadStore integration cases (brand-new, legacy-backfill, reopen-honors-persisted, corrupted-file-rejected). Session 1. (a48e5aa)

## [1.7.24] - 2026-04-20

### Added
- **ADR-028 Stage 1: AtomStore rehash-on-grow.** `EnsureHashCapacity` builds `.atomidx.new` at 2× buckets, re-inserts every live entry using stored per-bucket hashes (no recompute, no data-file reads), fsyncs, then two-step atomic rename to swap files. Runs under the QuadStore writer lock per ADR-020 — no concurrent reader contention possible. Load-factor trigger at 75 % in `InsertAtomUtf8` recomputes the target bucket after rehash. `ReconcileIndexFileState` in the `AtomStore` constructor recovers the three interrupted-rehash states per ADR-028 §4c (prefer pre-rehash state when swap was in-flight): canonical present → delete any `.new`/`.old` orphans; canonical missing with `.old` → discard `.new`, promote `.old`; canonical missing with only `.new` → salvage. Runs before the index `FileStream` is opened so renames are unpinned. 6 new rehash tests: forced rehash at 10 K atoms, persistence across reopen with hash table size derived from file length, each orphan scenario. (f369ccf)
- **`StorageOptions.ForceAtomHashCapacity` knob and `MERCURY_ATOM_HASH_INITIAL_CAPACITY` env var** let the caller honor `AtomHashTableInitialCapacity` exactly even in bulk mode — bypasses the 256 M-bucket floor that `BulkMode` normally applies. Used by ADR-028 Stage 2 validation to exercise rehash-on-grow under bulk load. Production bulk loads leave it unset. Mercury.Cli reads the env var and, when set, builds `StorageOptions` with the override and `ForceAtomHashCapacity=true`. (bf84a4b)

### Validated
- **Stage 2 gradient at 1 M / 10 M / 100 M triples.** `MERCURY_ATOM_HASH_INITIAL_CAPACITY=16384` forces the rehash path to fire ~8 / ~11 / ~14 times across the gradient. Predicate-bound `SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }` returns exact-match row counts (53,561 / 439,703 / 3,212,485) to the 2026-04-19 baseline at every scale, confirming rehash preserves every `(string → atomId)` mapping through every doubling. 100 M crosses past the 58 M Bug-5 ceiling cleanly (no overflow, no probe-depth degradation). Full details in `docs/validations/adr-028-rehash-gradient-2026-04-20.md`. ADR-028 stays Accepted pending Stage 3 (full 21.3 B); Stage 3 is blocked on ADR-029 Reference profile (14 TB Cognitive projection doesn't fit 8 TB disk).

## [1.7.23] - 2026-04-20

### Added
- **CLI `--limit <N>` flag for capped loads and converts.** Replaces the NT-only `head -n N` slice trick — works uniformly for Turtle and any other format where line-cut is not valid. Counts store-observable triples on `--load` / `--bulk-load`, emitted triples on `--convert`. Per-invocation, not a total store cap. Implemented via `CancellationTokenSource` in `RdfEngine` paths (`LoadFileAsync`, `LoadStreamingAsync`, `ConvertAsync`). Gate-before-add ensures exactly N triples land in the store; parser stops at next await once cancelled. 7 new tests covering exact-N, zero, over-source, null-default, Turtle format, file path API, and convert path. (a52fa24)

## [1.7.22] - 2026-04-19

### Fixed
- **`RebuildSecondaryIndexes` was ~25× slower than `--bulk-load` because `QuadIndex.SaveMetadata` msync ran per page allocation during rebuild.** The 1.7.15 defer-msync fix only applied when the index was opened in `BulkMode` (construction-time flag). Rebuild runs against a cognitive-mode-opened store, so every page split during GPOS/GOSP/TGSP construction triggered a full-region msync of the 256 GB sparse mmap. A 1 M rebuild didn't complete in 10 min. Fix: split the conflated `_bulkMode` flag in `QuadIndex` into (a) a construction-time decision that still pre-sizes the mmap for bulk loads, and (b) a runtime `_deferMsync` field with an `internal SetDeferMsync(bool)` method. `QuadStore.RebuildIndex` enables deferral around the rebuild loop, calls `Flush()` once at the end, then disables it. Same durability contract as the bulk-load path (single msync per rebuild phase). Measured: 1 M rebuild 2.9 s, 10 M rebuild 42 s, 100 M rebuild 11 m 35 s — same ~1.5× scaling factor as bulk load.
- **`TrigramIndex.AppendToPostingList` dereferenced a stale pointer after the posting-list mmap was remapped.** When a posting list exceeded its inline capacity, `AppendToPostingList` computed `atomsPtr = _postingPtr + offset + …` *before* calling `EnsurePostingCapacity`, which can grow the file and atomically swap `_postingPtr` to a new mmap. The loop that copies old entries into the newly-allocated larger list then read from the stale pointer, hitting the previous (now unmapped) region — `System.AccessViolationException` at 10 M rebuild. Fix: recompute `atomsPtr` after `EnsurePostingCapacity` returns. Same class of bug as the ADR-020 remap-pointer invariants for `AtomStore`, just in a code path that predated that guidance.
- **`TrigramIndex.EnsurePostingCapacity` created the new mmap before extending the file.** Writes past the old file length into the newly-mapped-but-not-yet-extended region hit unmapped pages — same class as 1.7.12 Bug 4 (`QuadIndex` mmap didn't grow with the file). Fix: `SetLength` → map → swap → unmap old (the order ADR-020 §4 requires). Discovered together with the stale-pointer bug above during the 10 M rebuild gradient.

## [1.7.19] - 2026-04-19

### Fixed
- **Revert 1.7.16 word-wise FNV — it caused hash clustering on the 1 B bulk load.** `AtomStore.InternUtf8` overflowed the 4096-probe cap at bucket 178,897,824 with the hash table at **11.93 % load factor** (~30.5 M atoms of 256 M buckets) during a 1 B Wikidata ingest, around 116.5 M triples in. Root cause: the 1.7.16 word-wise FNV processed 8 bytes per round, but FNV-1a's avalanche is per-byte — collapsing 8 rounds into 1 weakened bit distribution on strings that share 8-byte prefixes (e.g., `<…entity/Q1000001>`, `<…entity/Q1000002>`), producing correlated hash trajectories for families of Wikidata entity IRIs. The 100 M slice didn't contain enough such atoms to trigger it; the 1 B slice did. Reverted `ComputeHashUtf8` to byte-at-a-time FNV-1a, which has known-good distribution. Gives back the ~12 % throughput win from 1.7.16 — correctness first. A faster hash with proper per-word mixing (xxHash64-style rounds) is deferred until we have a distribution-quality regression harness to verify it against adversarial Wikidata patterns.

## [1.7.18] - 2026-04-19

### Fixed
- **SPARQL `SELECT (COUNT(*) AS ?n)` and similar aggregate-only projections now surface the alias.** `SparqlEngine.ExecuteSelect` built its `projectedNames` array from `SelectClause.ProjectedVariableCount` only, ignoring the separate `AggregateCount` list the parser maintains for expressions like `(COUNT(*) AS ?n)`. A query whose only projection was an aggregate produced `Variables = []`, which the formatter rendered as `(no variables selected)` even though the executor correctly computed the value and bound it to `?n`. Discovered while sanity-checking a 10 M bulk load via `:count` in the REPL — the REPL reported `Count: 0` against a store that actually held 9,993,790 triples. Fix: after populating projected variables in the original order, append aggregate aliases (non-empty `AliasLength`) to `projectedNames`. The executor bindings already carried `?n`; only the projection list was wrong. `SELECT *` unaffected. Mixed shapes like `SELECT ?g (COUNT(*) AS ?n) WHERE {} GROUP BY ?g` also now surface `?n`. Regression test added.

## [1.7.17] - 2026-04-19

### Changed
- **Removed `Console.IsInputRedirected` auto-detect from `--no-repl`.** The 1.7.14 auto-detect was too clever — it broke legitimate REPL scripting like `echo ":stats" | mercury --store foo` or `cat queries.sparql | mercury`, silently exiting with no output instead of processing the piped commands. The REPL already handles piped stdin correctly: `StreamReader.ReadLine()` returns null on EOF and the loop exits. The actual motivating case (Rider's profiler keeping stdin open with no data) is now handled by passing `--no-repl` explicitly. Piped stdin with EOF works out of the box again. Explicit opt-out, no magic.

## [1.7.16] - 2026-04-19

### Performance
- **Bulk load 10 M: +12 % throughput (243 K → 272 K triples/sec).** `AtomStore.ComputeHashUtf8` was a byte-at-a-time FNV-1a loop; release profiling after the 1.7.15 SaveMetadata fix showed it at ~7 % of total time, dominated by the existing-atom probe path (each lookup pays one hash computation). Word-wise variant uses the same FNV-1a constants but processes 8 bytes per iteration via `BinaryPrimitives.ReadUInt64LittleEndian`, with a byte-wise tail for the last 0-7 bytes. `ComputeHash(ReadOnlySpan<char>)` reinterprets the chars as bytes and reuses the UTF-8 path. Hashes are recomputed on every lookup (never persisted across versions), so swapping the hash function is safe. BCL-only — no `System.IO.Hashing` dependency.

## [1.7.15] - 2026-04-18

### Performance
- **Bulk load 10 M: +275 % throughput (64.7 K → 243 K triples/sec).** `QuadIndex.SaveMetadata` unconditionally called `_accessor.Flush()` on every invocation — and on macOS that's an msync of the *entire* 256 GB sparse-mmap region, not a single metadata page. Under bulk load, `AllocatePage` calls `SaveMetadata` per new B+Tree page, so the load was issuing thousands of whole-region msyncs. dotTrace sampling reported it at 1.56 % of profile time — a severe under-count because sampling measures wall-clock hits, not the kernel-time amplification of a blocking msync stalling the whole pipeline. Fix: same shape as the 1.7.9 `FlushPage` fix (Bug 1). In bulk mode `SaveMetadata` does the mmap writes (no syscall) and returns; the single `Flush()` at `QuadStore.FlushToDisk()` covers durability for every metadata update made during the load. Cognitive mode unchanged — per-update durability preserved.

## [1.7.14] - 2026-04-18

### Added
- **CLI: `--no-repl` flag and auto-detection of non-TTY stdin.** `mercury --bulk-load file.nt` used to always drop into the REPL after the load — which blocks forever in `read(stdin)` under profilers, CI, child-process launches, or anything else that doesn't have a terminal. Now: if stdin is redirected (pipe, file, `/dev/null`), or `--no-repl` is passed, the CLI exits after the load completes. TTY stdin still drops into the REPL as documented. Discovered when the first dotTrace run wedged because the profiler's stdin isn't a TTY.

### Fixed
- **SPARQL parser: prefixed-name datatype before `;` in INSERT DATA.** `ParseTermForUpdate` only accepted `^^<full-iri>` and ignored `^^prefix:local`. A triple like `ex:s ex:date "2026-04-17"^^xsd:date ; ex:topic "first"` left the parser mid-literal, which misread the trailing `xsd:date ;` and either hung (default graph variant) or threw `Expected '}' but found ';'` (in-graph variant). Full-IRI datatypes were not affected. Legal per SPARQL 1.1 Update grammar but not exercised by the W3C sparql11-update conformance suite. Two regression tests landed yesterday pin this behavior; both pass now.

### Performance
- **Bulk load 10 M: +12 % throughput (57.7 K → 64.7 K triples/sec), GC heap −46 % (154 MB → 83 MB).** Four changes, all identified by dotTrace sampling on a release build:
  - *Atom IDs instead of strings through the batch buffer.* `QuadStore.AddBatched` was calling `AtomStore.GetAtomString` four times per triple to materialize IDs back into strings, buffering those strings, then having `QuadIndex.Add` re-intern them at commit. 40 M string allocations and 40 M redundant hash lookups per 10 M load. The buffer now holds `List<LogRecord>` (atom IDs already live in the record), and a new `ApplyToIndexesById` / `ApplyDeleteToIndexesById` pair routes IDs straight to `QuadIndex.AddRaw` / the new `DeleteRaw`. Removes 1.03 % of profile time in `GetAtomString`, 0.2 % in redundant intern, 0.86 % in `BulkMoveWithWriteBarrier` (string refs no longer tracked by GC). `Recover` and immediate-mode `Add`/`Delete` also switched to the ID path — fewer lookups, same semantics.
  - *Cache `DateTimeOffset.UtcNow` once per batch.* `AddBatched` was calling `UtcNow` per triple for the transaction-time column and `AddCurrentBatched` was calling it again for valid-from. Both now read `_batchTransactionTimeTicks` / `_batchCurrentFrom` captured in `BeginBatch`. Bitemporally equivalent (a batch is one moment) and removes 1.37 % of profile time.
  - *Stop fstat'ing the data file on every atom insert.* `AtomStore.EnsureDataCapacity` read `_dataFile.Length`, which on macOS is an `fstat()` syscall. Added a tracked `_dataCapacity` field updated in lock-step with `SetLength`. Saves 0.57 % of profile time.
  - *Stop fstat'ing the index file on every page allocation.* Same pattern in `QuadIndex.AllocatePage`. Added `_fileCapacity`. Saves 0.54 %.

## [1.7.13] - 2026-04-18

### Fixed
- **`AtomStore` hash table is no longer fixed at 16 M buckets.** The previous `HashTableSize` const (1 << 24) overflowed at ~15.5 M unique atoms (96.72 % load factor, 4096-probe limit). Crashed the 100 M bulk-load gradient at 58.3 M triples — which by then had exhausted 16 M buckets worth of unique entity IRIs, predicates, and literals. The const is now a per-instance `_hashTableSize` initialized from `StorageOptions.AtomHashTableInitialCapacity` (default 16 M, preserves cognitive behavior). Bulk mode bumps the table to 256 M buckets (8 GB sparse mmap), mirroring the `QuadIndex` 256 GB sparse-mmap pattern — physical disk usage tracks touched buckets, not virtual size. Existing stores reopen with their original layout because the bucket count is derived from the index file length. `Clear()` now zeroes in 1 GB chunks so bulk-mode tables don't overflow `Span`'s 2 GB limit. Option B (dynamic rehash-on-grow) stays on the roadmap; only relevant if a cognitive store ever approaches its configured ceiling.

## [1.7.12] - 2026-04-18

### Fixed (workaround)
- **Bulk-mode `QuadIndex` pre-sizes the mmap to 256 GB per index.** Previously the mmap was created at the initial file size (default 1 GB). When `AllocatePage` extended the file via `SetLength`, the existing mmap still covered only 1 GB — writes to pages past that boundary hit `AccessViolationException` in `SplitLeafPage`. Crashed during 100 M bulk-load gradient at 27.9 M triples (~150 K pages × 16 KB = 2.4 GB into a 1 GB mmap). This is a temporary workaround: macOS allocates 256 GB of virtual address space immediately but physical pages only on touch (sparse file), and the per-process VM ceiling (~64 TB) leaves room for full Wikidata at ~1.8 TB per index. Proper fix (mmap-grow via unmap + recreate, OR chunked mmap with stable per-chunk pointers) is a follow-up workstream — not required while this baseline is sufficient. Cognitive mode still uses the original 1 GB initial size; small stores stay small.

## [1.7.11] - 2026-04-18

### Fixed
- **N-Triples parser sliding-buffer lookahead.** Same class of bug as the Turtle parser fix in 1.7.4: `Peek` and `PeekAhead` did not refill the buffer when bytes lay past the current end. Worse, the original `Peek` had `return _endOfStream ? -1 : -1;` — a typo where the refill case was missing entirely (both branches return -1). Any literal larger than the 8 KB buffer hit "Unterminated string literal" prematurely. Discovered when the 100 M bulk-load gradient run crashed at line 27,515,974 of the Wikidata N-Triples slice — a 4,202-character MathML literal exceeded the buffer. Fix: looped self-refill via new `FillBufferSync` (mirror of `FillBufferAsync` using sync `_stream.Read`), same pattern as `TurtleStreamParser.Buffer.cs`. The N-Triples parser now handles arbitrarily long literals correctly, and slow-stream cases (Read returning small chunks) work via the loop.

## [1.7.10] - 2026-04-18

### Fixed
- **Bulk load no longer crashes during checkpoint with `AccessViolationException`.** `CheckpointIfNeeded` was running unconditionally during bulk load, calling `CollectPredicateStatistics` which scans the GPOS index. In bulk mode, GPOS receives no writes (only GSPO is populated; secondaries are deferred to `RebuildSecondaryIndexes`), so scanning an uninitialized B+Tree page walked into invalid memory. Crashed at ~20.8 M triples on the 100 M gradient run when WAL size triggered checkpoint. Fix: skip `CheckpointIfNeeded` entirely when `_bulkLoadMode` — bulk-load contract defers all durability to a single `FlushToDisk()` at load completion. (Defensive guards against scanning uninitialized indexes are a follow-up; this unblocks the gradient.)

## [1.7.9] - 2026-04-18

### Fixed
- **Bulk load no longer issues msync per page write.** `QuadIndex.FlushPage` was calling `MemoryMappedViewAccessor.Flush()` on every B+Tree page modification — that's `msync()` on macOS, and it flushes the **entire** mapped region (multi-GB), not a single page. With ~5 page writes per triple insert × 100 K triples per chunk, the bulk-load path was issuing 500 K full-region msyncs per chunk and pinning the SSD random-write IOPS at ~5,500/sec. This was the actual bottleneck (the 1.7.8 `FileOptions.WriteThrough` change was a no-op for the mmap write path). Now `FlushPage` is a no-op in bulk mode; `QuadIndex.Flush()` exposes the deferred msync; `QuadStore.FlushToDisk()` calls it on all four indexes at load completion alongside the WAL flush. Cognitive mode keeps per-page durability semantics. Expected throughput improvement: 10–100× — depends on how IOPS-bound the previous gradient was vs other costs (atom interning likely the next ceiling).

## [1.7.8] - 2026-04-18

### Fixed
- **`QuadIndex` honors `bulkMode` in its `FileStream` open options.** Previously opened with `FileOptions.WriteThrough` unconditionally; now branches the same way `WriteAheadLog` does. (Effect on the bulk-load hot path turned out to be minimal because writes go through the mmap accessor, not the FileStream — but the option mismatch was inconsistent with WAL design and worth correcting. The actual write-amplification bottleneck is fixed in 1.7.9.)

## [1.7.7] - 2026-04-17

### Fixed
- **`RdfEngine.ConvertAsync` now routes N-Triples output through `NTriplesStreamWriter`** — the convert fast-path previously wrote spans directly to a `StreamWriter`, bypassing the writer's `WriteLiteral` escape logic entirely. This made the 1.7.6 `WriteLiteral` fix dormant for the convert code path. Now the convert emits valid N-Triples end-to-end. Without this, `mercury --convert` kept producing invalid output even with 1.7.6 installed.

## [1.7.6] - 2026-04-17

### Fixed
- **N-Triples writer re-escapes unescaped quotes in literals** — `NTriplesStreamWriter.WriteLiteral` now determines the close-quote position by scanning backward from the suffix shape (`^^<...>` datatype, `@lang-tag`, or plain), rather than forward with backslash tracking. The Turtle parser unescapes `\"` to `"` in memory (the in-memory form is the logical value), so forward escape-tracking in the writer was unreliable once the escape information was lost. Symptom: any literal containing an unescaped quote in the in-memory representation — whose source Turtle used `\"` — was truncated at the first internal quote, producing invalid N-Triples. Discovered when the full Wikidata dump `latest-all.nt` (3.0 TB produced by 1.7.4 convert) failed the Mercury N-Triples parser at triple 2,718.
- **Round-trip regression tests added** — Turtle → N-Triples → parse round-trip for literals with escaped quotes, lang tags, datatypes, and internal backslashes. Closes the coverage gap where writers were never tested against their own readers in the "convert" combination. (`NTriplesStreamWriterTests.WriteTriple_*InternalQuotes*` and `RoundTrip_TurtleLiteralWithEscapedQuotes_ParsesBack`.)

## [1.7.5] - 2026-04-17

### Added
- **`--metrics-out <file>` flag** (mercury CLI) — appends JSONL records for `--convert`, `--load`/`--bulk-load`, and `--rebuild-indexes` operations. Each progress callback emits one record (denser than the throttled terminal display); each phase ends with a `*.summary` record. Captures triple counts, throughput (avg + recent), elapsed time, GC heap, working set, and free disk for benchmark artifacts and post-run analysis.

## [1.7.4] - 2026-04-17

### Fixed
- **Turtle parser sliding-buffer lookahead** — `PeekAhead` and `PeekUtf8CodePoint` now self-refill when the requested bytes lie past the current buffer end, looping until either enough bytes are present or the stream reaches EOF. Previously, multi-byte UTF-8 sequences and multi-character lookaheads (`@prefix`, `<<`, `"""`, `^^`) silently truncated when they straddled the buffer boundary, producing the cumulative "Expected '.' after triple" failure observed during Wikidata ingestion at line 12,741,234. Fixes the parser blocker tracked since 2026-04-06.
- **`PeekAhead` negative-offset guard** — added `pos < 0` check to prevent IndexOutOfRangeException in the triple-term parser's backward-lookahead path.

### Added
- **Boundary-differential test suite** (`ParserBoundaryDifferentialTests`) — 30 cases covering boundary positions for `@prefix`, `<<`, `"""`, multi-byte UTF-8, blank nodes, dot runs, and combined constructs under 1-byte-per-Read slow streams. Reproduces the Wikidata failure mode on synthetic ~5 KB inputs in milliseconds, eliminating the need for the 912 GB dataset to validate parser correctness.

## [1.7.3] - 2026-04-06

### Removed
- **Conditional breakpoint parameters** from `drhook_step_breakpoint` and `drhook_step_break_function` MCP tools — netcoredbg conditional breakpoints use the same func-eval path that deadlocks on macOS/ARM64. Underlying DAP plumbing preserved for future re-enablement.

## [1.7.2] - 2026-04-06

DrHook validation — diagnosed netcoredbg func-eval deadlock, removed broken tools, added integration tests and process metrics.

### Removed
- **`drhook_step_eval`** — netcoredbg's DAP evaluate request hangs indefinitely on macOS/ARM64. The func-eval machinery deadlocks; its internal 15s command timeout never fires. Diagnosed via file-based tracing in `DapClient.SendRequestAsync`. The DAP `context` parameter is irrelevant — netcoredbg ignores it.
- **Watch mode** (`drhook_step_watch_add/remove/list`) — depends on evaluate.

### Added
- **Process metrics in every step response** — OS-level (WorkingSet, PrivateBytes, ThreadCount) via `Process.GetProcessById` syscalls; managed-level (GC heap size, collection counts) via EventPipe `System.Runtime` counters. Deltas from previous capture included. No DAP eval needed.
- **11 integration tests** — exercise session lifecycle, stepping, variable inspection, breakpoint management, and conditional stopping against a live DAP session with pre-built VerifyTarget.
- **Conditional stopping patterns** — netcoredbg conditional breakpoints hang (same func-eval path). Two workarounds validated: (1) unconditional breakpoint inside code-level `if`; (2) `Debugger.Break()`.
- **VerifyTarget project** — pre-built .NET console app for integration tests (`tests/DrHook.Tests/Stepping/VerifyTarget/`).

### Fixed
- **`_sourceBreakpoints.Clear()` missing from `CleanupAsync`** — breakpoint registry was not fully reset between sessions.

### Changed
- **DEBUGGING.md** — documents known limitations, conditional stopping workarounds, launch requirements.
- **ADR-005** — status changed to Superseded. ADR-002 amended with eval hang findings.

## [1.7.1] - 2026-04-05

### Fixed
- **Turtle parser BCP-47 language tags** — tags containing digits (e.g., `@be-tarask`) were rejected. Fixed character class in `LANGTAG` production to include digits per RFC 5646.

## [1.7.0] - 2026-04-05

Wikidata-scale ingestion pipeline — Mercury can now load the full Wikidata dump (16.6B triples, 912 GB Turtle) on a single machine.

### Added

#### Bulk Load Foundation (ADR-027 Phase 1)
- **WAL bulk mode** — `FileOptions.None` with 64 KB buffer bypasses OS write-through cache. 4.3x faster than `WriteThrough` per micro-benchmark (40.8M records/sec at 3.1 GB/sec).
- **`CommitBatchNoSync`** — WAL commit marker without fsync. Single `FlushToDisk()` at load completion.
- **`StorageOptions.BulkMode`** — GSPO-only indexing during bulk load, skip GPOS/GOSP/TGSP/trigram.

#### Streaming I/O (ADR-027 Phase 2)
- **`LoadFileAsync` rewritten** — streams directly from disk with chunked batch commits. No MemoryStream buffering. Decoupled parse-then-write: parser fills buffer (no lock), buffer flushed to store (lock only during materialization).
- **Compression-aware format detection** — `FromPathStrippingCompression` handles `.ttl.gz`, `.nt.bz2`, etc.
- **Transparent GZip decompression** — BCL `GZipStream`, no external dependencies.
- **`ConvertAsync`** — streaming parser-to-writer pipeline, no store. Pure throughput test for parser validation.
- **Progress reporting** — `LoadProgress` with triples/sec, GC heap, working set, interval rate.

#### Deferred Secondary Indexing (ADR-027 Phase 4)
- **`RebuildSecondaryIndexes`** — scans GSPO, populates GPOS/GOSP/TGSP with dimension remapping via `AddRaw` (raw atom-ID insertion, no re-interning). Trigram index rebuilt from object literals.
- **`StoreIndexState`** — persisted state metadata (`Ready`/`PrimaryOnly`/`Building:<index>`). Query planner falls back to GSPO when secondaries unavailable.

#### CLI Convergence (ADR-027 Phase 5)
- **`--store <name>`** — named stores via `MercuryPaths` (e.g., `--store wikidata`)
- **`--bulk-load <file>`** — bulk load with deferred indexing
- **`--load <file>`** — standard load at startup
- **`--convert <in> <out>`** — streaming format conversion (no store, exits after)
- **`--rebuild-indexes`** — build secondary indexes from GSPO
- **`--min-free-space <GB>`** — disk space safeguard (default: 100 GB for bulk loads)
- **REPL commands** — `:load [--bulk] <file>`, `:convert <in> <out>`, `:rebuild-indexes`

#### Runtime Diagnostics
- **Startup diagnostics** — store path, index state, mode, free disk space, min threshold
- **Progress display** — every 10 seconds: elapsed (h:m:s), triples, avg rate, recent rate, GC heap, RSS
- **Completion summary** — triples, elapsed, avg rate, GC heap, working set, free disk remaining

### Fixed

- **Turtle parser buffer boundary bug** — `Peek()` returned `-1` when the input buffer was exhausted mid-statement, even when more data existed in the stream. Fix: `FillBufferSync()` shifts remaining data left and reads more, synchronously. The buffer slides through the stream at any fixed size — 32 bytes parses the same as 8 KB. No dynamic buffer growth needed.
- **FHIR ontology** (88,428 triples, statements up to 3,965 lines) now loads successfully.
- **100 KB IRI and 500 KB literal** — previously documented as parser buffer limitations. Eliminated by the sliding buffer fix.

### Added (Documentation)
- **DEBUGGING.md** — DrHook debugging methodology: when to observe, how to set breakpoints, workflow examples.

## [1.6.1] - 2026-03-30

Closes the test debugging gap — DrHook can now debug .NET test code through `dotnet test`.

### Added

- **`drhook_step_test` MCP tool** — debug .NET test methods end-to-end. Launches `dotnet test` with `VSTEST_HOST_DEBUG=1`, parses the testhost PID from stdout, attaches netcoredbg to the child process, sets breakpoints, and continues to the first hit. Same technique VS Code uses. Test code was the last unreachable target for DrHook stepping.

### Fixed

- **Test debugging gap** — previously documented as a known limitation ("dotnet test spawns a child process that the debugger cannot follow"). The limitation was in the approach (launching under debugger), not in the tooling. Hybrid launch-then-attach solves it.

## [1.6.0] - 2026-03-30

DrHook breakpoint registry, expression evaluation, and environment variable support.

### Added

#### DrHook — Breakpoint Registry (ADR-001)
- **Breakpoint registry** in `SteppingSessionManager` — tracks source, function, and exception breakpoints. Every mutation syncs the full set to DAP, eliminating silent set-and-replace behavior.
- **`drhook_step_breakpoint_remove`** — remove a specific source, function, or exception breakpoint
- **`drhook_step_breakpoint_list`** — list all active breakpoints with file, line, condition, and type
- **`drhook_step_breakpoint_clear`** — clear all breakpoints or by category (source/function/exception)
- **Multi-breakpoint DapClient overloads** — `SetBreakpointsAsync` and `SetFunctionBreakpointsAsync` accept lists
- **Registry seeding** — initial breakpoints from `LaunchAsync`/`RunAsync` seed the registry

#### DrHook — Expression Evaluation (ADR-002)
- **`drhook_step_eval` MCP tool** — evaluate C# expressions in the current stack frame via DAP `evaluate`. Supports property access, indexing, method calls, arithmetic, boolean logic. More targeted than `drhook_step_vars`.
- **`DapClient.EvaluateAsync`** — sends DAP `evaluate` request with frame context
- **Structured error returns** — failed evaluations return JSON with error message, not exceptions. The agent learns from what doesn't work.

#### DrHook — Environment Variables
- **`drhook_step_run` env support** — pass environment variables as `KEY=VALUE` strings to the launched process via DAP `launch` env field

### Changed

- **Tool descriptions updated** — breakpoint tools now say "Add" instead of "Set", removed "WARNING: set-and-replace" notes
- **`drhook_step_launch` description** — recommends `drhook_step_run` or `drhook_step_test` when possible

### Validated

- **ADR-004 final criterion** — netcoredbg `launch` does not follow `dotnet test` child processes. Confirmed empirically: testhost spawned via vstest socket protocol, breakpoint in test code never hit. Workaround validated: prebuilt file-based apps via `dotnet exec`.
- **All four DrHook ADRs accepted** — ADR-001, ADR-002, ADR-003, ADR-004

## [1.5.1] - 2026-03-29

DrHook process-owning stepping and DAP robustness — validated via ad-hoc Sky Omega MVP.

### Added

#### DrHook — Process-Owning Stepping (ADR-004)
- **`drhook_step_run` MCP tool** — launches a .NET executable under debugger control via DAP `launch` with `stopAtEntry`. Eliminates race conditions and MCP timeout issues that made `step_launch` (attach mode) impractical for AI agents. DrHook owns the target process lifecycle.
- **`DapClient.LaunchTargetAsync`** — sends DAP `launch` request with `program`, `args`, `cwd`, `stopAtEntry` parameters
- **Process lifecycle ownership** — `SteppingSessionManager` tracks `_ownsProcess` flag; launch mode terminates debuggee on disconnect, attach mode preserves it
- **ADR-004** — documents design, unknowns, and 5/6 verified success criteria

### Fixed

- **DAP byte framing for non-ASCII** — `Content-Length` is byte count but `DapClient` read chars via `StreamReader`. Non-ASCII characters (Swedish å, ö in type names, paths) caused byte/char misalignment, corrupting the DAP message stream. Fix: read raw bytes from `BaseStream`, decode UTF-8. Header parsing moved to byte-level to avoid `StreamReader` internal buffering. Bug was masked in DrHook.Poc because SteppingHost used ASCII-only code.

### Changed

- **CLAUDE.md** reduced from 879 to 271 lines (69%) — architecture details, SPARQL reference, and production hardening extracted to `docs/architecture/technical/`
- **README.md** documentation guide updated with link to Kjell Silverstein poetry collection

### Documentation

- **`docs/architecture/technical/mercury-internals.md`** — storage, durability, concurrency, zero-GC patterns
- **`docs/architecture/technical/sparql-reference.md`** — features, operators, formats, temporal extensions
- **`docs/architecture/technical/production-hardening.md`** — benchmarks, NCrunch, cross-process coordination
- **`docs/poetry/kjell-silverstein-collected.md`** — Sky Omega explained without a single line of code

---

## [1.5.0] - 2026-03-23

DrHook runtime observation substrate — Sky Omega's second MCP server.

### Added

#### DrHook — Runtime Observation Substrate (ADR-004)
- **DrHook core library** — .NET runtime inspection with two observation layers:
  - **EventPipe observation** — passive profiling (thread sampling, GC events, exception tracing, contention detection) with structured anomaly detection
  - **DAP stepping** — controlled execution via Debug Adapter Protocol (breakpoints, step-through, variable inspection) using netcoredbg
- **DrHook MCP server** (`drhook-mcp`) — 13 MCP tools exposing observation and stepping to AI coding agents, packaged as .NET global tool
- **Hypothesis-driven inspection** — every observation requires a stated hypothesis, forcing epistemic discipline (what do you expect vs what do you see)
- **Code version anchoring** — assembly version captured with every observation to prevent bitemporal desync
- **Signal summarization** — EventPipe output collapsed to structured summaries with anomaly flags (HOTSPOT, GC_PRESSURE, CONTENTION, EXCEPTIONS, IDLE)
- **File-based inspection target** (`examples/drhook-target.cs`) — five scenarios for testing DrHook capabilities
- **16 unit tests** across ProcessAttacher, DapClient, NetCoreDbgLocator, and SteppingSessionManager

### Changed
- **Mercury MCP server version** now reads from assembly attribute instead of hardcoded string
- **Directory.Build.props** Product name updated from "Sky Omega Mercury" to "Sky Omega"
- **install-tools.sh/.ps1** updated to include `drhook-mcp` in global tool installation
- **.mcp.json** updated with DrHook dev-time server configuration

---

## [1.4.0] - 2026-03-22

Transactional integrity and trigram read path — two major architectural advances.

### Added

#### WAL v2 — Transactional Integrity (ADR-023)
- **Transaction boundaries** — `BeginTx`/`CommitTx` markers in WAL enable crash-safe batch semantics; recovery replays only committed transactions
- **Deferred materialization** — batched writes buffer in memory, apply to indexes only at `CommitBatch()`; `RollbackBatch()` discards buffer without touching indexes
- **Per-write transaction time** — each write generates `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` stored in WAL and indexes; preserved through crash recovery
- **80-byte WAL v2 record** — includes `GraphId`, `TransactionTimeTicks`, and transaction markers
- **Replay idempotence** — WAL recovery is safe to re-run; already-applied records are skipped

#### Trigram Read Path (ADR-024)
- **Scan-level pre-filtering for `text:match`** — `MultiPatternScan` restricts enumerator to candidate object atoms from the trigram index, reducing full-text search from O(N) to O(k × log N)
- **Selectivity-based fallback** — candidate sets exceeding 10,000 atoms revert to brute-force scan to avoid index overhead on low-selectivity queries

### Fixed

- **`text:match` culture dependency** — switched to `OrdinalIgnoreCase` per ADR-014, fixing locale-sensitive matching on Swedish characters (å, ä, ö)

---

## [1.3.12] - 2026-03-21

Full-text search is now unconditional — the trigram index is always created.

### Removed

- **`EnableFullTextSearch` option** — `StorageOptions.EnableFullTextSearch` property removed; every `QuadStore` now unconditionally creates a `TrigramIndex`

---

## [1.3.11] - 2026-03-20

Full-text search enabled by default — LLMs can now discover and use `text:match` out of the box.

### Changed

- **`EnableFullTextSearch` defaults to `true`** — trigram index is now built automatically for all new stores; previously required explicit opt-in via `StorageOptions`
- **`mercury_query` MCP tool description** — now advertises `text:match(?var, "term")` for case-insensitive full-text search, making it discoverable by LLMs

---

## [1.3.10] - 2026-03-17

Exclusive store lock and `mercury_version` MCP tool.

### Added

- **Exclusive store lock** — persistent pools acquire a file lock (`store.lock`) preventing concurrent access from multiple processes; throws `StoreInUseException` with owner PID if store is already in use; OS releases lock automatically on crash
- **`mercury_version` MCP tool** — exposes server version at runtime via assembly `InformationalVersion`

### Fixed

- **Multi-process store corruption** — two `mercury-mcp` or `mercury` processes opening the same store would corrupt data; now the second process gets a clear error with actionable guidance

---

## [1.3.9] - 2026-03-17

QuadStorePool explicit store lifecycle — remove implicit creation side effects.

### Changed

- **`QuadStorePool` indexer (`pool["name"]`)** — now pure lookup; throws `KeyNotFoundException` if store doesn't exist (was: silently created store as side effect)
- **`Clear(name)` and `Switch(a, b)`** — now throw if stores don't exist (no implicit creation)
- **`PruneEngine`** — uses explicit `GetOrCreate("secondary")` for prune target

### Added

- **`QuadStorePool.EnsureActive(name)`** — creates store if needed, sets it as active; the proper API for initialization
- **`QuadStorePool.GetOrCreate(name)`** — creates store if needed, returns it; explicit about creation intent

### Fixed

- **Mercury MCP server fresh install** — `mercury-mcp` now calls `EnsureActive("primary")` on startup, fixing "No active store is set" error when `~/Library/SkyOmega/stores/mcp/` has no existing `pool.json`

---

## [1.3.8] - 2026-03-10

QuadIndex generic key fields and time-leading sort order (ADR-022).

### Changed

#### QuadIndex Generic Keys (ADR-022 Phase 1)
- **TemporalKey fields renamed** — `SubjectAtom`/`PredicateAtom`/`ObjectAtom` → `Primary`/`Secondary`/`Tertiary`; `GraphAtom` → `Graph`
- **QuadIndex method parameters** — `subject`/`predicate`/`obj` → `primary`/`secondary`/`tertiary`
- **QuadStore public API** — `obj` → `@object` (idiomatic C# keyword escape)
- **TemporalIndexType enum** — `SPOT`/`POST`/`OSPT`/`TSPO` → `GSPO`/`GPOS`/`GOSP`/`TGSP`

### Fixed

#### TGSP Index (ADR-022 Phases 2–3)
- **TGSP was a byte-for-byte duplicate of GSPO** — introduced `KeySortOrder` enum and `KeyComparer` delegate so TGSP uses `TimeFirst` sort order (ValidFrom leads), while GSPO/GPOS/GOSP use `EntityFirst`
- **Temporal range queries O(N) → O(log N + k)** — `CreateSearchKey` now produces time-leading bounds for `TimeFirst` indexes, enabling B+Tree seek instead of full scan

### Added

- **Page access instrumentation** (`#if DEBUG`) — `PageAccessCount`/`ResetPageAccessCount()` on `QuadIndex` for verifying index efficiency in tests
- **3 verification tests** — sort order correctness (TimeFirst, EntityFirst) and page access efficiency comparison

### Documentation

- **ADR-022** completed — all 4 phases implemented
- **Initial ADRs** for Lucy, James, Sky, and Mira cognitive components (Sky Omega 2.0)

---

## [1.3.7] - 2026-03-07

### Fixed

- **CLI argument validation** — prevent accidental store creation from unrecognized arguments

---

## [1.3.6] - 2026-03-05

CLI and MCP connectivity improvements.

### Added

- **`:attach` / `:a` REPL command** — attach to running MCP (or other Mercury instance) from within the CLI REPL, not just from the command line
- **`mercury_store` MCP tool** — exposes store path via MCP for Claude Code
- **`StorePathHolder`** — DI-injectable store path for MCP tools

### Fixed

- **Pipe prompt sync** — `ReadUntilPromptAsync` no longer false-matches `<...> ` in help text as a prompt, fixing delayed/out-of-sync responses in attached mode
- **`:detach` cleanup** — no more spurious "Cannot access a closed pipe" errors after detaching; graceful pipe disposal on all code paths
- **macOS store paths** — `MercuryPaths.Store()` now resolves to `~/Library/SkyOmega/stores/` on macOS instead of the non-standard `~/.local/share/`

### Changed

- **CLI prompt renamed** — `mercury>` → `cli>` for visual balance with `mcp>` and consistency with store names
- **Goodbye message** — now ends with double linefeed for cleaner terminal output

### Documentation

- All tutorials updated for `cli>` prompt
- ADR-006 updated for `cli>` prompt

---

## [1.3.0] - 2026-02-18

Breaking API surface changes: public facade layer and type internalization.

### Added

#### Public Facades (ADR-003)
- **`SparqlEngine`** — static facade for SPARQL query/update with `QueryResult`/`UpdateResult` DTOs, `Explain()`, `GetNamedGraphs()`, `GetStatistics()`
- **`RdfEngine`** — static facade for RDF parsing, writing, loading, and content negotiation across all six formats
- **`PruneEngine`** — static facade for dual-instance pruning with `PruneOptions`/`PruneResult` DTOs
- **`RdfTripleHandler`/`RdfQuadHandler`** — public delegates for zero-GC callback parsing

#### Public DTOs
- **`QueryResult`** — Success, Kind, Variables, Rows, AskResult, Triples, ErrorMessage, ParseTime, ExecutionTime
- **`UpdateResult`** — Success, AffectedCount, ErrorMessage, ParseTime, ExecutionTime
- **`StoreStatistics`** — QuadCount, AtomCount, TotalBytes, WalTxId, WalCheckpoint, WalSize
- **`PruneResult`** — Success, ErrorMessage, QuadsScanned, QuadsWritten, BytesSaved, Duration, DryRun
- **`PruneOptions`** — DryRun, HistoryMode, ExcludeGraphs, ExcludePredicates
- **`ExecutionResultKind`** enum — Empty, Select, Ask, Construct, Describe, Update, Error, ...

### Changed

#### Breaking: ~140 Types Internalized (ADR-003 Phases 3-4)
- All RDF parsers now internal: `TurtleStreamParser`, `NTriplesStreamParser`, `NQuadsStreamParser`, `TriGStreamParser`, `JsonLdStreamParser`, `RdfXmlStreamParser` — use `RdfEngine` instead
- All RDF writers now internal: `TurtleStreamWriter`, `NTriplesStreamWriter`, `NQuadsStreamWriter`, `TriGStreamWriter`, `RdfXmlStreamWriter`, `JsonLdStreamWriter` — use `RdfEngine` instead
- SPARQL internals now internal: `SparqlParser`, `QueryExecutor`, `UpdateExecutor`, `SparqlExplainer`, `FilterEvaluator`, `QueryPlanner`, `QueryPlanCache`, `LoadExecutor` — use `SparqlEngine` instead
- Content negotiation now internal: `RdfFormatNegotiator`, `SparqlResultFormatNegotiator` — use `RdfEngine.DetermineFormat()`/`NegotiateFromAccept()` instead
- Result writers/parsers now internal: `SparqlJsonResultWriter`, `SparqlXmlResultWriter`, `SparqlCsvResultWriter` and corresponding parsers
- OWL/RDFS reasoning now internal: `OwlReasoner`, `InferenceRules`
- **Mercury public surface reduced to 21 types** (3 facades, 2 protocol, 11 storage, 3 diagnostics, 2 delegates)

### Documentation

- **`docs/api/api-usage.md`** restructured around public facades (1,529 → 900 lines); all internal type examples removed
- **`docs/tutorials/embedding-mercury.md`** updated to use `SparqlEngine`, `RdfEngine` facades
- **CLAUDE.md** updated with Mercury public type count (21 types)
- **ADR-003** completed — Buffer Pattern for Stack Safety, extended to cover facade design and type internalization

---

## [1.2.2] - 2026-02-15

Complete tutorial suite and infrastructure fixes.

### Added

#### ADR-002 Tutorial Suite (Phases 1-5)
- **Phase 1 — The Front Door:** `getting-started.md` (clone to first query in 30 minutes), `mercury-cli.md`, `mercury-mcp.md`, examples README, CLAUDE.md and MERCURY.md bootstrap improvements
- **Phase 2 — Tool Mastery:** `mercury-sparql-cli.md`, `mercury-turtle-cli.md`, `your-first-knowledge-graph.md` (RDF onboarding), `installation-and-tools.md`
- **Phase 3 — Depth and Patterns:** `temporal-rdf.md`, `semantic-braid.md`, `pruning-and-maintenance.md`, `federation-and-service.md`
- **Phase 4 — Developer Integration:** `embedding-mercury.md`, `running-benchmarks.md`, knowledge directory seeding (`core-predicates.ttl`, `convergence.ttl`, `curiosity-driven-exploration.ttl`, `adr-summary.ttl`)
- **Phase 5 — Future:** `solid-protocol.md` (server setup, resource CRUD, containers, N3 Patch, WAC/ACP access control), `eee-for-teams.md` (team-scale EEE methodology with honest boundaries); Minerva tutorial deferred

### Fixed

#### AtomStore Safety (ADR-020)
- **Publication order fix** — store atom bytes before publishing pointer, preventing readers from seeing uninitialized memory
- **CAS removal** — removed unnecessary compare-and-swap on append-only offset
- **Growth ordering** — correct file growth sequencing

#### ResourceHandler Read Lock
- **Missing read lock** in `ResourceHandler` — added `AcquireReadLock`/`ReleaseReadLock` around query enumeration (ADR-021)

#### LOAD File Support
- **`LOAD <file://...>` wired into all update paths** — CLI, MCP tools, MCP pipe sessions, HTTP server
- **Thread affinity fix** — `LoadFromFileAsync` runs on dedicated thread via `Task.Run` to maintain `ReaderWriterLockSlim` thread affinity across `BeginBatch`/`CommitBatch`
- **CLI pool.Active initialization** — eagerly creates primary store to prevent `InvalidOperationException` on first access

### Documentation

- **ADR-002** status updated to "Phase 5 Partially Accepted"
- **STATISTICS.md** documentation lines updated to 26,292 (grand total 165,677)

---

## [1.2.1] - 2026-02-09

Pruning support in Mercury CLI and MCP, with QuadStorePool migration.

### Added

#### Pruning in Mercury CLI
- **`:prune` REPL command** with options: `--dry-run`, `--history preserve|all`, `--exclude-graph <iri>`, `--exclude-predicate <iri>`
- **QuadStorePool migration** — CLI now uses `QuadStorePool` instead of raw `QuadStore`, enabling dual-instance pruning via copy-and-switch
- **Flat-store auto-migration** — existing CLI stores at `~/Library/SkyOmega/stores/cli/` are transparently restructured into pool format on first run

#### Pruning in Mercury MCP
- **`mercury_prune` MCP tool** with parameters: `dryRun`, `historyMode`, `excludeGraphs`, `excludePredicates`
- **QuadStorePool migration** — MCP server now uses `QuadStorePool`, pruning switches stores seamlessly without restart

#### Infrastructure
- **`PruneResult`** class in Mercury.Abstractions for standardized pruning results
- **`Func<QuadStore>` factory constructor** for `SparqlHttpServer` — each request resolves store via factory, enabling seamless store switching after prune without HTTP server restart
- **Flat-store auto-migration** in `QuadStorePool` constructor — detects `gspo.tdb` in base path and restructures into `stores/{guid}/` + `pool.json`

### Changed

- **Mercury.Cli** — migrated from `QuadStore` to `QuadStorePool` (in-memory mode uses `QuadStorePool.CreateTemp`)
- **Mercury.Mcp** — migrated from `QuadStore` to `QuadStorePool` (`MercuryTools`, `HttpServerHostedService`, `PipeServerHostedService`)
- **SparqlHttpServer** — field changed from `QuadStore` to `Func<QuadStore>` factory; existing constructor preserved for backward compatibility

### Tests

- **17 new tests** (3,913 total): `ReplPruneTests` (7), `QuadStorePoolPruneTests` (6), `QuadStorePoolMigrationTests` (4)

---

## [1.2.0] - 2026-02-09

Namespace restructuring for improved code navigation and IDE experience.

### Changed

#### SPARQL Types Namespace (`SkyOmega.Mercury.Sparql.Types`)
- **Split `SparqlTypes.cs`** (2,572 lines, 37 types) into individual files under `Sparql/Types/`
- **New namespace** `SkyOmega.Mercury.Sparql.Types` — one file per type (Query, GraphPattern, SubSelect, etc.)
- Follows folder-correlates-to-namespace convention for better code navigation

#### Operator Namespace (`SkyOmega.Mercury.Sparql.Execution.Operators`)
- **Moved 14 operator files** from `Execution/` to `Execution/Operators/`
- **New namespace** `SkyOmega.Mercury.Sparql.Execution.Operators` — scan operators, IScan interface, ScanType enum
- Files: TriplePatternScan, MultiPatternScan, DefaultGraphUnionScan, CrossGraphMultiPatternScan, VariableGraphScan, SubQueryScan, SubQueryJoinScan, SubQueryGroupedRow, BoxedSubQueryExecutor, QueryCancellation, SyntheticTermHelper, SlotBasedOperators, IScan, ScanType

### Documentation

- **CLAUDE.md** updated with Operators/ and Types/ folder structure
- **STATISTICS.md** line counts updated

---

## [1.1.1] - 2026-02-07

Version consolidation and CLI improvements.

### Added

- **`-v`/`--version` flag** for all CLI tools (`mercury`, `mercury-mcp`, `mercury-sparql`, `mercury-turtle`)

### Changed

- **Centralized versioning** - `Directory.Build.props` is now the single source of truth for all project versions
- **Mercury.Mcp reset** from `2.0.0-preview.1` to `1.1.1` to align with unified versioning

---

## [1.1.0] - 2026-02-07

Global tool packaging, persistent stores, and Microsoft MCP SDK integration.

### Added

#### Global Tool Packaging (ADR-019)
- **`mercury`** - SPARQL CLI installable as .NET global tool
- **`mercury-mcp`** - MCP server installable as .NET global tool
- **`mercury-sparql`** - SPARQL query engine demo as global tool
- **`mercury-turtle`** - Turtle parser demo as global tool
- **Install scripts** - `tools/install-tools.sh` (bash) and `tools/install-tools.ps1` (PowerShell)

#### Persistent Store Defaults
- **`MercuryPaths`** - Well-known persistent store paths per platform
  - macOS: `~/Library/SkyOmega/stores/{name}/`
  - Linux/WSL: `~/.local/share/SkyOmega/stores/{name}/`
  - Windows: `%LOCALAPPDATA%\SkyOmega\stores\{name}\`
- **`mercury`** defaults to persistent store at `MercuryPaths.Store("cli")`
- **`mercury-mcp`** defaults to persistent store at `MercuryPaths.Store("mcp")`

#### Claude Code Integration
- **`.mcp.json`** - Dev-time MCP config for Claude Code at repo root
- **User-scope install** - `claude mcp add --scope user mercury -- mercury-mcp`

### Changed

#### Microsoft MCP SDK Migration
- **Replaced hand-rolled `McpProtocol.cs`** (~494 lines) with official `ModelContextProtocol` NuGet package (0.8.0-preview.1)
- **`[McpServerToolType]`** attribute-based tool registration via `MercuryTools.cs`
- **Hosted service model** - PipeServer and SparqlHttpServer as `IHostedService` implementations
- **`Microsoft.Extensions.Hosting`** - Proper application lifecycle management

#### CLI Library Extraction (ADR-018)
- Extracted CLI logic into testable libraries (`Mercury.Sparql.Tool`, `Mercury.Turtle.Tool`)

### Documentation

- **ADR-019** - Global Tool Packaging and Persistent Stores
- **ADR-018** - CLI Library Extraction
- **Mercury ADR index** updated with all 20 ADRs and correct statuses

---

## [1.0.0] - 2026-01-31

Mercury reaches production-ready status with complete W3C SPARQL 1.1 conformance.

### Added

#### SPARQL Update Sequences
- **Semicolon-separated operations** - Multiple updates in single request (W3C spec [29])
- **`ParseUpdateSequence()`** - Returns `UpdateOperation[]` for batched execution
- **`UpdateExecutor.ExecuteSequence()`** - Static method for atomic sequence execution
- **Prologue inheritance** - PREFIX declarations carry across sequence operations

#### W3C Update Test Graph State Validation
- **Expected graph comparison** - Tests now validate resulting store state, not just execution success
- **Named graph support** - `ut:data` and `ut:graphData` parsing from manifests
- **`ExtractGraphFromStore()`** - Enumerate store contents for comparison
- **Blank node isomorphism** - Correct matching via `SparqlResultComparer.CompareGraphs()`

#### Service Description Enrichment
- **`sd:feature` declarations** - PropertyPaths, SubQueries, Aggregates, Negation
- **`sd:extensionFunction`** - text:match full-text search
- **RDF output formats** - Turtle, N-Triples, RDF/XML for CONSTRUCT/DESCRIBE

### Changed

#### W3C Conformance (100% Core Coverage)
- **SPARQL 1.1 Query**: 421/421 passing (100%)
- **SPARQL 1.1 Update**: 94/94 passing (100%)
- **All tests** now validate actual graph contents, not just success status

### Fixed

#### SPARQL 1.1 CONSTRUCT/Aggregate Gaps (3 tests)
- **`constructlist`** - RDF collection `(...)` syntax in CONSTRUCT templates now generates proper `rdf:first/rdf:rest` chains
- **`agg-empty-group-count-graph`** - COUNT without GROUP BY inside GRAPH ?g now correctly returns count per graph (including 0 for empty graphs)
- **`bindings/manifest#graph`** - VALUES inside GRAPH binding same variable as graph name now correctly filters/expands based on UNDEF vs specific values

#### SPARQL 1.1 Update Edge Cases (10 tests)
- **USING clause dataset restriction** (4 tests) - USING without USING NAMED now correctly restricts named graph access
- **Blank node identity** (4 tests) - Same bnode label across statements now creates unique nodes per W3C scoping rules
- **DELETE/INSERT with mixed UNION branches** (2 tests) - UNION containing both GRAPH and default patterns now executes correctly via `_graphPatternFlags` tracking

### Documentation

- **ADR-002** status changed to "Accepted" - 1.0.0 operational scope achieved
- Release checklist complete per ADR-002 success criteria

---

## [0.6.2] - 2026-01-27

Critical stack overflow fix for parallel test execution.

### Fixed

#### Stack Overflow Resolution (ADR-011)
- **QueryResults reduced from 90KB to 6KB** (93% reduction)
  - Changed `TemporalResultEnumerator` from `ref struct` to `struct`
  - Pooled enumerator arrays in `MultiPatternScan` and `CrossGraphMultiPatternScan`
  - Boxed `GraphPattern` (~4KB) to move from stack to heap
- **All scan types dramatically reduced**:
  - `MultiPatternScan`: 18,080 → 384 bytes (98% reduction)
  - `DefaultGraphUnionScan`: 33,456 → 1,040 bytes (97% reduction)
  - `CrossGraphMultiPatternScan`: 15,800 → 96 bytes (99% reduction)
- **Parallel test execution restored** - Previously limited to single thread as workaround

### Changed

- Re-enabled parallel test execution in xunit.runner.json
- All 3,824 tests pass with parallel execution

### Documentation

- **ADR-011** completed - QueryResults Stack Reduction via Pooled Enumerators
- **StackSizeTests** added - Enforces size constraints to prevent regression

---

## [0.6.1] - 2026-01-26

Full W3C SPARQL 1.1 Query conformance achieved.

### Fixed

#### CONSTRUCT Query Fixes (5 tests now passing)
- **sq12** - Subquery computed expressions (CONCAT, STR) now propagate to CONSTRUCT output
  - Added `HasRealAggregates` to distinguish aggregates from computed expressions
  - Implemented per-row expression evaluation in subquery execution
- **sq14** - `a` shorthand (rdf:type) now correctly expanded in CONSTRUCT templates
- **constructwhere02** - Duplicate triple deduplication in CONSTRUCT WHERE
- **constructwhere03** - Blank node shorthand handling in CONSTRUCT WHERE
- **constructwhere04** - FROM clause graph context in CONSTRUCT WHERE

### Changed

#### W3C Conformance (100% core coverage)
- **SPARQL 1.1 Query**: 418/418 passing (previously 410/418)
- **SPARQL 1.1 Update**: 94/94 passing (unchanged)
- **Total W3C tests**: 1,872 passing

### Remaining Known Limitations
- `constructlist` - RDF collection syntax in CONSTRUCT templates (high complexity)
- `agg-empty-group-count-graph` - COUNT without GROUP BY inside GRAPH (high complexity)
- `bindings/manifest#graph` - VALUES binding GRAPH variable (high complexity)

---

## [0.6.0-beta.1] - 2026-01-26

Major W3C conformance milestone and CONSTRUCT/DESCRIBE content negotiation.

### Added

#### Content Negotiation for CONSTRUCT/DESCRIBE
- **RDF format negotiation** - Accept header parsing with quality values
- **Turtle output** (default) - Human-readable with prefix support
- **N-Triples output** - Canonical format for interoperability
- **RDF/XML output** - XML-based serialization

#### W3C Test Infrastructure
- **Graph isomorphism** - Backtracking search for blank node mapping
- **RDF result parsing** - Support for .ttl, .nt, .rdf expected results
- **CONSTRUCT test validation** - Previously skipped tests now enabled

### Changed

#### W3C Conformance (99% coverage)
- **Total tests**: 1,872 → 3,464 (W3C + internal)
- **SPARQL 1.1 Query**: 96% (215/224) - 9 skipped for SERVICE/entailment
- **SPARQL 1.1 Update**: 100% (94/94)
- **All RDF formats**: 100% conformance maintained

### Fixed

#### SPARQL Conformance Fixes
- **Unicode handling** - Supplementary characters (non-BMP) via System.Text.Rune
- **Aggregate expressions** - COUNT, AVG error propagation, HAVING multiple conditions
- **BIND scoping** - Correct variable visibility in nested groups
- **EXISTS/NOT EXISTS** - Evaluation in ExecuteToMaterialized path
- **CONCAT/STRBEFORE/STRAFTER** - Language tag and datatype handling
- **GRAPH parsing** - Nested group pattern handling
- **IN/NOT IN** - Empty patterns and expressions
- **GROUP BY** - Expression type inference

#### Parser Fixes
- **Turtle Unicode escapes** - \U escape sequences beyond BMP
- **Named blank node matching** - Consistent across parsers
- **Empty string literals** - Correct handling in result comparison

### Documentation

- **ADR-002** - Sky Omega 1.0.0 Operational Scope defined
- **ADR-010** - W3C conformance status updated
- **ADR-012** - Conformance fixes documented

---

## [0.5.0-beta.1] - 2026-01-01

First versioned release of Sky Omega Mercury - a semantic-aware storage and query engine with zero-GC performance design.

### Added

#### Storage Layer
- **QuadStore** - Multi-index quad store with GSPO ordering and named graph support
- **B+Tree indexes** - Page-cached indexes with LRU eviction (clock algorithm)
- **Write-Ahead Logging (WAL)** - Crash-safe durability with hybrid checkpoint triggering
- **AtomStore** - String interning with memory-mapped storage
- **Batch write API** - High-throughput bulk loading (~100,000 triples/sec)
- **Bitemporal support** - ValidFrom/ValidTo/TransactionTime on all quads
- **Disk space enforcement** - Configurable minimum free disk space checks

#### RDF Parsers (6 formats)
- **Turtle** - RDF 1.2 with RDF-star support, zero-GC handler API
- **N-Triples** - Zero-GC handler API + async enumerable
- **N-Quads** - Zero-GC handler API + async enumerable
- **TriG** - Full named graph support
- **RDF/XML** - Streaming parser
- **JSON-LD** - Near zero-GC with context handling

#### RDF Writers (6 formats)
- **Turtle** - With prefix support and subject grouping
- **N-Triples** - Streaming output
- **N-Quads** - Named graph serialization
- **TriG** - Named graph serialization with prefixes
- **RDF/XML** - Full namespace support
- **JSON-LD** - Compact output with context

#### SPARQL Engine
- **Query types** - SELECT, ASK, CONSTRUCT, DESCRIBE
- **Graph patterns** - Basic, OPTIONAL, UNION, MINUS, GRAPH (IRI and variable)
- **Subqueries** - Single and multiple nested SELECT
- **Federated queries** - SERVICE clause with ISparqlServiceExecutor
- **Property paths** - `^iri`, `iri*`, `iri+`, `iri?`, `path/path`, `path|path`
- **Filtering** - FILTER, VALUES, EXISTS, NOT EXISTS, IN, NOT IN
- **40+ built-in functions**:
  - String: STR, STRLEN, SUBSTR, CONTAINS, STRSTARTS, STRENDS, CONCAT, UCASE, LCASE, etc.
  - Numeric: ABS, ROUND, CEIL, FLOOR
  - DateTime: NOW, YEAR, MONTH, DAY, HOURS, MINUTES, SECONDS, TZ, TIMEZONE
  - Hash: MD5, SHA1, SHA256, SHA384, SHA512
  - UUID: UUID, STRUUID (time-ordered UUID v7)
  - Type checking: isIRI, isBlank, isLiteral, isNumeric, BOUND
  - RDF terms: LANG, DATATYPE, LANGMATCHES, IRI, STRDT, STRLANG, BNODE
- **Aggregation** - GROUP BY, HAVING, COUNT, SUM, AVG, MIN, MAX, GROUP_CONCAT, SAMPLE
- **Modifiers** - DISTINCT, REDUCED, ORDER BY (ASC/DESC), LIMIT, OFFSET
- **Dataset clauses** - FROM, FROM NAMED with cross-graph join support
- **SPARQL-star** - Quoted triples with automatic reification expansion
- **SPARQL EXPLAIN** - Query execution plan analysis

#### SPARQL Update
- INSERT DATA, DELETE DATA
- DELETE WHERE, DELETE/INSERT WHERE (WITH clause)
- CLEAR, DROP, CREATE
- COPY, MOVE, ADD
- LOAD (with size and triple limits)

#### Temporal SPARQL Extensions
- **AS OF** - Point-in-time queries
- **DURING** - Range queries for overlapping data
- **ALL VERSIONS** - Complete history retrieval

#### Query Optimization
- **Statistics-based join reordering** - 10-100x improvement on multi-pattern queries
- **Predicate pushdown** - 5-50x improvement via FilterAnalyzer
- **Plan caching** - LRU cache with statistics-based invalidation
- **Cardinality estimation** - Per-predicate statistics collection

#### Full-Text Search
- **TrigramIndex** - UTF-8 trigram inverted index (opt-in)
- **text:match()** - SPARQL FILTER function
- **Unicode case-folding** - Supports Swedish å, ä, ö and other languages

#### OWL/RDFS Reasoning
- **Forward-chaining inference** - Materialization with fixed-point iteration
- **10 inference rules**:
  - RDFS: subClassOf, subPropertyOf, domain, range
  - OWL: TransitiveProperty, SymmetricProperty, inverseOf, sameAs, equivalentClass, equivalentProperty

#### SPARQL Protocol
- **HTTP Server** - W3C SPARQL 1.1 Protocol (BCL HttpListener)
- **Content negotiation** - JSON, XML, CSV, TSV result formats
- **Service description** - Turtle endpoint metadata

#### Pruning System
- **PruningTransfer** - Dual-instance copy-and-switch compaction
- **Filtering** - GraphFilter, PredicateFilter, CompositeFilter
- **History modes** - FlattenToCurrent, PreserveVersions, PreserveAll
- **Verification** - DryRun, checksums, audit logging

#### Infrastructure
- **ILogger abstraction** - Zero-allocation hot path, NullLogger for production
- **IBufferManager** - Unified buffer allocation with PooledBufferManager
- **Content negotiation** - RdfContentNegotiator for format detection

### Architecture

- **Zero external dependencies** - Core Mercury library uses BCL only
- **Zero-GC design** - ref struct parsers, ArrayPool buffers, streaming APIs
- **Thread-safe** - ReaderWriterLockSlim with documented locking patterns
- **.NET 10 / C# 14** - Modern language features and runtime

### Testing

- **1,785 passing tests** across 62 test files
- **Component coverage**: Storage, SPARQL, parsers, writers, temporal, reasoning, concurrency
- **Zero-GC compliance tests** - Allocation validation

### Benchmarks

- **8 benchmark classes** - BatchWrite, Query, SPARQL, Temporal, Parsers, Filters, Concurrent
- **Performance baselines established** - Documented in CLAUDE.md

### Known Limitations

- SERVICE clause does not yet support joining with local patterns
- Multiple SERVICE clauses in single query not yet supported
- TrigramIndex uses full rebuild on delete (lazy deletion not implemented)

[1.3.8]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.8
[1.3.7]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.7
[1.3.6]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.6
[1.3.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.0
[1.2.2]: https://github.com/bemafred/sky-omega/releases/tag/v1.2.2
[1.2.1]: https://github.com/bemafred/sky-omega/releases/tag/v1.2.1
[1.2.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.2.0
[1.1.1]: https://github.com/bemafred/sky-omega/releases/tag/v1.1.1
[1.1.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.1.0
[1.0.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.0.0
[0.6.2]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.2
[0.6.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.1
[0.6.0-beta.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.0-beta.1
[0.5.0-beta.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.5.0-beta.1
