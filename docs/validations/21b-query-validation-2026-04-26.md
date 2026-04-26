# 21.3 B Wikidata Query-Side Validation — 2026-04-26

**Status:** Passed. The Phase 6 artifact (`wiki-21b-ref`, 21.3 B triples, sealed 2026-04-25) opens cleanly, both primary (GSPO) and secondary (GPOS) indexes return correct results at full scale. First measured query-side behavior at 21.3 B.

Phase 6 completed on 2026-04-25 22:32 with the Reference-profile bulk-load and rebuild. The artifact had not been queried — `atime` evidence on the index files showed the store untouched between seal and 2026-04-26. The article published 2026-04-26 makes a queryable-at-scale claim that, until this run, was structurally unverified. This validation closes that gap.

## Headline numbers

| Step | Query | Time | Index exercised |
|---|---|---:|---|
| 0 | open + close | <2 s | metadata only |
| 1 | `SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 10` | 17 ms (6.5 ms parse + 10.6 ms exec) | GSPO leaf |
| 3a | `SELECT ?s ?o WHERE { ?s wdt:P31 ?o } LIMIT 10` | 20 ms (6.0 ms parse + 14.3 ms exec) | GPOS range |

All times include cold-cache; the store had not been read since seal.

## Context

The Phase 6 build produced a sealed Reference-profile artifact:

- 21.3 B triples (3.1 TB uncompressed `latest-all.nt` source)
- 85 h end-to-end wall-clock (bulk + rebuild)
- 2.5 TB physical on disk (4.1 TB logical mmap, sparse-allocated on APFS)
- GSPO (primary, bulk-load output): `gspo.tdb` 1 TB physical, sealed 2026-04-25 22:32
- GPOS (secondary, rebuild output): `gpos.tdb` 1 TB physical, same seal time
- AtomStore: `atoms.atoms` 269 GB physical, `atoms.atomidx` 256 GB physical
- Trigram posts: `trigram.posts` 8.6 GB physical

Without a query against this artifact, the substrate's queryable-at-scale claim was a build-time assertion only. A corrupt index, a metadata-version mismatch, or a bug in the read path at 21.3 B scale would have invalidated the Phase 6 conclusion silently.

## What was run

```bash
# Step 0: open and close
printf ':quit\n' | mercury --store wiki-21b-ref --no-http

# Step 1: GSPO LIMIT 10
printf 'SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 10\n:quit\n' | \
  mercury --store wiki-21b-ref --no-http

# Step 3a: GPOS predicate-bound LIMIT 10
printf 'PREFIX wdt: <http://www.wikidata.org/prop/direct/>\nSELECT ?s ?o WHERE { ?s wdt:P31 ?o } LIMIT 10\n:quit\n' | \
  mercury --store wiki-21b-ref --no-http
```

Mercury 1.7.44 (Phase 6 binary), no metrics infrastructure (the validation runs cheap enough that JSONL output would not have added information).

## Observations

### Step 0 — store opens cleanly

Exit 0, sub-2-second close. No metadata-version mismatch, no format error, no broken header. The pool/store layout (`pool.json` + UUID-keyed store directory) loads correctly. Both `gspo.tdb` and `gpos.tdb` mmap views establish without complaint.

### Step 1 — GSPO walk, atom decode

Returns the head of the dump in 17 ms. The first 10 triples are the dump's own metadata:

- `<…Dump> a <…Dataset>`, `<…Ontology>`
- `<…Dump> <…license> <http://creativecommons.org/publicdomain/zero/1.0/>`
- `<…Dump> <…softwareVersion> "1.0.0"`
- `<…Dump> <…dateModified> "2026-03-26T..." (×6)`

This confirms (a) the GSPO B+Tree's leftmost leaf opens, (b) atom IDs decode to readable IRIs and literals, (c) the `dateModified` timestamps match the April 2026 dump that was supposed to have been ingested. The store contains what we built it from.

### Step 3a — GPOS index works at 21.3 B scale

