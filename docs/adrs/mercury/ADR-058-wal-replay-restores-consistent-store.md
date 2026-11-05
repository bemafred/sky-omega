# ADR-058: WAL Replay Restores a Consistent Store — The Checkpoint Flushes the Indexes and the Atom Store Before It Truncates the Log

## Status

**Status:** Accepted — 2026-11-04

Found by a scripted SIGKILL + restart repro of mercury-mcp 1.8.32 against a scratch
store (recorded in Mercury memory as `urn:finding:mercury-crash-recovery-root-cause`,
severity critical, deterministic). ADR-023 declared transactional integrity complete
in March 2026; this ADR narrows one guarantee that ADR-023's acceptance criteria did
not cover: a crash-safe RESTART from a WAL that was fsynced at commit time.

Revised 2026-11-04 after the defect damaged a production memory store, elite's
mercury-mcp store (see "The incident on elite" below). The damage did not need a
crash. Claude Code ends its MCP server with SIGINT, then SIGTERM 100 ms later, and
on a slow machine that ends mercury-mcp inside `pool.Dispose()`. A routine exit
loses what a `kill -9` loses. The revision adds D5–D7: durability must not depend
on shutdown, atoms become durable before the log commit that references them, and
open checks the atom header against the data.

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

### The incident on elite (2026-10-29 to 2026-11-04)

elite (Linux x64, 4 cores, Claude Code 2.0.76 from npm, mercury-mcp 1.8.32) holds
a Cognitive memory store created on 2026-10-27, store id `01a17edb…`. The
evidence was read over SSH and every step changed nothing. The sources are the
store's file headers and timestamps (kept through the 2026-11-04 OS move), the
Claude Code transcripts and MCP logs on elite, and the Mercury memory on zbook
(`urn:sky-omega:finding:elite-store-atom-id-reuse-incident`).

The on-disk headers on 2026-11-04:

| File | Header |
|---|---|
| `atoms.atoms` | dataPosition 1024, nextAtomId 0, atomCount 0, totalBytes 0, magic valid |
| `gspo.tdb` | root 1, next page 2, entries 92 |
| `gpos.tdb` | root 1, next page 2, entries 86 |
| `gosp.tdb`, `tgsp.tdb` | root 1, next page 2, entries 0 |

The atom header still holds the values `LoadMetadata` writes for a new file,
although the data region holds atoms from offset 1024. `HashAtomStore.SaveMetadata`
runs only in `Flush` and `Dispose`, and neither completed after the store was
created. Every start of mercury-mcp therefore opened the atom table as empty.

Timeline (UTC):

- **2026-10-29 ~11:15.** The tools are upgraded from 1.8.30 to 1.8.32 and `/mcp` reconnects
  Mercury. Afterwards `mercury_stats` reports 92 quads, but every query returns
  nothing: the quads reference atom IDs the reopened atom table no longer knows.
- **11:19–11:47.** The same session writes new findings. Interning starts again at ID 1, so
  the new terms overwrite the slots the 92 older quads reference
  (`atoms.atoms` at offset 1024 now starts with
  `<urn:session:2026-10-29-install-tools-race>`).
- **12:04.** An open `?s ?p ?o` scan returns scrambled triples, such as
  `<urn:file:src/Mercury.Runtime/BTreeFile.cs> rdf:type <urn:file:src/Mercury/Storage/HashAtomStore.cs>`.
  These are the older quads read through overwritten atoms. At 12:12 the session
  retracted them as a false alarm after checking only the triples it had just
  written. **The retraction was wrong.** The new triples were intact because their IDs
  were the ones doing the overwriting.
- **2026-10-31.** elite hangs (failing RAM, two modules later replaced). No Mercury files
  change between 2026-10-29 11:47 and 2026-11-03.
- **2026-11-03 12:33–12:36.** A read-only session: 92 quads, 0 atoms, two named graphs
  both shown as `<>`, every query empty. `gspo.tdb` was last modified at
  12:36:34.86, the second the session ran `/exit`. Shutdown saved the `gspo` and
  `gpos` headers and ended before `gosp`, `tgsp` and the atom store, which
  `QuadStore.Dispose` closes last.

