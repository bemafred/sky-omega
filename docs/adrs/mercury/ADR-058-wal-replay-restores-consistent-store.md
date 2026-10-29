# ADR-058: WAL Replay Restores a Consistent Store — The Checkpoint Flushes the Indexes and the Atom Store Before It Truncates the Log

## Status

**Status:** Proposed — 2026-10-29

Found by a scripted SIGKILL + restart repro of mercury-mcp 1.8.32 against a scratch
store (recorded in Mercury memory as `urn:finding:mercury-crash-recovery-root-cause`,
severity critical, deterministic). ADR-023 declared transactional integrity complete
in March 2026; this ADR narrows one guarantee that ADR-023's acceptance criteria did
not cover: a crash-safe RESTART from a WAL that was fsynced at commit time.

## Context

### What the repro showed (`mercury-mcp 1.8.32` + `c7ae2ed2`)

Scratch store at `/ssd/mercury-repro`, mercury-mcp with `--enable-http-updates` on an
isolated port. Insert 3 triples into a named graph via `POST /sparql/update`. Server
returns `{"success":true,"affected":3}`. Same-process `SELECT COUNT(*) WHERE { GRAPH ?g { ?s ?p ?o } }`
returns 3. `wal.log` grows from 0 → 400 B (5 fixed-size 80-byte records:
`BeginTx(1), Add(1), Add(1), Add(1), CommitTx(1)`).

`kill -9` the server. `wal.log` is still 400 B on disk — the fsync at `CommitBatch`
reached the storage device. Restart the server on the same store directory. The
WAL is replayed and truncated to 80 B (one `Checkpoint(1)` record). Query returns
`0`. All 3 triples ack'd as committed are gone.

A follow-up `INSERT` of one triple into the restarted process exposes a second
failure. A `SELECT * WHERE { GRAPH ?g { ?s ?p ?o } }` returns 3 bindings — one is
the fresh triple; the other two have empty-string subject and object fields. They
are the pre-crash B+tree entries, surviving via per-page `msync`, whose atom-ID
references now resolve to the atoms the fresh `INSERT` placed in slots 1–4 (which
had held the original graph, two subjects, and one object before the crash).
`atoms.atoms` on disk, before and after the restart's new write, confirms the
overwrite directly: slot 1 was `<urn:repro:durability>`, is now `<urn:repro:new>`.

### Where the pieces live today

Write path (SPARQL `INSERT DATA`, Cognitive profile):

1. `SparqlEngine.Update` → `UpdateExecutor.ExecuteInsertData` →
   `QuadStore.BeginBatch` → N × `QuadStore.AddCurrentBatched` →
   `QuadStore.CommitBatch`.
2. `AddCurrentBatched` → `AddBatched` (QuadStore.cs:940). `_atoms.Intern` for each
   RDF term, then `_wal.AppendBatch(record, _activeBatchTxId)` (no fsync), then
   `_batchBuffer.Add(record)`. **No index mutation yet.**
3. `CommitBatch` (QuadStore.cs:1251). Writes `CommitTx` to the WAL and fsyncs
   (`_wal.CommitBatch` → `_logFile.Flush(flushToDisk: true)`). Then drains the
   buffer: for each record, `ApplyRecordToIndexesById` → `ApplyToIndexesById` →
   `_gspoIndex!.AddRaw(...)` (and the three sibling indexes) → `InsertIntoTree`
   writes B+tree pages through the `MemoryMappedViewAccessor` and calls
   `_file.FlushPage()` (an `msync`) per operation.
4. `CheckpointIfNeeded`. For a small batch, `ShouldCheckpoint` returns false; no
   checkpoint runs.

Checkpoint (`QuadStore.CheckpointInternal`, QuadStore.cs:2466):

1. `CollectPredicateStatistics`.
2. `_trigramIndex.Flush`.
3. `_wal.Checkpoint` — writes the `Checkpoint` record, fsyncs, truncates `wal.log`
   to that one record (`WriteAheadLog.cs:194`, `TruncateLog` at 226).

`CheckpointInternal` **does not call `_gspoIndex!.Flush()` or `SaveMetadata` on any
of the four B+Tree index files, and does not persist the atom store's next-ID
counter.**

Restart (`QuadStore` constructor):

1. Open atom store (`HashAtomStore`). The on-disk atom bytes and offsets survive
   per-write writes, but **the "next atom ID" counter is in-memory only** —
   `HashAtomStore` recovers interned atoms into its hash from the on-disk layout,
   so lookup by string works, but there is no durable record of "the next free ID
   is N" that `Intern` could use to avoid colliding with a value the recovery is
   about to re-reference.
2. Open each `TemporalQuadIndex` → `BTreeFile.LoadMetadata` (BTreeFile.cs:157).
   Reads the magic at file offset 24. If present, loads `_rootPageId`,
   `_nextPageId`, `_entryCount` from offsets 0, 8, 16. **`_entryCount` is
   persisted only through `SaveMetadata`, called from `AllocatePage`
   (BTreeFile.cs:221), not from `IncrementEntryCount` (line 262).** For an insert
   that fits in the root page without allocating, the on-disk `_entryCount` stays
   `0` through unbounded in-memory growth.
