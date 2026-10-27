# ADR-054: DELETE Ends a Fact's Valid Time — Bitemporal Delete Semantics for the Cognitive Profile

## Status

**Status:** Accepted — 2026-10-27 (Proposed → Accepted the same day, after Q1 and Q2 were settled)

Found while settling ADR-053's Q1 (how a correction is recorded). On a Cognitive store, a SPARQL DELETE removes the
fact from every point-in-time view, including views from before the delete, so the store loses the history it exists to
keep. This contradicts the documented and stated meaning of a delete:

- [ADR-001](ADR-001-compaction.md): "triples are never physically removed, only marked with end-validity timestamps".
- Recorded in Mercury on 2026-05-30 as the design intent: "Deletes don't remove triples — they close valid-time
  intervals. As-of queries at a timestamp before the DELETE return the prior state."
- [ADR-008](../ADR-008-workload-profiles-and-validation-attribution.md): "Full bitemporal (valid-time +
  transaction-time on every triple). Versioning, soft-delete, audit trails."

So the meaning is settled; the implementation doesn't follow it. This ADR decides how to make it follow, what happens
to the history already lost, and fixes three further delete defects found on the way.

## Context

### What a delete does today

`QuadStore.DeleteCurrent` (used by every SPARQL delete: DELETE DATA, DELETE templates, DELETE WHERE) calls
`Delete(s, p, o, MinValue, MaxValue)`, which reaches `TemporalQuadIndex.DeleteFromTree` in each of the four indexes:

```csharp
if (IsSameDimensions(entry.Key, key) && !entry.IsDeleted)
    if (entry.Key.ValidFrom <= key.ValidTo && entry.Key.ValidTo >= key.ValidFrom)
    {
        entry.IsDeleted = true;              // ValidTo is left as it was
        entry.ModifiedAt = key.TransactionTime;
        ...
        return true;                          // first overlapping entry only
    }
```

and `MatchesTemporalQuery` excludes `IsDeleted` from AS OF and DURING at every time:

```csharp
TemporalQueryType.AsOf => !entry.IsDeleted && key.ValidFrom <= _query.AsOfTime && key.ValidTo > _query.AsOfTime,
TemporalQueryType.AllTime => true,
```

A delete is therefore a **retraction**: "this version was never true". The fact keeps `ValidTo = ∞`; only ALL VERSIONS
still shows it, flagged.

### Observed (probe, 1.8.22, Cognitive profile)

| # | Sequence | Expected | Observed |
|---|---|---|---|
| 1 | `status "open"` valid from t0−2h; then `DELETE { … ?o } INSERT { … "fixed" }` | AS OF t0−1h: `open` | AS OF t0−1h: **nothing** |
| 2 | `INSERT DATA x` ; `DELETE DATA x` ; `INSERT DATA x` | `x` current | reports 1 affected; `x` **not current** |
| 3 | `DELETE DATA x` twice | second reports 0 | second reports **1** |
| 4 | `x` valid [t0−3h, t0−2h) and [t0−1h, ∞); `DELETE DATA x` | current version ended | the **past** version flagged; `x` **still current** |
| 5 | case 1, store closed and reopened (WAL replay) | as 1 | as 1 (recovery is consistent with the defect) |

Causes:

- **1:** a delete flags the entry instead of ending its valid time.
- **2:** `InsertIntoLeaf` Case B ("both entries current: RDF idempotency, no-op") doesn't check `IsDeleted`. A flagged
  entry with `ValidTo = ∞` counts as current, so the re-insert is dropped. The Graph profile's `VersionedQuadIndex`
  handles this correctly ("insert with key present and IsDeleted → un-delete"); the temporal index doesn't.
- **3:** `DeleteBatched` returns true when the atoms exist, without checking for a live version.
- **4:** the delete marks the first entry whose interval overlaps `[MinValue, MaxValue]`, which is the earliest
  version. It also scans only the leaf page `FindLeafPage` lands on.

### The live memory store

A census of a copy of the Mercury MCP store (Cognitive, 1.8.22): 5,835 versions; **297 flagged deleted, in 17 graphs**,
written 2026-04 (32), 2026-05 (155), 2026-06 (103) and 2026-10 (7). The most frequent predicates are `nextAction` (32),
`label` (29), `type` (24), `comment` (23), `status` (21) and `headCommit` (21): resume points and statuses being moved
forward. **No live version has a closed valid time.** So every change of state ever recorded in the memory store was
recorded as "never true", and the store holds no history of a fact ending.