The incident needed neither a crash nor the hang. Each damaging step was a routine
end of the server process.

### How the client ends the server

Confirmed on elite on 2026-11-04 with scratch stores under `/data/shutdown-test`
(`bin/` holds the scripts, `r0/` and `r1/` the stores and logs). Each run writes
the same 3-triple `INSERT DATA` and reads the headers afterwards. A wrapper
timestamps the server's stderr and then `exec`s into mercury-mcp, so the client's
signals reach the server directly.

| Run | How the server ends | Headers after exit | After reopen |
|---|---|---|---|
| R0, control | raw JSON-RPC, then stdin EOF; exit about 150 ms later | nextAtomId 8, atomCount 8, 3 entries in all four indexes, WAL checkpointed to 80 B | intact |
| R1, `claude -p` (2.0.76) | Claude's debug log: `Sending SIGINT` at 13:44:28.330, `SIGINT failed, sending SIGTERM` at .432, `exited cleanly` at .482; the server printed `MCP Server shutting down...` (the start of `pool.Dispose()`) and was gone about 10 ms later | atom header as new, 0 entries in all four indexes, WAL 400 B uncheckpointed | 0 quads, 0 atoms: the acknowledged write is lost |

- **What the client does.** Claude Code sends SIGINT, waits 100 ms, then sends
  SIGTERM, and SIGKILL later. mercury-mcp handles the SIGINT as a graceful
  stop. The SIGTERM arrives during `pool.Dispose()` and ends the process,
  because by then no handler for it is registered (Q7).
- **Why nothing was saved in R1.** After a mutation, `Dispose` runs
  `CheckpointInternal` (predicate statistics, trigram flush) before any index or
  atom header is saved, so the kill lands before anything is written. A
  read-only session skips the checkpoint and gets as far as the first index
  headers, which is the elite 2026-11-03 state.
- **Why zbook is fine.** zbook (Claude Code 2.1.318, mercury-mcp 1.8.39) logs the same
  SIGINT and never a SIGTERM. Its store files are written about 55 ms after the
  SIGINT, and its headers are consistent (257 atoms, 449 entries in all four
  indexes). Whether a store survives comes down to machine speed against a
  100 ms window. Neither client version is at fault.

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
   per-write writes. The header at offset 0 has fields for the next atom ID (offset 8)
   and the atom count (offset 16), and `LoadMetadata` reads them. But
   **`SaveMetadata` runs only in `Flush` and `Dispose`**, and neither the commit
   nor the checkpoint calls them, so the header holds whatever the last
   *completed* `Dispose` wrote. On elite that was the values for a new file
   (see the incident). `Intern` then hands out IDs that are already in use.
   Correction 2026-11-04: the first version of this ADR called the counter
   "in-memory only". It has a durable field that is never kept current.
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
- **The atom bytes themselves.** A WAL record (`LogRecord`, 80 bytes) holds TxId,
  operation, the graph, subject, predicate and object *IDs*, the valid and
  transaction times, and a checksum. It does not hold the strings. Interning
  writes the atom bytes through the mmap and does not flush them. A process kill
  leaves them in the page cache, so they survive. A power loss or kernel hang
  (elite hung on 2026-10-31) can lose atom bytes that a fsynced `CommitTx`
  already references. Replay then rebuilds quads that point at atoms that were
  never written.

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

### D5. Durability does not depend on shutdown

`Dispose` is not a durability point. The store must be consistent if the process
ends at any instruction, including inside `Dispose`, inside `CheckpointInternal`,
and between the close of one index and the next. The client in the incident is
an ordinary MCP host with a 100 ms grace period. A terminal closed with the
session in it, the OOM killer and a power cut give less. The order in which
`Dispose` closes components then no longer affects correctness. `Dispose` can
still checkpoint, to keep the next open fast, but nothing may rely on it having
finished.

### D6. Atoms are durable before the commit that references them

`CommitBatch` makes the atom bytes, the offsets and the atom header (next ID and
count) durable before it fsyncs `CommitTx`. A committed log record then never
references an atom that a power loss can take away, and the header on disk never
lags the IDs in the log. The order is: atoms (data, offsets, header), then the WAL
commit. The indexes stay as they are, written after the commit and recoverable by
replay.

