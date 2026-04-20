# ADR-029: Store Profiles — Cognitive, Graph, Reference, Minimal

## Status

**Status:** Proposed — 2026-04-19

## Context

Mercury has one storage schema. Every triple, in every store, in every named graph, pays for the full bitemporal apparatus: `ValidFrom`, `ValidTo`, `TransactionTime` per entry, plus `CreatedAt`, `ModifiedAt`, `Version`, `IsDeleted` per B+Tree record. This is correct for Mercury's primary use case — cognitive memory with temporal semantics, provenance, and audit trails. It is wrong for every other use case.

The hard evidence surfaced during the 2026-04-19 storage gradient (see [ADR-027 § Measured Storage Footprint](ADR-027-wikidata-scale-streaming-pipeline.md#measured-storage-footprint-2026-04-19) and [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md)):

- A full four-index bitemporal store of 21.3 B Wikidata triples projects to **~13.8 TB**.
- The validation hardware is an 8 TB SSD. **It doesn't fit.**
- 88 bytes per B+Tree entry decomposes: **24 B atom IDs, 24 B bitemporal timestamps, 32 B entry metadata, 8 B alignment**. Only the atom IDs are load-bearing for a Wikidata mirror.

This was surfaced as a hardening artifact — the full Wikidata gradient was supposed to stress-test Mercury's bulk load, index rebuild, and hash table. It did exactly that: eight distinct bug classes caught across two days. But it also uncovered a structural mismatch that no gradient step alone reveals: **Mercury's schema is optimized for cognitive memory (rich, temporal, audited) and penalizes reference knowledge (immutable, non-temporal, dump-sourced)**.

The mismatch is not incidental. Lucy (the cognitive memory component) and a Wikidata mirror are **both first-class uses of Mercury**. They live on the same substrate, behind the same SPARQL endpoint, and should be queryable from the same client with the same API. What differs is the data economy: a cognitive store is 50-100 GB of richly-versioned facts; a Wikidata mirror is 2-3 TB of immutable snapshot.

### The observation that forces this ADR

Without this split, one of two things is true:

1. Mercury forces Wikidata into a cognitive schema — 14 TB, doesn't fit, disqualified as a reference backend on commodity hardware.
2. Mercury becomes "that cognitive thing" and never serves reference data — the full Wikidata capability we've been validating is wasted as a mere benchmark.

Neither is where we want to be. The WDP team's Blazegraph replacement evaluation (July 2025 – June 2026, finalists QLever and Virtuoso) is the most visible reference-knowledge need in the RDF ecosystem. Mercury with 100 % W3C SPARQL conformance and zero external dependencies is plausibly relevant to that conversation — but only if it can actually hold the data on realistic hardware.

### Constraints in effect

- **ADR-020** — AtomStore single-writer contract. Unchanged: profiles do not alter concurrency semantics.
- **ADR-027** — Wikidata bulk ingest pipeline, validated through 1 B. The `Reference` profile targets the Stage-3 case where storage is the constraint.
- **ADR-028** (proposed) — Rehash-on-grow. Orthogonal to this ADR: atom hash table grows the same way regardless of profile.
- **BCL-only**. Profiles add schema variants but no external dependencies; the existing `FileStream`, `MemoryMappedFile`, `System.Buffers.Binary` machinery is sufficient.
- **SPARQL semantics** are profile-independent for everything except temporal queries. A `Reference` store simply cannot answer `AS_OF` — the planner surfaces that as a query error, not silent wrong results.

## Decision

### 1 — Introduce a StoreProfile declared at creation, durable in store metadata

Four profiles cover the observed design space:

| Profile | Indexes | Temporal? | Versioning? | Graph? | Entry size | Use case |
|---|---|---|---|---|---|---|
| **Cognitive** | GSPO, GPOS, GOSP, TGSP | yes | yes | yes | 88 B | Lucy, cognitive memory, provenance-sensitive |
| **Graph** | GSPO, GPOS, GOSP, TGSP | no | yes | yes | 64 B | Classic named-graph store with soft-delete |
| **Reference** | GSPO, GPOS | no | no | yes | 32 B | Read-mostly reference dumps (Wikidata, DBpedia) |
| **Minimal** | GSPO | no | no | no | 24 B | Single-graph Linked Data endpoints |

Profile is chosen at `mercury --create-store --profile <name>` time, written to `store-schema.json` in the store's base directory, and read on every subsequent open. **Profile cannot change after creation** — migrating between profiles means rebuilding the store from a data source.

Cognitive stays the default. Everything today works unchanged.

### 2 — Store schema is a durable record, not a runtime flag

`store-schema.json` (canonical JSON, in the store's base directory alongside `index-state`):

```json
{
  "profile": "Reference",
  "indexes": ["gspo", "gpos"],
  "hasGraph": true,
  "hasTemporal": false,
  "hasVersioning": false,
  "keyLayoutVersion": 1
}
```

A mismatch between the recorded schema and the runtime's supported schemas (e.g., opening a `Reference` store with a build that only knows `Cognitive`) is a hard error on open, not a silent degradation.

### 3 — Two concrete key/entry variants, not generics

Rather than parameterizing `QuadIndex<TKey, TEntry>` and paying for layer-of-indirection everywhere, introduce two concrete types:

- `TemporalQuadIndex` — the current `QuadIndex`, unchanged. Key = `TemporalKey` (56 B), Entry = `TemporalBTreeEntry` (88 B).
- `ReferenceQuadIndex` — new. Key = `ReferenceKey` (24 B or 32 B depending on `hasGraph`), Entry = key-only (no `ChildOrValue`/`CreatedAt`/`ModifiedAt`/`Version`/`IsDeleted`).

`QuadStore` owns an `IQuadIndex[]` array. At construction, it reads the schema and instantiates the appropriate concrete type per index. Shared interface for the few polymorphic operations (`AddRaw`, `DeleteRaw`, `Query*`, `Flush`, `SetDeferMsync`).

The reasoning: generics over struct layouts in C# work but cost JIT compile time per instantiation and make the code harder to read. Two concrete classes of ~1200 lines each is easier to audit than one generic class with a thousand `where T : struct, IKey<T>` constraints.

### 4 — SPARQL planner respects the schema

Mercury's `QueryExecutor` already uses `StoreIndexState` to know which indexes are populated. Extend the check: if a query requires capabilities the schema doesn't have (e.g., `SELECT ... WHERE { ?s ?p ?o FILTER(sky:asOf(?s, "2024-01-01"^^xsd:date)) }` against a `Reference` store), fail at plan time with a clear message — not silently during execution.

### 5 — Offset and ID widths stay at 64-bit signed (`long`) for now

The savings from moving atom IDs to packed 48-bit integers (~680 GB at full Wikidata) are real but much smaller than the schema-reduction win (~7-10 TB). Bit-packing adds subtle serialization complexity with no upside until the schema-reduction win is banked. Defer to a future ADR if needed.

### 6 — The cognitive profile is the default — explicitly

`--profile` without a value defaults to Cognitive. This preserves current behavior for every existing user: any `mercury --store foo` command today gets a Cognitive store, same as before this ADR. Profiles are opt-in, not opt-out.

### 7 — Reference profile mutation semantics — bulk-mutable, session-API immutable

The Context above frames Reference as "immutable, non-temporal, dump-sourced". That framing is true in spirit but imprecise as a contract. Production hardening requires an explicit commitment to *what* is immutable and *at which layer*. This clause makes that commitment.

**The decision.** A Reference profile store accepts mutations **only** via the bulk-load path, never via the session API.

- **Session API** (SPARQL UPDATE, `QuadStore.Add`, `QuadStore.Delete`, any per-triple write): rejected at plan time with a clear error. Same discipline as the temporal-query rejection in Decision 4 — a `Reference` store is a different *kind* of store, and queries/updates that require capabilities it doesn't have fail loudly, not silently.
- **Bulk-load path** (`mercury --store X --bulk-load file.nt` or the equivalent programmatic interface): allowed, including against an existing Reference store. Subsequent bulk loads append to the GSPO/GPOS indexes. The bulk-load path owns the store exclusively during load (consistent with ADR-020 single-writer), honors its own durability discipline (see [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md)), and writes metadata (schema, index-state) on completion.
- **Deletion** is out of scope for this decision. Without versioning, a Reference store cannot soft-delete; a hard-deletion workflow would require B+Tree entry removal and atom-store GC and is a separate ADR if demanded. Today, "deleting" from a Reference store means reloading from an updated source.

**Alternatives considered and rejected:**

- *(A) Session-API mutable without versioning.* Would collapse the distinction between Reference's write model and Graph's. Rejected because it forces Reference to carry a WAL and per-session mutation tracking ([ADR-031](ADR-031-read-only-session-fast-path.md) piece 2), losing the structural-read-only fast path that makes Reference's cost model predictable. Also erodes the profile matrix — if Reference is session-mutable, Graph's distinctness needs separate justification (see open question).
- *(B) Strictly immutable after initial load (no incremental bulk).* Cleaner in principle but forces full reload for every source update. Wikidata publishes new dumps on a cadence that's faster than a full reload takes at projected throughput — forcing a full reload for every update is operationally heavy. Keeping Reference bulk-appendable matches real reference-data maintenance without compromising session-API semantics.
- *(C) Delete Reference profile entirely; use Graph for all mutable reference-like cases.* Considered and rejected: the 32 B vs 64 B entry-size win is the primary motivation for Reference's existence at all, and that win requires schema simplifications (no versioning, no soft-delete metadata, no per-entry timestamps) that Graph cannot share.

**Commitments this decision makes downstream:**

- The bulk-load path must support appending to an existing Reference store — profile mismatch remains a hard error (Decision 2), but profile match is a valid append. Already implicitly true of today's bulk-load; Decision 7 makes it explicit.
- [ADR-031](ADR-031-read-only-session-fast-path.md) piece 1 (Reference-profile unconditional read-only session fast path) is compatible: bulk-load runs in a separate process-level session with its own Dispose discipline; session-API callers only ever see a read-only store.
- Deletion workflows are not provided by this ADR. If they become required, a separate ADR must define hard-delete semantics (B+Tree removal, atom-store GC, trigram cleanup).

**What walk-back would cost.** If Reference ever needs session-API mutability:
1. Add a WAL to the Reference profile (currently absent by Decision 1's schema).
2. Add per-session mutation tracking and (likely) escalation (ADR-031 piece 2/3 machinery).
3. Decide hard-delete semantics or explicitly forbid DELETE.

Non-trivial but bounded. The escape hatch exists; the choice is not irreversible, just load-bearing.

**Open question defined by this clause:** how does bulk-append handle duplicate triples already present in the store? Options are silent dedup (extra B+Tree lookup per entry — cost at 21.3 B scale is meaningful), error on conflict (correctness-safe, operationally hostile), or accept-duplicates (simplest, wastes storage, distinguishes the two entries by atom IDs not structure — impossible since structural equality is the only equality we have). Defer explicit resolution to the bulk-append implementation, but flag as required-before-Reference-ships.

## Consequences

### Positive

- **Wikidata fits on 8 TB at Reference profile.** Projected ~2.6 TB all-in (2 indexes × ~1.1 TB + atoms + trigram). Fits with ~3.5× margin on commodity hardware.
- **Two-persona Mercury on one host.** A cognitive store and a reference Wikidata mirror can live side-by-side on the same filesystem, served from two `SparqlHttpServer` endpoints (different ports). Federated queries via `SERVICE` link them. Same engine, same protocol, different data economies.
- **Clean semantic contract per store.** "This store doesn't have temporal semantics" is a fact, not a hope. Callers can rely on it. Queries that require temporal capabilities fail loudly, not silently.
- **Faster bulk loads for reference data.** Less data to write per triple (32 B vs 88 B per entry) ≈ 2.5× lower storage bandwidth, proportionally faster loads.
- **Lower memory footprint for reference workloads.** Smaller B+Tree pages at steady-state cache better; a Reference store's working set at 21.3 B is ~2.6 TB vs ~14 TB at Cognitive, so more of it fits in page cache.
- **Enables participation in the WDP Blazegraph-replacement conversation.** Mercury with 100 % W3C SPARQL conformance, BCL-only, single-machine Wikidata capability — that's a specific capability nobody else is claiming.

### Negative

- **Two key/entry layouts to maintain.** Changes to B+Tree serialization must be made in both places, or shared through careful abstraction. Not hard, but it's real ongoing work.
- **Tests matrix roughly doubles** for anything that touches storage. Manageable with fixtures parameterized by profile.
- **Cross-profile migration requires rebuild.** You cannot convert a Cognitive store into a Reference store by editing metadata; you reload from source (or a SPARQL CONSTRUCT dump). Acceptable — profile is chosen at creation deliberately.
- **SPARQL planner complexity grows.** A new class of "can this query run against this schema" check. Not a large amount of code, but a new concern.

### Risks

- **Cognitive regression.** The primary existing workload must not regress from this refactor. Mitigation: Cognitive remains the default, all existing tests pass unchanged, `TemporalQuadIndex` is literally the current `QuadIndex` renamed — no behavioral change.
- **Profile-mismatch bugs.** A store opened with the wrong profile expectations must fail clearly, not produce wrong data. Mitigation: schema is read on every open; a mismatch is a hard error with a descriptive message.
- **Scope creep toward N profiles.** Resist the urge to add profiles opportunistically. Four is already generous. Adding a fifth requires a new ADR and a specific use case.

## Implementation plan

**Phase 1 — Design and schema**
- Finalize profile enumeration and per-profile key/entry layouts.
- Define `store-schema.json` canonical format and validator.
- Add `StoreProfile` / `StoreSchema` types to Mercury.Abstractions.
- Unit tests: schema round-trip serialization, mismatch detection.

**Phase 2 — Implementation**
- Rename current `QuadIndex` to `TemporalQuadIndex` (behavior unchanged). Add `IQuadIndex` interface capturing the polymorphic surface.
- Implement `ReferenceQuadIndex` with `ReferenceKey` (24/32 B) and key-only entries. Share `AtomStore`, `PageCache`, serialization conventions.
- Extend `QuadStore` constructor to read schema, instantiate the right index family.
- Extend `StorageOptions` with `Profile` property.
- Extend CLI: `mercury --create-store --profile <name>` and `mercury --store X --profile <name> --bulk-load file.nt` (profile only honored at creation).
- Tests: correctness of each profile against the same bulk-load fixture, cross-profile query behavior (Reference store rejects temporal queries cleanly).

**Phase 3 — Wikidata Reference validation**
- Load `latest-all.nt` at Reference profile.
- Target: completes within the 8 TB disk, in under 12 hours (compared to the 50-minute 1 B Cognitive load, 21.3× data and ~3× cheaper per entry).
- Query validation: WDBench + a handful of hand-picked community queries via `--profile-query` mode. Capture p50/p95/p99 latencies and compare to WMF-published QLever/Virtuoso numbers.
- Publish the benchmark artifact.

**Phase 4 — Two-persona deployment proof**
- Run `mercury-mcp` (cognitive) on port 3030 and a Reference Wikidata endpoint on port 3031 simultaneously on the same host.
- Exercise a federated query — e.g., "for every Person in my cognitive memory, find their Wikidata QID." This demonstrates the two-persona story as an actual working artifact, not an architecture slide.

## Open questions

- **Does Graph profile pull its weight as a distinct profile?** It's between Cognitive and Reference: versioning without temporal. May collapse into Reference if no clear use case shows up. Defer to Phase 2 — implement Cognitive and Reference first, add Graph only if demanded.
- **What should `Minimal` drop beyond the Graph column?** Possibly no trigram index either. Depends on use case; defer until one surfaces.
- **Cross-profile federation in a single store?** Named graphs with different schemas? Probably out of scope — a store is a store. If you want two, run two.
- **Should schema be part of the Solid protocol's `access-control` surface?** Orthogonal to this ADR but relevant to ADR-015 (Mercury.Solid). Defer.
- **APFS compression as a complementary option.** Not changing Mercury's architecture, but running `afsctool -cx -9` across a Reference store might compress another 20-40 %. Complementary to profile reduction, not a replacement. Documenting as a deployment option, not a design change.

## References

- [ADR-020](ADR-020-atomstore-single-writer-contract.md) — AtomStore concurrency contract (unchanged)
- [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) — Wikidata pipeline and storage footprint measurements
- [ADR-028](ADR-028-atomstore-rehash-on-grow.md) — Rehash-on-grow (orthogonal, complementary)
- [full-pipeline-gradient-2026-04-19.md](../../validations/full-pipeline-gradient-2026-04-19.md) — Source of the per-scale storage numbers this ADR builds on