Reading the GSPO leaf pages directly (the Q1 dry run) finds **323** flagged entries, not 297. The census walked the
graphs `GetNamedGraphs` lists, and a graph whose every entry is deleted (the test graphs below) is not listed.

Defect 2 may also have dropped facts: any value set again after being deleted (a status returning to an earlier value)
was silently not stored. The insert was a no-op and left no record, and the WAL has been checkpointed since, so how
many were lost can't be determined from the store.

## Decision

**A SPARQL delete ends the fact: the version that holds at the delete's transaction time gets that time as its
`ValidTo`. "This version was never true" is a separate operation, Retract, which SPARQL doesn't use.**

### D1. End (SPARQL DELETE, `DeleteCurrent`)

- At transaction time T, the version with `ValidFrom ≤ T < ValidTo` and `!IsDeleted` gets `ValidTo = T` and
  `ModifiedAt = T`. `IsDeleted` stays false. AS OF before T returns it, AS OF T and after don't, and ALL VERSIONS shows
  the closed interval.
- Only that version changes. Earlier versions, already closed, are untouched. If no version holds at T (none exists,
  all are closed, or the only one starts after T), nothing changes and the delete reports 0.
- **The version is found when the delete is issued, and the WAL record names it** (Q2, resolved): `End` records the
  version's `ValidFrom` and the new `ValidTo = T`. Applying the record sets `ValidTo = T` on the live entry with exactly
  that `ValidFrom`, if its `ValidTo` is later than T. Otherwise it does nothing, so applying the record twice is the
  same as once.
- The lookup walks the leaf chain (`NextLeaf`) over all entries with the same dimensions, not just one page.
- All four indexes are changed the same way. `HandleTemporalUpdate` already shortens `ValidTo` in place, which is safe
  because no two entries share dimensions and `ValidFrom`.
- T is the record's transaction time (`_batchTransactionTimeTicks` in a batch), not the clock at apply time. The index
  keeps milliseconds, so T and the recorded `ValidFrom` are the index's millisecond values, converted to ticks for the
  record and back without loss.

### D2. Retract (`Retract`, `RetractBatched`)

- `QuadStore.Delete(s, p, o, validFrom, validTo)` and `DeleteBatched(…, validFrom, validTo)` are renamed `Retract` and
  `RetractBatched`. They mark the version **whose interval is exactly** `[validFrom, validTo)` with `IsDeleted` and
  `ModifiedAt = T`, as today's delete does. This is the operation for "recorded in error", and what ADR-053's term
  migration uses. Its record is the existing operation 2, renamed `Retract`. A new record always carries a finite
  interval, the one being retracted.
- They are separate methods, not a flag on one method (no behavior flags). The two have different meanings, so they get
  different names.

### D3. Insert after End or Retract creates a new version

- Case B (the current-duplicate no-op) applies only to an entry that isn't `IsDeleted`. A retracted entry never blocks
  an insert.
- After an End the previous version is closed, so a re-insert is a new version `[T2, ∞)`, through the existing Case C.

### D4. A no-op rewrite leaves no boundary

In one Modify, a quad in both the delete set and the insert set (`DELETE { x } INSERT { x } WHERE …`) is removed from
both before applying. Its net effect is "x holds", and ending it at T and restarting it at T would put an artificial
version boundary into its history. `UpdateExecutor.ApplyChanges` does this, where the two sets already meet.

### D5. Affected counts are what changed

A delete counts a version only if it ended (D1) or was retracted (D2). The batch path can't use "atoms exist" as the
answer. Because End and Retract find their version when issued (D1, Q2), they already know whether one exists: no
version means no WAL record, and a count of 0.

Finding the version when issued is done against the indexes plus the records already buffered in the same batch, so an
Add followed by an End of the same quad within one batch ends the version the Add creates. No SPARQL path produces that
sequence: D4 removes it from a Modify, and DATA operations are single-kind. The store API, though, allows it.

### D6. The history view says when a retraction was recorded

