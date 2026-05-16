# ADR-029 Minimal Profile Validation — 2026-05-16

**Mercury version:** 1.7.68
**ADR status transition:** Minimal profile shipped; ADR-029 matrix now fully complete with all four profiles as distinct concrete index implementations.

## Summary

ADR-029's final matrix-completion gesture lands as a single slice. The Minimal profile is the simplest RDF store shape Mercury supports: single GSPO index, 24 B key-only entries, no graph dimension, no temporal, no versioning. Use case: single-graph Linked Data endpoints — a SchemaOrg subset, an ontology server, an application-specific knowledge graph. Distinct concrete class (`MinimalQuadIndex`) per the no-behavior-flags rule.

## Full matrix (post-Minimal)

| Profile | Concrete index | Key size | Entry size | Indexes | Has graph | Has temporal | Has versioning |
|---|---|---:|---:|---|---|---|---|
| Cognitive | `TemporalQuadIndex` | 56 B | 88 B | GSPO/GPOS/GOSP/TGSP | yes | yes | yes |
| Graph | `VersionedQuadIndex` | 32 B | 64 B | GSPO/GPOS/GOSP/TGSP | yes | no | yes |
| Reference | `ReferenceQuadIndex` | 32 B | 32 B (key-only) | GSPO/GPOS | yes | no | no |
| **Minimal** | **`MinimalQuadIndex`** | **24 B** | **24 B (key-only)** | **GSPO only** | **no** | **no** | **no** |

Entry size sweep: 88 B → 64 B → 32 B → 24 B = 3.67× range across the four profiles. Per-page entry count sweep: 185 → 255 → 511 → 681 entries per 16 KB page.

## Minimal profile semantics

- **Single index**: GSPO only. No predicate-first / object-first / time-leading variants. Predicate-bound or object-bound queries scan the full GSPO range (acceptable for Linked Data endpoint scale — a SchemaOrg subset typically fits in tens of thousands of triples).
- **No graph dimension**: `hasGraph: false` in the schema. Any non-empty graph in `AddCurrentBatched(s, p, o, graph)` or `QueryCurrent(s, p, o, graph)` raises `ProfileCapabilityException` at the API boundary with a clear message ("Minimal profile does not support named graphs").
- **No WAL**: Minimal follows Reference's pattern — `BeginBatch` / `AddCurrentBatched` / `CommitBatch` is the bulk-load entry; durability via `FlushToDisk` at session end; no per-write fsync, no checkpoint discipline.
- **No session-API single-triple Add()**: the WAL-durable single-triple write path is rejected via `RequireWriteCapableProfile` (Minimal has `HasVersioning: false`). Use `BeginBatch` as the bulk-load entry, per Decision 7.
- **Re-add semantics**: silent no-op enforced at the B+Tree level. RDF set semantics.
- **No soft-delete**: there's no IsDeleted metadata. Deletion is not part of the Minimal session API (Decision 7 stance applies); "delete and reload" is the contract.
- **RebuildSecondaryIndexes**: a no-op (no secondaries to rebuild). Transitions state to Ready and returns.

## Implementation

| File | Lines | Purpose |
|---|---:|---|
| `src/Mercury/Storage/MinimalQuadIndex.cs` | ~530 | B+Tree implementation. Mirrors `ReferenceQuadIndex`'s split-leaf/internal-entry structure (24 B leaf entries, 32 B internal entries) but without the AppendSorted optimization (Minimal isn't aimed at Wikidata-scale). |
| `src/Mercury/Storage/QuadStore.cs` | +60 LOC dispatch | `_gspoMinimal` field, constructor switch, BeginBatch/AddBatched/CommitBatch/RollbackBatch branches, `AddMinimalBulkTriple`, `QueryMinimalCurrent`, `TemporalResultEnumerator` fourth branch, RebuildSecondaryIndexes no-op branch. |
| `src/Mercury.Cli/Program.cs` | 1 line | `--profile` help updated to `<Cognitive\|Graph\|Reference\|Minimal>`. |
| `tests/Mercury.Tests/Storage/MinimalQuadIndexTests.cs` | 8 tests | Layout invariants, basic add+query, idempotent add, page split at >681 entries, persistence, Clear, specific-tertiary query. |
| `tests/Mercury.Tests/Storage/QuadStoreMinimalProfileTests.cs` | 9 tests | Open with persisted schema, batched-add round-trip, idempotent add, named-graph-in-add rejection, named-graph-in-query rejection, direct-Add() rejection, AS_OF rejection, persistence across reopen, RebuildSecondaryIndexes no-op. |
| `tests/Mercury.Tests/Storage/QuadStoreProfileDispatchTests.cs` | 1 test renamed | `Minimal_OpenThrowsNotSupported` → `Minimal_OpensSuccessfullyWithPersistedSchema`. |