3. `Recover` (QuadStore.cs:2412). Enumerates WAL via `LogRecordEnumerator`
   (WriteAheadLog.cs:595), which collects `batchTxIds` and `committedTxIds`
   on a first pass and yields `Add`/`Retract`/`End` records belonging to a
   committed batch (or single writes with no `BeginTx`). For each,
   `ApplyRecordToIndexesById`.
4. On `recoveredCount > 0`, `CheckpointInternal`. The three steps above:
   statistics, trigram, WAL truncate. The replayed index writes stay in mmap;
   the on-disk index metadata header stays stale; the atom store's next-ID
   counter stays uninitialized.

### Why the data is lost

Two separate state components that recovery needs are not durable:

- **B+Tree metadata header (`_entryCount`, `_nextPageId`).** The page bytes written
  by the first process reach disk through per-op `FlushPage msync`, but the header
  reads `_entryCount = 0`. The restarted process constructs its in-memory view
  from the stale header. Whether queries short-circuit on `_entryCount == 0`
  or traverse the tree anyway, the next write grows past `_nextPageId = 2` with no
  knowledge of pages the previous process allocated (if any), compounding the
  corruption.
- **AtomStore next-ID counter.** The restarted process's `Intern` of a new string
  returns an atom ID that collides with the ID the first process had already
  assigned to a different string. The WAL's `Add` records reference IDs by number;
  after a post-restart write, those numbers resolve to the wrong strings. The
  B+tree pages that survived crash were msynced with the old numbering, and now
  live in a store whose numbering is different.

### What ADR-023 did and did not cover

ADR-023 (Completed, 2026-03-22) added the `BeginTx`/`CommitTx` markers to the WAL
and a `_batchBuffer` so index mutations defer to `CommitBatch`. Its acceptance
criteria (RollbackBatch leaves no state, WAL has transaction boundaries, time is
captured at the batch) are met. ADR-023 did not run a crash-restart conformance
test. The scenario this ADR addresses is a correct `CommitBatch` followed by a
process crash before any `Checkpoint` ran — a window ADR-023 does not forbid and
does not cover.

## Decision

### D1. `CheckpointInternal` flushes the index metadata and the atom store before truncating the WAL

Before `_wal.Checkpoint` runs, the four `TemporalQuadIndex` instances (`_gspoIndex`,
`_gposIndex`, `_gospIndex`, `_tgspIndex`), the atom store (`_atoms`), and the
trigram index are brought to a state where the on-disk form, taken alone, is
sufficient to reconstruct the in-memory view. "Sufficient to reconstruct" is
defined by D3's validation.

Shape (subject to Q1): a `Persist()` call on each component that is responsible
for writing all of its in-memory counters and headers to disk and `msync`ing them.
For `BTreeFile` that is `SaveMetadata` (writing `_rootPageId`, `_nextPageId`,
`_entryCount`, magic) followed by its own `Flush` (whole-region `msync`). For
`HashAtomStore` that is persisting the next-ID counter and whatever other
in-memory state it holds (the hash table itself is already backed by
`atoms.atomidx`; next-ID is the identified gap).

### D2. The WAL truncates only after D1 succeeds

`_wal.Checkpoint` is the last step of `CheckpointInternal`, after the three
components have persisted. If any `Persist` fails, the WAL is not truncated and
the process is left to retry (or fail hard and let the next open recover from the
intact log).

### D3. Recovery is a restart conformance test, not only a code path

A new test (shape: `tests/Mercury.Tests/Storage/CrashRecoveryTests.cs`) scripts the
SIGKILL path: write a batch, SIGKILL mid-flight, restart in-process, assert the
data is present with the correct terms. The probe in this ADR is the first test
case. The test runs for every store profile that has a WAL (Cognitive, Graph) and
for both single-triple and multi-triple commits. It is the acceptance test for
D1 and D2.

### D4. The replay target is explicit

The replay in `Recover` applies to the state D1 persists at checkpoint time. When
replay begins, the TDB files, atom store, and trigram index are at the state they
held at the last checkpoint — not at a stale snapshot that msynced-but-did-not-
checkpoint. The implementation does not need to reset the TDB files on open; it
needs the on-disk headers to be internally consistent so the replay's inserts
behave the same way they did in the writer.

## Consequences

- Every checkpoint pays the cost of two more `msync`s per index file (four indexes
  × ~24 bytes of header per file is a cache-line write) plus whatever
  `HashAtomStore.Persist` costs for its next-ID counter. The size is small; the
  cost is the `msync` round-trip. ADR-031 Piece 2 already gates `CheckpointInternal`
  on `_sessionMutated`, so pure-read sessions are unaffected.
