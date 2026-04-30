# Phase 7c Round 1 — SortedAtomStore + Parallel BZip2

**Status:** Planned — 2026-04-30. Round 1 of the Phase 7c performance-rounds sequence. Combines the in-flight ADR-034 close-out with parallel bz2 decompression in a single ingest-side round, validated end-to-end at 21.3 B Wikidata.

**Inputs:**
- WDBench cold baseline 1.7.47 sealed against `wiki-21b-ref` ([reproduction recipe](../validations/wdbench-2026-04-29-reproduction.md), tag `v1.7.47-wdbench-baseline`)
- ADR-034 SortedAtomStore Accepted, Phase 1B-5d disk-backed AssignedIds Completed (commit `d832702`)
- ADR-035 Phase 7a metrics infrastructure Completed (1B baseline at [`adr-035-phase7a-1b-2026-04-27.md`](../validations/adr-035-phase7a-1b-2026-04-27.md))
- ADR-036 BZip2 streaming substrate: Phase 1 single-threaded Completed (1.7.45); Phase 2 block-parallel Completed (commit `a11f873`); throughput measured (commits `b39f186`, `0c3b1ae`) — 2.62× ceiling, scanner-bound
- `wiki-21b-ref` retired from disk; SSD headroom recovered for Round 1's transient peak

**Output:** `wiki-21b-ref-r1` — sealed Reference store on SortedAtomStore + parallel-bz2-streamed source. Successor to the retired v1.

## Scope

### In

- **ADR-034 SortedAtomStore Phase 1 close-out**
  - Phase 1B-5d — disk-backed AssignedIds for >100 M scale
  - Phase 1B-6 — gradient validation 1 M / 10 M / 100 M against HashAtomStore baseline
  - Phase 3 — 21.3 B Reference end-to-end with HashAtomStore Phase 6 baseline (85 h) as comparator
  - Phase 4 — Status Accepted → Completed