Total: 17 new tests + 1 retired throw-assertion.

## File-format magic numbers (cross-profile mismatch detection)

| Profile | Magic | Decoded |
|---|---|---|
| Cognitive | `0x54454D504F52414C` | "TEMPORAL" |
| Graph | `0x4752415048494458` | "GRAPHIDX" |
| Reference | `0x5245464552454E4E` | "REFERENN" |
| Minimal | `0x4D494E494D414C00` | "MINIMAL\0" |

Opening a wrong-profile store fails at metadata load with a clear `InvalidDataException`. The four-way schema mismatch is detected at metadata load before any data corruption can occur.

## Storage-suite regression

| Slice | Storage tests |
|---|---:|
| Pre-Minimal (Graph profile Completed) | 575 |
| 1.7.68 (Minimal) | **592** (575 + 17 new) |

Zero regressions across Cognitive / Graph / Reference paths.

## Design notes

**No behavior flags.** Per `feedback_no_behavior_flags.md` (2026-05-16, the rule that mandated the matrix-completion arc), `MinimalQuadIndex` is a distinct concrete class. Not a `ReferenceQuadIndex` with `hasGraph: false`. Not a parameterization. The leaf/internal entry sizes differ (24/32 vs Reference's 32/40); the magic number differs; the file layout differs. Tests have to mind it too — that's not a cost, it's the contract.

**No AppendSorted optimization.** Reference has `BeginAppendSorted` / `AppendSorted` / `EndAppendSorted` for sequential-append bulk-load (ADR-032 radix external sort). Minimal doesn't — its target workload doesn't justify the surface. If a Minimal workload surfaces past-RAM-working-set behavior, follow the ADR-032 pattern as a sibling round.

**Why public `Add()` rejects Minimal.** ADR-029 Decision 7 framed Reference as session-API-immutable: bulk-load is the only write path. Minimal inherits this stance — `RequireWriteCapableProfile` checks `HasVersioning` which is false for both Reference and Minimal. The public `Add(...)` / `Delete(...)` single-triple WAL-durable methods reject; `BeginBatch` / `AddCurrentBatched` / `CommitBatch` is the bulk-load entry.

**Post-implementation refactoring opportunity (deferred).** With all four profiles now shipping as distinct concrete classes, the post-implementation analysis the user authorized (find shared behavior, refactor only when straightforward) becomes possible. Candidates worth examining: `TemporalResultEnumerator`'s growing flag-discriminator (now four branches) and the page-cache/mmap-base setup that repeats across the four index classes. Both are deferred to a separate "ADR-029 post-completion refactor" slice; this commit doesn't touch them.

## Cumulative matrix-completion arc (2026-05-16)

| Version | Slice | Scope |
|---|---|---|
| 1.7.65 | Graph commit 1 | `VersionedQuadIndex` types + storage tests |
| 1.7.66 | Graph commit 2 | `QuadStore` session-API + query path integration |
| 1.7.67 | Graph commit 3 | Bulk-load + `RebuildSecondaryIndexes` + CLI for Graph |
| **1.7.68** | **Minimal slice** | **`MinimalQuadIndex` + `QuadStore` + CLI + tests** |

Four release bumps, four profiles, one day. ADR-029 closed completely.

## References

- [ADR-029](../adrs/mercury/ADR-029-store-profiles.md) — store-profile matrix, original spec.
- [feedback_no_behavior_flags](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md) — the design rule that mandated distinct concrete classes.
- [adr-029-graph-profile-2026-05-16](adr-029-graph-profile-2026-05-16.md) — sibling validation for the Graph profile slice.
- `src/Mercury/Storage/MinimalQuadIndex.cs` — the Minimal profile B+Tree.
- `tests/Mercury.Tests/Storage/MinimalQuadIndexTests.cs` — 8 storage-layer tests.
- `tests/Mercury.Tests/Storage/QuadStoreMinimalProfileTests.cs` — 9 E2E tests.