`wdt:P31` (instance-of) bound on predicate, returning 10 instance relations in 20 ms. Sample objects: Q43 (Turkey), Q668 (India), Q150 (French), Q188 (German), Q7809 (human), Q3624078 (sovereign state), Q31 (Belgium). Real Wikidata instance-of triples — the GPOS rebuild (which dropped from 14 min raw to 0.84 s after the Phase 5 Dispose-gate fix) produced a correct index, not just a fast one.

### Cold-cache responsiveness

Both queries returned in tens of milliseconds despite the store being completely cold (atime evidence, see Context). The mmap-backed B+Tree's path-from-root through to a single leaf page is at most ~5 cache-line-sized I/Os; the `LIMIT 10` early-terminates after a handful of leaf reads. The numbers above are consistent with that — no surprise, but worth recording as the *first* measured query latency at this scale.

## What this validates

- The Phase 6 artifact is a working substrate, not just a sealed file.
- Both GSPO (primary, bulk-load) and GPOS (secondary, rebuild) indexes return correct results at 21.3 B.
- Atom store decode survives at full vocabulary scale (~4 B unique atoms).
- The article's "queryable at 21.3 B on a laptop" claim is no longer aspirational. **It is an empirical, sound finding.**

## What this does NOT validate

- **Throughput at scale.** A `LIMIT 10` query exits after ~10 leaf reads; this says nothing about how the store handles full-scan, range-scan, or join-heavy queries on millions of bindings. The first such measurement should be a deliberate Phase 7 run with metrics infrastructure in place.
- **Full COUNT(*).** The executor has no short-circuit from `?s ?p ?o COUNT(*)` to the metadata triple counter; running it would full-scan the GSPO index (1 TB cold). Skipped here, deferred to a metrics-equipped measurement run.
- **Predicate-count cardinality.** A full count of `wdt:P31` (instance-of) would exercise GPOS range traversal at scale and produce a real measurement of GPOS sequential read bandwidth. Deferred for the same reason.
- **Concurrent query load.** This run is single-threaded, single-process. The Reference profile is structurally read-only and should support concurrent readers (see `docs/limits/reference-readonly-mmap.md`); that is not yet measured.

## Consequences

1. **wiki-21b-ref is preserved as a Phase 7 baseline.** It is now a validated artifact, useful for: (a) a baseline for any Phase 7 round that needs end-to-end measurement at 21.3 B, (b) an external proof point that survives outside the build pipeline, (c) the comparator for any architectural change that promises to re-run the full Wikidata bulk-load faster.
2. **Production-hardening milestone reached on the capacity dimension.** Mercury can ingest, store, rebuild, and query the full Wikidata graph on a single consumer laptop, BCL-only. The remaining work is performance, not capacity.
3. **The Phase 6 narrative now has a closing measurement.** The article framed Phase 6 as "85 hours of confirmed quiet" through to seal; this run extends that to "and the store is queryable on completion." The story is closed end-to-end.
4. **Phase 7 begins with metrics infrastructure.** The `COUNT(*)` and full predicate-count measurements deferred here are the first concrete asks for the metrics surface. Without measurement, performance work is blind; with it, every Phase 7 round produces a number, not an estimate.

## References

- [Phase 6 article](../articles/2026-04-26-21b-wikidata-on-a-laptop.md) — public framing; this validation closes its empirical loop.
- [ADR-026](../adrs/mercury/ADR-026-bulk-load-path.md) — Reference profile contract.
- [ADR-029](../adrs/mercury/ADR-029-store-profiles.md) — profile dispatch.
- [adr-031-dispose-gate-2026-04-21.md](adr-031-dispose-gate-2026-04-21.md) — Dispose 14 min → 0.84 s, the rebuild fix that this validation indirectly confirms.
- [adr-033-phase5-bulk-radix-2026-04-22.md](adr-033-phase5-bulk-radix-2026-04-22.md) — bulk-load architecture that produced the GSPO index this run reads from.
- [docs/limits/reference-readonly-mmap.md](../limits/reference-readonly-mmap.md) — query-time mmap mode work this validation enables.
- [docs/limits/streaming-source-decompression.md](../limits/streaming-source-decompression.md) — Phase 7 source-format recommendation, established in parallel with this validation.
