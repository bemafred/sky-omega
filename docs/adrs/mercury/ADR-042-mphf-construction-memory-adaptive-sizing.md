# ADR-042: MPHF construction memory adaptive sizing — substrate-correct data shapes for 4 B-atom builds

## Status

**Status:** Completed — 2026-05-16 (Proposed 2026-05-11; Accepted 2026-05-16; Completed 2026-05-16 — Parts 1+4 shipped 1.7.60, Parts 2+3 shipped 1.7.62, Part 5 host-adaptive validation shipped 1.7.64 via shared `ProcessMemoryProbe`; projected peak RSS at 4 B atoms reduced from ~100 GB to ~15 GB; output byte-equivalence preserved at every slice)

## Context

The 1.7.55 `mercury --rebuild-mphf` run against the cycle-10 sealed atoms (4,005,235,528 atoms) measured the substrate's peak working-set RSS at **89–102 GB** during level-0 BBHash construction — ~80 % of available RAM on the 128 GB substrate target host. This is ~3× the readahead phase's projected 31 GiB peak (ADR-040). MPHF construction, not readahead, is the substrate's binding memory constraint at 4 B atom scale.

ADR-040 makes the *merge phase* host-adaptive. It does not address MPHF, which runs after MergeAndWrite returns the sealed atom store and operates on its own memory budget. ADR-042 closes the host-portability gap by making MPHF construction substrate-correct: every in-memory structure in `BBHashBuilder.Build` either reduces to the minimum necessary for correctness, or streams to mmap-backed disk storage instead of holding peak-shape RAM.

The current implementation prioritizes algorithmic clarity (three passes per level, with intermediate position storage) over memory minimality. At gradient scales (≤ 100 M atoms), the wasteful shape is invisible. At 4 B atoms, it dominates the substrate's host-portability surface.

### Measured baseline (1.7.55 rebuild-mphf, 2026-05-11)

Direct decomposition of the 100 GB peak observed at level 0:

| Structure | Allocation | Necessary? | Why current is wasteful |
|---|---:|---|---|
| `remaining = ChunkedList<long>` populated with [1..N] | 32 GB | **No** at level 0 | Level 0 input is the dense range 1..N — a `for (long i = 1; i ≤ N; i++)` loop expresses it without allocation. Only at level 1+ does `remaining` need to be the bumped subset. |
| `translation = ChunkedArray<long>(N)` held in RAM until `WriteTo` | 32 GB persistent | **No** | Final destination is `atoms.idx` — a uint32 mmap'd file. The in-memory copy is only needed because `MphfTranslationTable.WriteTo` is called once at the end with the full array. Streaming write to a memory-mapped output as positions get assigned eliminates the in-memory peak. |
| `keyPositions = ChunkedArray<long>(remainingCount)` per level | 32 GB at level 0, decaying | **No** | The three-pass algorithm stores positions in pass 1, reads them in pass 2 (place/bump) and pass 3 (translation fill). A re-hash approach computes positions on demand in passes 2 and 3 — adds ~3 ns × N hashes per re-pass = ~12 sec extra CPU at level 0, eliminates the entire 32 GB ChunkedArray. |
| Per-atom `byte[]` allocations from `getKey(i)` = `atoms.GetAtomSpan(i).ToArray()` | ~770 GB of GC churn across all passes | **No** | `SplitMix64Hash.Hash64` already accepts `ReadOnlySpan<byte>`. The `Func<long, byte[]>` API forces an allocating copy; a `delegate void GetKeyTo(long, IBufferWriter<byte>)` or `Span<byte> GetKey(long)` returning a span into a caller-owned scratch buffer eliminates per-call allocation. |

**Total achievable peak working set at 4 B atoms:** ~15 GB (BitVectors at level 0 + `bumped` transient at level 0→1).
**Current peak:** ~100 GB.
**Reduction:** 6–7×.

This is "substrate-vs-MVP" discipline — the current implementation works on the 128 GB target host but cannot run on a 32 GB or 64 GB host even though the algorithm is fundamentally O(N) memory in the BitVectors alone.

## Hypothesis (falsifiable, four-part)

**H1 — Range iterator at level 0 reduces level-0 peak by 32 GB with no correctness impact.** Replacing the `remaining = ChunkedList<long>` fill loop at level 0 with a direct `for (long i = 1; i ≤ N; i++)` iteration in the level-0 hash loops produces byte-identical translation output and byte-identical `atoms.mphf` + `atoms.idx` files.

**Falsified if:** any output file differs by a single byte vs the 1.7.55 reference, OR memory savings less than 30 GB.

