# ADR-031 Piece 2 Validation — Dispose Gate at 1 B — 2026-04-21

**Status:** Phase 4 exit criterion measurement for [ADR-031](../adrs/mercury/ADR-031-read-only-session-fast-path.md). Validates the mutation-tracked Dispose gate against the 648 GB `wiki-1b` Cognitive store — the same store that produced the 14-minute Dispose in the 2026-04-19 Dispose profile.

## Exit criterion (from the production-hardening roadmap)

> 1 B cognitive store predicate-bound COUNT: total wall time (open + query + Dispose) under 60 s, Dispose phase under 1 s.

## Result

Two consecutive runs against the same store:

| Run | Total wall | Parse | Query exec | Dispose + open overhead | Target (<60 s total, <1 s Dispose) |
|---|---:|---:|---:|---:|---:|
| 1 | **45.08 s** | 6.3 ms | 44.24 s | **0.84 s** | ✓ / ✓ |
| 2 | 44.37 s   | 6.0 ms | 43.53 s | 0.84 s   | ✓ / ✓ |

Both runs pass both thresholds with ~33 % margin on total wall time and a Dispose+open envelope that's more than 1000× under the old cost.

## What the gate replaced

The 2026-04-19 [Dispose profile](dispose-profile-2026-04-20.md) (written at session end 2026-04-20 after the full-pipeline gradient) attributed the 14 min Dispose to `CollectPredicateStatistics` called from `CheckpointInternal()`. The gradient run's breakdown:

| Phase | Time |
|---|---:|
| Open + query (`SELECT (COUNT(*)) WHERE { ?s <http://schema.org/about> ?o }`) | 49.3 s |
| Dispose (CollectPredicateStatistics scanning 991 M entries in GPOS + trigram flush + WAL checkpoint marker) | **14 m (~840 s)** |
| **Total** | **~15 min** |

That 14 min was paid on every read-only close of a 1 B Cognitive store — ADR-031 identified it as the single largest tax on interactive use of the cognitive profile at scale.

## Mechanism

ADR-031 Piece 2 (shipped `a918b80`, version 1.7.32):

```csharp
// Dispose gates CheckpointInternal on whether anything was mutated this session.
if (_sessionMutated)
    CheckpointInternal();
```

`_sessionMutated` is a volatile bool on `QuadStore`, set at every mutation entry point
(`ApplyToIndexesById`, `ApplyDeleteToIndexesById`, `AddReferenceBulkTriple`, `Clear`,
`RebuildSecondaryIndexes`) and reset by `CheckpointInternal` after it successfully
writes the WAL checkpoint marker. A pure-query session never flips the flag, so Dispose
skips `CollectPredicateStatistics` (the 14 min item) plus the trigram flush and the WAL
checkpoint-marker write.

For the wiki-1b run: the session opened the store, parsed one SPARQL query, scanned
GPOS for the predicate-bound COUNT (44 s), returned one row, and disposed. No mutation
paths were touched. The flag stayed false. Dispose ran through `_wal?.Dispose()` and
each index's `Dispose` (which calls `SaveMetadata` — a ~4-word mmap write, microseconds
on a warm file) and returned. The remaining ~0.8 s is process startup, store open
(memory-map the B+Tree files), WAL header read, and process exit teardown — all of
which were always cheap.

## Metrics captured

ADR-030 Phase 1's `JsonlMetricsListener` ran on both measurement queries via the CLI's
extended `--metrics-out` flag. The emitted records:

```jsonl
{"phase":"query","ts":"2026-04-21T07:22:13.5421120+00:00","profile":"Cognitive","kind":"Select","parse_ms":6.2708,"exec_ms":44241.3291,"rows":1,"success":true}
{"phase":"query","ts":"2026-04-21T07:22:57.9069620+00:00","profile":"Cognitive","kind":"Select","parse_ms":5.9901,"exec_ms":43528.8429,"rows":1,"success":true}
```

First practical use of the Phase 3 infrastructure. `rows: 1` because `SELECT (COUNT(*) AS ?n)` returns a single row bound to the aggregate; the value 14,831,540 is the count. `parse_ms` and `exec_ms` are the parse and execution phases reported directly by SparqlEngine.

## Reproduction

```bash
# Build + install 1.7.33 (or later).
./tools/install-tools.sh

# Measure end-to-end wall time and capture metrics.
rm -f /tmp/wiki-1b-phase4.jsonl
time ( echo 'SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }' | \
  mercury --store wiki-1b --metrics-out /tmp/wiki-1b-phase4.jsonl --no-http )

cat /tmp/wiki-1b-phase4.jsonl
```

Any prior-revision Mercury (pre-`a918b80`) will reproduce the ~15 min total wall time
on the same store; any post-`a918b80` revision lands in the 45 s range.

## Provenance

- Hardware: MacBook Pro M5 Max, 128 GB RAM, 8 TB SSD
- Store: `wiki-1b` — 648 GB Cognitive profile, built 2026-04-18/19 via the full-pipeline
  gradient (991,797,873 queryable triples, all four indexes Ready state)
- Mercury: 1.7.33 (Phase 4 code at 1.7.32 commit `a918b80`, plus the 1.7.33
  `JsonlMetricsListener` AutoFlush fix so `--metrics-out` survives process exit without
  an explicit shutdown hook)
- Wikidata slice: April 2026 dump, filtered to the first 1 B NT lines

## Why this matters

The ADR-031 Piece 2 gate turns the cognitive profile from "slow to close and therefore
painful to interact with" into "close-is-free" — which restores the REPL, the Mercury
MCP session pattern, and any ad-hoc query workflow to the latency regime the user
actually expects from a local store. Combined with the ADR-029 profile split already
shipped, the cognitive-profile UX at Wikidata scale is now a 45-second end-to-end round
trip per query instead of a 15-minute one, without sacrificing the statistics-refresh
that mutation-following Disposes still run for the planner's benefit.

## References

- [ADR-031](../adrs/mercury/ADR-031-read-only-session-fast-path.md) — Read-only session
  fast path; Piece 2 is what this validation measures
- [ADR-030 Phase 1 metrics](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) —
  Infrastructure used to capture the measurement
- [Dispose profile 2026-04-20](dispose-profile-2026-04-20.md) — Source of the 14 min
  attribution this validation falsifies
- [Full-pipeline gradient 2026-04-19](full-pipeline-gradient-2026-04-19.md) — Provenance
  of the `wiki-1b` store and the original 49.3 s query baseline
