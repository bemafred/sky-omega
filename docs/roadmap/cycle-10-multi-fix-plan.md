# Cycle 10 — multi-fix plan with per-fix metric attribution

**Status:** Proposed — 2026-05-08
**Methodology shift:** first cycle to ship multiple architectural changes in a single release, with attribution via per-fix instrumentation rather than separate runs.

## Why a multi-fix cycle (the methodology shift)

Cycles 1 → 9 followed the discipline "one architectural change per cycle, gradient-validated, then ship." That made sense while we were uncertain about validation methodology. Cycle 9 confirmed it: ADR-037's projected ~5 h parser saving (from cycle 8 instrumentation) matched the measurement at 21.3 B almost exactly (4 h 57 m measured vs ~5 h projected). Prediction-from-instrumentation is now warranted.

That earned confidence is what permits compression. Each cycle costs ~36–50 h plus implementation/discussion overhead. With instrumentation that decomposes contributions, we can stack fixes per cycle and let the metrics attribute their effects, rather than running a separate cycle per fix.

The trade: interaction risk between fixes vs cycle-throughput gain. Mitigated by:

- Each fix has its own dedicated attribution metric (orthogonal to other fixes).
- Each fix passes gradient validation at small scale *individually* before composition.
- The cycle accepts that interactions are observable but not always separately attributable.
- If a fix is suspected of interacting badly with another, its metric will surface the mismatch — at which point we pivot.

This first multi-fix cycle is conservative on count and generous on instrumentation. Once the methodology is itself validated, future cycles can stack more aggressively.

## Fix queue for cycle 10

Three categories, six total fixes:

### Category A — Observability prerequisites (must land before perf fixes)

These are *not* perf optimizations themselves. They exist so the perf fixes can be measured properly. Without them, cycle 10's attribution would replicate cycle 9's silent-phase + lagged-emission symptoms.

