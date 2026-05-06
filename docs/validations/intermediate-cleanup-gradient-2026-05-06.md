# Intermediate cleanup hook — 1M/10M/100M smoke + observability check (2026-05-06)

**Status (2026-05-06):** Round 2 #2 — phase-transition cleanup hook in `SortedAtomStoreExternalBuilder.MergeAndWrite`. Unit test covers correctness (chunks deleted, counters match). The 1M/10M/100M sweep below was run as a smoke + observability check, not as a perf gradient — the change is off the hot path and has no plausible regression mechanism. The gradient was over-scoped for this change class; kept as artefact rather than discarded because the JSONL is real data. The actual production claim — 3.6 TB reclaimed at end-of-merge instead of end-of-run — manifests only at 21.3B and is deferred to cycle 9.

## What this exercise proves (and what it does not)

Cycle 8 (2026-05-04/05) surfaced that `MergeAndWrite` consumes occurrence chunks but does not delete them — at 21.3B Wikidata, 3.6 TB of post-merge chunks were held until run end, putting the GSPO drain phase within ~600 GB of `MinimumFreeDiskSpace` abort. On smaller hosts, the same orphan tail would have aborted the run mid-flight. See `docs/limits/intermediate-cleanup-deferred-to-run-end.md` for the surfacing trace.

The fix (mitigation 1 from the limits doc): delete chunk files in the `MergeAndWrite` `finally` block, immediately after readers Dispose. Each reader's Dispose calls `_pool.Drop(_path)` which closes the underlying FD, so `File.Delete` is safe at that point. `MergeCompletedEvent` extended with `ChunksDeleted` + `ChunkBytesReclaimed` so the cleanup is observable from JSONL without re-running with extra logging.

What this 1M/10M/100M sweep covers:

1. **Counter wiring** — JSONL emission of `chunks_deleted` and `chunk_bytes_reclaimed` actually fires in the production pipeline, not just under unit test conditions.
2. **Filesystem confirmation at non-trivial scale** — 18 chunks at 100M, all gone post-merge.

What this sweep does NOT cover:

- The production claim (3.6 TB → reclaimed-at-merge-end). That manifests only at 21.3B; gradient scales here reclaim 0.16–16 GB. Cycle 9 closes the claim.
- A perf delta. There isn't one to detect — the change is `File.Delete` in a `finally`, off the hot path. Running this gradient was over-scoped for the change class; pattern surfaced and recorded (`gradient-scope-matches-change-class`).

## Source + run command

- `latest-all.ttl.bz2` (Wikidata canonical, 122 GB compressed, dump dateModified `2026-03-26T14:01:30Z`)
- Local commit (post-1.7.48): cleanup hook + event-field additions + test
- Per-run command:

```bash
dotnet src/Mercury.Cli/bin/Release/net10.0/mercury.dll \
  --store wiki-{N}-r2cu \
  --bulk-load /Users/bemafred/Library/SkyOmega/datasets/wikidata/full/latest-all.ttl.bz2 \
  --profile Reference \
  --limit {N} \
  --metrics-out /tmp/round2-cleanup-gradient/{N}.jsonl \
  --metrics-state-interval 30 \
  --no-http --no-repl
```

## Headline numbers

| Scale | Triples stored | Wall-clock | Avg triples/sec | Atoms emitted | Chunks | `chunks_deleted` | `chunk_bytes_reclaimed` | Merge duration |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 M   | 1,000,000   | 4 s    | 340,658 | 318,407    | 1  | 1  | 163.7 MB  | 259 ms     |
| 10 M  | 10,000,000  | 27 s   | 363,849 | 2,971,690  | 2  | 2  | 1.63 GB   | 2.58 s     |
| 100 M | 100,000,000 | 4 m 45 s | 350,028 | 26,997,858 | 18 | 18 | 16.4 GB   | 30.15 s    |

Across all three scales: `chunks_deleted == chunk_count == spill_count`. `chunk_bytes_reclaimed` rises near-linearly with input (chunk bytes ≈ raw atom-occurrence bytes + 12-byte record overhead). Merge wall-clock at 100 M is 30.15 s — bracketed by the 1.7.48 production-rate envelope (the 21.3 B cycle 8 merge processed ~3.5 B atom occurrences at ~1.0 M/s steady-state above cache-fit; 100 M merge at ~10 M/s is consistent with below-cache-fit regime per `project_three_regime_merge`).

## Filesystem confirmation

Post-run inspection of each store's `bulk-tmp/sorted-vocab/occurrences/` directory:

```
$ ls /Users/bemafred/Library/SkyOmega/stores/wiki-100m-r2cu/.../occurrences/
total 0
```

The directory remains (caller owns its lifecycle); chunk files are gone. Compare to pre-fix behavior (cycle 8 trace): the 18 chunks at 100 M would have totaled ~16.4 GB sitting on disk until run end.

## What this does NOT prove

The 21.3 B disk-pressure win projected from cycle 8 (3.6 TB reclaimed at end-of-merge instead of end-of-run) is not exercised at gradient scale. The 100 M scale reclaims ~16 GB; the architectural relief is qualitatively the same but quantitatively orders of magnitude smaller. Production validation requires the next 21.3 B run (cycle 9, scheduled after Round 2 #1 pipelined spill ships).

The gradient is sufficient to ship the change — correctness + observability + no regression — but does not retire the limit's "production validated" status until cycle 9 closes it at scale.

## References

- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `MergeAndWrite` finally block (cleanup hook)
- `src/Mercury.Abstractions/SortedAtomMetrics.cs` — `MergeCompletedEvent` extended fields
- `src/Mercury/Diagnostics/JsonlMetricsListener.cs` — emits `chunks_deleted` + `chunk_bytes_reclaimed`
- `tests/Mercury.Tests/Storage/SortedAtomStoreExternalBuilderTests.cs` — `MergeAndWrite_DeletesConsumedChunks_AndReportsCounts`
- `docs/limits/intermediate-cleanup-deferred-to-run-end.md` — limit entry
- `/tmp/round2-cleanup-gradient/{1m,10m,100m}.jsonl` — JSONL artefacts (regenerable)
- `urn:sky-omega:bug:orphaned-chunks-after-merge-2026-05-05` (Mercury) — surfacing incident
- `urn:sky-omega:incident:cycle8-21b-instrumented-2026-05-04` (Mercury) — surfacing run
