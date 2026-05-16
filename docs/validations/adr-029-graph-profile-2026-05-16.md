# ADR-029 Graph Profile Validation — 2026-05-16

**Mercury version:** 1.7.67
**ADR status transition:** Graph profile added to the matrix; ADR-029 status updated to reflect Graph as Completed alongside Cognitive and Reference. Minimal profile remains deferred.

## Summary

ADR-029's matrix-completion gesture lands across three commits in one day (1.7.65 → 1.7.67). The Graph profile fills the structural gap between Cognitive (88 B entries, bitemporal) and Reference (32 B entries, read-mostly): 64 B entries with versioning + soft-delete but no temporal dimension. Each profile gets a distinct concrete index implementation per the no-behavior-flags rule (`feedback_no_behavior_flags.md`, 2026-05-16) — no parameterization-by-flag, no inheritance hierarchy. Post-implementation analysis for shared behavior is allowed only when straightforward.

## Layout (file format)

| Profile | Concrete index | Key size | Entry size | NodeDegree | File magic |
|---|---|---:|---:|---:|---|
| Cognitive | `TemporalQuadIndex` | 56 B (bitemporal) | 88 B | 185 | `0x54454D504F52414C` ("TEMPORAL") |
| **Graph** | **`VersionedQuadIndex`** | **32 B** | **64 B** | **255** | **`0x4752415048494458` ("GRAPHIDX")** |
| Reference | `ReferenceQuadIndex` | 32 B | 32 B (key-only) | 511 | — (separate format) |

Graph's 64 B = 32 B `VersionedKey` (Graph + Primary + Secondary + Tertiary) + 8 B ChildOrValue + 24 B versioning metadata (CreatedAt + ModifiedAt + Version + IsDeleted + Flags + Reserved). 255 entries per 16 KB page — between Cognitive's 185 and Reference's 511, exactly as the entry-size geometry predicts.

## Mutation semantics

| Operation | Existing entry state | Behavior |
|---|---|---|
| `AddRaw(G,P,S,T)` | not present | new entry: `Version=1`, `CreatedAt=ModifiedAt=now`, `IsDeleted=false` |
| `AddRaw(G,P,S,T)` | present, `!IsDeleted` | **no-op** (RDF set semantics — `Version` does NOT advance) |
| `AddRaw(G,P,S,T)` | present, `IsDeleted` | un-delete: `Version++`, `ModifiedAt=now`, `IsDeleted=false` |
| `DeleteRaw(G,P,S,T)` | present, `!IsDeleted` | soft-delete: `Version++`, `ModifiedAt=now`, `IsDeleted=true`; returns `true` |
| `DeleteRaw(G,P,S,T)` | missing or `IsDeleted` | returns `false` |
| `Query(...)` (live) | any | filters `IsDeleted=true` |
| `QueryAllVersions(...)` (audit) | any | includes deleted entries |

The "no-op on re-add of live entry" default was settled in the planning chat: RDF set semantics, no version bump on idempotent inserts. The alternative ("explicit touch" API) was rejected as scope creep.

## Profile boundary checks (ADR-029 Decision 4)

| Profile | Temporal queries (AS_OF, Range, AllTime) |
|---|---|
| Cognitive | accepted |
| Graph | **rejected** at the API boundary with `ProfileCapabilityException` mentioning Graph + "no temporal dimension" |
| Reference | rejected at the API boundary |

The exception is thrown by `QuadStore.Query` before any planner work — fail-loudly, not silent-degrade. Matches Decision 4's intent.

## Commits

| Version | Date | Scope |
|---|---|---|
| 1.7.65 | 2026-05-16 | `VersionedKey` / `VersionedBTreeEntry` / `VersionedQuadIndex` + 14 storage-layer tests. No QuadStore integration; class is invokable from tests but not yet user-visible. |
| 1.7.66 | 2026-05-16 | `QuadStore` session-API + query path integration. `_gspoGraph` / `_gposGraph` / `_gospGraph` / `_tgspGraph` fields, constructor dispatch, `ApplyToIndexesById` Graph branch, `QueryGraphCurrent`, `TemporalResultEnumerator` Graph branch. 11 E2E tests including AS_OF rejection and graph-isolation. RebuildSecondaryIndexes throws explicit guidance. |
| 1.7.67 | 2026-05-16 | `RebuildGraphSecondaryIndexes` + `RebuildGraphIndex` helper. Bulk-load path now works: open in bulk-mode, BeginBatch / AddCurrentBatched / CommitBatch (primary GSPO only written inline), `RebuildSecondaryIndexes` populates GPOS / GOSP / TGSP / trigram. CLI `--help` updated to include Graph in the documented profile list. ADR-029 status updated to Completed for Graph. |