`ResolvedTemporalQuad` gains `RetractedAt` (`DateTimeOffset?`, set when `IsDeleted`, from `ModifiedAt`). An ended
version needs nothing new: its `ValidTo` is the end.

### D7. Repair the history already lost

`mercury --repair-deletes <store>` (with `--dry-run`) repairs each entry flagged by a SPARQL delete. Today every
flagged entry in a Cognitive store came through `DeleteCurrent`, since nothing calls the interval delete except tests.
It groups entries into **deletion events** (same graph, same `ModifiedAt`: one delete request) and applies one of two
actions per event:

- **End** (the default): what D1 would have written, `ValidTo = ModifiedAt` and `IsDeleted = false`. That is the
  meaning ADR-001 documented.
- **Restore**, for an event named with `--restore <graph>@<deletion time>`: `IsDeleted = false` with `ValidTo`
  unchanged. This is for a delete that was itself the mistake (Q1). The facts never stopped holding.

The command also:

- skips and lists an entry whose `ModifiedAt` is earlier than its `ValidFrom` (deleted before it started to hold);
- prints, per event, every entry (graph, subject, predicate, object, interval before and after, action), whether a
  later write replaced the same subject and predicate, and the totals;
- reads `ModifiedAt` from the index entry. No API exposes it today, and the dry run that settled Q1 read it from the
  page bytes;
- runs with `mercury-mcp` stopped, on the store directory, through the index entry point D1 adds;
- must run before ADR-053's migration, which writes retractions that are meant to stay retractions.

### D8. Recovery refuses an operation it doesn't know

`QuadStore.Recover` applies `Add` and `Delete` and silently skips anything else. After this ADR it applies `Add`, `End`
and `Retract`, and **throws on any other operation** in a valid record, naming the operation and the record's position.
A log written by a newer Mercury then fails loudly in an older one from now on, instead of losing writes. Binaries from
before this ADR keep skipping silently; see the downgrade note under Q2.

### Scope

Cognitive profile only. Graph has no valid time, and its `VersionedQuadIndex` delete (flag, version bump, un-delete on
insert) is already correct for a store without valid time. Reference and Minimal have no delete.

## Open questions (settled)

- **Q1. Should every flagged entry be repaired as ended? Resolved 2026-10-27: yes, except one event, which is
  restored.** A read-only dry run on a copy of the MCP store read all 323 flagged entries (with `ModifiedAt`) from the
  GSPO leaf pages and grouped them into 96 deletion events. For each entry it checked whether the same graph, subject and
  predicate got a new value within five minutes:

  | Group | Entries | What it is | Action |
  |---|---|---|---|
  | State progressions | 112 | `status`, `maturity`, `nextAction`, `headCommit` moved forward; resume points in `ck/graph` rewritten as a delete of 2 and a delete of 1 seconds apart, each followed by the new value | End |
  | Rewrites minutes after writing | 60 | 2026-04-19 (32, two minutes after writing), `discipline:validation` (17), 2026-05-06 (6), 2026-05-05 (5): wording and IRI fixes | End: the old wording was what the store held for those minutes |
  | Test graphs | 26 | `urn:bug-test:g`, `urn:trunc-test:g`, `urn:cycle-test:g`, `urn:scratch:modify-test`, `urn:adr050-verify`, `urn:test:adr-044-mcp-dogfood`, `urn:adr052:dogfood` | End |
  | Removed without replacement | 6 | ADR-007 finding-to-probe mappings, removed after 12 hours | End |
  | **2026-05-30 23:25:12, `urn:sky-omega:session:2026-05-30`** | **119** | **The recorded incident `<urn:sky-omega:incident:reactive-jump-to-recovery-on-mercury-delete>`**: a correction meant for about 40 triples deleted the whole session graph. 106 of the 119 have no later counterpart anywhere in the store | **Restore** |

  The 2026-05-30 delete was the error, not a change in the facts, so ending them at 23:25 would write a false history.
  Restoring them brings the session graph back as it was before the delete, including the forms the correction was
  aimed at (prefixed datatypes such as `"2026-05-30T00:00:00Z"^^xsd:dateTime`). ADR-053's migration then normalizes
  those forms. The incident record says "As-of queries at a timestamp before the DELETE return the prior state"; under
  the code before this ADR, that was false.

  The full list is in the session scratchpad (`num/dryrun-list.txt`), and the implementation reruns the dry run on the
  live store with `mercury-mcp` stopped. The copy was taken while `mercury-mcp` was running, and its two newest atoms
  read back empty: the process hadn't written their text yet. So a copy of a live store is not a consistent snapshot,
  and the counts above are from the copy.