**H2 — Memory-mapped streaming translation eliminates 32 GB persistent allocation.** Replacing `ChunkedArray<long>` translation with a memory-mapped `uint32[N]` file (the `atoms.idx` output format directly, with the 16-byte header pre-written) and writing each assigned position as the iterative phase places it produces byte-identical `atoms.idx` output.

**Falsified if:** `atoms.idx` byte-differs vs the 1.7.55 reference, OR kernel page-cache pressure from the mmap'd file degrades overall build wall-clock by > 10 %.

**H3 — Re-hash second pass eliminates 32 GB per-level `keyPositions` scratch.** Replacing the three-pass-with-stored-positions structure with two-passes-plus-rehash produces byte-identical output for ~12 sec extra CPU at level 0 (negligible vs the multi-minute level-0 wall-clock).

**Falsified if:** byte-difference vs reference, OR extra CPU cost > 60 sec at level 0 (4× the projection).

**H4 — Span-based `GetKey` API eliminates GC pressure during construction.** Replacing `Func<long, byte[]>` with a callback that fills a caller-owned `Span<byte>` (sized to the maximum atom byte length, or a per-call writeable buffer pool) reduces gen-0 GC frequency during MPHF construction from "thousands of collections per minute" to single-digit collections across the entire build.

**Falsified if:** measured gen-0 collections during MPHF construction unchanged from the byte[]-allocating baseline.

The four hypotheses compose: H1 + H3 are zero-risk reductions; H2 trades RAM for kernel-managed mmap; H4 trades API surface for GC quiet.

## Decision

### Part 1 — Range iterator at level 0

Replace:

```csharp
var remaining = new ChunkedList<long>();
for (long i = 1; i <= keyCount; i++) remaining.Add(i);
```

With a level-0 special path that iterates over [1..keyCount] directly without materializing the list:

```csharp
// Level 0: iterate keys 1..N directly, no allocation
for (int levelIdx = 0; levelIdx < _maxLevels; levelIdx++)
{
    long remainingCount = levelIdx == 0 ? keyCount : remaining!.Count;
    Func<long, long> getInputIdx = levelIdx == 0
        ? k => k + 1                       // 1-based input index
        : k => remaining![k];              // from previous level's bumped set
    ...
}
```

