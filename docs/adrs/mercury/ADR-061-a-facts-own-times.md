# ADR-061: A Memory's Two Times — The Cognitive Store Keeps When It Memorized and When It Forgot Each Fact, and Cognitive SPARQL Selects and Reads Both

## Status

**Status:** Proposed — 2026-11-11

Rewritten on 2026-11-11 from the draft of 2026-11-10 ("A Fact's Own Times"). That draft let a query read the times
the store already keeps. In the design discussion of 2026-11-11 Martin set the purpose of those times, reduced them to
two, and chose the words. This text follows those decisions.

Each decision below is marked:

- **Decided**: Martin's decision, 2026-11-11.
- **Proposed**: Sky's addition that follows from a decision. Martin has not confirmed it.

Martin's first review, the same day, decided six more things:

- a time without a timezone is UTC (D10);
- the code names the two times `MemorizedAt` and `ForgottenAt`, from the start (D11);
- pruning keeps both times (D12);
- the stored layout changes later, as the later part of this ADR (D13, [Later: the stored layout](#later-the-stored-layout));
- the dialect is called Cognitive SPARQL (D14);
- the rename is the first step. It is done (Step 2), on his direction, while the rest is under review.

On 2026-11-12, after reading this text, Martin decided one more thing: the five attributes that leave carry the
prefix `Legacy` in the code until they are removed (D15). That rename is done too (Step 2).

The file keeps its name: the changelog, the 1.8.41 release notes, the solution file and the ADR index link to it.

## Why

### The purpose

The time a memory store keeps is for recall. Martin, 2026-11-11:

> We need functionality that can answer what have we been upto last week? Last month? What did we do in January? The
> temporality is for recall purposes. Relative to your subjective experience. It is not for validating/invalidating
> individual quads.

And the goal:

> … give memory/cognitive-storage excellent temporal awareness, functionally akin to a human brain but not lossy,
> instead, better and more reliable. Excellent recall support.

Human memory misdates events and loses the old version when a memory is revised. A store that stamps every
memorization and every forgetting itself, and never erases, does neither.

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

**Not verified:** that no entry of the live memory store has `ValidFrom` different from `TransactionTime`, or
`IsDeleted` set. Step 3 counts them.

This section names the attributes as they were on 2026-11-10. In the code they are now `MemorizedAt`, `ForgottenAt`
(D11) and `LegacyTransactionTime`, `LegacyCreatedAt`, `LegacyModifiedAt`, `LegacyVersion`, `LegacyIsDeleted` (D15).

### Profiles

Only the Cognitive profile keeps these times ([ADR-029](ADR-029-store-profiles.md)). Graph has version metadata for
soft deletes; Reference and Minimal have no time. `QuadStore.RequireTemporalProfile` already rejects temporal queries on
them.

## Decision

### D1. Each memory version has two times, set by the store — Decided

- **memorized**: when the store took the fact in.
- **forgotten**: when the store let it go. Open while the fact is remembered.

Both are points on one axis: the memory's own time, read from the store's clock at the write. No writer states them,
and nothing edits them afterwards. A fact that is memorized, forgotten and memorized again has two versions, each with
its own times (ADR-054 D3 stays).

At most one version of a quad is remembered at a time. Memorizing a fact that is already remembered changes nothing and
records nothing: re-affirmation and recall are not stored on the quad.

**Assumption, stated so it can be challenged:** one store is one subject's memory. "Forgotten" is a fact about the
whole store. Two subjects sharing one store could not forget separately.

### D2. Forgetting is the SPARQL delete, and nothing is erased — Decided

Martin: "From a SPARQL point of view, forget is a retract. And a soft delete. We know the when. Which is a good thing.
We have immutability."

- `INSERT` memorizes. `DELETE` forgets: it sets the forgotten time of the remembered version to the delete's time.
- The version stays in the store, with both times.
- There is no second kind of removal. `Retract`, `RetractBatched`, `IsDeleted` and `RetractedAt` go. A fact recorded
  in error is forgotten like any other, and the store still knows it once held it and when.

This replaces ADR-054 D2 (Retract) and D6 (`RetractedAt`). ADR-054 D1, D3, D4, D5 and D8 stay.

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

### D5. The last operation decides the end state — Decided

Operations in one request run in order, and the last one decides the state, also when their times are equal. A fact
inserted, deleted and inserted again inside one millisecond is remembered.

This is a rule for the write logic, and does not touch the clock.

**Not verified:** read from the code, not run. `INSERT DATA {x} ; DELETE DATA {x} ; INSERT DATA {x}` inside one
millisecond may drop the last insert, because `InsertIntoLeaf` takes an entry with the same quad and times for a WAL
replay (Case A). Step 4 tests it.

### D6. The words — Decided

```
MEMORIZED [?t] [SINCE a] [BEFORE b]      after a triple pattern
FORGOTTEN [?t] [SINCE a] [BEFORE b]      after a triple pattern
INCLUDING FORGOTTEN                      after the query
```

- `MEMORIZED ?t` binds when the matched memory was memorized.
- `FORGOTTEN ?t` binds when it was forgotten. It is unbound while the memory is remembered.
- `SINCE a` keeps the memories whose event time is at or after `a`.
- `BEFORE b` keeps the memories whose event time is before `b`.
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

# What did we do in January?
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

A pattern without `FORGOTTEN`, in a query without `INCLUDING FORGOTTEN`, matches the remembered memories: every version
whose forgotten time is open. It needs no clock reading. Today's default reads the clock at each scan and compares.

### D8. The old clauses go — Decided

| Removed | Why |
|---------|-----|
| `AS OF t` | It names one exact moment. Martin: "it mimics a specific, exact, time … I can't think of a use case for it." Neither could Sky find one in recall. |
| `REMEMBERED AT t` | Sky's proposed new name for `AS OF`. Dropped with it. |
| `BETWEEN a AND b` | `SINCE a BEFORE b` says the same and states its bounds. |
| `ALL VERSIONS` | `INCLUDING FORGOTTEN` takes its place. |
| `RECORDED`, `VALID` | The 2026-11-10 draft's working names. `MEMORIZED` and `FORGOTTEN` replace them. |
| `DURING [a, b]` | **Proposed.** Martin has not named it. It selects by overlap with valid time, which no recall question asks. |

The old clauses are removed in the same step that adds the words. Two temporal languages side by side would be two
paths to keep in agreement.

### D9. What the words keep from the 2026-11-10 draft — Proposed

1. **Mercury-only, and non-intrusive.** No standard query can reach the words, so the W3C results cannot move. No pragma
   or switch.
2. **Temporal stores only.** On Graph, Reference or Minimal the words are an error through `RequireTemporalProfile`.
   Never silently unbound.
3. **A time belongs to the version a pattern matched.** Each matched version is its own solution with its own times.
4. **No cost when unused**, and nothing allocated on the query hot path.
5. **Clear of SPARQL 1.2.** The words take none of its new syntax: triple terms `<<( )>>`, reifiers `~`, annotations
   `{| |}`, the `VERSION` directive.

### D10. The exact rules of the words — Proposed, except the first

- **A time without a timezone is UTC — Decided.** Martin: "UTC, yes. Of course." It is the rule everywhere else in
  Mercury. A bound that means a local day carries its offset: `"2026-01-01+01:00"^^xsd:date`.

- **An empty range is empty.** `SINCE a BEFORE b` with `a` at or after `b` matches nothing. It is not an error, and it
  never means "outside the range": that takes two patterns and `UNION`.
- **Each bound at most once per word.** `MEMORIZED SINCE a SINCE b` is a syntax error.
- **Both words may follow one pattern**, each with its own bounds: `MEMORIZED SINCE a FORGOTTEN BEFORE b`.
- **A remembered memory has no forgotten time.** No `FORGOTTEN` bound selects it. The store keeps "open" as a maximum
  value, which would otherwise pass `FORGOTTEN SINCE a`.
- **A pattern that carries `FORGOTTEN` matches forgotten memories too.** `INCLUDING FORGOTTEN` does the same for every
  pattern. Without this rule a `FORGOTTEN` bound in a query without `INCLUDING FORGOTTEN` would match nothing, silently.
- **A bound is an expression without variables**: a literal, or `NOW() - "P7D"^^xsd:dayTimeDuration`. It is evaluated
  once per query. A bound that depends on a variable is a `FILTER` on the bound time:
  `?s ?p ?o MEMORIZED ?t . FILTER(?t >= ?started)`.
- **A time is an `xsd:dateTime` in UTC with a millisecond fraction.** A bound may also be an `xsd:date`: its first
  instant.
- **A version memorized and forgotten in the same clock reading is kept.** Its two times are equal. It shows under
  `INCLUDING FORGOTTEN`.
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

### D12. Every copy carries both times unchanged — Decided

Martin: "Prune must preserve temporality."

- Today `HistoryMode.FlattenToCurrent`, the pruning default, rewrites every time to the time of the prune. After such a
  prune, "what did we do in January" answers "nothing".
- The preserving modes keep `ValidFrom` and `ValidTo` and rewrite the rest.
- Pruning, a rebuild and a conversion copy a version's memorized and forgotten times as they are. Pruning chooses which
  versions to copy, never what their times become.
- **Proposed:** with two times and no retraction, pruning has two modes: copy the remembered memories, or copy all.
- A copy between stores needs a way to carry the two times that no ordinary writer has. It is internal to Mercury.

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

## Open questions

### The language

- **Q1. `FORGOTTEN` without `INCLUDING FORGOTTEN`** (D10): does the word widen its own pattern, or is it an error to use
  it without the clause?
- **Q2. `INCLUDING FORGOTTEN` on a subquery.** Whether today's clause is accepted on a sub-SELECT is not checked. With
  `FORGOTTEN` on a pattern, the clause may be needed only on the outermost query.
- **Q3. The abbreviated triple forms.** Sky's proposal: the words follow an object and apply to the one triple pattern
  that object completes.
  - In a `,` list each object may carry its own words.
  - In a `;` list the words follow each object in the same way.
  - A blank-node property list is an object list too, so the same rule holds inside `[ ]`.
- **Q4. Patterns that match no single stored version.**
  - A property path longer than one predicate: a syntax error?
  - A pattern inside `SERVICE`: a syntax error?
  - A triple inferred by `OwlReasoner`: it is materialized, so it has times of its own. They say when the reasoner
    wrote it.
- **Q5. `DURING`** (D8): dropped with the others?
- **Q6. The planner.** The pattern tree must carry each pattern's bounds to its scan, and choose TGSP when the
  memorized range is the most selective part.

### Names

- **Q7. The word "bitemporal".** The documents call the Cognitive profile bitemporal: valid time and transaction time.
  This ADR gives it one axis with two points per version. What do we call it?

### Other ADRs

- **Q8. ADR-053's migration uses Retract.** [ADR-053](ADR-053-one-rdf-term-one-atom.md) (Proposed) D5 rewrites an
  atom's form by retracting the version and adding it again with the same valid time. Without Retract and without
  stated times it needs another way: a conversion that rewrites the atom and keeps both times, since the memory learned
  nothing new.

## Steps

1. **Martin's review** of this text: D8 to D12 where marked Proposed, Q1 to Q8, and the later part.
2. **The rename** (D11) — **done 2026-11-11.** `MemorizedAt` and `ForgottenAt` in the code and in the four current
   technical documents that name the fields, with no change of behaviour. Every test passes with only the names
   changed. ADRs, release notes and the changelog keep the old names: they describe the store as it was.
   - **The `Legacy` prefix** (D15) — **done 2026-11-12**, the same way: the code, and the two current technical
     documents that name `TransactionTime`. Every test passes with only the names changed.
3. **Count the live store**, read-only, as a probe reviewed before it runs:
   - versions in total;
   - versions whose memorized time differs from `LegacyTransactionTime`;
   - versions with `LegacyIsDeleted` set.
4. **Tests for D5 on today's code:** insert, delete, insert of one quad in one request and one millisecond; a delete
   that follows an insert in the same millisecond. Fix what fails.
5. **The words:** the grammar in `SparqlParser`, the bounds and time variables carried to the scan, the times bound from
   the matched version. The old clauses are removed in the same step.
   - Tests: each word alone and combined; both orders of the bounds; the empty range; joins where each pattern keeps its
     own times; the error on Graph, Reference and Minimal.
   - Every W3C result unchanged.
6. **One removal** (D2): `Retract`, `RetractBatched` and `RetractedAt` go, and nothing writes `LegacyIsDeleted`. The public
   store methods follow the words: `Add` with a stated time, `QueryAsOf`, `TimeTravelTo`, `QueryChanges` and
   `QueryEvolution` go or change.
7. **Pruning carries both times** (D12), with a test that a pruned store answers a period question as its source did.
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

## Later: the stored layout

Sky's proposal, except where it is marked **Decided**. It starts when Steps 1 to 9 are done.

Until then the store keeps five more attributes per version that nothing will read:

| Attribute | After Steps 1 to 9 |
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

- **Both times are carried unchanged — Decided** (D12).
- An entry with `LegacyIsDeleted` set: forgotten = the earlier of `ForgottenAt` and `LegacyModifiedAt`, the time of
  its retraction.

### L4. The conversion cannot damage the store it reads

- It builds the new files beside the old ones and switches when they are complete.
- A crash during the conversion leaves the version 1 store as it was.
- The WAL is checkpointed first.
- The old files are kept until the converted store has been opened and checked. A command removes them.

### Open questions of the later part

- **LQ1. Entries whose `MemorizedAt` differs from `LegacyTransactionTime`** are the ones where the stored time could be a
  stated one. The memory stores have never been pruned, so none is expected. Step 3 counts them.
- **LQ2. Automatic or by command.** Martin: "possibly … automatic, on open". An open that converts takes longer than
  any other open, and the MCP server opens the store at start. Does the server convert, or does a command do it first?
- **LQ3. Disk space.** The conversion needs room for both layouts at once. It checks before it starts
  (`DiskSpaceGuard`).
- **LQ4. The Graph profile** uses `VersionedQuadIndex` and has no times. The later part does not change it.

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
- C-SPARQL: Barbieri, Braga, Ceri, Della Valle, Grossniklaus, "C-SPARQL: SPARQL for Continuous Querying", WWW 2009.
- T-SPARQL: Grandi, "T-SPARQL: A TSQL2-like Temporal Query Language for RDF", ADBIS 2010 (CEUR Vol-639); Tappolet and
  Bernstein, "Applied Temporal RDF", ESWC 2009.
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