- **Q2. WAL records. Resolved 2026-10-27** by reading `WriteAheadLog.cs`, `LogRecord` and `QuadStore.Recover`:
  - **Format.** A record is a fixed 80 bytes with a one-byte `LogOperation` (`Add = 1`, `Delete = 2`, `BeginTx = 3`,
    `CommitTx = 4`, `Checkpoint = 255`) and fields for graph, subject, predicate, object, `ValidFromTicks`,
    `ValidToTicks`, `TransactionTimeTicks` and a checksum over all of them. A new operation value needs no layout
    change, and the checksum already covers the operation byte.
  - **Decision: a new operation, `End = 5`**, with the fields of D1 (the ended version's `ValidFrom`, the new
    `ValidTo = T`). Operation 2 is renamed `Retract` and keeps its value. Telling End from Retract by whether the
    interval is `[MinValue, MaxValue]` would give one record two meanings by its values, which is a behavior flag in the
    data.
  - **Why the record names the version.** `Recover` replays every committed record after the last checkpoint, in order,
    onto indexes that may already hold any subset of those records' effects (they are memory-mapped and flushed
    independently). A record that said only "end what holds at T" would be ambiguous on replay when the same quad was
    re-inserted in the same millisecond: the new version also starts at T, so it holds at T. Naming the version by its
    `ValidFrom` makes every replay apply to the same entry, and applying it again changes nothing.
  - **Old records.** Before this ADR, `DeleteCurrent` wrote operation 2 with `[MinValue, MaxValue]`. A checkpoint
    truncates the log to the checkpoint record, and `Dispose` runs one when the session changed anything, so such
    records survive only a crash under an older binary. They are replayed with the meaning they had when written
    (retract the first overlapping version), which is what the indexes may already reflect. D7's repair then turns
    them into ended versions with the rest.
  - **Downgrade.** A binary from before this ADR skips operation 5. Opening an uncheckpointed log written by this ADR's
    code with an older binary would lose its Ends. After a clean shutdown the log holds only a checkpoint, so a
    downgrade is safe then. D8 makes any later format addition fail loudly instead.

## Consequences

- **Positive:**
  - Point-in-time recall works for anything that changed: "what was the status AS OF last week" returns last week's
    status. This is the status-driven recall act Lucy is built on, and today it can't work for any fact that ever
    changed.
  - Setting a value back to an earlier one stores it (D3), instead of silently dropping it.
  - DELETE ends the current version, not the earliest one (case 4), and counts are true.
  - ADR-001's description becomes true of the code.
- **Negative:**
  - Ended facts stay in the AS OF index range, so a scan at a past time reads more entries. Current-time scans already
    filter by valid time, so they read the same entries as before; the extra cost is only in history queries.
  - `QuadStoreTests.QueryEvolution_DeletedTriple_IsVisible` and `QueryEvolution_MixedDeletedAndActive_ShowsAll` assert
    `IsDeleted` after `DeleteCurrent`; they change to assert a closed interval. That is the behavior change these tests
    pin.
  - The repair rewrites history in existing stores, which is reversible only from a copy. The dry run and Q1 cover that.
- **Neutral:**
  - SPARQL results for the current time don't change.
  - Mercury's AS OF is valid time. A transaction-time query ("what did the store say on day X") is not added here;
    Retract plus `RetractedAt` keeps that information available for one later.

## Alternatives considered

- **Keep the retraction meaning and document it.** Rejected: it contradicts ADR-001 and the stated intent, and it makes
  the Cognitive profile's temporal indexes pointless for any fact that changes. Graph would then do the same job with
  less storage.
- **End by inserting a closing record instead of changing the entry** (append-only valid time). Purer in being
  append-only, but every AS OF scan would have to pair versions with closing records. The index already shortens
  `ValidTo` in place on a temporal update (`HandleTemporalUpdate`), and the transaction-time record of the change is
  kept in `ModifiedAt`.