| # | Fix | Limits register | Expected effect |
|---|---|---|---|
| A1 | `DrainProgressEvent` for GSPO drain phase | [`observability-discipline-systematic-not-reactive`](../limits/observability-discipline-systematic-not-reactive.md) | Drain-phase progress observable in real time (today's silent gap closed) |
| A2 | Time-based metric emission throttle | [`metric-emission-backpressure-on-shared-disk`](../limits/metric-emission-backpressure-on-shared-disk.md) | Metric channel emits every N seconds in addition to per-record-count, capping backpressure-on-shared-disk |

### Category B — Performance optimizations (the actual work)

| # | ADR | Falsifiable hypothesis | Attribution metric |
|---|---|---|---|
| B1 | [ADR-038 Part 1](../adrs/mercury/ADR-038-merge-read-side-optimization.md) — prefix-compress intermediate chunks | Intermediate volume drops 50–70 %; cache-fit ratio rises from 3 % to 9–12 % | `intermediate_volume_bytes` (new) — sum of chunk file sizes |
| B2 | [ADR-038 Part 2](../adrs/mercury/ADR-038-merge-read-side-optimization.md) — per-chunk frontier readahead | Long-tail-cold-cache regime shrinks or vanishes; merge wall-clock drops 20–40 % | merge regime distribution (rate histograms by quartile) + `read_ahead_hits/misses` (new) |
| B3 | [ADR-039](../adrs/mercury/ADR-039-mphf-on-sealed-atom-set.md) — MPHF over sealed atom set | `GetAtomId(string)` median 10–30× faster at 4 B atoms; query-side wall-clock drops proportionally | `mphf_construction_seconds` + `get_atom_id_ns` (sampled per-call latency) |

ADR-038 sidecar (per-chunk anchor offset table) is bundled with B1 + B2 — same chunk file format, same effort, single coherent change.

### Category C — Cheap experimental tunings (deferred unless time permits)

| # | Tuning | Expected effect | Attribution metric |
|---|---|---|---|
| C1 | Larger merge stream buffer 8 KB → 1 MB | More chunk data prefetched per syscall; smaller syscall count | `chunk_read_syscalls` (new) |
| C2 | `madvise(MADV_SEQUENTIAL)` on chunk files | Kernel readahead more aggressive within each chunk | Indirect — observed in B2's regime distribution if B2 doesn't already eliminate the dependency |

Drop these from cycle 10 unless A and B come together fast and there's headroom in the implementation calendar. Reason: each is a small win, and they may be redundant with B2's much larger effect.

## Sequencing

```
Phase 1: Observability (A1, A2)
  ├─ Implementation: ~1-2 days at AI pace
  ├─ Unit tests + per-listener thread-safety checks
  ├─ No gradient required (pure instrumentation, no behavior change)
  └─ Ship as 1.7.51

Phase 2: Performance Round 3 (B1, B2, B3)
  ├─ Implementation: ~3-5 days at AI pace
  │   ├─ B1 first (chunk file format change — gates B2's sidecar)
  │   ├─ B2 second (depends on B1's chunk format)
  │   └─ B3 in parallel (independent of B1+B2)
  ├─ Per-fix gradient at 1M/10M/100M for correctness
  │   ├─ B1: intermediate volume reduction validated
  │   ├─ B2: long-tail regime mitigation observed
  │   └─ B3: MPHF construction works, lookups correct, speedup observed
  ├─ Composite gradient at 100M with all three fixes layered
  └─ Ship as 1.7.52

Phase 3: Cycle 10 production validation
  ├─ Full 21.3 B Wikidata Reference + Sorted bulk-load + rebuild on 1.7.52
  ├─ Free disk preflight + cycle 9 store deletion if needed
  ├─ Detached launch via nohup (cycle 9 pattern)
  └─ Post-cycle attribution: read JSONL, decompose contributions per fix
```

Total elapsed Phase 1 + Phase 2 + Phase 3 ≈ 1 week assuming dedicated execution.

## Per-fix metric attribution discipline

For each fix in cycle 10, the JSONL must contain enough state to answer: "How much of the cycle 10 total improvement vs cycle 9 came from THIS fix?"

| Fix | Attribution method |
|---|---|
| A1 (DrainProgressEvent) | N/A — observability only |
| A2 (time-based emission) | Verify metric file lag < 30 s during high-bandwidth phases |
| B1 (intermediate compression) | `intermediate_volume_bytes` ratio: cycle 9 baseline / cycle 10 actual. Direct measurement. |
| B2 (frontier readahead) | Long-tail rate floor: cycle 9 (0.16 M/s) → cycle 10 (≥ 1 M/s if H2 holds). Compare regime histograms. |
| B3 (MPHF) | Sample `GetAtomId(s)` 10,000 times during WDBench rerun; compare median + p99 vs cycle 9 baseline (binary search). |

End-of-cycle validation doc (`docs/validations/cycle-10-{date}.md`) consolidates the per-fix attribution into one table that's apples-to-apples per fix.

## Falsification triggers (for any fix during gradient)

If a fix's gradient at 100 M shows:

- **B1: < 30 % intermediate volume reduction** → compression scheme is wrong (varint too aggressive or suffix-length encoding broken). Drop from cycle 10; revisit.
- **B2: long-tail rate floor unchanged** → frontier cache isn't the right fix; bottleneck is elsewhere (CPU? offsets file? resolver?). Drop from cycle 10; pivot to dotnet-trace investigation.
- **B3: MPHF construction time > 4 h at 100 M (extrapolating to > many hours at 4 B)** → BBHash implementation needs profiling; drop from cycle 10. OR construction works but lookup verification adds enough overhead to nullify the gain → pivot to a different MPHF variant.

If any fix fails its falsification trigger, drop it from the cycle 10 stack. The remaining fixes still ship; cycle 10 still runs. Don't compose more than three perf fixes at production scale on the first multi-fix cycle — increases interaction risk without proportional gain.

## Risk management

- **Hidden regression from one fix masked by another's improvement.** Mitigation: gradient-validate each fix individually against 1.7.50 baseline at 100 M. A regression there fails the falsification trigger and the fix doesn't enter cycle 10.
- **Concurrency interaction between B2 (readahead) and ADR-037 (pipelined spill).** Both touch concurrency. ADR-037 is parser-side; B2 is merge-side. Different phases, different threads, different mutexes. Should be independent — but worth explicit verification in the gradient.
- **MPHF construction adds bulk-load wall-clock.** Projected 30 min – 2 h at 4 B atoms. Cycle 10 total wall-clock budget: cycle 9's 35.6 h + MPHF construction time. If MPHF is the binding constraint on cycle 10's wall-clock improvement, that's expected (we trade build-time for amortized query-time gain).
- **Cycle 10 is the FIRST multi-fix cycle.** If attribution gets murky despite the per-fix metrics, treat that as a signal — the methodology needs refinement before the next compression. Future cycles inherit any attribution-discipline tweaks surfaced here.

## Comparison to cycle 9 — what to expect

| | Cycle 9 (measured) | Cycle 10 (projected from H1, H2, H3) |
|---|---:|---:|
| Phase A wall-clock | 26 h 20 m | ~22–24 h *(B1 reduces intermediate; B2 mitigates long-tail; merge phase shortens)* |
| Phase B wall-clock | 9 h 15 m | ~9 h *(no architectural change to rebuild; B3's MPHF doesn't apply here)* |
| MPHF construction | n/a | +30 min – 2 h *(new step inside Phase A)* |
| **Total wall-clock** | **35 h 35 m** | **~30–34 h projected** |
| Query-side `GetAtomId` | log₂ binary search | 10–30× faster median *(B3 win)* |
| WDBench rerun median | TBD | TBD; expected meaningful drop on bound-term-heavy queries |

The phrase "projected" is load-bearing here per [feedback_no_projection_baselines](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_projection_baselines.md): cycle 10 hasn't run yet. The numbers above are forecasts to be falsified by measurement. Cycle 10's measured values vs cycle 9's measured values is the only valid headline comparison.

## Open questions for review

1. Is conservative-on-count (3 perf fixes) the right starting size for the first multi-fix cycle, or is the cycle compression worth more aggressive stacking?
2. Should C1/C2 (cheap tunings) be folded into Phase 1 (since they're trivial) or held back as cycle 11+ candidates?
3. Should cycle 10 include the WDBench rerun in the same script, or is that a separate post-cycle step?
4. Pre-cycle 10 disk: cycle 9's `wiki-21b-ref-r2` is 2.13 TB — keep as comparison-baseline for query-side comparison, or delete to maximize free disk for cycle 10?

## References

- [ADR-038 — Merge-phase read-side optimization](../adrs/mercury/ADR-038-merge-read-side-optimization.md)
- [ADR-039 — MPHF on sealed atom set](../adrs/mercury/ADR-039-mphf-on-sealed-atom-set.md)
- [`docs/limits/observability-discipline-systematic-not-reactive.md`](../limits/observability-discipline-systematic-not-reactive.md)
- [`docs/limits/metric-emission-backpressure-on-shared-disk.md`](../limits/metric-emission-backpressure-on-shared-disk.md)
- Cycle 9 result: `urn:sky-omega:incident:cycle9-21b-complete-2026-05-08` (Mercury) — the baseline cycle 10 measures against
- [feedback_optimization_taxonomy](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_optimization_taxonomy.md) — algorithmic / architectural / avoiding-work / tuning all count as optimization
- [feedback_no_projection_baselines](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_projection_baselines.md) — measurement-vs-measurement discipline for headline comparisons
