# ADR-064: A Fact's States — The Graph and Minimal Profiles Number Each Fact's States, Keep No Time, and Never Change a State They Hold

## Status

**Status:** Accepted — 2026-11-16

Accepted by Martin on 2026-11-16: "Commit. Proposals accepted." Every proposal below was accepted with it, so every
decision is now his. The code of Steps 3 to 8 can start.

Steps 3 to 9 were built on 2026-11-17, on omega, one commit each, with Step 6 before Step 5 (see [Steps](#steps)).
Mercury.Tests: 6,455 passed, 156 skipped, 0 failed. Whether the ADR is Completed is Martin's review.

Proposed — 2026-11-16. Martin, the same day: "New ADR is mandated, this session has proved it." The session is the
ADR-061 and ADR-062 work of 2026-11-16: fixing the Cognitive rebuild, and then asking what the Graph profile's
version fields are for ([ADR-062](ADR-062-what-a-read-yields.md) Q6).

Martin answered Q1 to Q5 the same day. Q5, the order, he answered with a rule: "Is there a chain of depencies?
Follow them. If not, it does not matter." The order is in [Steps](#steps).

Each decision below is marked:

- **Decided**: Martin's decision, 2026-11-16, in his words where they are quoted.
- **Accepted**: Sky's proposal, accepted by Martin with the ADR on 2026-11-16.

## Why

### The intent

Martin, 2026-11-16:

> State in Mercury is intended to be immutable, deletes are to be soft. This is also intended for the cognitive
> profiles. Minimal is like Graph, except it does not maintain indexes. Reference is readonly by design. In Graph, a
> fact can be asserted, retracted/deleted and asserted again. Same goes for minimal. What a client sees is different
> from the state held, due to the immutability. This is not a contradiction.

And, the same day, on four questions:

> 1. Minimal - no named graphs, no trigram index.
> 2. Graph - only version is required, Graph keeps no time at all.
> 3. Graph is not a lightweight Cognitive, access to version is not for SPARQL, although it could be used via library
>    api usage. Reasonable but I have no example at this moment.

### Only Cognitive has a model

[ADR-061](ADR-061-a-facts-own-times.md) gave the Cognitive profile a model worked out from its purpose: a temporal
RDF set, its periods, and invariants I1 to I19. The other three profiles never got one. They were made by taking
fields away from the Cognitive entry until it was small enough, and their meaning came from the fields that were left
(see [Context](#context)). The defects measured on 2026-11-16 all come from that. This ADR writes the model for Graph
and Minimal, and states what each of the four profiles is for.

## Context

### How the profiles came about

- **April 2026, [ADR-029](ADR-029-store-profiles.md).**
  - 21.3 billion Wikidata triples at the Cognitive entry's 88 bytes came to a projected 13.8 TB, on an 8 TB disk.
    ADR-029 introduced profiles to cut the bytes per entry. Its context, on the full bitemporal apparatus: "This is
    correct for Mercury's primary use case — cognitive memory … It is wrong for every other use case."
  - Cognitive and Reference were built (Completed 2026-04-26).
  - Graph and Minimal were rows in the table, each with an open question: "Does Graph profile pull its weight as a
    distinct profile? … add Graph only if demanded", and for Minimal, "Depends on use case; defer until one surfaces."
- **16 May 2026, "matrix completion".** Graph and Minimal were built in one day, 1.7.65 to 1.7.68, with no user named.
  Both were made by subtraction:
  - **Graph is the Cognitive entry without its times.** It kept `CreatedAt`, `ModifiedAt`, `Version` and `IsDeleted`.
    ADR-061 later found that the Cognitive store itself read `CreatedAt` and `Version` nowhere. It also kept TGSP,
    whose key without the time has the same order as GSPO's.
  - **Minimal is Reference without named graphs.** So it took over Reference's write model: no log, no session
    writes, no soft delete. ADR-029's April table already gave Minimal no versioning, and the validation record of
    the day says that "delete and reload" is the contract.
- **28 June 2026.** The Graph profile got its only user outside the tests, DrHook.Capture (`844ea96`), which writes
  "raw, append-only triples". It keeps its own times as content: each message has an `at` triple.
- **November 2026.** ADR-061 wrote the Cognitive model, and ADR-062 found that every read returned the Cognitive
  result shape, with made-up times on the other three profiles.

So the differences between the profiles were justified by footprint, and their semantics followed from the bytes. This
ADR takes the reverse order: purpose, then what is held, then the layout.

### What the Graph profile does today

Read in `VersionedQuadIndex` and `QuadStore` at `c3b6025`:

| Operation | The fact's entry | Result |
|-----------|------------------|--------|
| assert | none | a new entry: `Version` 1, `CreatedAt` and `ModifiedAt` from the wall clock, not deleted |
| assert | not deleted | nothing |
| assert | deleted | `Version` + 1, `ModifiedAt` from the wall clock, not deleted |
| retract | not deleted | `Version` + 1, `ModifiedAt` from the wall clock, deleted |
| retract | none, or deleted | nothing |

- Because a repeated assert or a repeated retract changes nothing, a fact's states alternate. **`Version` alone holds
  the whole sequence:** an odd version is asserted, an even one retracted. `IsDeleted` is its parity.
- The update is in place, so the earlier `ModifiedAt` values are overwritten. They are times the profile is not meant
  to have (D1). The sequence of states is not lost.
- A read for a client skips deleted entries. The store's quad count counts every entry, retracted ones included.
- The log record is the one Cognitive uses (`LogRecord`), with its three time fields unused. An Add or a Retract
  record holds the operation that was asked for.
- TGSP is written, flushed, counted, rebuilt and cleared, and no query reads it.

Measured on omega at `c3b6025`, in a scratch clone, with two probes in
[`tools/probes/graph-version-fields/`](../../../tools/probes/graph-version-fields/):

- **`what-the-graph-profile-keeps.cs`.** A Graph store with a supplied clock fixed at 2031-01-01: assert x, retract
  x, assert x, assert y, retract y.
  - GSPO: x at v3, created at 19 and modified at 46 ms after the probe started; y at v2, retracted, modified at 70.
    The other three indexes hold y modified at 71. **Each index stamps its own wall-clock time, and the supplied clock
    is ignored.**
  - The store's quad count is 2; `SELECT ?s WHERE { ?s ?p ?o }` returns 1 row.
  - **After `RebuildSecondaryIndexes`,** GSPO is unchanged. GPOS, GOSP and TGSP each hold one entry: x at v1, created
    at the rebuild's time. y is gone. The rebuild reads what a client sees (`Query(-1, -1, -1, -1)`, deleted entries
    skipped) and inserts it anew.
  - `QueryEvolution` and `QueryAsOf` on the Graph store throw `ProfileCapabilityException`. No read returns a fact's
    states.
- **`a-killed-graph-store-replays-its-log.cs`.** A new Graph store: assert x, retract x, assert x (three commits).
  All four indexes hold x at v3. The process kills itself (SIGKILL, as `tests/Mercury.CrashTarget` does; exit 137).
  - **On reopen all four indexes hold x at v5,** and a second open keeps v5. `SELECT` returns 1 row, which is right.
  - Recovery applied Add, Retract and Add again to indexes that already held them: nothing, v4, v5. States v4 and v5
    never happened. ADR-061 M7 learned that "the log holds the change that was decided at the write, not the operation
    that was asked for", but only for Cognitive.

Read, not run:

- **A prune** (`PruningTransfer`) reads the source as a client sees it and asserts each fact it keeps into the
  target, so every kept fact starts again at v1 there.

### What the Minimal profile does today

- One GSPO index of 24-byte entries that hold the key only. No named graphs: a non-empty graph is refused at the API.
  No trigram index.
- No log. Writes go only through `BeginBatch`, `AddCurrentBatched` and `CommitBatch`. `Add` and `Delete` are refused
  by `RequireWriteCapableProfile`, because the schema says `hasVersioning: false`. Nothing can be retracted.
- A rollback cannot undo a batch: what `AddCurrentBatched` wrote is already in the index (`QuadStore`, at
  `RollbackBatch`).

### The Reference profile

Sealed after one bulk load ([ADR-007](../ADR-007-sealed-substrate-immutability.md), and
[ADR-034](ADR-034-sorted-atom-store-for-reference.md) D7 for a store with a sorted atom store). ADR-029 D7 still says
"Subsequent bulk loads append to the GSPO/GPOS indexes". ADR-034 suspended that for sorted atom stores and promised
"ADR-029's amendment when ADR-034 ships", which was never written. A Reference store with a hash atom store, made
before ADR-034, still takes a second bulk load (`QuadStore`, the single-bulk-load check, applies to sorted stores
only).

### Stores that exist

Checked 2026-11-16: omega holds one Cognitive store (`mcp`) and three Reference stores. zbook holds two Cognitive
stores (`cli`, `mcp`). Neither has a Graph or a Minimal store. elite, omega-ubuntu and omega-windows were not
checked. DrHook.Capture creates a Graph store at `<stores>/drhook` the first time it runs. Martin, the same day:
"There are no old stores."

## The model

### M1. What each profile is for

| | Cognitive | Graph | Minimal | Reference |
|---|---|---|---|---|
| Purpose | a mind's memory, recalled relative to its own experience (ADR-061) | facts that change, kept without a clock | Graph without secondary indexes | a copy of someone else's knowledge |
| Held per fact | every period, from memorized to forgotten | every state, numbered: asserted, retracted, asserted… | as Graph | membership, as loaded |
| States ordered by | the store's clock, in milliseconds | the fact's version | as Graph | one state only |
| Writes | session writes with a log; one owner, one writer | session writes with a log | session writes with a log | one bulk load, then sealed |
| Indexes | GSPO, GPOS, GOSP, TGSP | GSPO, GPOS, GOSP (D3) | GSPO | GSPO, GPOS |
| Named graphs | yes | yes | no | yes |
| Trigram index | yes | yes | no | yes |
| A client sees | what is remembered now; Cognitive SPARQL reads the past | what is asserted now | what is asserted now | the set |

The reasons for the differences:

- **Time is Cognitive's alone.** "The temporality is for recall purposes. Relative to your subjective experience"
  (ADR-061). Only a memory asks when it took something in. A time that a Graph client needs is content, a triple, as
  DrHook.Capture's `at` is. "Graph is not a lightweight Cognitive."
- **Every profile but Reference takes writes.** Reference's value is its provenance, "loaded from source X with
  filter Y at time T" (ADR-007), and changing it in place would make that ambiguous. Its cost model follows from the
  seal: no log, a read-only mapping, a sorted atom store with a perfect hash (ADR-034, ADR-039).
- **Every profile that takes writes deletes softly.** Reference deletes nothing.
- **Secondary indexes trade write cost for read paths.** Cognitive and Graph write each fact into three indexes, so
  that reads which bind the predicate or the object have an index. Reference leaves out GOSP to fit Wikidata on the
  8 TB disk; ADR-029 gives no other reason. Minimal writes each fact once, and a read that binds the predicate or the
  object scans GSPO. TGSP orders by time first, so it serves only a profile that has time.
- **The entry's size follows from what is held.** ADR-029 had it the other way round.

### M2. A fact's states

For the Graph and Minimal profiles:

- **A fact's states form a sequence:** asserted, retracted, asserted, and so on. A fact that was never asserted has
  none.
- **The version names a state.** Version `n` is the fact's `n`-th state: odd is asserted, even is retracted. The store
  holds each fact's latest version, and that one number holds the sequence.
- **Every operation that changes a fact's state is a state of its own.** No clock groups operations, as a clock
  reading does in Cognitive (ADR-061 M2). Assert, retract and assert in one batch give version 3.
- **The store keeps no time.** The versions of two facts cannot be compared, and there is no "the store as it was":
  that is what Cognitive's time is for.
- **A client sees the facts whose latest version is odd.** The quad count counts every fact the store holds.

### M3. Invariants

- **G1.** A fact's identity is its RDF terms: graph, subject, predicate and object. On Minimal, which has no graphs,
  subject, predicate and object.
- **G2.** Asserting a fact that is asserted, or retracting one that is not, changes nothing.
- **G3.** Every other assert or retract raises the fact's version by one.
- **G4.** A version once held never changes and never decreases, except by G3.
- **G5.** Every read answers from the same versions, whichever index serves it (ADR-061 I13).
- **G6.** A rebuild carries every fact's version unchanged, retracted facts included (I14).
- **G7.** Recovery changes nothing. Replaying the log onto indexes that have each applied some first part of it gives
  the versions an uninterrupted run gives, provided recovery starts at or before the earliest point any index has
  reached and runs to the end of the log (I15).
- **G8.** No repair, rebuild or prune resolves a disagreement between stored copies silently. It reports what
  differs, and the source is kept (I19).
- **G9.** Nothing in a Graph or Minimal entry is read from a clock.

### M4. What an operation does

| Operation | The fact's latest version | Result |
|-----------|---------------------------|--------|
| assert | none | version 1 |
| assert | odd | nothing |
| assert | even `n` | version `n + 1` |
| retract | none | nothing |
| retract | odd `n` | version `n + 1` |
| retract | even | nothing |

### M5. The log holds the change decided at the write

As ADR-061 M7 decided for Cognitive, and for the same reason: the indexes are separate files, and recovery may meet
indexes that have applied different parts of the log.

- A record says **"the fact goes from version `n` to version `n + 1`"**, with `n = 0` for the first assert.
- It applies when the index holds the fact at version `n`, and changes nothing otherwise. Applying it twice changes
  nothing, and it reads no clock.
- With today's records, the probe's v3 became v5. With these, the replayed records find v3 where they expect v0, v1
  and v2, and nothing changes.

## Decision

### D1. Graph keeps no time: the entry holds the quad, the child pointer and the version — Decided

"Graph - only version is required, Graph keeps no time at all." `CreatedAt` and `ModifiedAt` go. `IsDeleted` goes
too: the version's parity says it (M2). That last step was Sky's reading of "only version is required", accepted
with the ADR.

### D2. Minimal is Graph without secondary indexes — Decided

"Minimal is like Graph, except it does not maintain indexes … Same goes for minimal." "Minimal - no named graphs, no
trigram index."

- One GSPO index. No named graphs and no trigram index, as today.
- Session writes with a log, soft deletes and the version, as Graph (M2 to M5). The schema says `hasVersioning: true`,
  so `RequireWriteCapableProfile` lets them through.
- A bulk load writes GSPO directly; there is nothing to rebuild.

### D3. Graph's TGSP goes — Decided

"Graph's TGSP goes, no time -> no need." With no time, its key has the same order as GSPO's, and no query reads it.
Graph keeps GSPO, GPOS and GOSP.

### D4. The version is not read by SPARQL — Decided

"Graph is not a lightweight Cognitive, access to version is not for SPARQL." No word of the query language selects or
binds a version.

- A read in the library API is possible, but nothing uses it: "Reasonable but I have no example at this moment." So
  none is added or promised until a user is named.
- The store's own operations, the rebuild and recovery, read the version through the index cursor
  ([ADR-062](ADR-062-what-a-read-yields.md) D3). That read is internal.

### D5. The log holds the decided change — Accepted

For Graph and Minimal, as in M5. The record's form belongs to the implementation. `LogRecord` already has room: the
three time fields Graph does not use are 24 bytes.

### D6. A rebuild carries what the store holds — Accepted

As the Cognitive rebuild does since `c3b6025`:

- It compares first: each entry of a target index must be in GSPO as it is, with its version. If any is not, it
  changes nothing, says so and throws (G8).
- Otherwise it clears each target and carries every GSPO entry, retracted facts included, with its version (G6).

### D7. A prune starts every fact at version 1 — Decided

"Start at 1." A prune writes a new store, and a copy is another store (ADR-061 M3). Each asserted fact the prune
selects is asserted there anew, at version 1. No retracted fact is carried. Today's prune does this already
(`PruningTransfer` asserts each fact it keeps into the target; read, not run), so D7 changes no code.

### D8. The layout changes, and no store is converted — Decided

"There are no old stores. Convert is not needed. No need for recreate."

- The Graph and Minimal entries change (D1, D2). The sizes are measured when the layout is built.
- *Accepted:* each index file's magic number changes with its layout, as each profile's has its own already. A file
  in the earlier layout is then refused by the check at open that exists, and is never read as the new one. Nothing
  else is added.

### D9. ADR-029 is amended when this ADR is accepted — Accepted

- Its table: Graph without time and with three indexes; Minimal with versioning, a log and session writes; the entry
  sizes as built.
- Its open questions on Graph and Minimal: answered here.
- D7: a Reference store takes one bulk load (D10).
- The validation records of 2026-05-16 stay as they are: they record what was built and measured that day.

### D10. A Reference store takes one bulk load — Decided

"It should stop, yes, it is read only by design." The check that refuses a second bulk load applies to every
Reference store, whatever its atom store. Today it applies to a sorted atom store only (`QuadStore`'s constructor,
ADR-034 D7).

### Not changed

- **Cognitive** (ADR-061). Its model is the one this ADR follows.
- **Reference** stays sealed (ADR-007), and D10 closes the one way it could still be changed.
- **What a client sees.** Every query on Graph and Minimal answers as it does today, apart from the defects measured
  above.

## Questions

All answered by Martin on 2026-11-16.

- **Q1. Graph's TGSP** (D3): it goes?
  *Answered:* "Graph's TGSP goes, no time -> no need."
- **Q2. A prune's versions** (D7): does a pruned Graph store carry each fact's version, or start every fact again at
  version 1? *Answered:* "Start at 1."
- **Q3. Old stores** (D8): converted, or refused when opened, with a message to re-create the store?
  *Answered:* "There are no old stores. Convert is not needed. No need for recreate."
- **Q4. A Reference store with a hash atom store** takes a second bulk load today (ADR-029 D7). Does it stop?
  *Answered:* "It should stop, yes, it is read only by design." (D10)
- **Q5. Order** against ADR-061's remaining steps. *Answered:* "Is there a chain of depencies? Follow them. If not,
  it does not matter." The chain is in [Steps](#steps).

## Steps

### The order follows the dependencies

What remains of ADR-061 (its plan, 2026-11-14): P4, pruning, which waits for the form of the selection query; P5,
ADR-062's cursor, whose Steps 2 to 5 are done and Step 6 is under way; P6, the words; P7, one removal, with ADR-062's
part two; P8, consumers and documents; P9, dogfood on the live memory store, which waits for its index differences to
be resolved (ADR-061 LQ5); and ADR-061's later part, the Cognitive layout, after its Steps 1 to 11.

The links, read in the code on 2026-11-16:

- **ADR-062 Step 6 comes before every read built here or in ADR-061.** ADR-061 P6 builds the words on the cursor
  (ADR-062 D8). This ADR's rebuild reads the held version through it (D4, D6). Built on the enumerators first, each
  would be built twice.
- **This ADR's log (D5) comes before ADR-061 P7.** P7 removes `Retract` and `RetractBatched`. A Graph delete writes a
  `Retract` record today: `DeleteCurrent` through `LogRecord.CreateRetract`, and `DeleteCurrentBatched` through
  `RetractBatched`. If P7 came first, it would have to keep `Retract` for Graph.
- **D5 comes before Minimal's write path (D2),** which writes D5's records.
- **ADR-061 P6, P7 and P8 keep their own order.**
- **Nothing else is linked.** ADR-061 P4, D10 and the documents can go anywhere in the order.
- **This ADR's code waits for its acceptance.**

### The steps

1. **Martin's review** of this text — **done 2026-11-16**: the ADR is accepted, with every proposal.
2. **ADR-062 Step 6** finishes. The Graph cursor row carries the version, the store's own read of it (D4).
3. **Tests that state the model,** written first and failing on today's code:
   - the table of M4, on Graph and on Minimal;
   - a rebuild that carries retracted facts and their versions, and refuses when the indexes differ (G6, G8);
   - recovery after a real kill, as `tests/Mercury.CrashTarget` does (G7);
   - every index agrees after every operation (G5).

   **Graph's, written 2026-11-17** and run on `91cbf5c`: 12 tests, of which 9 fail.
   - `FactStatesTests`: the table of M4 in single writes and in one batch passes, since today's index already
     follows it. Recovery fails in 4 of its 5 cases, each holding a fact at v5 that an uninterrupted run holds at v3.
     The case that passes has every index at the earlier point, where replaying the operations from exactly there
     gives the right versions.
   - `GraphRebuildTests`: a lost GPOS and GOSP are rebuilt with 4 entries, not 6, and a difference in version is not
     refused.
   - `GraphLayoutTests`: the same writes at other times give different files; TGSP exists; the earlier layout's
     magic number is accepted.

   What an index holds is read from its file once the store is closed, through the index's cursor, so no read of the
   store returns a version (D4). Each test is committed with the step that makes it pass. Minimal's come with Step 7.
4. **The log holds the decided change** (D5). Before ADR-061 P7. **Done 2026-11-17:**
   - A Graph store logs one record, `NextVersion`: the fact goes from the version in the record to the next. It is
     applied in each index only when that index holds the fact at that version. The record's version uses the bytes
     of `MemorizedAtTicks`, and its other time fields are 0.
   - Each assert or retract is decided from the version GSPO holds, read through the cursor (D4). In a batch, each
     one starts from the version the batch's earlier operations took the fact to (M2).
   - The Graph index has one change, `NextVersion`. Its soft delete and its un-delete on a re-add are gone.
   - In recovery, a fact's first assert counts by its transaction, as Cognitive's decided changes do (ADR-058).
   - On a Graph store, `Add` and `Retract` with stated times assert and retract the fact. A Graph store writes no
     `Add` or `Retract` record, and refuses one in its log.
   - `AddCurrent` and `AddCurrentBatched` say whether the fact's state changed, where they said "true" before.
   - `FactStatesTests` pass.
5. **The rebuild** carries what the store holds (D6). **Done 2026-11-17, after Step 6:**
   - It compares first: an entry of GPOS or GOSP that GSPO does not hold at the same version is counted. If any is,
     it changes nothing, logs a warning and throws: "The rebuild changed nothing: GPOS holds n and GOSP m entries
     that GSPO does not hold as they are" (G8).
   - Otherwise it empties each index and carries every fact GSPO holds, of every graph and retracted ones too, at
     its version (`VersionedQuadIndex.CarryEntry`, G6). It saves each count as exact up to GSPO's transaction, as the
     Cognitive rebuild does (ADR-058), and logs what each index held before and holds now.
   - `AddRaw` is gone: the Graph index's changes are `NextVersion` and the rebuild's `CarryEntry`.
   - `GraphRebuildTests` pass. Each fails under its mutation: a comparison without the version, and a carry at
     version 1.
6. **The layout** (D1, D3, D8): the entry holds the key, the child pointer and the version; Graph's TGSP goes; each
   file's magic number changes with its layout. **Done 2026-11-17, before Step 5:** the two are not linked, and built
   first, the layout lets the rebuild be written once, for the entry and the indexes Graph keeps.
   - A Graph entry is 44 bytes: the 32-byte key, the 8-byte child pointer and the 4-byte version. 371 entries fit a
     page, where 255 of the 64-byte entry did. `CreatedAt`, `ModifiedAt`, `IsDeleted`, `Flags` and `Reserved` are
     gone, and nothing in the index reads a clock (G9).
   - A read for a client yields the facts whose version is odd. `QueryHeld`, formerly `QueryAllVersions`, yields
     every fact held with its version, for the store's own operations.
   - The magic number is "GRAPHVER" (`0x4752415048564552`). A file in the earlier layout, "GRAPHIDX", is refused at
     open by the check that exists.
   - A Graph store has GSPO, GPOS and GOSP; its schema lists those three.
   - `GraphLayoutTests` pass: the same writes at other times leave byte-identical index files, a Graph store has no
     `tgsp.tdb`, and a file with the earlier magic number is refused.
   - Seen, not changed: a store refused at open leaves the files it had opened before the refused one open. That is
     the existing check's behaviour for every profile, and D8 adds nothing to it.
7. **Minimal's write path** (D2): the log, session writes, soft deletes, the version. **Done 2026-11-17:**
   - A Minimal store has a log. Its writes, single or batched, go through Graph's: each assert or retract is decided
     from the version GSPO holds and logged as `NextVersion` (D5). A write that names a graph is refused before
     anything is logged. Its schema says `hasVersioning: true`.
   - The leaf entry is 28 bytes, the 24-byte key and the 4-byte version, so 584 fit a page where 681 did. Internal
     entries are as before. The magic number is "MINIMVER" (`0x4D494E494D564552`); the earlier "MINIMAL\0" is
     refused.
   - The batch path with no log is gone, along with what came with it. A rollback could not undo a batch, because
     what `AddCurrentBatched` wrote was in the index at once. Now the batch is applied at commit, and a rollback
     leaves the store as it was.
   - A Minimal store collects no predicate statistics at a checkpoint: it has no GPOS to scan, and it collected none
     before it had a log.
   - The tests: `FactStatesTests` run the table of M4 and recovery after a real kill on Minimal too, and
     `FactStatesLayoutTests`, formerly `GraphLayoutTests`, run identical files and the refusal of the earlier magic
     number on both profiles. On `c37dd8b`, before this step, a single write to a Minimal store was refused, as
     `Minimal_DirectAdd_ThrowsProfileCapability` stated. That test is replaced by ones for a single write, for the
     refusal of a named graph, and for a rollback.
8. **A Reference store takes one bulk load** (D10). **Done 2026-11-17:**
   - Measured first, with
     [`tools/probes/reference-single-bulk-load/`](../../../tools/probes/reference-single-bulk-load/). A Reference
     store with a hash atom store that holds its load takes a second one in two ways: opened again in bulk mode, which
     D10 names, and with a batch while it is open to be queried, which the probe found. Both leave GSPO holding what
     GPOS does not. A sorted store refused the first way. The second way failed only for a new atom, which a sorted
     atom store cannot add at runtime.
   - A batch is Reference's one write path, its bulk-load entry (ADR-029 D7), so both are a second load under D10.
     A Reference store that holds atoms when it opens holds its load. It refuses bulk mode at open and `BeginBatch`
     while it is open, whatever its atom store, and names D10. One load may still be many batches of one session.
   - `ReferenceSingleLoadTests`: on `f13407c` four of five failed, the hash store taking both loads and the sorted
     store's refusal naming ADR-034 only. All five pass.
   - Seen, not changed: disposing a store while a batch holds the write lock throws `SynchronizationLockException`,
     which hides the exception that ended the batch. It is outside this ADR.
9. **Documents:** ADR-029 (D9), `mercury-internals.md`, `api-usage.md`, `MERCURY.md` where it describes the profiles,
   `STATISTICS.md`. **Done 2026-11-17:**
   - ADR-029: the table of Decision 1 (Graph: three indexes, 44 B, versioned; Minimal: versioned, 28 B), Decision 7
     and its downstream commitment (one bulk load, D10), and its two open questions on Graph and Minimal, each
     amended where it stands, with the earlier text kept. The validation records of 2026-05-16 are as they were.
   - `mercury-internals.md`: the WAL record on Graph and Minimal (`NextVersion`), the indexes each keeps, and that
     only Cognitive reads a clock.
   - `README.md`: its one line on the four profiles.
   - `api-usage.md` and `MERCURY.md` say nothing of Graph's or Minimal's layout or writes, so nothing changed there.
     `MERCURY.md`'s "single bulk-load, then sealed" for Reference already holds.
   - `STATISTICS.md` holds measurements per release, on every test machine. It is measured at the next release cut,
     as its own maintenance note says.

ADR-061 P6 needs only Step 2 of this list. Its P7 needs Step 4 as well. Steps 3 to 8 change Mercury code; they can
start since the acceptance. Step 2 is done (ADR-062 Step 6, `29db7b5`). Step 4 is done (2026-11-17), so ADR-061 P7
waits for nothing in this ADR.

## Not in this ADR

- The Cognitive model, its words and its layout (ADR-061).
- A read of the version in SPARQL (D4), or in the library API until a user is named.
- The Reference seal (ADR-007), apart from D10.

## References

- [ADR-029](ADR-029-store-profiles.md): the four store profiles.
- [ADR-061](ADR-061-a-facts-own-times.md): the Cognitive model; M3 (a copy is another store), M7 (the log holds the
  decided change), I13 to I15, I19, D12 (a prune), its plan P0 to P9.
- [ADR-062](ADR-062-what-a-read-yields.md): what a read yields, the index cursor; Q6 is answered here.
- [ADR-007](../ADR-007-sealed-substrate-immutability.md): sealed substrates are re-created, not modified.
- [ADR-034](ADR-034-sorted-atom-store-for-reference.md): the sorted atom store; D7, a single bulk load.
- Validation records of 2026-05-16: [Graph](../../validations/adr-029-graph-profile-2026-05-16.md),
  [Minimal](../../validations/adr-029-minimal-profile-2026-05-16.md).
- [`tools/probes/graph-version-fields/`](../../../tools/probes/graph-version-fields/): the two probes of 2026-11-16.