- **A flag on `Delete` choosing End or Retract.** Rejected: two meanings get two names (no behavior flags).

## Validation plan

1. **Store tests, Cognitive profile**, each one sequence and inline assertions:
   - End: AS OF before T returns the fact; AS OF T and after don't; ALL VERSIONS shows `[ValidFrom, T)`,
     `IsDeleted = false`.
   - Two versions, the earlier one closed: End changes only the current one (case 4).
   - Versions spread over more than one leaf page: End finds the current one.
   - Re-insert after End and after Retract: a new current version (case 2).
   - Retract with two adjacent versions (`ValidTo` of one = `ValidFrom` of the next): only the exact one is marked.
   - Counts: deleting an ended or absent fact reports 0 (case 3), in the single and batch paths.
   - Recovery: End and Retract records replayed from the WAL after an unclean close give the same state. Replaying the
     same log twice gives the same state (each record applied twice is a no-op). Delete and re-insert in the same
     millisecond, then an unclean close, replay to "ended, then a new current version", not to an ended re-insert.
     A pre-ADR operation-2 record with `[MinValue, MaxValue]` replays as it did. A valid record with an unknown
     operation makes recovery throw (D8).
2. **SPARQL facade:** the memory status rewrite (case 1) with AS OF before and after; `DELETE { x } INSERT { x }`
   leaves one unbroken version (D4); DELETE WHERE and DELETE DATA count only ended versions.
3. **Existing suites green:** `TemporalSparqlTests` (AS OF / DURING / ALL VERSIONS), the temporal differential tests,
   Graph-profile tests unchanged, W3C conformance.
4. **Repair on a copy of the MCP store, taken with `mercury-mcp` stopped:** `--dry-run` lists 323 entries in 96 events
   (or explains any difference), then the repair with `--restore urn:sky-omega:session:2026-05-30@2026-05-30T23:25:12Z`,
   then: the 119 entries of that event are current again;
   0 flagged entries from SPARQL deletes; for a known resume point, AS OF a date in May returns the `nextAction` that
   held then.
5. **Dogfood:** after repairing the live store, a `mercury_query` AS OF the morning of 2026-10-27 returns
   `sky:status "open"` for `<https://sky-omega.org/bugs/mercury-update-where-legacy-pattern>`, and the current query
   returns `"fixed"`.

## Implementation (2026-10-27)

**What changed**

- **WAL** (`WriteAheadLog.cs`): `LogOperation.Delete = 2` is renamed `Retract`, and `End = 5` is new;
  `LogRecord.CreateEnd` and `CreateRetract` replace `CreateDelete`. The record layout is unchanged.
- **Index** (`TemporalQuadIndex.cs`):
  - `EndRaw` shortens `ValidTo` on the live entry with an exact `ValidFrom`. Applying it again is a no-op.
  - `RetractRaw` flags the live entry with an exact interval.
  - Both find their entry with `FindLiveEntry`, which walks the leaf chain.
  - `FindVersionHoldingAt` finds the version that holds at T, on the GSPO index.
  - `ReplayPreAdr054Delete` is today's overlap delete, kept only for replaying old records.
  - `RepairDeleted` repairs one index's flagged entries in place.
  - `InsertIntoLeaf`: Case A (a replayed Add) compares `ValidFrom` and transaction time, not `ValidTo`. Otherwise a
    replayed Add would bring back a version an End had shortened. Cases B and C skip retracted entries (D3).
  - The enumerator carries `ModifiedAt`.
- **Store** (`QuadStore.cs`):
  - `DeleteCurrent` and `DeleteCurrentBatched` write and apply End. The batched form looks at the batch's own buffered
    records first.
  - `Delete`/`DeleteBatched` are renamed `Retract`/`RetractBatched` and match an exact interval.
  - One `ApplyRecordToIndexesById` serves batch commit and recovery, and throws on an unknown operation (D8).
  - `ResolvedTemporalQuad` gains `RetractedAt` (D6).
  - `RepairDeletes(restore, dryRun, report)` is D7.
  - A Graph store keeps its versioned delete.
- **Update** (`UpdateExecutor.ApplyChanges`): a quad in both the delete and the insert set leaves both (D4).
- **CLI**: `mercury --repair-deletes [--dry-run] [--restore <graph>@<time>]`. It runs without the HTTP endpoint or the
  REPL, disposes the pool, and exits.