- **ADR-036 Phase 2 — `ParallelBZip2DecompressorStream`**
  - Promotes [`bz2-decompression-single-threaded.md`](../limits/bz2-decompression-single-threaded.md) Latent → Resolved
  - Added as Phase 2 within ADR-036 (parallel to ADR-035's sub-phase pattern)
  - Conservative worker count — `Environment.ProcessorCount / 3` ≈ 6 workers on M5 Max (leaves 12 cores for parser + Mercury internals)
- **Phase 7a metrics overlay** on every gradient point — capability already shipped, just enabled via `--metrics-out`
- **`dotnet-trace` overlay on the 1B run only** — F1 telemetry lap; not on the 21.3B headline run, to keep that number free of sampling overhead

### Out — deferred to later rounds, evidence-first

- **ADR-034 Phase 2 BBHash MPHF.** Defers to a sub-round between Round 1.5 and Round 2 — *only if* Round 1.5's WDBench trace identifies binary-search lookup as a long-tail driver. ADR-034 Phase 4 transitions Accepted → Completed with Phase 1 only; Phase 2 is a separately measured ship.
- **AtomStore prefix compression + bit-packed atom IDs.** Round 2. Both gated on Round 1's sorted layout sealing.
- **A/B comparison vs Phase 6 HashAtomStore at 21.3 B on the same hardware.** v1 substrate is retired. Comparison is JSONL-to-JSONL against the 04-27 1 B Reference baseline (300 K triples/sec, 55 m 22 s) and the Phase 6 85 h headline. If a dispute arises, the [reproduction recipe](../validations/wdbench-2026-04-29-reproduction.md) regenerates v1 from source + commit.

## Decisions locked at kickoff

The following were confirmed during the Round 1 plan review:

| # | Decision | Value |
|---|---|---|
| 1 | MPHF Phase 2 inclusion | **Out of Round 1.** Defers to evidence-first sub-round after Round 1.5 trace. |
| 2 | Parallel bz2 worker count | **Re-revised 2026-04-30 after measurement: 4 workers when used.** Initial call "conservative ~6"; revised to "aggressive ~14 (80%)" under unmeasured ~9× projection; re-revised to **4** after measurement (commits `b39f186`, `0c3b1ae`) showed scanner-bound 2.62× ceiling — workers beyond 4 idle. Parallel bz2 reframed as **convert-path optimization** (~57% wall-clock reduction), **not** bulk-load enabler. Bulk-load uses single-threaded bz2 (parser-bound at 17.5 MB/s, single-threaded bz2 at 30 MB/s already exceeds). See ADR-036 Phase 2 §"Workload-separated verdict". |
| 3 | Plan persistence | **This document.** `docs/roadmap/phase-7c-round-1.md`, citable, durable. |
| 4 | Substrate naming | **`wiki-21b-ref-rN` keyed to the round.** Round 1 produces `wiki-21b-ref-r1`; Round 2 produces `wiki-21b-ref-r2`; `wiki-21b-ref` symlinks to current. The retired Phase 6 substrate predates the convention and keeps its identity via `v1.7.47-wdbench-baseline` tag + recipe. |
| 5 | Run cadence | **21.3 B over long weekend, background process.** Steps 1–4 (gradient + 1 B trace lap) run mid-week; step 5 (21.3 B headline) starts Friday-evening-class, runs unattended, status checks via JSONL tail + summary. |

## Sequence

| # | Step | Substrate | Wall-clock | Artifact | Status |
|---|---|---|---:|---|---|
| 1 | ADR-034 Phase 1B-5d disk-backed AssignedIds | code | hours | impl + unit tests | ✅ commit `d832702` |
| 2 | ADR-036 Phase 2 `ParallelBZip2DecompressorStream` | code | hours | impl + unit tests + measurement | ✅ commits `a11f873`, `b39f186`, `0c3b1ae` |
| 3 | Gradient 1 M / 10 M / 100 M Reference (SortedAtomStore + **single-threaded bz2** in production path) | three small stores | hours each | correctness equivalence vs HashAtomStore + perf JSONL per scale | ⏭ next |
| 4 | 1 B Reference end-to-end (gradient close-out + trace lap) | `wiki-1b-ref-r1-trace` | ~30–40 min projected | `adr-034-1b-2026-XX-XX.md` + JSONL + dotnet-trace `.nettrace` | ⏭ |
| 5 | 21.3 B Reference end-to-end (headline run) | `wiki-21b-ref-r1` | ~50–60 h projected (1.4× from 85 h, conservative) | `adr-034-21b-2026-XX-XX.md` + JSONL only (no trace) | ⏭ |
| 6 | Substrate r1 reproduction recipe + tag | git | minutes | `wdbench-2026-XX-XX-r1-reproduction.md` + `v1.7.4X-round1-baseline` | ⏭ |
| 7 | ADR transitions | docs | minutes | ADR-034 Accepted → Completed; ADR-036 Phase 2 Completed (✅ done already); bz2 limit Latent → Resolved | partial — ADR-036 Phase 2 done |

**Note on Step 3 production-path bz2:** the gradient + 1 B + 21.3 B runs use single-threaded `BZip2DecompressorStream`. Per ADR-036 Phase 2 measurement, parallel bz2 doesn't help bulk-load (parser-bound at 17.5 MB/s; single-threaded already produces 30 MB/s). Parallel bz2 stays available as a capability for convert workloads. The original framing — *"parallel bz2 enables cheap two-pass-over-source"* — was based on an unmeasured projection that didn't survive measurement; the architectural conversation about two-pass-vs-single-pass is now a parser-walks-twice (~36 h at 21.3 B) vs intermediate-disk (~5 TB) trade-off, not a bz2-throughput trade-off.

## Validation gates

Each gradient step has a hard gate before promoting to the next. A failed gate halts and demands root-cause before the next scale.

| Scale | Gates |
|---|---|
| 1 M | Bit-for-bit query equivalence vs HashAtomStore on a 200-query test corpus. Single divergence → halt. |
| 10 M | Equivalence + throughput ≥ HashAtomStore (no regression). Regression → root-cause before 100 M. |
| 100 M | Equivalence + perf delta + Phase 7a metric channel sanity. HashAtomStore probe-distance histogram clean curve; SortedAtomStore binary-search-depth-histogram per ADR-034 Decision 8. |
| 1 B | Trace-driven hotspot attribution. Compare to `adr-035-phase7a-1b-2026-04-27.md`. Surprises (bz2 producer-side blocking, vocab-build pass beyond 1–2 h estimate, GC pressure not seen at 100 M) → halt and revise before 21.3 B commitment. |
| 21.3 B | **Pre-committed halt criteria:** (a) bulk rate < 200 K triples/sec sustained > 1 h (projects > 70 h end-to-end); (b) `--min-free-space 200 GB` floor crossed; (c) any rebuild phase > 4× its 1 B-projection. |

The 1 B trace-lap is the F1 telemetry corner. If it's clean, the 21.3 B run is just executing the validated config at scale. If it isn't, we don't commit a long weekend to a known-bad config.

## Storage discipline

ADR-034 documents ~1 TB transient disk during 21.3 B vocab build:

- Pass-1 vocab spill (external sort of `(stringToken, triple-index, position)` records): ~340 GB transient
- Pass-2 GSPO sort (per ADR-033): ~680 GB transient
- Final r1 store: probably ~1.8–2.2 TB (smaller than v1's 2.5 TB if SortedAtomStore packs more compactly — actual size measured during the run)
- Source `latest-all.ttl.bz2`: ~110 GB read-only (can live elsewhere)

**Worst-case peak: source + Pass-1-temp + Pass-2-temp + partially-built-store.** Projected to stay under 3 TB; tracked live via Phase 7a `disk_free` channel. With v1 retired, current free is ~3.0 TB on the laptop NVMe — comfortable but not unbounded. **`--min-free-space 200 GB` enforced throughout.**

## Background-process discipline

The 21.3 B headline run (step 5) is unattended for ~50–60 h over a long weekend.

**Pre-run checks (Friday):**
- `df -h` — confirm ≥ 3 TB free on `~/Library/SkyOmega/stores`
- 1 B trace-lap (step 4) clean and signed off
- `latest-all.ttl.bz2` SHA pinned in run notes
- Mercury commit pinned, tag drafted (e.g. `v1.7.4X-round1-baseline`)
- `--metrics-out`, `--metrics-state-interval 60`, `--no-repl` all set
- Output redirected (`nohup` or screen/tmux) so terminal disconnect doesn't kill the process
- Lid-close behavior verified (caffeinate or external power + display-sleep-only)

**Status checks during the run:**
A "Status?" check resolves to: tail the most recent N records of the JSONL, summarize as elapsed wall-clock + triples loaded + current rate + GC-heap + RSS + disk-free. Phase 7a's existing channels make this trivial — no new infrastructure needed.

Suggested status one-liner (returns a one-screen summary):

```bash
mercury wdbench-status --jsonl ~/SkyOmega/runs/r1-21b-2026-XX-XX.jsonl
```

If the harness doesn't already have a `--status` shape, this is a small ~1 hr addition during step 1 — worth it.

**Halt-and-recover:**
- Hardware fault / lid-close / power: bulk-load resumes from the last chunk-flush boundary if the JSONL trail is intact (existing Phase 6 behavior; verify still holds with SortedAtomStore Pass 1's intermediate state).
- Halt-criteria triggered: kill the process, archive the JSONL + partial store, root-cause from the trail before retry.

## What Round 1 produces

- **`wiki-21b-ref-r1`** — sealed Reference store, SortedAtomStore Phase 1 (no MPHF), parallel-bz2-streamed source. Successor to retired v1. Subject to its own retirement at end of Round 2.5 by the same discipline.
- **Symlink `wiki-21b-ref` → `wiki-21b-ref-r1`** — scripts and the WDBench harness target the symlink; no path edits needed across rounds.
- **Tag `v1.7.4X-round1-baseline`** on the Mercury commit that built the substrate.
- **JSONL artifacts** in `docs/validations/`:
  - `adr-034-1b-2026-XX-XX.md` + `.jsonl` (1 B trace-lap)
  - `adr-034-21b-2026-XX-XX.md` + `.jsonl` (21.3 B headline)
  - `parallel-bz2-1b-2026-XX-XX.md` (or absorbed into the ADR-034 1B note if cleaner)
- **Reproduction recipe** at `docs/validations/wdbench-2026-XX-XX-r1-reproduction.md`, mirroring the 04-29 pattern.
- **ADR-034 Completed** (Phase 1 only), **ADR-036 Phase 2 Completed**, **`bz2-decompression-single-threaded.md` Resolved**.

## Round 1.5 (immediately after Round 1)

Fresh WDBench cold baseline against `wiki-21b-ref-r1` — same 1,199 queries (660 paths + 539 c2rpqs), same harness, same 60 s cancellation cap.

- **Comparison:** JSONL-to-JSONL against the 04-29 baseline. Same harness, post-Round-1 substrate. The win surface is the long tail (p95 = 29.85 s, p99 = 49.50 s in the v1 baseline).
- **Trace overlay:** this run *does* get a `dotnet-trace` overlay — separate run from the clean baseline JSONL, same pattern as the 1 B trace lap. The trace tells us:
  - Whether SortedAtomStore binary search shows up in the long tail → promote MPHF Phase 2 to its own micro-round
  - Whether atom-store memory shape is now load-bearing on query latency → confirms Round 2 prefix compression value
  - What's left in the long tail that *neither* Round 1 nor Round 2 will touch → Round 3 candidates

Round 1.5 produces a new disclosure-marked baseline + the trace artifact that sequences the rest of Phase 7c.

## References

- [ADR-034 SortedAtomStore for Reference](../adrs/mercury/ADR-034-sorted-atom-store-for-reference.md) — the architectural decision; Phase 1 implementation closes here, Phase 2 deferred per evidence-first
- [ADR-036 BZip2 Streaming Decompression](../adrs/mercury/ADR-036-bzip2-streaming-decompression.md) — Phase 1 substrate Completed; Phase 2 parallel decoder added in this round
- [`docs/limits/bz2-decompression-single-threaded.md`](../limits/bz2-decompression-single-threaded.md) — promoted Latent → Resolved when Phase 2 ships
- [`docs/limits/bit-packed-atom-ids.md`](../limits/bit-packed-atom-ids.md) — unblocked by Round 1's dense sequential ID assignment; ships in Round 2
- [`docs/limits/atomstore-prefix-compression.md`](../limits/atomstore-prefix-compression.md) — gated on Round 1's sorted layout; ships in Round 2
- [`docs/validations/adr-035-phase7a-1b-2026-04-27.md`](../validations/adr-035-phase7a-1b-2026-04-27.md) — 1 B Reference baseline (HashAtomStore + single-threaded bz2); the comparator for Round 1's 1 B trace lap
- [`docs/validations/wdbench-2026-04-29-reproduction.md`](../validations/wdbench-2026-04-29-reproduction.md) — v1 substrate retirement + reproduction recipe
- [`docs/roadmap/production-hardening-1.8.md`](production-hardening-1.8.md) — the parent roadmap; this document is the Round 1 sub-plan under the `7c-rounds` row
- [ADR-008 Workload Profiles and Validation Attribution](../adrs/ADR-008-workload-profiles-and-validation-attribution.md) — every measurement in this round is Reference-profile