## E2E test coverage

`tests/Mercury.Tests/Storage/QuadStoreGraphProfileTests.cs` (11 tests):

1. **Graph_OpensWithPersistedSchema** — opens with `Profile = Graph`, persists schema, WAL present
2. **Graph_Add_RoundTripsThroughQueryCurrent** — single-triple add + query
3. **Graph_AddSameTripleTwice_RemainsSingleEntry** — RDF set semantics (no-op on re-add)
4. **Graph_BatchedAddAndCommit_PopulatesAllIndexes** — `BeginBatch` / `AddBatched` / `CommitBatch` populates GSPO/GPOS/GOSP/TGSP via inline writes
5. **Graph_Delete_SoftDeletesAndQueryHidesEntry** — soft-delete + live query filters out
6. **Graph_ReAddAfterDelete_UnDeletes** — re-add un-deletes, query returns the entry again
7. **Graph_Persistence_TriplesPersistAcrossReopen** — dispose + reopen, entries persist
8. **Graph_AsOfQuery_ThrowsProfileCapability** — `QueryAsOf` against Graph rejected
9. **Graph_RangeQuery_ThrowsProfileCapability** — `Query(... TemporalQueryType.Range ...)` rejected
10. **Graph_BulkLoadAndRebuild_PopulatesAllSecondaryIndexes** — bulk-mode → BeginBatch/AddCurrentBatched/CommitBatch → RebuildSecondaryIndexes → predicate-bound query (GPOS path), object-bound query (GOSP path), subject-bound query (GSPO path) all return correct counts
11. **Graph_GraphIsolation_NamedGraphsDoNotLeak** — same `(s, p, o)` in different named graphs do not cross-pollute

Plus 14 `VersionedQuadIndex`-level tests in `tests/Mercury.Tests/Storage/VersionedQuadIndexTests.cs` covering layout invariants, mutation semantics, page splits at >255 entries, persistence, Clear, graph isolation, wildcards.

## Storage-suite regression

| Slice | Storage tests passing |
|---|---:|
| Pre-1.7.65 (4 ADRs Completed) | 550 |
| 1.7.65 (VersionedQuadIndex) | 564 (550 + 14) |
| 1.7.66 (QuadStore Graph integration) | 575 (564 + 11) |
| 1.7.67 (Rebuild + CLI) | 575 (one test changed from "rebuild throws" to "rebuild works"; no net count change) |

Zero regressions across the existing Cognitive + Reference paths.

## Design notes

**Why not refactor TemporalResultEnumerator into three concrete types?** The struct already discriminated Reference vs Cognitive via a flag (`_isReference`) before Graph landed. Extending that pattern with `_isGraph` matches existing code; refactoring into per-profile result types would touch every SPARQL caller. The `feedback_no_behavior_flags.md` rule is upheld where it matters — at the **storage layer** (TemporalQuadIndex / VersionedQuadIndex / ReferenceQuadIndex are distinct concrete classes). The projection layer is acknowledged technical debt for a future "look at this once all four profiles ship" refactor pass.

**Why no AppendSorted in VersionedQuadIndex?** Graph uses random-insert `AddRaw` during rebuild because Graph isn't aiming for Wikidata-scale where sequential-append's write amplification savings become load-bearing. If a Graph workload surfaces past in-memory page cache, follow the ADR-032 radix-external-sort pattern as a sibling round.

**Why a shared WAL LogRecord shape (not a smaller Graph-specific record)?** Each Graph WAL record carries 24 unused bytes (ValidFromTicks / ValidToTicks / TransactionTimeTicks). The WAL parsing surface stays uniform; the data-economy cost is bounded by the WAL's typical session-API rate (no Wikidata-scale Graph workload is targeted). If WAL volume becomes a binding cost for some future Graph deployment, a compact variant can be added without breaking the existing shape (LogRecord has reserved bytes).

## References

- [ADR-029](../adrs/mercury/ADR-029-store-profiles.md) — store-profile matrix, original.
- [feedback_no_behavior_flags](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md) — design rule that mandated distinct concrete classes.
- `src/Mercury/Storage/VersionedQuadIndex.cs` — the Graph profile B+Tree implementation.
- `src/Mercury/Storage/QuadStore.cs` — dispatch sites: constructor switch, `ApplyToIndexesById`, `QueryGraphCurrent`, `RebuildGraphSecondaryIndexes`.
- `tests/Mercury.Tests/Storage/VersionedQuadIndexTests.cs` — 14 storage-layer tests.
- `tests/Mercury.Tests/Storage/QuadStoreGraphProfileTests.cs` — 11 E2E tests.