- **Pruning** (found while checking the readers of `IsDeleted`): `PreserveAll` copied a retracted version into the
  target as a plain Add, so it held again there. It now retracts the copy in the same batch. To make that possible,
  `RetractBatched` also finds a version added earlier in the same batch. `docs/tutorials/pruning-and-maintenance.md`
  now describes ended and retracted versions.

**Validation results**

1. **Store tests** (`DeleteEndsValidTimeTests`, 17), with each validation-plan item 1 case written as one inline
   sequence:
   - End and AS OF;
   - two versions;
   - 401 versions across leaf pages;
   - counts, single and in a batch;
   - Add then End in one batch;
   - Retract with adjacent versions;
   - re-insert after End (through SPARQL) and after Retract;
   - WAL replay of Add + End onto indexes that already hold them, opened twice;
   - a delete and re-insert in the same millisecond, replayed;
   - a pre-ADR-054 operation-2 record;
   - an unknown operation;
   - the repair (end, restore, left, dry run, rerun).

   The replay tests append the records with `WriteAheadLog` while the store is closed. The log is opened
   `FileShare.None`, and `Dispose` checkpoints, so this is the way to put a store in the state "indexes flushed, log
   not yet checkpointed".
2. **SPARQL facade** (in the same file): the status rewrite with AS OF before and after; `DELETE { x } INSERT { x }`
   leaves one unbroken version and reports 0; DELETE DATA and DELETE WHERE of an ended quad report 0.
3. **Existing suites:** the six tests that pinned the old meaning changed:
   - `QueryEvolution_DeletedTriple_IsVisible` and `QueryEvolution_MixedDeletedAndActive_ShowsAll` now assert a closed
     interval and no retraction;
   - the `TemporalQuadIndexTests` delete tests became retract tests with exact intervals, plus one showing that an
     overlapping interval doesn't retract;
   - `SessionMutationFlagTests.Delete_SetsFlag` uses `DeleteCurrent`.

   Two pruning tests were added: `PreserveVersions` keeps a deleted fact as history, and `PreserveAll` keeps a
   retraction retracted.

   Results: Mercury.Tests 4,776 passed, 7 skipped, 0 failed (W3C conformance included); Mercury.Solid.Tests 25/25;
   SkyOmega.Bcl.Tests 12/12; solution build 0 warnings.
4. **Repair on a copy of the MCP pool**, with the source build of `mercury`:
   - The dry run finds 323 entries in 96 events, as the Q1 probe did.
   - `--restore "<urn:sky-omega:session:2026-05-30>@2026-05-30T23:25:12.044Z"` ends 204 and restores 119.
   - A second dry run finds 0.
   - On the repaired copy, the status of `<https://sky-omega.org/bugs/mercury-update-where-legacy-pattern>` AS OF
     2026-10-27T06:00Z is `"open"`, and now it is `"fixed"`.
   - The 2026-05-30 session graph has 184 current triples.
   - A `ck:` resume point AS OF 2026-06-08 returns the `nextAction` that held then.

   The copy was taken while `mercury-mcp` was running; the live repair runs with it stopped.
5. **Dogfood:** pending. It needs `mercury` and `mercury-mcp` installed from this change, `mercury-mcp` stopped for the
   repair, and the MCP reconnected for the AS OF query.

## References

- [ADR-001](ADR-001-compaction.md) (soft delete with end-validity timestamps), [ADR-029](ADR-029-store-profiles.md)
  (profiles; Graph's versioned delete), [ADR-053](ADR-053-one-rdf-term-one-atom.md) (its migration retracts, and depends
  on this ADR).
- `src/Mercury/Storage/TemporalQuadIndex.cs` (`DeleteFromTree`, `InsertIntoLeaf`, `HandleTemporalUpdate`,
  `MatchesTemporalQuery`), `src/Mercury/Storage/QuadStore.cs` (`Delete`, `DeleteCurrent`, `DeleteBatched`,
  `ApplyDeleteToIndexesById`), `src/Mercury/Storage/VersionedQuadIndex.cs` (the Graph profile's un-delete).
- Mercury: `<https://sky-omega.org/bugs/mercury-delete-erases-valid-time-history>`.
