# Memory Time in Mercury

A Cognitive store remembers. For each quad it keeps the periods in which the quad was remembered, and each period has
two times on the store's own clock: when the quad was memorized and when it was forgotten. That axis is called memory
time ([ADR-061](../../adrs/mercury/ADR-061-a-facts-own-times.md)).

Only the Cognitive profile keeps times ([ADR-029](../../adrs/mercury/ADR-029-store-profiles.md)). Graph, Reference and
Minimal stores keep none: a read of times on them is an error, never an empty answer.

## One axis, not two

Earlier versions of these documents called the Cognitive profile bitemporal: valid time, when a fact is true in the
world, and transaction time, when the database recorded it. The store mixed the two. A caller could state a valid time,
and the store also stamped the time of the write. ADR-061 separates them:

- **Memory time is the store's.** No writer states it. The store reads its clock under the write lock, at one
  millisecond resolution, and every write in one reading is simultaneous.
- **World time is content.** When something happened in the world is a triple, named for its source: a commit's time
  from git, a measurement's time from its log. The two memory times say only when the store took a triple in and let
  it go.

## The model

- `INSERT` memorizes. A quad that is not remembered begins a period at the write's clock reading. A quad that is
  remembered stays as it is: memorizing it again changes nothing and records nothing.
- `DELETE` forgets. It ends the open period at the write's clock reading. The period stays in the store, with both
  times. Nothing erases a period; there is no second kind of removal.
- A quad has at most one open period. A quad memorized, forgotten and memorized again at three clock readings has two
  periods.
- Inside one clock reading the last operation on a quad decides. A quad forgotten and memorized again in one reading
  continues its period; a quad memorized and forgotten in one reading was remembered at no time the store can
  represent, and leaves no period.
- Once the clock has moved past a reading, what the store holds for that reading never changes.
- Every range is half-open. A period is `[memorized, forgotten)`: a quad forgotten at `t` is not remembered at `t`.
- A remembered memory has no forgotten time. It is absent, not a far-future value.

What a query sees, and what a write counts as affected, follow from this. A write counts a quad when it changes whether
the quad is remembered.

## Reading memory time in SPARQL