### D7. Open checks the atom header against the data

On open, `HashAtomStore` walks the atom records that follow the header. Atoms are
written one after another, so a record that belongs to the store starts exactly
where the previous one ends. Every record that continues the chain is taken: the
next ID moves past it, and the header is saved. No ID that is present on disk is
handed out again, whether the atoms came from a batch that crashed before its
commit or from a Mercury older than this ADR that never saved its header.

A chain that continues and then breaks is what ID reuse leaves: newer atoms written
over the start of older ones. The store then refuses every write (`WriteRefusal`,
checked by `QuadStore` before each write) and logs why. Reads still work. An offset
entry right after the header that does not continue the chain is a leftover, since
`Clear` before this ADR left offset entries in place. It is ignored, and `Clear`
now zeroes them.

D7 stops further damage. It cannot find damage that is already complete: when the
reused atoms form an unbroken chain (the newer session wrote at least as many
atoms as the older one had), the store looks consistent, and only outside evidence
shows that older quads point at reassigned IDs. What a repair does is Q6.

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
- No change to the public API. D6 changes the write path inside `CommitBatch`:
  one more `msync` of the atom regions written by the batch, plus the atom
  header, before the WAL fsync. Bulk mode keeps its own contract (Q5).
- D7 can turn a store that opens today, with wrong data, into one that refuses
  writes. That is intended: a store that knows it is inconsistent stops taking
  writes.

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
  perf-conservatism change for a different ADR. The incident sharpens this: an
  MCP session usually ends before either threshold, so in practice every session's
  writes depend on replay. (c) stays acceptable only together with D6 and a replay
  that works; D3's tests decide it.
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
- **Q6. Repairing a store damaged before the fix.** elite's store holds 92 `gspo`
  entries whose atom IDs were reassigned on 2026-10-29. Atoms past the end of the
  newer data may still hold the original strings, but an ID's string cannot be
  told apart from a reused one without outside evidence. Options: (a) a
  `mercury` verify command that reports the header against the data and the quads
  whose atoms changed meaning; (b) a repair that rebuilds the header from the data
  and marks suspect quads; (c) export what can be read, then rebuild the store.
  elite's store is the first case. Work on a copy.
- **Q7. Why SIGTERM ends the process inside `Dispose`.** Answered 2026-11-05.
  `RunAsync` disposes the host before it returns, and disposing `ConsoleLifetime`
  removes its SIGINT and SIGTERM handlers. A SIGTERM after that gets the default
  action and ends the process while `Main` is still in `pool.Dispose()`: no
  `ProcessExit`, no `finally`. On zbook, a probe (a host, `RunAsync`, then
  2 s of work after it) exits 143 after 2 of 10 work steps on a SIGTERM; with its
  own `PosixSignalRegistration` kept until the work ends, it finishes and exits 0.
  mercury-mcp itself: SIGINT, then a SIGTERM delivered inside `pool.Dispose()` (the
  process is frozen with SIGSTOP when it prints `MCP Server shutting down...`, so the
  SIGTERM lands there every time). Before the change it was killed by the signal
  6 of 6 times, after it it finished and exited 0 6 of 6 times. Fixed in
  `src/Mercury.Mcp/Program.cs`: SIGINT and SIGTERM handlers registered before the host
  starts and disposed when `Main` returns, after `pool.Dispose()`. They set
  `Cancel` (no default action) and stop the host. D5 is still the fix; this
  lets a normal stop finish its checkpoint instead of leaving the work to the next
  open's replay. A SIGKILL still ends the process at once, and D5 covers that.

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
6. **Client shutdown sequence:** write, then send SIGINT and SIGTERM 100 ms apart
   (the Claude Code sequence), reopen. The write is present with its terms. Run it
   after a mutation and after a read-only session.
7. **Killed inside `Dispose`:** kill the process at each step of `Dispose`
   (during the checkpoint, between index closes, before the atom store closes).
   Every reopen is consistent.
