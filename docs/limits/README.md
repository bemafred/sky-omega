# Sky Omega Limits Register

Known limits, scaling thresholds, and deferred decisions that have been *characterized* but not *acted on*. Each entry sits between Emergence (the unknown surfaced) and Engineering (the decision committed). They are deliberate non-decisions, not forgotten work.

## Why this directory exists

ADRs capture decisions. Validations capture measurements. Neither is the right home for "we know this exists, we know roughly when it would matter, and we have candidate fixes — but we are not acting on it now."

Such items, when buried in an ADR's Consequences or Open Questions section, become invisible after the ADR is marked Completed. This register surfaces them by design. Every limit is one short file; the index below makes the full set visible at a glance.

## Format

Each entry follows a lightweight template:

```markdown
# Limit: <title>

Status:        Latent | Monitoring | Triggered | Resolved
Surfaced:      <date>, via <link to validation, ADR, or experience>
Last reviewed: <date>
Promotes to:   <when this should become an ADR — concrete trigger condition>

## Description
## Trigger condition
## Current state
## Candidate mitigations
## References
```

Borrows from ADR but lighter — no Decision section, no alternatives-considered, no implementation plan. Those land in the ADR if and when the limit is promoted.

## Status meanings

| Status | Meaning |
|---|---|
| **Latent** | Known to exist, not currently affecting any production path. |
| **Monitoring** | Approaching a trigger condition; periodic measurement warranted. |
| **Triggered** | Now affecting a real workload. Promotion to ADR overdue. |
| **Resolved** | Addressed by an ADR or by changed circumstances. Kept in the register for paper trail. |

## Active entries

| Limit | Status | Trigger condition |
|---|---|---|
| [Predicate-statistics memory](predicate-statistics-memory.md) | Latent | `CollectPredicateStatistics` running on a Cognitive store > ~5 B triples, or any non-bulk write path on a 21.3 B Reference store |
| [Hash function quality](hash-function-quality.md) | Latent | Need for sustained ingest throughput improvement once schema-reduction wins (ADR-029) are banked, OR adversarial-input concerns surface |
| [Bit-packed atom IDs](bit-packed-atom-ids.md) | Latent | Storage becoming binding even after Reference profile (~680 GB additional savings projected at 21.3 B Wikidata) |
| [B+Tree index mmap remap](btree-mmap-remap.md) | Latent | Single store past 1 TB of B+Tree data (~33B ReferenceKey entries) or incremental workloads that cannot plan size at open time |
| [Bulk-load memory pressure](bulk-load-memory-pressure.md) | Latent | Bulk-load swap activity correlating with throughput drop, OR host with < 128 GB RAM, OR scale past full Wikidata 21.3B |
| [Sorted atom store for Reference](sorted-atom-store-for-reference.md) | Latent | Atom-store hash cache pressure becomes binding on Reference bulk throughput, OR scale past 21.3B Wikidata, OR memory footprint becomes load-bearing |
| [Reference read-only mmap](reference-readonly-mmap.md) | Latent | Reference query-side latency or per-process memory footprint becomes binding, OR cross-process shared query becomes a use case, OR SortedAtomStore work ships and bundles the seal/reopen pattern |
| [Streaming source decompression](streaming-source-decompression.md) | Latent | Disk-constrained deployment ingesting Wikidata-class datasets, OR compressed-only workflow (cloud blob), OR measured BZip2 < 30 MB/sec, OR parse pipeline acceleration shifts bottleneck to source-read |
| [Rebuild progress observability](rebuild-progress-observability.md) | Latent | Rebuild silent phase exceeds ~1 hour, OR automation/CI needs stuck-vs-progressing detection, OR a future architectural change introduces another silent phase |
| [Metrics coverage review](metrics-coverage-review.md) | Latent (review) | Catalog of 8 categories of observability gaps. Individual categories split out into their own entries when triggered (rebuild progress already split). |
| [Per-index subdirectory layout](per-index-subdirectory-layout.md) | Latent | Two-SSD utilization (WAL + data split, or per-index placement), OR backup/replication wanting per-index granularity, OR profile-specific layout asymmetries |
| [Cancellable executor paths](cancellable-executor-paths.md) | **Triggered** | Already triggered. Observed twice during the 2026-04-27/28 WDBench cold baseline (paths event loss + c2rpqs 4.86 h hang on a 60 s cancellation cap). ADR draft recommended within 1-3 days. |
| [Property-path grammar gaps](property-path-grammar-gaps.md) | **Triggered** | Already triggered. Parse-only sweep over WDBench paths + c2rpqs found 12 of 1,199 queries (1.0%) failing parse on three shapes: `^(P)*`, `^((A\|B))+`, `(^A/B)`. ADR for grammar completeness recommended; option B (split `ParsePathPrimary` / `ParsePathExpr`) preferred for long-term extensibility. |

## Adding a new entry

1. Create `docs/limits/<short-name>.md` using the template above.
2. Add a row to the table in this README under "Active entries".
3. Reference the entry from any ADR that surfaced or implies it, so the ADR's reader can follow the trail.
4. Set `Surfaced` to the date the limit was characterized, not the date you wrote the entry.

## Promoting an entry to an ADR

When a limit moves to Triggered (or pre-emptively when a project-level decision is being made):

1. Draft an ADR that captures the decision (Proposed status, normal ADR workflow).
2. Update the limit's Status to Resolved.
3. Add a "Promoted to: ADR-NNN" line.
4. Move the row from "Active entries" to a "Resolved" section (created if needed).
5. Do not delete the file — the historical record matters.
