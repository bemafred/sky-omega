# ADR-061: A Memory's Two Times — The Cognitive Store Keeps When It Memorized and When It Forgot Each Fact, and Cognitive SPARQL Selects and Reads Both

## Status

**Status:** Proposed — 2026-11-11

Rewritten on 2026-11-11 from the draft of 2026-11-10 ("A Fact's Own Times"). That draft let a query read the times
the store already keeps. In the design discussion of 2026-11-11 Martin set the purpose of those times, reduced them to
two, and chose the words. This text follows those decisions.

Each decision below is marked:

- **Decided**: Martin's decision, 2026-11-11 unless another date is given.
- **Proposed**: Sky's addition that follows from a decision. Martin has not confirmed it.

Martin's first review, the same day, decided six more things:

- a time without a timezone is UTC (D10);
- the code names the two times `MemorizedAt` and `ForgottenAt`, from the start (D11);
- pruning keeps the times (D12; on 2026-11-14 he decided what a prune transfers);
- the stored layout changes later, as the later part of this ADR (D13, [Later: the stored layout](#later-the-stored-layout));
- the dialect is called Cognitive SPARQL (D14);
- the rename is the first step. It is done (Step 2), on his direction, while the rest is under review.

On 2026-11-12, after reading this text, Martin decided one more thing: the five attributes that leave carry the
prefix `Legacy` in the code until they are removed (D15). That rename is done too (Step 2).

On 2026-11-13 Martin decided what the store is: a temporal RDF set, not an event log. On 2026-11-14 he confirmed the
form derived from that decision. It is [The model](#the-model), written into this text on 2026-11-14:

- D1, D2, D5 and D10 said things the model contradicts. Their wording is corrected, and each correction is dated.
- The logical thing is now called a **period**, where this text said "version". The stored thing is an **entry**.
- The store was run against the model on 2026-11-14 (M11), and the live memory store was counted (Step 3).
- Later that day the store's four index files were compared entry by entry (M11, LQ5), and four things that follow
  from the model were added as proposals: what each pruning mode keeps (D12), the query that gives `M(t)` (Q10), what
  the stored shape Martin chose asks of M7 and I13 (the later part), and the path where a committed write is not
  applied (M7).

**The decisions were closed on 2026-11-14.** Martin reviewed a report of what was still open and accepted its six
packages: the language (D8 to D10), what a caller can observe (D2, M9, Q10, Q15), what the store does when a committed
write cannot be applied (M7), pruning (D12, as he clarified it), the two other ADRs this one touches (Q8), and the
scope of acceptance. No part of the decision text is marked Proposed any more. What is still Proposed is in the later
part, which acceptance does not cover.

**What acceptance of this ADR covers:**

- the model (M1 to M9) and its invariants (I1 to I19);
- Cognitive SPARQL's words and their rules (D6 to D10), and what a caller can observe;
- recovery and failure as requirements (M7), and pruning (D12).

**What it does not cover:** the stored layout, the conversion of existing stores, and the rule for the entries of the
live store that disagree between its indexes. They are [the later part](#later-the-stored-layout), a design phase
reviewed on its own. Two requirements bind that phase already: a conversion carries every period unchanged (I14), and
nothing resolves a disagreement between stored copies silently (I19).

**What is left to the implementation plan:** the planner (Q6), a clock that tests control (Q14), the form of the
diagnostic (Q13), the word the documents use in place of "bitemporal" (Q7), how a failure is made on purpose in a
test, and whether recovery can run without reopening the store.

The file keeps its name: the changelog, the 1.8.41 release notes, the solution file and the ADR index link to it.

## Why

### The purpose

The time a memory store keeps is for recall. Martin, 2026-11-11:

> We need functionality that can answer what have we been upto last week? Last month? What did we do in January? The
> temporality is for recall purposes. Relative to your subjective experience. It is not for validating/invalidating
> individual quads.

The times say when the memory took something in. When a thing happened in the world is content (D3): the store dates
the memory of it, not the event.

And the goal:

> … give memory/cognitive-storage excellent temporal awareness, functionally akin to a human brain but not lossy,
> instead, better and more reliable. Excellent recall support.

Human memory misdates events and loses the old version when a memory is revised. A store that sets each fact's
memorized and forgotten time itself, and keeps every period in which the fact was remembered, does neither.

### What the store cannot answer today

- **"What did we memorize in a period?" has no query.**
  - `AS OF t` returns what was valid at `t`. That includes everything older than `t`.
  - `DURING [a, b]` returns every version whose valid time overlaps the range. A record from before the range that is
    still valid is in it.
  - Neither selects by when a fact was written.
- **No query can read a time.** The three clauses choose versions and bind nothing.
- **So the agent writes times by hand**, as `dcterms:created` triples, and recall filters on them. Observed 2026-11-10
  (DrHook ADR-017 session):
  - Seven of eight hand-written values were wrong, by 1 to 25 seconds; two had first been written 16 and 17 minutes
    off.
  - A subagent needed 75 `AS OF` queries to bisect the eight true write times, because no query could ask for them.
  - The `lucy` skill's recency recall orders by `dct:date`, which these records do not carry, so they never sort.

## Context

### What the store keeps per version today

A Cognitive store keeps four `TemporalQuadIndex`es (GSPO, GPOS, GOSP, TGSP). Each entry is 88 bytes and carries seven
attributes besides the quad:

| Attribute | Set by | Read by |
|-----------|--------|---------|
| `ValidFrom` | the write: the batch's clock reading, or a time the C# caller states | every temporal query; leads the TGSP index |
| `ValidTo` | open at the write; a SPARQL delete sets it to the delete's time ([ADR-054](ADR-054-delete-ends-valid-time.md) D1) | every temporal query |
| `TransactionTime` | the batch's clock reading | the last sort tie-breaker, and WAL-replay identity in `InsertIntoLeaf` |
| `CreatedAt` | the write | nothing |
| `ModifiedAt` | the write; a retraction | `RetractedAt` and the ADR-054 repair |
| `Version` | the write, always 1 | nothing |
| `IsDeleted` | `Retract`, "recorded in error" (ADR-054 D2) | queries skip the entry; no SPARQL operation can set it |

### Two time axes were mixed

The layout follows the textbook bitemporal model:

- **valid time**: when a fact holds in the world;
- **transaction time**: when the store recorded it.

That model serves a database that validates facts against the world. It is not what a memory needs, and in practice
the store does not use it that way:

- No SPARQL update and no MCP tool can state a time. `UpdateExecutor` and `Mercury.Mcp` never mention `ValidFrom` or
  `ValidTo`. Every memory write gets "from now, open-ended".
- So for every write the agent makes, `ValidFrom` is the time of the write and `ValidTo` is the time of the delete.
  They already are the memory's own times, under names that say "the world".
- `TransactionTime` repeats `ValidFrom`. `CreatedAt` and `Version` are read by nothing.
- A time in the world (a commit at 18:22:33, a run's start) is content. The memory states it as a triple, named for its
  source, and that triple has memory times of its own.

Only two paths write a stated valid time: `Mercury.Pruning` copies, and `examples/Mercury.Examples/TemporalExamples.cs`.

The memory stores have never been pruned (Martin, 2026-11-11). So nothing has written a stated time into them, and
every `ValidFrom` in them should be the time of its write.

**Counted 2026-11-14 (Step 3), on a copy of the live memory store:** of 8,238 entries, 84 have a memorized time
different from `LegacyTransactionTime`, and none has `LegacyIsDeleted` set. In all 84 the memorized time is earlier
by less than 10 ms, and all were memorized between 2026-03-29 and 2026-04-19. So no entry holds a stated time that is
far from its write.

This section names the attributes as they were on 2026-11-10. In the code they are now `MemorizedAt`, `ForgottenAt`
(D11) and `LegacyTransactionTime`, `LegacyCreatedAt`, `LegacyModifiedAt`, `LegacyVersion`, `LegacyIsDeleted` (D15).

### Profiles

Only the Cognitive profile keeps these times ([ADR-029](ADR-029-store-profiles.md)). Graph has version metadata for
soft deletes; Reference and Minimal have no time. `QuadStore.RequireTemporalProfile` already rejects temporal queries on
them.

## The model

**Decided — 2026-11-13.** On 2026-11-14 Martin confirmed the form derived here: "now I think you have the model
right." M7 and M9 were Sky's proposals; Martin accepted them on 2026-11-14 with the decision closure.

Martin, 2026-11-13:

> Mercury's Cognitive profile is a temporal RDF set, not an event log. The identity of an RDF quad is determined by its
> RDF terms. Repeated operations do not create additional facts or identities. A fact's temporal membership is what we
> model. We do not promise to preserve every intermediate operation or state transition.

The model comes before the decisions because they rest on it. It was written after them.

### M1. Memory time, the set, and a period

- **Memory time** is the one time axis: the readings of the store's clock, in UTC, one per millisecond (D4).
- **`M : T → P(Q)`.** `M(t)` is the set of RDF quads the store remembers at the representable time `t`:
  - `M(t)` is the set as the last operation stamped `t` left it;
  - when no operation is stamped `t`, `M(t) = M(t − 1)`.
- A **period** of a quad `q` is a longest unbroken run of readings at which `q` is in the set. It is written `[m, f)`:
  `q` is in `M(t)` exactly when `m ≤ t < f`.
  - `m`, its **memorized** time, is the first reading at which `q` is remembered.
  - `f`, its **forgotten** time, is the first reading after that at which `q` is not remembered. It is absent while
    `q` is remembered.
- A period says when a quad belongs to the set. It is not a thing with an identity of its own. The operations that
  began and ended it are not kept.
- `M` describes the set at each representable time. It says nothing about what happened between two readings, and the
  store does not promise to keep that.

### M2. Inside one clock reading

One rule follows from M1: **inside one clock reading, only the last operation on a quad counts, as if it were the only
operation in that reading.** Martin decided its two cases on 2026-11-13:

- **No period of zero length.** "A fact memorized and forgotten within the same clock reading has no nonempty
  membership interval. … Do not introduce artificial records, identities, sequence numbers, or special timestamp
  handling to preserve such operations."
- **A period continues across a forget and a memorize in one reading.** "If an existing fact is forgotten and memorized
  again within one clock reading, its membership period continues uninterrupted. If an absent fact is memorized,
  forgotten, and memorized again within that reading, the resulting state contains one membership period beginning at
  that time."

The rule says what memory time can show. It does not claim that the earlier operations never happened.

### M3. What the model assumes

- **One store has one owner and one writer.** Martin, 2026-11-14: "A cognitive profile store is owned by one, and only
  one, instance. A single writer." A copy of a store is another store. Memory time is that one writer's clock, and no
  other clock takes part.
- **The writer applies operations one at a time, in the order it receives them.** "Last" in M2 means last in that
  order.
- **The clock does not go backwards** (D4). This is assumed and not guarded.
- **An operation is stamped while the writer holds the write lock** (I12). So stamps never decrease in the order of
  application.

### M4. Invariants

The set:

- **I1.** A quad's identity is its four RDF terms: graph, subject, predicate, object. Until
  [ADR-053](ADR-053-one-rdf-term-one-atom.md) is done the store compares atoms, and one term can have more than one.
- **I2.** A quad has at most one open period.
- **I3.** Memorizing a quad that is remembered changes nothing.
- **I4.** Forgetting a quad that is not remembered changes nothing.

Periods:

- **I5.** Every period has `m < f`, or no `f`. No period is empty.
- **I6.** Two periods of one quad never overlap and never touch. Between them is at least one reading at which the quad
  is not remembered.
- **I7.** Two periods of one quad never begin at the same reading, and only the latest can be open. So "the period of
  `q` that began at `m`" is one period, found from values the store already holds. No identifier is added for it.
- **I8.** An absent forgotten time is absent. No maximum value stands for it above storage.

Writes:

- **I9.** Inside one clock reading, any sequence of operations on a quad gives what its last operation alone gives.
- **I10.** Once the clock has moved past `t`, and the write in progress, if there is one, has completed, `M(t)` never
  changes. A batch is stamped when it begins and can complete later.
- **I11.** Both times of a period are readings of a store's clock, taken at a write. No writer states one. A copy
  carries the times of the store it was copied from (I14).
- **I12.** An operation is stamped while the writer holds the write lock.

The stored form:

- **I13.** Every read answers from the same periods, whichever stored structure serves it.
- **I14.** A rebuild and a conversion carry every period unchanged. A prune carries the open period of each remembered
  quad it selects, unchanged, and nothing else (D12).
- **I15.** Recovery changes nothing. Replaying the log onto indexes that have each applied some first part of it gives
  the periods an uninterrupted run gives. Two conditions: recovery starts at or before the earliest point any index has
  reached, and it runs to the end of the log.
- **I19.** No repair, rebuild, prune or conversion resolves a disagreement between stored copies silently. It reports
  what differs, and the source is kept.

I10, I11, I13 and I14 were worded more exactly on 2026-11-14, and I19 was added then. Martin accepted the changes with
the decision closure.

Kept apart from the set:

- **I16.** A reader sees the store as the last completed write left it (M9). That is not always what `M` holds for the
  current reading.
- **I17.** A clock reading is not a transaction, and a request is not a moment.
- **I18.** A diagnostic is never a quad and never changes `M`. Turning diagnostics off changes no answer (M8).

### M5. What an operation does

An operation at reading `t` looks at the quad's latest period, and at nothing else:

| Operation | The quad's latest period | Result |
|-----------|--------------------------|--------|
| memorize | none | a period begins: `[t, –)` |
| memorize | open | nothing |
| memorize | forgotten at `t` | the period continues: its forgotten time is removed |
| memorize | forgotten before `t` | a new period begins: `[t, –)` |
| forget | none | nothing |
| forget | forgotten | nothing |
| forget | open, memorized before `t` | the period ends: `[m, t)` |
| forget | open, memorized at `t` | the period is removed: the quad was remembered at no representable time |

`–` is an absent forgotten time. The rule needs no operation identity, no sequence number and no second timestamp. It
has no case for an operation stamped earlier than the period it meets: with one writer and a clock that does not go
backwards, there is none.

### M6. Expected results

| Before reading `t` | Operations at `t`, in order | After |
|--------------------|-----------------------------|-------|
| not remembered | memorize | `[t, –)` |
| remembered since `m` | memorize | unchanged |
| remembered since `m` | forget | `[m, t)` |
| not remembered | forget | unchanged |
| not remembered | memorize, forget | no period |
| remembered since `m` | forget, memorize | `[m, –)`, unbroken |
| not remembered | memorize, forget, memorize | `[t, –)` |
| remembered since `m` | forget, memorize, forget | `[m, t)` |
| forgotten at `f`, before `t` | memorize | `[m, f)` and `[t, –)` |
| forgotten at `f`, before `t` | memorize, forget | `[m, f)` only |

Across two readings nothing is merged:

| Before | Operations | After |
|--------|------------|-------|
| memorized at `t` | forget at `t + 1` | `[t, t + 1)`: a real period of one millisecond |
| forgotten at `t` | memorize at `t + 1` | `[m, t)` and `[t + 1, –)` |

So one request can give either result. `INSERT x ; DELETE x` leaves a short period when the clock moved between the two
operations, and none when it did not. Both are correct: memory time shows what it can resolve.

### M7. Recovery, and a write that cannot be applied — Decided 2026-11-14

Three requirements are decided. The form of the records is not: it belongs to the implementation and to the stored
layout.

1. The log holds the change that was decided at the write, not the operation that was asked for.
2. Applying a record is idempotent, and it reads no clock.
3. Recovery starts at or before the earliest point any index has reached, and runs to the end of the log (I15).

I15 has a consequence for the log. The store keeps the periods in several indexes, and after a crash each index can
have applied a different part of the log. If recovery decided each operation again (M5) on one index and gave the
outcome to the others, the indexes would end up different: an enumeration found 2,911 such cases among 6,688 small
ones.

So the log holds the change that was decided at the write, and not the operation that was asked for. For today's
form, where every index holds every period, the records are:

| Record | Applies when | Otherwise |
|--------|--------------|-----------|
| begin a period at `t` | the quad has no period that began at `t` | nothing |
| end the period that began at `m`, at `t` | that period is open | nothing |
| remove the period that began at `t` | that period is open | nothing |
| continue the period that began at `m` and ended at `t` | that period's forgotten time is `t` | nothing |

- Each index finds "the period of `q` that began at `m`" by I7. Today's `End` record already names the memorized time
  this way.
- Applying a record reads no clock and compares no times for order.
- **The start of recovery (read, not run).** The checkpoint moves it. `CheckpointInternal` runs only under the write
  lock, or at open after recovery; it saves every index to disk before it truncates the log. So the start moves only to
  a point every index has reached.
- **One path is not closed in today's code (read 2026-11-14, not run):** a committed record that is not applied, in a
  process that lives on.
  - `CommitBatch` makes the commit durable in the log first. It then applies each record of the batch to GSPO, GPOS,
    GOSP, TGSP and the trigram index, one after the other.
  - If that throws part-way, the records after it reach no index, and the one being applied may have reached some.
    Nothing is set that refuses later writes. The next batch discards the records still waiting.
  - A later checkpoint, or the one a clean close makes, then moves the start past them, and they are gone. If the
    process restarts before any checkpoint, recovery applies them. So what the store remembers depends on which comes
    first, and I13 and I15 can both be broken without a crash.
  - The one throw that the code names on that path is the trigram table's overflow (`TrigramIndex`, after 1,024
    probes). It comes after the four indexes of one record and before the rest of the batch.

**A committed write that cannot be applied — Decided 2026-11-14.**

- **The durable commit in the log is the commit point.** From then on the write is part of the store, at its stamp.
- **A store that could not apply a committed write is not consistent, and says so.** Until recovery has run it:
  - answers no read: indexes that may disagree do not serve a result;
  - takes no write: a later write would be decided (M5) against indexes that lack the earlier one;
  - makes no checkpoint, so the start of recovery never moves past a record an index has not received;
  - closes without a checkpoint.
- **If recovery fails, the store does not open.** The log is kept as it is.
- **What the caller can conclude from an error:**
  - before the commit: nothing changed, and `M` is as it was;
  - after the commit: the write holds once recovery has run. It is not known to have failed.
  - A retry is safe in both cases. Memorizing a remembered quad and forgetting one that is not remembered change
    nothing (I3, I4), so a repeated write leaves the period and its times as the first one made them.
- **Left to the implementation plan:** whether recovery can run in the same process, and how a test makes this path
  fail on purpose. That needs a way into production code (Q14).

### M8. Knowledge, diagnostics and judgement are three layers

Martin, 2026-11-13: a memorize and a forget inside one clock reading "may indicate unnecessary or misguided client
behavior. That is potentially useful information — but it is information about the agent's behavior, not temporal RDF
membership."

| Layer | Holds | Whose |
|-------|-------|-------|
| RDF memory | which quads are remembered at each representable time (`M`) | Mercury |
| Diagnostics | operations Mercury observed that `M` does not show | Mercury reports them, as structured diagnostics |
| Judgement | whether the behaviour was a mistake | James (Operational Epistemology) |

- A diagnostic is never a quad and never changes `M` (I18).
- Mercury reports only what it observed. The writer meets the case in two rows of M5: a memorize of a quad forgotten at
  this reading, and a forget of a quad memorized at this reading. It observes the second operation, and it knows the
  first from a stored time equal to the current reading. It never concludes anything from stored times alone.
- Such a sequence is not a defect by itself. A retry, a batch or a correction can produce it.
- **Left to the implementation plan:** the form of the diagnostic, and whether and where it is kept (Q13).

### M9. What a reader sees is a separate contract

`M` says what the history holds. It does not say what a query sees while writes are going on.

- A reader sees the store as the last completed write left it (I16). Inside the current clock reading that can be a
  quad `M` will not hold for this reading (memorized, then forgotten before the reading ends), or a forgotten time that
  a later memorize in the same reading removes.
- A clock reading is not a transaction, and a request is not a moment (I17).

**The contract — Decided 2026-11-14:**

- **One state.** A query is answered from one state of the store: the store as some completed write left it. No write
  becomes visible while the query runs.
- **A write operation is seen whole or not at all.** A `DELETE … INSERT … WHERE` is one write: no query sees its
  deletions without its insertions.
- **A request with several operations is several writes**, and a query can run between them.
- **Every write that completed before the query began is seen.**
- **Recency.** Every write a query did not see is stamped at or after that query's `NOW()`. So a reader that asks
  "what is new since my last look" with the `NOW()` of its last look as the bound misses nothing (Q11). A write
  stamped in the same millisecond as the bound can come back twice.

How the store keeps this (a lock, or another way) is an implementation choice. So is which index answers a query.

- **Today's code (read, not run):** a query holds the read lock and the writer holds the write lock for a whole batch,
  so a query sees whole batches. One SPARQL request with several operations is several batches.
- **Today's code breaks the recency rule in one place (read, not run):** `AddCurrent` reads the clock before it takes
  the write lock (I12), so a single write can carry a stamp earlier than the `NOW()` of a query that did not see it.
  The batch path stamps under the lock.

### M10. How the model was checked

This checks the reasoning. It does not check Mercury.

- **By enumeration**, in a script that touches no store: every sequence of up to 8 operations on one quad over 4 clock
  readings, 65,537 sequences. For each, the rule of M5 gives the periods M1 defines, and I2, I5, I6, I7 and I9 hold.
  The records of M7, replayed from any earlier point onto an index stopped at any point, give the uninterrupted result
  in all 2,652,185 cases.
- **By a second implementation**, written by sky-zbook from the description alone: the same agreement, and also for a
  recovery that is itself interrupted and started again. It ran on zbook and was not repeated on omega.
- **`M(t)` from the words** (Q10), added 2026-11-14: for each of those sequences and each time, 393,222 cases, the
  query of Q10 matches the one period that holds the time, or nothing.
- **The bounds are small.** Quads are independent in the model, so one quad is enough. Nothing shows that 8 operations
  and 4 readings are.
- The script is not in the repository.

### M11. Today's code against the model

**Run on 2026-11-14** on omega (macOS arm64, Mercury 1.8.41, commit `1a59d75`), on new temporary stores, through the
public API:

| Operations | Today's result | Runs | Breaks |
|------------|----------------|------|--------|
| one batch: add `x`, delete `x`, add `x` (`x` new) | `x` is forgotten; one entry with equal times | 50 of 50 | I9, I5 |
| one batch: add `x`, delete `x` (`x` new) | one entry with equal times | 50 of 50 | I5 |
| one batch: delete `x`, add `x` (`x` remembered) | two periods that touch | 50 of 50 | I6 |
| insert `x` again while it is remembered, with another entry between the two in TGSP's key order | TGSP alone gains an entry; after `x` is deleted, a time-range read still returns it | 2 of 2 cases; 3 of 3 controls clean | I13 |

- A batch has one clock reading by construction. sky-zbook saw the first and the fourth row on zbook on 2026-11-13.
- "Between in TGSP's key order" is not "between in time": a quad written in the same request as the first `x`, after
  it, has the same time and a larger atom, and lies between.
- **Through one SPARQL request the operations did not share a clock reading on omega**, in 200 runs each of
  `INSERT ; DELETE ; INSERT`, `INSERT ; DELETE` and `DELETE ; INSERT`. Each operation is a batch of its own, and they
  were at least 4 ms apart. Those runs gave what the model gives for different readings. sky-zbook reported the first
  row through one request on zbook (2026-11-12).

The causes, read from the code:

- **First row:** `InsertIntoLeaf` takes the second add for a replay of the first, because the quad, `MemorizedAt` and
  `LegacyTransactionTime` are equal. It does not compare `ForgottenAt`.
- **Second row:** the delete looks for the entry holding at `t`, finds the one memorized at `t`, and ends it at `t`.
- **Third row:** an add after an entry that ended at `t` inserts a new entry from `t`.
- **Fourth row:** each index decides "already remembered" from the neighbouring entry in its own sort order. In TGSP,
  where the memorized time leads, another entry can lie between. The delete then names the memorized time found in
  GSPO, and the extra TGSP entry is never ended.
- **I12 (read, not run):** `AddCurrent` reads the clock before `Add` takes the write lock. The batch path reads it
  under the lock.

**The live memory store**, counted on 2026-11-14 on a copy taken at 04:51 UTC (Step 3). The first two counts that day
read through the store's own queries. They reached 81 graphs and 8,187 entries, and found 187 quads whose periods
differ between TGSP and GSPO. The third count read the four index files as bytes and reached everything. Its numbers
stand:

- **8,238 entries of 8,236 distinct quads in 89 graphs**, in GSPO, GPOS and GOSP alike. TGSP holds 8,285. The counts in
  the file headers are right: every entry is on a leaf page that a scan reaches, and none is flagged deleted.
- **GSPO, GPOS and GOSP hold exactly the same periods.** That fits the cause named above for the fourth row, which
  exists only where the memorized time leads the key.
- **In GSPO nothing breaks I2, I5 or I6:** no entry with equal times, none forgotten before it was memorized, no two
  periods that touch or overlap, no quad with two open periods. Two quads have more than one period.
- **No read reaches 51 of the entries.** They are in 8 graphs in which nothing is remembered now.
  - `GRAPH ?g` lists the graphs by a scan of what is remembered now (`NamedGraphEnumerator`), under `ALL VERSIONS` too.
    A graph that is wholly forgotten is found by no query with a graph variable. `INCLUDING FORGOTTEN` would inherit
    that.
  - Pruning lists the graphs the same way (`PruningTransfer`; read, not run). For a prune that is right: it transfers
    only what is remembered (D12).
  - All 8 are test and scratch graphs, with 25, 9, 6, 5, 3, 1, 1 and 1 entries.
- **I13 is broken in two ways, and in no other.** TGSP against GSPO, entry by entry:

  | Entries | In GSPO | In TGSP | What they are |
  |---------|---------|---------|---------------|
  | 204 | forgotten | open, with the same memorized time | as many as the repair of [ADR-054](ADR-054-delete-ends-valid-time.md) ended |
  | 47 | no entry at that memorized time | open | 9 repeat a fact that is remembered; 38 are later tellings of 32 facts that GSPO holds as forgotten |

  - **The 204.** ADR-054 records that its repair ended 204 entries on this store and restored 119. The repair works
    on each index's own flagged entries, and its code assumes all four reach the same result. The equal number says
    these are those entries, and that TGSP held none flagged, so nothing changed there. That is a reading of the
    number, not of the entries. Why the old delete would not have flagged an entry in TGSP is not established.
  - **The 9** are the fourth row of the table above: a fact told again while it was remembered.
  - **The 32 facts** were told, forgotten and told again, before ADR-054 (1.8.23, 2026-10-27). A re-insert after a
    delete was dropped then (ADR-054's context says so), and only TGSP kept it.
    - 26 are in the graph of the session of 2026-04-19: the session, three bugs and one observation. They were told at
      08:23:42, forgotten at 08:25:52 and told again at 08:26:24.
    - 6 describe `urn:sky-omega:data:conformance-vs-dogfood`. They were told on 2026-05-17 at 09:52:16, forgotten at
      09:53:23, and told again one to three times in the next nine minutes.
    - A query for any of them returns nothing today. The log of that time is gone, so the store cannot show whether a
      delete followed the last telling.
  - **A delete reaches TGSP today.** The one graph forgotten after ADR-054 was released is equal in GSPO and TGSP.
  - **So the two kinds of read disagree.** A read through TGSP returns 204 forgotten entries as remembered: `DURING`
    reads TGSP today, and `MEMORIZED SINCE … BEFORE …` would under L1. A default read does not return 32 facts the
    memory was told again.
- **The 84 entries** whose memorized time differs from `LegacyTransactionTime` differ by less than 10 ms, the memorized
  time being the earlier, and are from 2026-03-29 to 2026-04-19. Two clock readings at one write would give that. The
  code of that time was not read.
- **The copy is kept**, with its hashes, the probes and their outputs, on omega in
  `~/diag/mcp-omega-snapshot-20261114T0451Z`. The live store keeps changing; the numbers above are the copy's.
- **Until the differences are resolved (LQ5)**, two commands would resolve them without saying so, each by reading
  GSPO: `mercury --rebuild-indexes` and a prune. Nothing runs them automatically.

## Decision

### D1. Each period of a fact has two times, set by the store — Decided

- **memorized**: the first clock reading at which the fact is remembered.
- **forgotten**: the first clock reading after that at which it is not. Absent while the fact is remembered.

Both are points on one axis, memory time: the store's own clock, read at the write. No writer states them (I11). Once
the clock has moved past a reading, what the store holds for that reading never changes (I10).

A fact that is memorized, forgotten at a later reading and memorized again at a reading after that has two periods,
each with its own times. Inside one clock reading the last operation decides (M2).

At most one period of a quad is open (I2). Memorizing a fact that is already remembered changes nothing and records
nothing (I3): re-affirmation and recall are not stored on the quad.

**Corrected 2026-11-13, by the model:**

- "nothing edits them afterwards" was true only from the next clock reading on. Inside the reading of a write, a later
  operation can still remove a period or continue it.
- "memorized, forgotten and memorized again has two versions" was true only when the three fall in three clock
  readings.
- "when the store took the fact in" and "let it go" described operations. The two times describe membership.

**Assumption, stated so it can be challenged:** one store is one subject's memory. "Forgotten" is a fact about the
whole store. Two subjects sharing one store could not forget separately.

### D2. Forgetting is the SPARQL delete, and it ends a period — Decided

Martin: "From a SPARQL point of view, forget is a retract. And a soft delete. We know the when. Which is a good thing.
We have immutability."

- `INSERT` memorizes. `DELETE` forgets: it sets the forgotten time of the fact's open period to the delete's clock
  reading.
- The period stays in the store, with both times.
- There is no second kind of removal. `Retract`, `RetractBatched`, `IsDeleted` and `RetractedAt` go. A fact recorded
  in error is forgotten like any other, and the store still holds the period in which it was remembered.

This replaces ADR-054 D2 (Retract) and D6 (`RetractedAt`). ADR-054 D1, D4 and D8 stay.

**Corrected 2026-11-13, by the model:**

- The heading said "and nothing is erased". One thing leaves no trace: a fact memorized and forgotten inside one clock
  reading was remembered at no representable time, so it has no period (M2).
- The immutability Martin names holds for every reading the clock has passed (I10).
- ADR-054 D3 and D5 stay for operations at different clock readings. Inside one reading the model decides:
  - D3: a fact forgotten and memorized again in one reading continues its period. No new period begins.
  - D5: an add followed by a delete of the same quad in one batch leaves no period. Today it leaves an entry with
    equal times (M11).

**What an update counts as affected — Decided 2026-11-14** (Q9). An operation counts a quad when it changes whether
the quad is remembered.

- Memorizing a quad that is remembered counts nothing, and so does forgetting one that is not (I3, I4).
- A delete of a quad memorized in the same clock reading counts it: the quad was remembered and is not any more, though
  no period is left.
- The count is a result of the request. Nothing of it is stored.
- **Today's code:** a delete already counts what ended (ADR-054 D5). `INSERT DATA` counts every quad it is given,
  remembered or not (`UpdateExecutor`), and changes with this rule.

### D3. World time is content — Decided

When something happened in the world is a triple, named for its source. The store's two times say only when the memory
took the triple in and let it go.

- No SPARQL write states a memory time. There is no write extension.
- A record does not stamp its own write time. Existing `dcterms:created` values stay as they are: memory vocabulary is
  research data.

### D4. The clock is trusted — Decided

Martin: "Time moves forward, on faulty hardware that might not be true but we view that as an extraordinary even, like
brain injury. We do not guard for stochastic time. Time is relied on and the timeline is dependable."

- No guard against a clock that steps back.
- Events inside one clock reading are simultaneous, and that is correct. Martin: "It is not a problem if order is
  confused due time resolution … Time axis resolution is for recall purposes, not for high speed event logging."
- The resolution stays one millisecond. No finer unit, and no rule that each batch gets a time of its own.
- **The clock is the store's one writer's clock — Decided 2026-11-14.** A store has one owner and one writer, and a
  copy is another store (M3). A clock that is behind the times already in the store is the clock stepping back, which
  the first point leaves unguarded. It is not a case of its own.

### D5. The last operation decides the end state — Decided

The writer applies operations in the order it receives them. Inside one clock reading the last operation on a fact
decides, as if it were the only one in that reading (M2). A fact inserted, deleted and inserted again inside one
millisecond is remembered, with one period.

This is a rule for the write logic, and does not touch the clock.

**Corrected 2026-11-13, by the model:** this said "operations in one request". The rule is for one clock reading,
whether the operations came in one request or in several. A request is not a moment (I17): its operations can fall in
different readings, and then each reading has its own result (M6).

**Observed 2026-11-14 (M11):** today's code breaks the rule. One batch that adds, deletes and adds a new quad leaves it
forgotten, in 50 of 50 runs, because `InsertIntoLeaf` takes the second add for a WAL replay of the first. Step 4 holds
the tests.

### D6. The words — Decided

```
MEMORIZED [?t] [SINCE a] [BEFORE b]      after a triple pattern
FORGOTTEN [?t] [SINCE a] [BEFORE b]      after a triple pattern
INCLUDING FORGOTTEN                      after the query
```

- `MEMORIZED ?t` binds when the matched memory was memorized.
- `FORGOTTEN ?t` binds when it was forgotten. It is unbound while the memory is remembered.
- `SINCE a` keeps the memories whose time is at or after `a`: the memorized time after `MEMORIZED`, the forgotten time
  after `FORGOTTEN`.
- `BEFORE b` keeps the memories whose time is before `b`.
- `SINCE` and `BEFORE` work alone, together, and in either order. Together they are the intersection: the half-open
  range `[a, b)`.
- `INCLUDING FORGOTTEN` makes every pattern of the query match forgotten memories too.

Martin kept `MEMORIZED` though `SINCE a BEFORE b` alone would parse: "We keep MEMORIZED, it speaks of purpose." The
word says whose time it is: the memory's, not the world's.

```sparql
# What did we memorize last week?
SELECT ?g ?s ?label ?when WHERE {
  GRAPH ?g { ?s rdfs:label ?label MEMORIZED ?when SINCE "2026-11-02"^^xsd:date BEFORE "2026-11-09"^^xsd:date . }
} ORDER BY ?when

# What did we memorize in January?
SELECT ?g ?s ?label WHERE {
  GRAPH ?g { ?s rdfs:label ?label MEMORIZED SINCE "2026-01-01"^^xsd:date BEFORE "2026-02-01"^^xsd:date . }
}

# What have we forgotten since the first of November, and when?
SELECT ?g ?s ?p ?o ?when WHERE {
  GRAPH ?g { ?s ?p ?o FORGOTTEN ?when SINCE "2026-11-01"^^xsd:date . }
}
```

The period is on one pattern, the anchor, and not on the whole query. A period for the whole query would cut every join
to an older memory: last week's finding about a component named in June would lose the component.

**Where the words stand in the grammar.** In SPARQL 1.1 a bare word cannot follow a triple's object, except the keywords
that begin the next pattern (`FILTER`, `OPTIONAL`, `GRAPH` and so on). A new keyword there is unambiguous.
`INCLUDING FORGOTTEN` stands where `ALL VERSIONS` stands today, after the solution modifiers.

### D7. The default view is what is remembered now — Decided

A pattern without `FORGOTTEN`, in a query without `INCLUDING FORGOTTEN`, matches the remembered memories: every period
that has no forgotten time. It needs no clock reading. Today's default reads the clock at each scan and compares.

### D8. The old clauses go — Decided

| Removed | Why |
|---------|-----|
| `AS OF t` | It names one exact moment. Martin: "it mimics a specific, exact, time … I can't think of a use case for it." Neither could Sky find one in recall. |
| `REMEMBERED AT t` | Sky's proposed new name for `AS OF`. Dropped with it. |
| `BETWEEN a AND b` | `SINCE a BEFORE b` says the same and states its bounds. |
| `ALL VERSIONS` | `INCLUDING FORGOTTEN` takes its place. |
| `RECORDED`, `VALID` | The 2026-11-10 draft's working names. `MEMORIZED` and `FORGOTTEN` replace them. |
| `DURING [a, b]` | **Decided 2026-11-14.** It selects by overlap with a range, which no recall question asks. What it could answer, the words answer (Q10). |

The old clauses are removed in the same step that adds the words. Two temporal languages side by side would be two
paths to keep in agreement.

### D9. What the words keep from the 2026-11-10 draft — Decided 2026-11-14

1. **Mercury-only, and non-intrusive.** No standard query can reach the words, so the W3C results cannot move. No pragma
   or switch.
2. **Temporal stores only.** On Graph, Reference or Minimal the words are an error through `RequireTemporalProfile`.
   Never silently unbound.
3. **A time belongs to the period a pattern matched.** Each matched period is its own solution with its own times.
4. **No cost when unused**, and nothing allocated on the query hot path.
5. **Clear of SPARQL 1.2.** The words take none of its new syntax: triple terms `<<( )>>`, reifiers `~`, annotations
   `{| |}`, the `VERSION` directive.

### D10. The exact rules of the words — Decided 2026-11-14, except where another date is given

- **A time without a timezone is UTC — Decided 2026-11-11.** Martin: "UTC, yes. Of course." It is the rule everywhere
  else in Mercury. A bound that means a local day carries its offset: `"2026-01-01+01:00"^^xsd:date`.

- **An empty range is empty.** `SINCE a BEFORE b` with `a` at or after `b` matches nothing. It is not an error, and it
  never means "outside the range": that takes two patterns and `UNION`.
- **Each bound at most once per word.** `MEMORIZED SINCE a SINCE b` is a syntax error.
- **Both words may follow one pattern**, each with its own bounds: `MEMORIZED SINCE a FORGOTTEN BEFORE b`.
- **A remembered memory has no forgotten time — Decided 2026-11-13.** Martin: "An absent ForgottenAt remains absent in
  the logical representation." No `FORGOTTEN` bound selects it, and `FORGOTTEN ?t` leaves `?t` unbound. Today the store
  keeps "open" as a maximum value, which would pass `FORGOTTEN SINCE a`; that value never appears above storage (I8).
- **A pattern that carries `FORGOTTEN` matches forgotten memories too.** `INCLUDING FORGOTTEN` does the same for every
  pattern. Without this rule a `FORGOTTEN` bound in a query without `INCLUDING FORGOTTEN` would match nothing, silently.
  - It follows from D6: `FORGOTTEN ?t` leaves `?t` unbound for a remembered memory, so the pattern matches remembered
    and forgotten memories alike.
  - A bound selects: `FORGOTTEN SINCE a` and `FORGOTTEN BEFORE b` match only memories that have a forgotten time in
    the range.
- **`INCLUDING FORGOTTEN` stands on the outermost query only.** It applies to every pattern of the query, a subquery's
  and a property path's included. A subquery cannot carry it. It does not reach into `SERVICE`, which another store
  answers. Today's clause is parsed in the same place.
- **In the abbreviated forms the words follow an object** and apply to the one triple pattern that object completes.
  In a `,` list and in a `;` list each object may carry its own words, and the same holds inside `[ ]`.
- **The words are an error on a pattern that matches no single stored period:** a pattern with any property-path
  operator, and any pattern inside `SERVICE`. The rule is strict on purpose; it can be widened later without breaking
  a query that is valid now. A triple inferred by `OwlReasoner` is materialized and has times of its own: they say
  when the reasoner wrote it.
- **A graph variable ranges over every graph that holds a period**, in a query with `INCLUDING FORGOTTEN` and in a
  pattern that carries `FORGOTTEN`. In the default view it ranges over the graphs that hold something remembered.
  Today it does the second under `ALL VERSIONS` too (M11), so forgetting the last fact of a graph hides the graph's
  history from every query that does not name it.
- **Every range is half-open, `[a, b)`:** the range of `SINCE a BEFORE b`, and a period `[m, f)` itself. A memory
  forgotten at `b` is not forgotten before `b`.
- **A bound is an expression without variables**: a literal, or `NOW() - "P7D"^^xsd:dayTimeDuration`. It is evaluated
  once per query. A bound that depends on a variable is a `FILTER` on the bound time:
  `?s ?p ?o MEMORIZED ?t . FILTER(?t >= ?started)`.
- **A time is an `xsd:dateTime` in UTC with a millisecond fraction.** A bound may also be an `xsd:date`: its first
  instant.
- **No period has equal times — Decided 2026-11-13.** A fact memorized and forgotten in the same clock reading has no
  period, and no word finds one (M2, I5). This replaces the rule proposed here on 2026-11-11, that such a version is
  kept with two equal times and shows under `INCLUDING FORGOTTEN`. Martin withdrew it.
- **The words work in an Update's `WHERE`**, since it is the same pattern path as a query's
  ([ADR-052](ADR-052-update-where-one-pattern-path.md)):
  - `DELETE { ?s ?p ?o } WHERE { ?s ?p ?o MEMORIZED BEFORE … }` forgets what was memorized before a time.
  - `INSERT { ?s ?p ?o } WHERE { ?s ?p ?o FORGOTTEN SINCE … }` memorizes again what was forgotten since a time.

### D11. The code names the two times from the start — Decided

Martin: "If we rename ValidFrom and ValidTo to more suitable names, at start, I think that is beneficial."

- `ValidFrom` becomes `MemorizedAt`, and `ValidTo` becomes `ForgottenAt`, in the index key, the WAL record, the store's
  methods and results, pruning, the tests and the documents. Martin: "MemorizedAt and ForgottenAt is good, yes."
- The names follow the code's own form for a time (`CreatedAt`, `ModifiedAt`). A bare `Forgotten` would read as a flag.
- It is a rename: no behaviour changes and no stored byte moves. It is the first code step, so the words are built on
  names that say what the times are.
- 360 lines in 16 files of `src`, `tests`, `benchmarks` and `examples` used the old names.
- The other five attributes are marked as leaving (D15).
- **Until D2 is done the names say more than the code does.** A caller can still state both times (`Add` with a stated
  time), and they then hold a stated valid time, not the time of the write.
- This text still says `ValidFrom` and `ValidTo` where it describes the store before the rename.

### D12. A prune keeps what is remembered, with its times — Decided

Martin, 2026-11-11: "Prune must preserve temporality."

Martin, 2026-11-14:

> the purpose of pruning, just like in gardening, is to remove unwanted world information and not needed knowledge -
> hence, forgotten memories need not be transferred. Asking for forgotten in a freshly pruned copy should returning
> nothing and given a source store with memorized knowledge over time where everything has been deleted (forgotten)
> must effectively become an empty store, no matter what period we examine. Apart from this, pruning will also allow
> filtering the graph, using a selection query.

- **A prune transfers what is remembered now, and nothing that is forgotten.** Each remembered quad keeps the memorized
  time of its present period (I14). A period that has ended is not transferred, and neither is an earlier period of a
  quad that is remembered again.
- **What the pruned store answers:**
  - the default view, and every `MEMORIZED` question about what is remembered, as its source did when it was pruned;
  - nothing to a `FORGOTTEN` question, and nothing more under `INCLUDING FORGOTTEN` than without it;
  - a source in which everything is forgotten gives an empty store, at every time one asks about.
- **A prune can take a selection query**, which says which of the remembered memories are transferred. Today's
  filters by graph and predicate are such selections. The form of the query is for the implementation plan.
- **A graph in which nothing is remembered is not transferred.** So a prune lists the graphs by what is remembered
  now, as today's code does (M11).
- **A prune never changes a time.** Today `HistoryMode.FlattenToCurrent`, the pruning default, rewrites every time to
  the time of the prune. After such a prune, "what did we memorize in January" answers "nothing". That goes.
- **There is one way to prune.** Today's three history modes go: one rewrites the times, and the other two transfer
  what is forgotten or retracted. Whoever wants everything kept copies the store.
- **A rebuild and a conversion are not prunes.** They carry every period, with both times, unchanged (I14).
- **A prune reports a disagreement between the source's stored copies and does not resolve it silently** (I19).
- A copy between stores needs a way to carry a time that no ordinary writer has. It is internal to Mercury.

**Replaced 2026-11-14:** Sky's proposal of two modes with "copy all" as the default. Martin's clarification above
decided the question the other way: forgotten memories are what a prune removes.

### D13. The stored layout changes later — Decided

Martin: "It is wise to separate rebuilt from language/syntax changes … [it] can be done later." He chose a later
section of this ADR over an ADR of its own.

- The words do not need a new layout. The two times are already stored.
- Reducing the entry to the two times, and converting existing stores, is
  [Later: the stored layout](#later-the-stored-layout). Nothing in it starts before the words are in use.
- Until then the five other attributes stay in the entry, under their `Legacy` names (D15). D2 removes what writes
  `LegacyIsDeleted`; the later part removes the field.

### D14. The dialect is called Cognitive SPARQL — Decided

Martin: "Cognitive SPARQL it is."

- Cognitive SPARQL is SPARQL 1.1 with the words of D6. Every SPARQL 1.1 query means what it meant.
- The name says where the words work: on a store with the Cognitive profile ([ADR-029](ADR-029-store-profiles.md)).
- It is written out. It is never shortened to C-SPARQL, which is another language.
- The names not taken:
  - C-SPARQL is Continuous SPARQL, a language for queries over RDF streams (Barbieri and others, WWW 2009).
  - T-SPARQL is a TSQL2-like temporal query language for RDF (Grandi, ADBIS 2010), and also a shorthand in Tappolet
    and Bernstein's temporal RDF (ESWC 2009). Both are about valid time in the world.
  - "Temporal SPARQL" is the general term for querying valid time in the world, which D3 leaves to triples.
- One web search on 2026-11-11 found no other language called Cognitive SPARQL.

### D15. The attributes that leave are named `Legacy` until they are removed — Decided

Martin, 2026-11-12: "we should probably rename the timestamps that will be removed later … so that their names convey
this situation", then "Legacy" and "All five, clear intent."

- The five are `LegacyTransactionTime`, `LegacyCreatedAt`, `LegacyModifiedAt`, `LegacyVersion` and `LegacyIsDeleted`.
- **Why:** between the rename and the layout change the entry holds two kinds of attribute, the two that stay and five
  that go. With the prefix, a line of new code that reads one of the five shows it. Steps 5 to 7 are written on this
  code.
- **Where:** the Cognitive key and entry (`TemporalKey`, `TemporalBTreeEntry`), the quad an index scan returns
  (`TemporalQuad`), and the two copies on the public result `ResolvedTemporalQuad` (`LegacyTransactionTime`,
  `LegacyIsDeleted`).
- **A comment on each field** says what still reads it and which step removes it:

  | Field | Still read by | Removed |
  |-------|---------------|---------|
  | `LegacyTransactionTime` | the last sort tie-breaker; the test for a WAL replay of the same Add | the later part (L1) |
  | `LegacyCreatedAt` | nothing | the later part (L1) |
  | `LegacyModifiedAt` | `RetractedAt` (until Step 6), the ADR-054 repair, and the conversion (L3) | the later part (L1) |
  | `LegacyVersion` | nothing | the later part (L1) |
  | `LegacyIsDeleted` | every scan, the ADR-054 repair, and the conversion (L3). Step 6 removes what writes it | the later part (L1) |

- **Not marked:**
  - the Graph profile's `CreatedAt`, `ModifiedAt`, `Version` and `IsDeleted` in `VersionedQuadIndex`. They have the
    same names and they stay;
  - the `transactionTime` parameters of the write path. They carry the batch's clock reading, which is where
    `MemorizedAt` comes from;
  - the WAL record's `TransactionTimeTicks`. Both profiles use it, and replay needs it (L1);
  - `RetractedAt`. It goes with `Retract` in Step 6, and its name already says what it belongs to.
- `[Obsolete]` was not used: the build treats warnings as errors, and the fields are still read.
- It is a rename: no behaviour changes and no stored byte moves. 109 lines in 9 files of `src` and `tests` changed.

## Questions

Each question is kept as it was asked, with how it was closed. Four are left to the implementation plan (Q6, Q7, Q13,
Q14). None is open for the decisions.

### The language

- **Q1. `FORGOTTEN` without `INCLUDING FORGOTTEN`** (D10): does the word widen its own pattern, or is it an error to use
  it without the clause?
  - **Closed 2026-11-14:** it widens its own pattern (D10).
- **Q2. `INCLUDING FORGOTTEN` on a subquery.** With `FORGOTTEN` on a pattern, the clause may be needed only on the
  outermost query.
  - **Closed 2026-11-14:** on the outermost query only (D10). Read from the code: today's clause is parsed for a query
    and not for a sub-SELECT.
- **Q3. The abbreviated triple forms.** The words follow an object and apply to the one triple pattern that object
  completes.
  - **Closed 2026-11-14:** as asked (D10).
- **Q4. Patterns that match no single stored period:** a property path, a pattern inside `SERVICE`, a triple inferred
  by `OwlReasoner`.
  - **Closed 2026-11-14:** the words are an error on a property-path operator and inside `SERVICE`. An inferred
    triple is materialized and has its own times (D10).
- **Q5. `DURING`** (D8): dropped with the others?
  - **Closed 2026-11-14:** dropped (D8).
- **Q6. The planner.** The pattern tree must carry each pattern's bounds to its scan, and choose the index that suits
  them.
  - **Left to the implementation plan.** Which index answers a query changes no answer (I13).

### Names

- **Q7. The word "bitemporal".** The documents call the Cognitive profile bitemporal: valid time and transaction time.
  This ADR gives it one axis with two points per period. What do we call it?
  - **Decided 2026-11-13:** the axis is called memory time.
  - **Left to the implementation plan:** what the documents say in place of "bitemporal" (Step 8).

### Other ADRs

- **Q8. ADR-053's migration uses Retract.** [ADR-053](ADR-053-one-rdf-term-one-atom.md) (Proposed) D5 rewrites an
  atom's form by retracting the version and adding it again with the same valid time. Without Retract and without
  stated times it needs another way.
  - **Closed 2026-11-14:** the migration is a conversion (I14). It rewrites the stored form of a term and carries
    every period unchanged: the memory learned nothing new, so nothing is memorized and nothing is forgotten.
  - Where two stored forms are one term, they are one quad (I1), and the quad is remembered whenever either form was.
    Their periods are merged by that rule.
  - It is built on the internal copy that carries a period's times (D12), after Step 6, and without Retract. ADR-053
    D5 says so now.
- **ADR-062.** Its two-time type gives an absent forgotten time as absent (I8). Its cursor comes before the words
  (Step 5), by its D8. Nothing in this ADR's decisions depends on how the cursor is built.

### From the model (2026-11-13 and 2026-11-14)

- **Q9. What a delete counts as affected, inside one clock reading.**
  - **Closed 2026-11-14:** an operation counts a quad when it changes whether the quad is remembered (D2).
- **Q10. `M(t)` has no word.** "Was `q` remembered at `t`" is the model's first question, and `AS OF` is gone (D8).
  - **Closed 2026-11-14:** no new word. With `b` one millisecond after `t`:

    ```sparql
    { ?s ?p ?o MEMORIZED ?m BEFORE b }
    UNION
    { ?s ?p ?o MEMORIZED ?m BEFORE b FORGOTTEN ?f SINCE b }
    ```

    It gives each quad of `M(t)` once, with the bounds of the period that holds `t`. With `b` a date it gives what was
    remembered just before that day began.
  - Shown on the model (M10). It is not shown on a store: the words are not built (Step 5).
  - On a pruned store it answers from what the prune kept (D12).
- **Q11. "What is new since my last look".**
  - **Closed 2026-11-14:** the store guarantees that every write a query did not see is stamped at or after that
    query's `NOW()` (M9). A reader that uses the `NOW()` of its last look as its next bound misses nothing. A bound
    read after the query returned, or from another clock, can skip a write. The `lucy` skill's recency recall is the
    first such reader (Step 8).
- **Q12. What a reader sees during writes.**
  - **Closed 2026-11-14:** the contract is in M9.
- **Q13. The diagnostic** (M8): its form, and whether and where it is kept.
  - **Left to the implementation plan.** What binds it is decided: it is never a quad, and it reports only what the
    writer observed (I18, M8).
- **Q14. Tests that need one clock reading** (Step 4). A batch has one by construction. A sequence of requests does
  not: on omega they were at least 4 ms apart. Testing the request path inside one reading needs a clock the test
  controls, which is a change to production code.
  - **Left to the implementation plan.** What binds it: the test supplies the clock's readings, and no writer states a
    time (I11).
- **Q15. A graph that is wholly forgotten.**
  - **Closed 2026-11-14:** under `INCLUDING FORGOTTEN`, and in a pattern that carries `FORGOTTEN`, a graph variable
    ranges over every graph that holds a period (D10).

## Steps

**The order, decided 2026-11-14.** Steps 4, 7, 10 and 11 touch neither the cursor nor the words and can start at once.
Step 5 follows the cursor of ADR-062. Step 6 follows Step 5.

1. **Martin's review** of this text — **done 2026-11-14**, with the decision closure. The later part is reviewed on
   its own.
2. **The rename** (D11) — **done 2026-11-11.** `MemorizedAt` and `ForgottenAt` in the code and in the four current
   technical documents that name the fields, with no change of behaviour. Every test passes with only the names
   changed. ADRs, release notes and the changelog keep the old names: they describe the store as it was.
   - **The `Legacy` prefix** (D15) — **done 2026-11-12**, the same way: the code, and the two current technical
     documents that name `TransactionTime`. Every test passes with only the names changed.
3. **Count the live store**, read-only, as a probe reviewed before it runs — **done 2026-11-14**, on a copy. The
   numbers are in M11:
   - entries in total: 8,187;
   - entries whose memorized time differs from `LegacyTransactionTime`: 84, each by less than 10 ms;
   - entries with `LegacyIsDeleted` set: 0;
   - and, against the model: 187 quads whose periods differ between TGSP and GSPO.
   - a second count the same day broke down the 187 and the 84 (M11).
   - a third count the same day read the index files as bytes and closed what the first two left open (M11): the
     store holds 8,238 entries in 89 graphs, 51 of them in 8 wholly forgotten graphs that no read lists; GPOS and GOSP
     equal GSPO; TGSP differs from GSPO in 204 entries that are open there and in 47 that only it holds.
   - **Open:** why the old delete left TGSP's entries unflagged, and the rule for each difference (LQ5).
4. **Tests for the model on today's code.** Every row of M6, and the four rows of M11, each with the result the model
   gives. A batch gives one clock reading by construction (Q14). Fix what fails: the rows of M11 fail today.
   The affected counts follow the rule of D2.
5. **The words:** the grammar in `SparqlParser`, the bounds and time variables carried to the scan, the times bound from
   the matched period. The old clauses are removed in the same step.
   - Tests: each word alone and combined; both orders of the bounds; the empty range; joins where each pattern keeps its
     own times; the error on Graph, Reference and Minimal.
   - Every W3C result unchanged.
   - **Order** ([ADR-062](ADR-062-what-a-read-yields.md) D8, decided 2026-11-12): the cursor of ADR-062, its part one,
     comes before this step. The words are built once, on the cursor.
6. **One removal** (D2): `Retract`, `RetractBatched` and `RetractedAt` go, and nothing writes `LegacyIsDeleted`. The public
   store methods follow the words: `Add` with a stated time, `QueryAsOf`, `TimeTravelTo`, `QueryChanges` and
   `QueryEvolution` go or change.
   - **One change with ADR-062's part two** (ADR-062 D8): the two result types and the public names change here, so the
     public surface changes once.
7. **Pruning** (D12): one way to prune, the remembered memories with their memorized times, a selection query, and the
   internal copy that carries a time. Tests:
   - a pruned store answers the default view and every `MEMORIZED` question about what is remembered as its source
     did;
   - a `FORGOTTEN` question on it finds nothing;
   - a source in which everything is forgotten gives an empty store.
8. **Consumers:**
   - the `lucy` skill's recency recall uses `MEMORIZED`, and its temporal modes are rewritten;
   - the agent stops stamping `dcterms:created`;
   - the Mercury MCP tool description, `MERCURY.md`, `README.md`, `docs/architecture/technical/sparql-reference.md`,
     `docs/architecture/technical/temporal-rdf.md`, `docs/api/api-usage.md`, the tutorials, the temporal examples and
     benchmarks.
9. **Dogfood** on the live memory store:
   - ask "what did we memorize last week" and "in October" with one query each;
   - read the eight write times of 2026-11-10 with one query, and compare them with the bisected values in
     `urn:sky-omega:obs:2026-11-10-write-times-from-the-store`.
10. **A committed write that cannot be applied** (M7): the store refuses reads, writes and checkpoints until recovery
    has run, and the two kinds of error can be told apart. The single-write path stamps under the write lock (I12).
11. **Every graph that holds a period** is found by a graph variable under `INCLUDING FORGOTTEN` (D10). Until Step 5 the
    same holds for `ALL VERSIONS`.

## Later: the stored layout

**Not covered by the acceptance of this ADR — Decided 2026-11-14.** This part is a design phase that is reviewed on
its own: the layout, the conversion of existing stores, their cost, and the rule for the entries of the live store
that disagree between its indexes. Everything in it is Sky's proposal, except where it is marked **Decided**. It starts
when Steps 1 to 11 are done.

Two things bind it already: a conversion carries every period unchanged (I14), and nothing resolves a disagreement
between stored copies silently (I19).

**Since this part was written** Martin has chosen another shape for the stored form than L1's four indexes: a set
keyed by the quad alone, holding its latest period, and a timeline in time order that a search by time can cut by
binary search. It is not written here yet. L1 stands as it was proposed until then.

A first analysis of that shape against the model, 2026-11-14. The first two points are checked by enumeration, in a
script that touches no store (65,537 sequences, 2,652,185 cases of a stop and a replay). The rest is reasoning.

- **The set fits M5.** The rule of M5 looks only at a quad's latest period, and that is what the set holds. I1 and I2
  hold by construction: one key per quad, one period per key. The fourth row of M11 cannot happen, because the
  decision is made once, on the set, and the timeline receives what was decided.
- **M7's "remove" record needs one more thing.** Removing a period that began in this clock reading uncovers the
  quad's earlier period, and the set no longer holds it. With M7's records as written a set ends wrong in 126,009
  cases: memorize at 1, forget at 2, memorize at 3, forget at 3 leaves it empty, where the latest period is `[1, 2)`.
  When the record carries the period it uncovers, every case ends right. The writer knows that period: it replaced it
  in the same clock reading.
- **M7's other claim holds.** Applying a record to the set needs no comparison of times. A check that expected to
  need one found no failing case.
- **I13 needs other words.** The set holds one period per quad and the timeline holds all. What must agree is each
  quad's latest period.
- **A quad's earlier periods are only in the timeline.** A question about the past on a narrow pattern, such as the
  query of Q10 for one subject, reads the timeline up to that time. The live store has two quads with an earlier
  period.
- **What an entry of the timeline is, is open:** a period, or a boundary of a period. With periods, a `FORGOTTEN`
  range has no order to search, and ending a period changes an entry in the middle. With boundaries, both ranges can
  be cut by binary search, and the timeline only grows at its end, except for the entries of the current clock
  reading (I10). A boundary is the beginning or the end of a period in `M`. It is not an operation: operations that
  leave no period leave no boundary.
- **Not analysed yet:** the conversion into this shape, and its cost on a store of real size.

Until then the store keeps five more attributes per entry that nothing will read:

| Attribute | After Steps 1 to 11 |
|-----------|--------------------|
| `LegacyTransactionTime` | A copy of the memorized time. |
| `LegacyCreatedAt` | A copy of the memorized time. |
| `LegacyModifiedAt` | Read only to date a retraction. D2 removes retraction. |
| `LegacyVersion` | Not read. |
| `LegacyIsDeleted` | Not written once D2 is done. |

Each is a second statement of something the two times already say, and a second statement can disagree with the
first.

### L1. An entry holds the quad, the two times and the child pointer

Martin allowed the redesign: "we can redesign and reduce the temporal properties tied to quads."

- **An entry holds:** graph, subject, predicate, object, `MemorizedAt`, `ForgottenAt`, and the tree's child pointer.
  That is 56 bytes, from 88.
- **Dropped:** `LegacyTransactionTime`, `LegacyCreatedAt`, `LegacyModifiedAt`, `LegacyVersion`, `LegacyIsDeleted`, and
  the flags and reserved bytes.
- **The four indexes stay.** TGSP leads with the memorized time, so `MEMORIZED SINCE a BEFORE b` on an otherwise open
  pattern is a range scan. A `FORGOTTEN` range has no index and filters while it scans.
- **The WAL record** carries what replay needs: the operation, the quad and its time.

### L2. The schema says which layout a store has

`StoreSchema.KeyLayoutVersion` is 1 today, and every open reads it. The new layout is version 2. A build that knows
only version 1 already refuses a higher version.

### L3. A version 1 Cognitive store is converted when it is opened

Martin: "We can translate old data, convert/migrate it to a preferred state. Rebuild a store … It could possibly be
automatic, on open. We do not need to mind other stores, there are only ours in use."

- **Both times are carried unchanged — Decided** (I14).
- An entry with `LegacyIsDeleted` set: forgotten = the earlier of `ForgottenAt` and `LegacyModifiedAt`, the time of
  its retraction.

### L4. The conversion cannot damage the store it reads

- It builds the new files beside the old ones and switches when they are complete.
- A crash during the conversion leaves the version 1 store as it was.
- The WAL is checkpointed first.
- The old files are kept until the converted store has been opened and checked. A command removes them.

### Open questions of the later part

- **LQ1. Entries whose `MemorizedAt` differs from `LegacyTransactionTime`** are the ones where the stored time could be a
  stated one. The memory stores have never been pruned, so none was expected. Step 3 counted 84 in the live memory
  store (2026-11-14), each less than 10 ms before its transaction time (M11).
- **LQ2. Automatic or by command.** Martin: "possibly … automatic, on open". An open that converts takes longer than
  any other open, and the MCP server opens the store at start. Does the server convert, or does a command do it first?
- **LQ3. Disk space.** The conversion needs room for both layouts at once. It checks before it starts
  (`DiskSpaceGuard`).
- **LQ4. The Graph profile** uses `VersionedQuadIndex` and has no times. The later part does not change it.
- **LQ5. Entries the model has no place for.** A version 1 store can hold an entry with equal times, two periods of
  one quad that touch, and an extra open entry in TGSP (M11). The conversion needs a rule for each. The live memory
  store holds none of the first two. Its TGSP differs from GSPO in two ways (M11), and neither index alone is right:
  GSPO is what a default query reads today, and only TGSP holds 32 facts that were told again and lost. Other stores
  are not counted.
  - **Proposed 2026-11-14, a rule for each difference.** Martin has not decided it.
    - *Forgotten in GSPO, open in TGSP (204 entries):* GSPO's forgotten time stands. The fact was deleted, and GPOS
      and GOSP say the same.
    - *An extra open entry for a fact GSPO remembers (9):* dropped. Memorizing a remembered fact changes nothing (I3).
    - *A later telling of a fact GSPO holds as forgotten (38 entries, 32 facts):* the fact gets a second period, open,
      from its first later telling. That is what the model gives for memorize, forget, memorize, and the last thing
      the store was asked about each of them was to remember it. What cannot be known: whether a delete followed
      that the store did not record.
  - **The conversion reads every graph**, also one that is wholly forgotten (M11).
  - **Nothing is resolved before the rule is decided**, and the copy the counts were made on is kept (M11).

### Steps of the later part

1. The version 2 entry and key, with every index and the WAL record.
2. The conversion, with tests:
   - a version 1 store with remembered, forgotten and `LegacyIsDeleted` entries gives the same answers to every query of D6
     after it is converted;
   - a conversion stopped part-way leaves the version 1 store openable.
3. The live memory stores are converted, each after a copy is taken.
4. `STATISTICS.md`, `docs/architecture/technical/mercury-internals.md` and the storage documents.

## Not in this ADR

- **World time as a language feature** (D3). It stays triples.
- **A finer clock, or a guard on it** (D4).

## References

- [ADR-029](ADR-029-store-profiles.md): store profiles.
- [ADR-052](ADR-052-update-where-one-pattern-path.md): an Update's `WHERE` is the query's pattern path.
- [ADR-053](ADR-053-one-rdf-term-one-atom.md): its migration uses Retract (Q8).
- [ADR-054](ADR-054-delete-ends-valid-time.md): a delete ends a fact. D2 and D6 are replaced by D2 here.
- [ADR-059](ADR-059-every-w3c-test-runs.md): every W3C test runs.
- [ADR-062](ADR-062-what-a-read-yields.md): what a read yields on each profile; its D8 sets the order against Steps 5
  and 6 here.
- C-SPARQL: Barbieri, Braga, Ceri, Della Valle, Grossniklaus, "C-SPARQL: SPARQL for Continuous Querying", WWW 2009.
- T-SPARQL: Grandi, "T-SPARQL: A TSQL2-like Temporal Query Language for RDF", ADBIS 2010 (CEUR Vol-639); Tappolet and
  Bernstein, "Applied Temporal RDF", ESWC 2009.
- [Time is not identity, a cookbook](../../architecture/technical/time-is-not-identity-cookbook.md), version 0.2:
  modelling guidance for the model. It does not replace this ADR.
- `src/Mercury/Storage/TemporalQuadIndex.cs`: the entry layout and `InsertIntoLeaf`.
- `src/Mercury.Abstractions/StoreSchema.cs`: `KeyLayoutVersion`.
- Mercury, graph `urn:sky-omega:session:2026-11-07-adr016-step5`:
  - `urn:sky-omega:obs:2026-11-10-write-times-from-the-store`
  - `urn:sky-omega:obs:2026-11-10-mercury-time-extension-shape`
  - `urn:sky-omega:obs:2026-11-11-two-times-per-version-analysis`
  - `urn:sky-omega:obs:2026-11-11-martin-decides-two-times-no-clock-guard`
  - `urn:sky-omega:obs:2026-11-11-memory-time-resolution-is-for-recall`
  - `urn:sky-omega:obs:2026-11-11-martin-chooses-range-words-half-open`
  - `urn:sky-omega:obs:2026-11-11-martin-keeps-memorized`
  - `urn:sky-omega:obs:2026-11-11-martin-settles-the-memory-time-words`
  - `urn:sky-omega:obs:2026-11-11-martin-reviews-adr061-first-pass`
  - `urn:sky-omega:obs:2026-11-11-dialect-name-research`
  - `urn:sky-omega:obs:2026-11-13-martin-decided-temporal-rdf-set-not-event-log`
  - `urn:sky-omega:obs:2026-11-13-memory-time-model-derived-and-checked`
  - `urn:sky-omega:obs:2026-11-13-zbook-attack-on-the-memory-time-model-and-my-assessment`
  - `urn:sky-omega:obs:2026-11-13-zbook-independent-check-of-resolved-record-replay`
  - `urn:sky-omega:obs:2026-11-13-new-since-my-last-look-is-exact-under-one-rule`
  - `urn:sky-omega:obs:2026-11-14-martin-a-cognitive-store-has-one-owner-clock-behind-is-not-a-case`
  - `urn:sky-omega:obs:2026-11-14-cookbook-v02-compared-with-the-model-and-checkpoint-read`
  - `urn:sky-omega:obs:2026-11-14-probes-run-today-against-the-model-and-live-store-count`