8. **Header behind the data:** open a store whose atom header holds the values for a new
   file while atom records exist (elite's shape). The store refuses writes and
   reports the mismatch (D7). No ID is reused.
9. **elite end-to-end:** repeat R1 from "How the client ends the server" with
   Claude Code 2.0.76 on elite. After the fix, the reopened store holds the 3
   triples.
10. **Measurement:** record the per-checkpoint overhead of D1's extra `msync`s
   under `benchmarks/Mercury.Benchmarks`, and the end-to-end effect on
   `MercuryWalBenchmarks` (if the shape names it). This is a measurement, not a
   target; the correctness fix is the point.

## Implementation (started 2026-11-04)

Done, on zbook (Linux x64), with every test in the solution passing (6,676):

- **D3: the restart tests** (`tests/Mercury.Tests/Storage/CrashRecoveryTests.cs`). A
  separate process (`tests/Mercury.CrashTarget`) writes and kills itself, and the test
  reopens the store. The cases: kill after a commit, two killed sessions in a row
  (elite's shape), a killed session then a clean one, a batch killed before its commit,
  ten killed sessions with batches of up to 2,000 triples, and kills inside `Dispose`
  after 0–30 ms. Each checks every triple with its terms and the exact quad count. All
  ten failed before the fix and pass after it.
- **D6.** `CommitBatch` and the single-write paths call `IAtomStore.Flush` before the
  WAL commit. It writes the header and msyncs data, offsets and hash table, and is a
  no-op when nothing was interned. `FlushToDisk` (bulk) flushes the atoms before the WAL too.
- **D1 and D2.** `CheckpointInternal` flushes the atoms and saves every index's count
  durably before `_wal.Checkpoint` truncates the log. An exception there leaves the log.
- **Index counts.** `BTreeFile` keeps the saved count apart from the live one, with
  `CountTxId` (header offset 32): the WAL transaction the saved count is exact for.
  Page allocation and root splits save only the structure (`SaveStructure`).
  `CommitBatch` and `Add` save the count after applying, without an msync, and the
  checkpoint saves it with one. On replay, an entry already present from a transaction
  after `CountTxId` (the replayed Add is a no-op) is counted, once.
- **D5** holds by construction: after a commit the files are consistent, so a kill
  anywhere in `Dispose` loses nothing. The `dispose-killed` tests check it.
- **D7**, with tests on edited files (`AtomStoreRecoveryTests.cs`): a header behind
  the data, a chain that breaks, a leftover offset, and `Clear` then reopen.

Cost, measured on zbook (NVMe, `/data`, Release, 1,000 single `AddCurrent` with new
terms and 200 `CommitBatch` of 50): a single write went from 4.0–4.3 ms to 6.5–6.8 ms
(the three atom msyncs), and a batch of 50 stayed within noise (127 ms before,
127–136 ms after).

Not done yet:

- **The Graph profile.** It gets the atom fix (D6, D7) and the checkpoint order, but
  its keys carry no transaction time, so replay cannot tell an entry the crashed
  process inserted from a duplicate Add. Its count can stay low after a crash. The
  crash target only writes Cognitive stores so far.
- **The trigram index after a crash** is not covered by a test.
- **A kill inside a leaf split.** The entries move to the new page before the parent
  learns about it, so a kill in that window (microseconds per split) can leave entries
  reachable only through the leaf chain. This predates this ADR and is not addressed.
- **Power loss on Windows.** `MemoryMappedViewAccessor.Flush` there is
  `FlushViewOfFile`, which does not reach the disk without `FlushFileBuffers`. This
  holds for every mmap'd file Mercury writes, not only the atoms.
- **Q6** (repair) and **validation 9** (rerun R1 on elite with Claude Code 2.0.76
  once the tools are installed there). Q7 is answered and fixed (see Q7).

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
  diagnosis this ADR formalizes, in elite's store, which this defect has since
  damaged).
- Mercury memory on zbook: `urn:sky-omega:finding:elite-store-atom-id-reuse-incident`
  and `urn:sky-omega:finding:mcp-shutdown-escalation-kills-dispose` (the incident
  and the shutdown test).
- elite: the Claude Code sessions `fcf1c727-…` (2026-10-29) and `205c5e8a-…`
  (2026-11-03) under `~/.claude/projects/-ssd-sky-omega/`, and the test stores
  and scripts in `/data/shutdown-test`.
