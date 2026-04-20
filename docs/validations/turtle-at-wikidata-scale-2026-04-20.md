# Turtle Bulk-Load at Wikidata Scale — 2026-04-20

**Status:** Passed. 100 M triples loaded from `latest-all.ttl` at 292 K/sec with zero parser errors. Justifies dropping the 912 GB Turtle dump from disk.

Phase 4 of [ADR-027](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md) covered N-Triples at 1 B; the Turtle bulk-load path had never been exercised at Wikidata scale. This run closes that gap with the minimum evidence needed to justify removing the uncompressed `.ttl` dump from local storage.

## Headline numbers

| Input | Triples | Elapsed | Avg throughput | Recent peaks | Store size | Mercury |
|---|---|---|---|---|---|---|
| `latest-all.ttl` (912 GB, Wikidata April 2026) | **100 M** (--limit) | 5 m 41 s | **292,489 /sec** | ~900 K /sec | 24 GB | 1.7.23 |

## Context

The bulk-load gradient on 2026-04-17/18/19 covered `latest-all.nt` end-to-end: 1 M → 10 M → 100 M → 1 B. The equivalent Turtle path (`latest-all.ttl`, 912 GB) had only the W3C Turtle test suite as evidence of correctness — which covers grammar coverage but not Wikidata-specific scale behavior: multi-line entity blocks, 31 prefix declarations in the preamble, `;` and `,` continuations across lines, dense property abbreviations like `wd:Q42 wdt:P31 wd:Q5`. The question was whether the streaming Turtle parser would survive the same throughput and memory profile as the NT path had.

## What was run

```
mercury --store wiki-ttl-100m \
        --bulk-load ~/Library/SkyOmega/datasets/wikidata/full/latest-all.ttl \
        --limit 100000000 \
        --no-repl --no-http \
        --metrics-out /tmp/wiki-ttl-100m.metrics.jsonl
```

`--limit` is new in 1.7.23 ([commit](../../), `CLI: add --limit <N> for capped loads and converts`). Counts store-observable triples and caps bulk-load before the source is exhausted.

## Observations

### Parser correctness at scale — no issues

No grammar errors, no buffer-boundary faults, no memory blowup through 100 M triples. The sliding-buffer fix from 1.7.4 ([project_parser_blocker](../../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/project_parser_blocker.md)) continues to hold — 170 M was where that originally blocked, and we're now well past it on NT; this Turtle run confirms the same parser family handles Wikidata's Turtle dialect cleanly at 100 M.

### Throughput — Turtle ≈ NT at this scale

292 K/sec Turtle vs 331 K/sec NT (from the 1 B bulk-load at 1.7.22 on 2026-04-19). The ~12 % gap is plausibly parser overhead for prefix resolution and continuation handling. Bytes-per-triple on disk are much lower in Turtle (prefix abbreviations like `wd:` instead of full IRIs), so wall-clock throughput stays close despite the parser cost.

### Recent-rate oscillation — ~40 K to ~900 K per 10-second window

Visible in the progress output: the `recent /sec` number swings between ~40 K and ~900 K across intervals. Average stays steady around 290 K. Plausibly B+Tree split cadence (page splits stall the write path) or I/O buffering cycles. Not a correctness concern at this scale, but worth keeping in mind when [ADR-030](../adrs/mercury/ADR-030-bulk-load-and-rebuild-performance.md) adds standing measurement infrastructure — smoothing this out (or understanding the cause) is exactly the kind of question a proper histogram would answer.

### Memory — stable

GC heap flat at ~115 MB across the entire run. Working set climbed linearly to 24 GB during load (expected: posting-file mmap growth), settled to 10 GB after completion. No allocation pressure, no pauses.

### Store size — matches NT exactly

100 M Turtle → 24 GB store. The prior (now-deleted) `wiki-100m` store from an NT load of 100 M was also 24 GB. Same atoms, same B+Tree layout — the source format doesn't leak into the storage footprint. Expected, but worth confirming.

## What this validates

- Turtle bulk-load at 100 M with Wikidata-specific grammar patterns.
- `--limit` semantic (exact store-observable count) works through the full pipeline.
- Turtle throughput is within striking distance of NT — no format-specific perf cliff.

## What this does NOT validate

- Full 21.3 B Turtle load. We never ran the whole file. Extrapolating from 100 M: ~20 h at 292 K/sec to ingest all 21.3 B, compared to NT's ~18 h. That's a projection, not a measurement.
- Turtle-to-Turtle convert roundtrip (`--convert in.ttl out.ttl`). Out of scope for this run.
- Rebuild of a Turtle-sourced store. Rebuild is format-independent (reads GSPO, writes secondaries), so there's no reason to expect it to behave differently from the NT-sourced 1 B rebuild — but it wasn't run here.

## Consequences

1. **Safe to drop the uncompressed `latest-all.ttl` (912 GB)** from local storage. If full-scale Turtle testing ever matters, re-expand from `latest-all.ttl.bz2` (114 GB) which stays on disk as insurance.
2. **`--limit` is a durable CLI capability** for future gradient runs on any format — retires the `head -n N | .nt` slice trick for good.
3. **The test store `wiki-ttl-100m` (24 GB) has served its purpose** — findings captured here, store can be deleted.

## References

- [ADR-027](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md) — parent pipeline ADR (Completed 2026-04-19 after the 1 B gradient close)
- [bulk-load-gradient-2026-04-17.md](bulk-load-gradient-2026-04-17.md) — NT gradient through 100 M
- [full-pipeline-gradient-2026-04-19.md](full-pipeline-gradient-2026-04-19.md) — NT gradient through 1 B (bulk + rebuild)
- [parser-at-wikidata-scale-2026-04-17.md](parser-at-wikidata-scale-2026-04-17.md) — the sliding-buffer fix that unblocked parsers past 170 M