Cognitive SPARQL is SPARQL 1.1 with three words. The full rules are in the
[SPARQL reference](sparql-reference.md#memory-time-cognitive-sparql-adr-061).

```
MEMORIZED [?t] [SINCE a] [BEFORE b]      after a triple pattern
FORGOTTEN [?t] [SINCE a] [BEFORE b]      after a triple pattern
INCLUDING FORGOTTEN                      after the query
```

A query without the words sees what is remembered now, and reads no clock.

```sparql
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

# What did we memorize in January?
SELECT ?g ?s ?label WHERE {
  GRAPH ?g { ?s rdfs:label ?label MEMORIZED SINCE "2026-01-01"^^xsd:date BEFORE "2026-02-01"^^xsd:date . }
}

# Every period of one fact, remembered or forgotten, with its times
SELECT ?m ?f WHERE {
  <http://ex.org/alice> <http://ex.org/worksFor> <http://ex.org/acme> MEMORIZED ?m FORGOTTEN ?f .
} ORDER BY ?m

# Forget what was memorized before a time
DELETE { GRAPH ?g { ?s ?p ?o } }
WHERE  { GRAPH ?g { ?s ?p ?o MEMORIZED BEFORE "2026-01-01"^^xsd:date } }
```

The words bind to the pattern they follow, so each pattern of a join keeps its own times. "What was remembered at a
time" has no word of its own: it is two patterns in a `UNION`, shown in the SPARQL reference.

## Reading and writing from code

```csharp
using var store = new QuadStore("./mydb");

// Memorize and forget: each takes its time from the store's clock
store.AddCurrent("<http://ex.org/alice>", "<http://ex.org/worksFor>", "<http://ex.org/acme>");
store.DeleteCurrent("<http://ex.org/alice>", "<http://ex.org/worksFor>", "<http://ex.org/acme>");

// What is remembered now, on every profile: the quads, without times
store.AcquireReadLock();
try
{
    foreach (var quad in store.QueryCurrent("<http://ex.org/alice>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty))
        Console.WriteLine($"{quad.Subject} {quad.Predicate} {quad.Object}");
}
finally { store.ReleaseReadLock(); }

// The memories with their times, Cognitive only: the bounds the words take
store.AcquireReadLock();
try
{
    foreach (var memory in store.QueryMemories("<http://ex.org/alice>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty,
                 includingForgotten: true))
        Console.WriteLine($"{memory.Object} memorized {memory.MemorizedAt:O}, forgotten {memory.ForgottenAt?.ToString("O") ?? "-"}");
}
finally { store.ReleaseReadLock(); }
```

- `QueryCurrent` returns `ResultEnumerator`, which yields `ResolvedQuad`: the graph and the three terms.
- `QueryMemories` returns `TemporalResultEnumerator`, which yields `ResolvedTemporalQuad`: the quad, `MemorizedAt`,
  and `ForgottenAt`, null while the memory is remembered. Its parameters `memorizedSince`, `memorizedBefore`,
  `forgottenSince`, `forgottenBefore` and `includingForgotten` are the bounds of the words. A bound between two
  milliseconds is taken at the next one. On a store that keeps no times it throws `ProfileCapabilityException`.
- A batch writes many quads in one clock reading: `BeginBatch`, `AddCurrentBatched` and `DeleteCurrentBatched`, then
  `CommitBatch` or `RollbackBatch`.
- The clock is `StorageOptions.Clock`, a `TimeProvider`: the system clock unless one is given. A test supplies its
  readings there. `NOW()` in a query reads the same clock.

More examples are in [API usage](../../api/api-usage.md) and `examples/Mercury.Examples` (`temporal`).

## How it is stored

A Cognitive store has four B+Tree indexes, each a file of 16 KB pages: `gspo.tdb`, `gpos.tdb`, `gosp.tdb` and
`tgsp.tdb`. Every index holds every period; they differ in the order of the key.

```
Entry (88 bytes)
┌──────────┬─────────────────────┬─────────────┬─────────────┬─────────────────────┬───────┬────────────────────┐
│ Graph(8) │ three terms (3 × 8) │ Memorized(8)│ Forgotten(8)│ LegacyTransaction(8)│Child 8│ legacy fields (24) │
└──────────┴─────────────────────┴─────────────┴─────────────┴─────────────────────┴───────┴────────────────────┘
  GSPO, GPOS, GOSP: graph, the terms in the index's order, memorized, forgotten
  TGSP:             memorized first, then graph, subject, predicate, object, forgotten
```

- A read chooses GSPO, GPOS or GOSP by the terms it binds. Which index answers changes no answer: every index holds
  the same periods.
- TGSP leads with the memorized time. No read chooses it today. The store writes and rebuilds it, and its tests check
  that it holds what the other three hold.
- An open period is stored with a forgotten time in the far future. That value never leaves the store: above storage
  a remembered memory has no forgotten time.
- The fields named `Legacy` are from the earlier model and leave with a later change of the stored layout (ADR-061,
  "Later: the stored layout"). The entry then holds the quad, the two times and the child pointer.
- Every write goes through the write-ahead log (`wal.log`) and is applied to the four indexes by the quad and its
  memorized time. A log written by an earlier version still replays as written, its retractions included.

See [Mercury internals](mercury-internals.md) for the log, recovery and concurrency.

## Pruning

A prune copies a store into a new one and keeps what is remembered, each memory with the time it was memorized
(ADR-061 D12). Nothing forgotten is copied. A `FORGOTTEN` question on a pruned store finds nothing, and a source in
which everything is forgotten gives an empty store. A prune never changes a time. Whoever wants every period kept
copies the store. See [Pruning and maintenance](../../tutorials/pruning-and-maintenance.md).

## References

- [ADR-061: A fact's own times](../../adrs/mercury/ADR-061-a-facts-own-times.md): the model, the words, pruning
- [ADR-062: What a read yields](../../adrs/mercury/ADR-062-what-a-read-yields.md): the result types
- [ADR-029: Store profiles](../../adrs/mercury/ADR-029-store-profiles.md)
- [Time Is Not Identity: a cookbook for temporal data modeling](time-is-not-identity-cookbook.md)