Level 0 saves 32 GB. Levels 1+ retain the existing ChunkedList structure (necessary because `bumped` is a sparse subset of the previous level's `remaining`).

### Part 2 — Memory-mapped streaming translation

`MphfTranslationTable` is already mmap-backed on the read path. Generalize for write:

1. At `Build` start, create the output `atoms.idx` file at full size (`16 + 4 × N` bytes) via `FileStream.SetLength` + `MemoryMappedFile.CreateFromFile(..., MemoryMappedFileAccess.ReadWrite)`.
2. Write the 16-byte header (magic + version + entry-count + reserved).
3. Hand the `MemoryMappedViewAccessor` to `BBHashBuilder.Build` instead of receiving a `ChunkedArray<long>` back. Build writes each assigned position via `view.Write(16 + 4 * mphfPos, (uint)inputIdx)` as positions are computed in passes 2 and 3.
4. At Build end, flush the mmap.

The kernel page cache handles the working set — sequential writes have excellent locality, the substrate doesn't fight for anonymous memory.

`BBHashBuilder.BuildResult` signature changes:

```csharp
public sealed record BuildResult(BBHash Mphf);   // Translation removed — written directly to atoms.idx
```

### Part 3 — Re-hash strategy (eliminate keyPositions)

Restructure each level from three passes to two:

**Pass 1 (collision detection):** for each remaining key, hash → position → set `seen[pos]`, set `collided[pos]` if `seen[pos]` was already set. Discard the position. Does NOT store positions.

**Pass 2 (place + bump + translate):** for each remaining key, re-hash → position. If `!collided[pos]`: set `bv[pos]`, compute `mphfPos = globalOffset + bv.Rank(pos)`, write `translation[mphfPos] = inputIdx` via mmap. Else: add `inputIdx` to `bumped`.

The hash is computed twice per key. At ~3 ns/hash × N keys × `0.39^levelIdx` ≈ 12 sec extra CPU at level 0, 5 sec at level 1, 2 sec at level 2 — single-digit total seconds across all levels at any N. The 32 GB allocation savings dwarf the CPU cost.

**Note:** `bv.Rank(pos)` requires the rank table to be built before pass 2. So pass 2 must run AFTER `bv.BuildRankTable()`. Sequence:

1. Pass 1 (hash all, populate seen + collided)
2. Iterate seen bits in order, set `bv` for non-collided positions (alternatively: re-derive bv from `seen AND NOT collided`)
3. `bv.BuildRankTable()`
4. Pass 2 (hash all, place or bump)

Or alternative ordering that saves one pass: after pass 1, `bv` can be computed bit-vector-arithmetic-style: `bv_words[i] = seen_words[i] & ~collided_words[i]`. Same result without iterating individual bits. This is two passes total over keys + a linear bit-vector scan, vs the current three passes.

### Part 4 — Span-based GetKey API

New delegate signature:

```csharp
public delegate ReadOnlySpan<byte> GetKeyDelegate(long inputIndex, scoped Span<byte> scratch);
```

The caller provides a `scratch` buffer (sized to the max atom byte length — known from `SortedAtomStore.MaxAtomByteLength` at construction time). `getKey` copies atom bytes into the scratch and returns a `ReadOnlySpan<byte>` slice over the written portion. The caller's buffer is reused across calls — zero allocations per atom.

`BBHashBuilder.Build` signature:

```csharp
public BuildResult Build(long keyCount, int maxAtomByteLength, GetKeyDelegate getKey)
```

Internal:

```csharp
Span<byte> scratch = stackalloc byte[maxAtomByteLength <= 1024 ? maxAtomByteLength : 0];
byte[]? scratchHeap = maxAtomByteLength > 1024 ? new byte[maxAtomByteLength] : null;
Span<byte> buffer = scratchHeap is not null ? scratchHeap.AsSpan() : scratch;
// ... in hash loop:
var keyBytes = getKey(inputIdx, buffer);
ulong h = SplitMix64Hash.Hash64(keyBytes, seed);
```

For Wikidata URIs, the typical length is ~60 bytes; outliers up to a few KB. `maxAtomByteLength = 4096` covers all practical cases via a single heap-allocated 4 KB buffer reused across the entire build.

### Part 5 — Observability: `MphfBuildBudgetEvent` + per-level progress

The current build is a complete black box from the operator's perspective — no progress events between Build start and Build end. The 1.7.55 rebuild-mphf run sat silent for an hour while RSS climbed past 100 GB; we had no signal whether it was healthy or stuck.

New structured events:

```csharp
public sealed class MphfBuildStartedEvent : MetricEvent
{
    public string Phase => "mphf_build_started";
    public long KeyCount;
    public double Gamma;
    public int MaxLevels;
    public int MaxAtomByteLength;
    public long ProjectedPeakBytes;       // for budget visibility
    public long AvailableMemoryBytes;
}

public sealed class MphfLevelCompletedEvent : MetricEvent
{
    public string Phase => "mphf_level_completed";
    public int LevelIndex;
    public long KeysAtStart;
    public long KeysPlaced;
    public long KeysBumped;
    public long BitCount;
    public double ElapsedSeconds;
    public long PeakRssBytes;             // captured at level end
}

public sealed class MphfBuildCompletedEvent : MetricEvent
{
    public string Phase => "mphf_build_completed";
    public int LevelCount;
    public long DenseKeyCount;
    public double TotalElapsedSeconds;
    public long FinalRssBytes;
    public long MphfBytes;
    public long IdxBytes;
}
```

Per-level events let the operator track convergence shape, level wall-clock, and memory shape during multi-hour builds. Tied into the standard `metrics-out` JSONL channel.

### Part 6 — Host-adaptive validation

ADR-040's `ProcessMemoryProbe` (P/Invoke for available physical bytes) probes the host before MPHF construction. If `availableBytes < projectedPeakBytes × 1.2`, the substrate logs a warning + can optionally throw to refuse the build. Threshold tunable via `MERCURY_MPHF_MEMORY_FRACTION` env var (default 0.8 — substrate uses up to 80 % of available).

Parts 1–4 together drop the projected peak from 100 GB to 15 GB at 4 B atoms, making a 32 GB host workable. Part 6 surfaces the projection as data.

## Consequences

### Positive

- **Substrate becomes host-portable at the MPHF layer.** 32 GB / 64 GB hosts can construct 4 B-atom MPHFs that today only fit on 128 GB+ hosts.
- **GC pressure during MPHF disappears.** Span-based key access eliminates ~770 GB of byte[] churn across a 4 B-atom build. Gen-0 collection frequency drops from many per minute to near-zero. JIT can optimize hot paths it couldn't before.
- **MPHF construction becomes observable.** The 1.7.55 black box becomes a progressive event stream — per-level convergence, RSS samples, dense-set size all queryable from JSONL.
- **Substrate-correct shapes.** Range iteration where it's a range; mmap where the output is a mmap'd file; rehash where storing positions costs 32 GB. The implementation matches the algorithm's shape rather than caching for convenience.

### Negative / risks

- **Re-hash adds CPU cost.** ~3 ns per hash × N keys per pass × number of passes = single-digit seconds total at any N. Negligible vs the multi-minute build wall-clock, but it is a non-zero cost where the current implementation is free.
- **mmap-backed translation creates kernel page-cache pressure during build.** Sequential write pattern means the kernel can stream pages out efficiently, but if the build phase runs concurrent with anything else that wants page cache (e.g., other Mercury processes), contention may surface.
- **Span-based API is a breaking change to `Func<long, byte[]>`.** Internal API — only `SortedAtomStoreExternalBuilder.BuildMphfFiles` calls it today; tests use the inline lambda form. Migration: ~50 lines across two callers + 10 test files.
- **Build observability adds JSONL event churn.** ~50 levels × 1 event each = trivial; not a concern.

### Neutral

- File format unchanged (`atoms.mphf` and `atoms.idx` produced by 1.7.56 must byte-equal those produced by 1.7.55 against the same `atoms.atoms` and same `baseSeed`). The validation plan asserts this directly.

## Validation plan

1. **Reference output preserved.** Run `mercury --rebuild-mphf wiki-21b-ref-r3` on 1.7.55 and 1.7.56 (post-implementation) against the same `atoms.atoms`. SHA-256 of `atoms.mphf` and `atoms.idx` must match exactly. Falsifiability: if either file byte-differs, ADR-042 is broken.
2. **Memory profile.** Capture peak RSS during both runs via `vmmap` or `Process.GetCurrentProcess().WorkingSet64` sampled every 5 s. 1.7.56 peak should be < 20 GB at 4 B atoms; 1.7.55's peak was 100 GB.
3. **Wall-clock parity.** End-to-end MPHF build wall-clock should be within ±10 % of 1.7.55. Re-hash CPU cost is bounded; GC quiet should compensate.
4. **GC observation.** Use the substrate's existing GC sampling event in `--metrics-out` to compare gen-0 collection count during MPHF construction between versions. Expect order-of-magnitude reduction in 1.7.56.
5. **Gradient validation.** Run `--rebuild-mphf` at 10 M, 100 M, 1 B, 4 B atom scales. Wall-clock should scale linearly; peak RSS should grow with `BitCount + bumped` only.
6. **Host-constrained validation.** Run on a 32 GB cloud VM at 100 M atoms (small enough to fit) and at 1 B atoms (would OOM today). The 1 B-atom run must complete on 1.7.56.

Validation document: `docs/validations/adr-042-mphf-construction-memory-{date}.md`.

## Alternatives considered

- **Stay with current implementation; document the 128 GB host requirement.** Punts substrate-portability. Inconsistent with the MIT-licensed substrate-discipline call. Rejected.
- **Reduce gamma below 2.0.** Smaller `bv` per level but more bumps per level → more levels needed. Doesn't reduce the dominant `translation` / `keyPositions` / `remaining` allocations. Rejected as orthogonal.
- **Disk-backed `keyPositions` instead of re-hash.** Mmap-backed temp file for positions; eliminates RAM but adds disk I/O. Rehash is simpler and the CPU cost is below noise. Rejected.
- **`ArrayPool<byte>` for getKey byte arrays.** Reduces gen-0 churn but still allocates; the pool's free list itself takes space. Span-based API is strictly better. Rejected.
- **Refactor BBHashBuilder to use disk-backed levels (read atoms incrementally).** Goes the other direction — pulls work onto disk for memory savings. The substrate's atoms.atoms is already mmap-backed; the level work is in-memory because it must be (random access into ~γN-bit BitVectors). The "wasteful" structures are wasteful for orthogonal reasons (API shape), not because the work is fundamentally memory-bound. Rejected as misframing.

## References

- ADR-040 — Readahead memory adaptive sizing (sibling; covers the *other* memory phase). ADR-040 alone does not deliver substrate host-portability — both ADRs are required.
- ADR-039 — MPHF on sealed atom set (the algorithm this ADR optimizes the substrate implementation of)
- 1.7.55 release notes (CHANGELOG.md) — introduces BBHash dense final-level + `mercury --rebuild-mphf`; the rebuild-mphf measurement that surfaced this ADR
- 1.7.55 rebuild-mphf log: `docs/validations/cycle10-phase3-21b-resume-2026-05-11.log`
- `src/Mercury/Storage/Mphf/BBHashBuilder.cs` — current wasteful implementation
- `src/Mercury/Storage/Mphf/MphfTranslationTable.cs` — write path that ADR-042 generalizes to stream
- [feedback_resource_limit_class_audit](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_resource_limit_class_audit.md) — substrate discipline: every interaction with a host-resource must be characterized, bounded; obscured contracts get runtime guards
- [feedback_no_vibe_coding](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_vibe_coding.md) — "Substrate-level work is built right the first time" — the 1.7.55 implementation is functionally correct but substrate-incorrect; ADR-042 is the right-first-time pass