- `_wal.Checkpoint`'s truncation is contingent, not unconditional. If the headers
  cannot be flushed (ENOSPC, EIO), the WAL stays intact and the next open replays
  the same records. The failure surface widens by one branch: "checkpoint failed,
  WAL is longer than its threshold, next write still ok" is a state to design for.
- The new restart test runs under `dotnet test`. It uses `Process.Kill` + reopen
  within the same test process; the probes already written in the Mercury test
  tree (`tests/Mercury.Tests/Storage/*`) show the shape.
- No change to the write path. `AddBatched` + `CommitBatch` still write and fsync
  WAL records at commit; the public API is unchanged.

## Open questions

- **Q1. The `Persist()` surface.** `BTreeFile.SaveMetadata` and `BTreeFile.Flush`
  are already public. `HashAtomStore` needs a method. Whether to introduce a
  shared interface (`IPersistableState`) or call each component by name from
  `CheckpointInternal` is a shape choice; the first is tidier, the second is
  smaller.
- **Q2. What `HashAtomStore` persists.** Next-ID counter is identified. Whether
  the hash bucket table is already durable (it looks mmap-backed per the file
  listing: `atoms.atomidx` 512 MB sparse) needs the same tracing as the TDB
  indexes before D1 is signed off. The empirical overwrite of slots 1–4 proves
  something is lost; this ADR names "next-ID counter" as the known gap and asks
  Q2 to validate the full state.
- **Q3. Checkpoint on commit, or on threshold.** `ShouldCheckpoint` fires on 16 MB
  of WAL or 60 s of time. The repro shows that a crash within those windows
  loses the batch. Options: (a) checkpoint on every `CommitBatch` (simplest, most
  I/O), (b) raise `ShouldCheckpoint` to batch-boundary sensitivity (checkpoint
  after N commits), (c) accept that a crash within the window re-replays from
  WAL and reach D1's guarantee only after the first checkpoint. (c) is this
  ADR's reading of ADR-023's design and is what D1 formalizes. (a) is a
  perf-conservatism change for a different ADR.
- **Q4. ADR-023 and the `GRAPH ?g` discovery.** In the follow-up repro, named
  graphs were listed correctly after restart (the atoms store had the graph IRI's
  bytes); the quad enumeration returned empty. Named-graph enumeration goes
  through a different path than quad scan. Whether named-graph enumeration should
  also be a durability acceptance test under D3 is a shape question for the test
  file.
- **Q5. Bulk-mode and `DeferMsync`.** `BTreeFile.FlushPage` is a no-op in bulk
  mode (`_deferMsync`), and `_wal.CommitBatchNoSync` + `_wal.FlushToDisk` are the
  bulk-load durability contract. D1 and D3 need to state the bulk-mode case
  explicitly: bulk mode has an explicit `FlushToDisk` at load end that is the
  checkpoint boundary; crash before `FlushToDisk` loses the bulk load, which is
  the documented contract.

## Validation plan

1. **Repro:** the scripted write / SIGKILL / reopen / query sequence that produced
   `urn:finding:mercury-crash-recovery-root-cause` runs green. 3 triples in,
   3 triples out, with their RDF terms intact.
2. **Multi-batch and multi-profile:** the same sequence with 10 batches of
   varying size and both Cognitive and Graph profiles. Each batch's data
   survives, each batch's terms resolve.
3. **Mid-batch crash:** `kill -9` between `BeginBatch` and `CommitBatch` leaves
   zero of the batch's triples visible after reopen (the `CommitTx` marker is
   missing, `LogRecordEnumerator` already filters). This confirms recovery
   semantics did not slip from "committed only" to "all records".
4. **Checkpoint-then-crash:** write, trigger a `Checkpoint` (e.g., 60 s wait or
   WAL > 16 MB), SIGKILL, reopen. Confirms the on-disk form D1 persists is in
   fact sufficient.
5. **W3C and Mercury's own tests** stay green. ADR-023's existing WAL
   contract-tests continue to pass.
6. **Measurement:** record the per-checkpoint overhead of D1's extra `msync`s
   under `benchmarks/Mercury.Benchmarks`, and the end-to-end effect on
   `MercuryWalBenchmarks` (if the shape names it). This is a measurement, not a
   target; the correctness fix is the point.

## References

- [ADR-023](ADR-023-transactional-integrity.md): WAL, batch rollback, transaction
  time. This ADR extends ADR-023's definition of "transactional integrity" to
  cover restart from a crashed-but-committed state.
- [ADR-029](ADR-029-store-profiles.md): Cognitive and Graph have WAL; Reference
  and Minimal do not. This ADR applies to the WAL profiles.
- [ADR-031](ADR-031-read-only-session-fast-path.md): Piece 2 gates
  `CheckpointInternal` on `_sessionMutated`. D1 adds work to that path for
  mutating sessions only.
- Mercury memory: `urn:finding:mercury-crash-recovery-root-cause` (the empirical
  diagnosis this ADR formalizes).
