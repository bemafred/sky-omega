# ADR-062: What a Read Yields — A Read on Any Profile Yields the Quad, Only a Cognitive Read Yields a Memory's Two Times, and One Cursor Replaces the Four Enumerators Behind Three Flags

## Status

**Status:** Proposed — 2026-11-12

Martin asked on 2026-11-12, after the `Legacy` rename of [ADR-061](ADR-061-a-facts-own-times.md) D15:

> Why does all profiles share ResolvedTemporalQuad? It seems to be something the previous refactoring into profiles
> left unattended?

The code and the git history say yes (see [Context](#context)). Martin's decision, the same day: it becomes an ADR.

Each decision below is marked:

- **Decided**: Martin's decision, 2026-11-12.
- **Proposed**: Sky's proposal. Martin has not confirmed it.

Two things are decided: this is an ADR of its own and not a part of ADR-061, and the order of its work against
ADR-061's (D8). D1 to D7 are proposed. Nothing in the code has changed for this ADR.

## Why

### A result must not state what the store does not know

A store has one of four profiles ([ADR-029](ADR-029-store-profiles.md)). Only the Cognitive profile keeps times. A
read on any profile returns the same type, `ResolvedTemporalQuad`, with three times and one flag. On the Reference,
Graph and Minimal profiles those four values are made up: the earliest time, the latest time, the earliest time, and
`false`. A reader cannot tell a made-up value from a kept one. The type says "memorized at the earliest time", and
that is not true: the store keeps no such time.

ADR-061 gives the two times one meaning: when this memory memorized a fact and when it forgot it. A made-up time in
the same member is the kind of false answer that ADR-061 removes.

### One struct holds four enumerators and three flags

`TemporalResultEnumerator` holds one enumerator per profile, all four by value, and three flags that say which one is
in use. `MoveNext` and `Current` branch on the flags, and each branch repeats the same logic for its own key type.

Martin ruled the pattern out on 2026-05-16, while the Graph and Minimal profiles were planned: no flag inside one type
that switches its behaviour; each variant gets its own concrete class. The four index classes follow the rule. The
enumerator in front of them got `_isGraph` and `_isMinimal` on that same day. The repeated logic is the copy-and-paste
he named on 2026-10-27.

### The names say Temporal for all four profiles

`TemporalResultEnumerator`, `ResolvedTemporalQuad`, `TemporalQueryType`, `TemporalIndexType` and `TemporalQueryMode`
are used by profiles that have no time. Three of the five are public.

## Context

All facts below were read from the code at `650e6a9` and from the git history on 2026-11-12.

### History

| Commit | Date | What it did |
|--------|------|-------------|
| `ce189c4` | 2025-12-29 | `ResolvedTemporalQuad` exists. There are no profiles: every store keeps times. |
| `b3e2964` | 2026-04-20 | ADR-029 Phase 2d, the Reference profile. `TemporalResultEnumerator` becomes "the routing seam": a second backing enumerator and the flag `_isReference`. The commit message gives the goal: "no branching at the caller, no new API surface". The time members get made-up values. |
| `fffc3d1` | 2026-05-16 | The Graph profile: a third backing enumerator and `_isGraph`. |
| `88598d4` | 2026-05-16 | The Minimal profile: a fourth backing enumerator and `_isMinimal`. |

The choice of 2026-04-20 is in the commit message only. The text of ADR-029 says nothing about the result type. The
choice kept the callers unchanged while the profiles were built, and nothing came back to it afterwards.

Two rules from that work bear on this ADR:

- **ADR-029 Decision 3:** two concrete key and entry variants, not generics. Its reason: generics over struct layouts
  cost JIT time per instantiation and make the code harder to read.
- **Martin's rule of 2026-05-16:** each variant gets its own concrete class and no flag switches behaviour inside one
  type. Shared behaviour may be extracted afterwards, but only when the extraction is straightforward: three
  near-duplicate classes are better than one parameterized class that does not quite fit any of them.

### The result type today

`ResolvedTemporalQuad` is a public `readonly ref struct` in `src/Mercury/Storage/QuadStore.cs`. What each profile puts
in each member:

| Member | Cognitive | Graph | Reference | Minimal |
|--------|-----------|-------|-----------|---------|
| `Graph` | kept | kept | kept | always empty (the profile has no graphs) |
| `Subject`, `Predicate`, `Object` | kept | kept | kept | kept |
| `MemorizedAt` | kept | the earliest time | the earliest time | the earliest time |
| `ForgottenAt` | kept | the latest time | the latest time | the latest time |
| `LegacyTransactionTime` | kept | the earliest time | the earliest time | the earliest time |
| `LegacyIsDeleted` | kept | the Graph entry's own `IsDeleted`, always `false` here | `false` | `false` |

Two notes:

- The Graph entry has its own `IsDeleted`, `CreatedAt`, `ModifiedAt` and `Version`. They are not the Cognitive fields
  and are not marked `Legacy` (ADR-061 D15). A Graph read passes its `IsDeleted` into the member that is now named
  `LegacyIsDeleted`. Only live entries reach that code, so the value is always `false`.
- On Cognitive, `Current` converts three times from milliseconds to `DateTimeOffset` for every row. The SPARQL engine
  never reads them. The cost is not measured.

### The enumerator today

`TemporalResultEnumerator` is a public `struct` in the same file (lines 3342 to 3749). It was a `ref struct` until
[ADR-011](ADR-011-queryresults-stack-reduction.md) made it a `struct`, so that a scan can keep an array of them.

- **Fields:** `_baseEnumerator`, `_referenceEnumerator`, `_graphEnumerator`, `_minimalEnumerator` (one per profile,
  all by value), `_isReference`, `_isGraph`, `_isMinimal`, the index order, the atom store, the set of candidate
  objects from the trigram index, and a pooled character buffer.
- **Constructors:** five, one or two per profile. Each sets one backing enumerator and leaves the other three at their
  default.
- **The flags appear on 24 lines.**
- **`MoveNext`** has four branches. Each one repeats the candidate filter: read the object's atom from the key by
  index order, and skip the row when the atom is not a candidate.
- **`Current`** has four branches. Three repeat the mapping from index order to subject, predicate and object.

### The four backing enumerators have one shape

Each is a position in a B+Tree leaf with a lower and an upper key:

| | Index object | Page | Slot | Lower key, upper key, current key | More |
|---|---|---|---|---|---|
| `TemporalQuadEnumerator` (Cognitive) | yes | yes | yes | `TemporalKey`, 7 × 8 bytes | the time selection (`TemporalQuery`), and the current entry's `LegacyIsDeleted` and `LegacyModifiedAt` |
| `VersionedQuadEnumerator` (Graph) | yes | yes | yes | `VersionedKey`, 4 × 8 bytes | whether deleted entries are included, and the current entry's `IsDeleted`, `Version`, `CreatedAt`, `ModifiedAt` |
| `ReferenceQuadEnumerator` | yes | yes | yes | `ReferenceKey`, 4 × 8 bytes | none |
| `MinimalQuadEnumerator` | yes | yes | yes | `MinimalKey`, 3 × 8 bytes | none |

The four index classes are `sealed` and implement `IQuadIndex`, which has `QuadCount`, `Flush`, `Clear` and
`Dispose`. It has no read method.

**The size of the shared enumerator is not measured.** Counting the declared fields gives roughly 230 bytes for the
Cognitive enumerator, 140 for Graph, 120 for Reference and 95 for Minimal, so roughly 600 bytes for the struct that
holds all four. This is a count, not a measurement: the runtime may lay the fields out differently. Step 2 measures
it.

### Who reads what

- **The SPARQL engine** reads `Graph`, `Subject`, `Predicate` and `Object` and nothing else. No file under
  `src/Mercury/Sparql` reads a time member or `LegacyIsDeleted` from a result.
- **The engine holds the enumerator by value** in `MergedGraphEnumerator` (one, plus an optional merge over several
  graphs), in `GraphMerge` and `TriplePatternScan` (arrays), and in `DescribeResults`.
- **Pruning** (`src/Mercury.Pruning/PruningTransfer.cs`) is the only reader of the time members in `src` outside the
  store. It runs on Cognitive stores.
- **Tests** read time members from results in four files: `DeleteEndsValidTimeTests` (24 places), `QuadStoreTests`
  (6), `TransactionalIntegrityTests` (4), `PruningTests` (1). All are Cognitive stores.

### The public methods

`QuadStore` has six public methods that return the enumerator. On Reference, Graph and Minimal, `Query` dispatches to
`QueryReferenceCurrent`, `QueryGraphCurrent` or `QueryMinimalCurrent` and throws `ProfileCapabilityException` for
anything but "current"; on Cognitive it calls `RequireTemporalProfile` and selects by time.

| Method | Works on | Callers outside `QuadStore.cs` and the tests | Calls in tests |
|--------|----------|----------------------------------------------|----------------|
| `QueryCurrent` | all four | Mercury 9, Solid 13, Pruning 3, benchmarks 12, examples 6 | 112 |
| `Query` (with a `TemporalQueryType`) | all four for "current"; Cognitive for the rest | (reached through the methods in this table) | |
| `QueryAsOf` | Cognitive | Mercury 2, benchmarks 1, examples 1 | 31 |
| `QueryEvolution` | Cognitive | Mercury 1, Pruning 2, benchmarks 1, examples 2 | 25 |
| `QueryChanges` | Cognitive | Mercury 1, benchmarks 1, examples 1 | 7 |
| `TimeTravelTo` | Cognitive | examples 2 | 2 |

ADR-061 Step 6 already says that `QueryAsOf`, `TimeTravelTo`, `QueryChanges` and `QueryEvolution` go or change.

[ADR-003](../ADR-003-mercury-encapsulation-and-api-surface.md) lists `TemporalResultEnumerator`,
`NamedGraphEnumerator` and `ResolvedTemporalQuad` as the three public storage enumerators. They are public because
public methods return them.

### The same pattern, outside the read

Two more places have the pattern. They are named here so the scope can be decided (Q4, Q5):

- `QuadStore` has eleven index fields, of which one profile's set is not null: four `TemporalQuadIndex`, four
  `VersionedQuadIndex`, two `ReferenceQuadIndex`, one `MinimalQuadIndex`. 26 lines of `QuadStore.cs` name a profile.
- `NamedGraphEnumerator` holds a Cognitive and a Graph enumerator and a mode that says which.

## Decision

D1 to D7 are **Proposed**. D8 is **Decided**.

### D1. A read on any profile yields the quad and nothing else — Proposed

The type that every profile's read returns has four members: graph, subject, predicate, object. It has no time and no
flag.

- No profile has to make a value up.
- The SPARQL engine loses nothing: it reads only these four.
- On Cognitive, a plain read no longer converts three times per row.

On Minimal the graph is empty because the profile has no graphs. That is a true answer and stays.

### D2. A memory's two times come only from a Cognitive read — Proposed

A second type carries the quad and its two times, `MemorizedAt` and `ForgottenAt`. Only reads that exist on the
Cognitive profile return it. Whoever holds a value of this type knows the times are kept, not made up.

- Which Cognitive reads exist is ADR-061's matter (Step 6: the public store methods follow the words). This ADR says
  only what they return.
- Pruning reads every version with both times through such a read (ADR-061 D12).
- `LegacyTransactionTime` and `LegacyIsDeleted` are not members of the new type. Until they leave the entry (ADR-061
  Step 6 and L1), what still needs them reads them inside the store: the ADR-054 repair, and the tests that check the
  transaction time, which then move to the index's own tests or go with the field.

Inside the engine, ADR-061 Step 5 lets a triple pattern with `MEMORIZED` or `FORGOTTEN` bind a time from the matched
version. The scan reads the two times from the position it holds (D3). A pattern with one of the words is rejected on
a store without times before a scan is built, so a scan never asks a store that keeps none.

### D3. One cursor for all profiles, and the per-profile part in the index class — Proposed

Something must choose the index implementation, since there are four and the engine has one path. There are three
ways to choose, and a fourth that only renames:

| | How the profile is chosen | Flags | Allocation per scan | What changes in the engine | What a scan holds |
|---|---|---|---|---|---|
| **A. Keep the struct, rename it, shrink the result** | a branch per row on a tag | stay | none | the result type's name | all four enumerators |
| **B. The engine takes the read type as a type parameter** | once per query; the JIT builds each operator once per profile | none | none | every operator becomes generic | one enumerator |
| **C. An enumerator object per scan** | a virtual call per row | none | one object per scan, or a pool | little | a reference |
| **D. One cursor struct; the index class moves it** | a virtual call per row (or per leaf) | none | none | the enumerator's name | one cursor |

**Proposed: D.** The four backing enumerators already have one shape (see Context). So:

- **One cursor struct** holds what all four hold: the index object, the page, the slot, the lower and upper bounds, the
  current quad as four atoms, and the two times of the current version. The bounds are sized for the widest key.
- **Each index class moves the cursor.** The four classes get one common read operation, which advances a cursor to the
  next entry that matches. Each class implements it for its own key and entry layout. Cognitive applies the time
  selection and fills the two times; Graph skips deleted entries.
- **The enumerator the engine holds** wraps the cursor and does, once, what is the same for every profile: the
  candidate filter, the mapping from index order to subject, predicate and object, and decoding atoms into the buffer.

Why D and not the others:

- **A** removes the made-up values but keeps the flags, the four copies and the size.
- **B** removes the flags without a call per row, but a type parameter spreads through every operator of the engine,
  and the JIT compiles the engine once per profile used. ADR-029 Decision 3 chose against generics for the indexes
  for those reasons, and here the change would reach the code that
  [ADR-045](ADR-045-graph-clause-feature-parity.md), [ADR-047](ADR-047-default-path-cutover.md) and
  [ADR-055](ADR-055-query-where-one-parse.md) have just made one path.
- **C** is the plainest form of the rule: four concrete enumerator classes behind one abstract type. It allocates per
  scan, or needs a pool with its own lifetime rules. The engine's scans are structs held by value so that a query
  allocates nothing.
- **D** keeps each variant in its own class and has no flag in the shared code. It allocates nothing, leaves the
  engine as it is, and a scan holds one cursor instead of four enumerators.

**D is an extraction of shared behaviour, and the rule of 2026-05-16 allows that only when it is straightforward.**
The risk the rule names is real here: one cursor with fields for the widest key, for Cognitive's time selection and
for Graph's deleted entries is a parameterized struct that fits no index exactly. There are two forms of the cursor,
and Step 3 chooses between them after the four `MoveNext` bodies have been compared:

- *One set of fields*, sized for the widest key. Simple to read; carries fields that three profiles do not use.
- *A block of bytes of the widest state's size*, which each index class reads as its own state struct. Each index
  keeps its own state type and its own `MoveNext` nearly as they are; only the space and the call are shared. The
  state holds no object references (atoms, page numbers, slots and times), so reading the block as a struct is safe
  for the garbage collector.

If neither form is straightforward once the bodies are compared, D is the wrong extraction. Then the choice is
between A with the result types of D1 and D2, and C with a pool, and it comes back to Martin.

What is not known about D, and must be known before this ADR can be Accepted:

1. **The cost of the call.** Today a row costs a predictable branch and a direct call. D costs a virtual or interface
   call per row. Whether that is visible next to decoding the atoms is not measured. If it is, the index can advance
   within a leaf and be called once per leaf.
2. **Whether one cursor can hold every index's state.** The table in Context says yes for the fields. The four
   `MoveNext` bodies have not been compared line by line.
3. **The cursor's size.** Roughly the Cognitive enumerator's, by count. Not measured.

The cursor has room for two times that only the Cognitive index fills. That is the weak point of D: inside the store,
the fields exist for every profile. They are internal, D2 keeps them out of every public type, and nothing reads them
on a profile without times.

### D4. What is the same for every profile is written once — Proposed

The candidate filter and the mapping from index order to subject, predicate and object exist once, in the enumerator
the engine holds (D3). Today the first exists four times and the second three times.

### D5. The engine stays one path — Proposed

No profile gets its own scan, its own operator or its own executor. The words of ADR-061 are the only thing a profile
changes in a query, and they are rejected by one check before the scan.

### D6. A type that every profile uses does not say Temporal — Proposed

The names are an open question (Q3). The rule is proposed: a name with "Temporal" or "Memory" in it belongs to the
Cognitive profile only. It covers the result type, the enumerator, `TemporalIndexType` (an index order: GSPO, GPOS,
GOSP, TGSP), and what is left of `TemporalQueryType` and `TemporalQueryMode` after ADR-061 removes `AS OF`, `DURING`
and `ALL VERSIONS`.

### D7. Measure before the mechanism is fixed — Proposed

D3 is proposed on the rules and on the shape of the code. Its unknowns are settled first (Steps 2 and 3), with
measurements compared only to measurements.

### D8. The cursor comes before ADR-061's words, and the result types come with ADR-061's Step 6 — Decided

This ADR stays separate from ADR-061. ADR-061 is about what the times mean and the words that ask for them, on the
Cognitive profile. This ADR is about what the profile work left in the read, on all four profiles. Its unknowns (D3)
must not hold up ADR-061's review.

Two steps of ADR-061 touch the enumerator. Step 5 carries the bounds and the time variables to the scan and binds the
times there. Step 6 removes and changes the public read methods. This ADR's work has two parts, and each has its
place:

1. **The cursor** (D3, D4) comes first. It is internal and changes no public type.
2. **ADR-061 Step 5**, the words, follows. The words are built once, on the cursor.
3. **The two result types and the public names** (D1, D2, D6) are one change with **ADR-061 Step 6**. The public
   surface changes once.

ADR-061's Steps 3 and 4 (the count of the live store and the D5 tests) do not touch the enumerator and can run at any
time.

Between part one and part two, the enumerator over the cursor still returns `ResolvedTemporalQuad`. The made-up
values stay for that time, written in one place instead of three.

## Open questions

- **Q1. Order against ADR-061.** Answered on 2026-11-12: D8.
- **Q2. The mechanism** (D3): D as proposed, or another.
- **Q3. Names.** For the quad every profile yields, the two-time type, the enumerator, the cursor, the read
  operation, the index order. No names are proposed yet. The cursor and the read operation need their names before
  Step 4; the public names can wait until Step 7.
- **Q4. The store class.** `QuadStore`'s eleven index fields and its profile branches are the same pattern. Does this
  ADR cover them, or only the read? *Proposed: only the read.* The write path and the store's fields are a larger
  change with its own risks, and a common read operation on the index classes (D3) is the first piece either way.
- **Q5. `NamedGraphEnumerator`.** Two enumerators and a mode. With a common read operation it needs one cursor.
  *Proposed: in this ADR, as the last step of part one.* Its public shape does not change.
- **Q6. The Graph profile's own version fields.** No read exposes `CreatedAt`, `ModifiedAt` or `Version`, and
  `IsDeleted` reaches a result only as an always-`false` value. Is that intended, or is a Graph read missing
  something? This ADR does not change it; D1 removes the always-`false` member.
- **Q7. The public surface.** The two public types change name and shape, and a third appears. That breaks callers of
  the storage API outside this repository, if there are any. The count of 28 public types in `CLAUDE.md` and the list
  in ADR-003 change.

## Steps

The order follows D8.

1. **Martin's review** of this text: D1 to D7 and Q2 to Q7.

**Part one: the cursor.** Before ADR-061 Step 5. No public type changes.

2. **Measure,** read-only, as a probe reviewed before it runs:
   - the size of `TemporalResultEnumerator`, of each of the four backing enumerators and of `MergedGraphEnumerator`;
   - a baseline of the existing query benchmarks (`SparqlBenchmarks`, `StorageBenchmarks`, `TemporalBenchmarks`) on
     the code as it is.
3. **Compare the four `MoveNext` bodies** line by line, and write down the cursor in one of its two forms (D3). If
   neither form is straightforward, D3 changes before any code does. The cursor and the read operation get their
   names (Q3).
4. **The common read operation** on the four index classes, and the cursor, next to the existing enumerators. Each
   index's own tests run against both and must agree.
5. **The enumerator over the cursor** replaces the four-enumerator struct. The three flags, four of the five
   constructors and the repeated branches go. It still returns `ResolvedTemporalQuad`, with the made-up values
   written once. Every test passes; every W3C result is unchanged. The benchmarks of Step 2 are run again and
   compared.
6. **`NamedGraphEnumerator`** on the cursor (Q5).

**Then ADR-061 Step 5:** the words, built on the cursor.

**Part two: the result types.** One change with ADR-061 Step 6.

7. **The two result types** (D1, D2) **and the public names** (D6), in the change that removes and changes the public
   read methods (ADR-061 Step 6). The engine reads the quad type. Pruning and the Cognitive tests read the two-time
   type. The made-up values go.
8. **Documents:** ADR-003's list, the public type count in `CLAUDE.md`, `mercury-internals.md`, `api-usage.md`,
   `STATISTICS.md`.

Steps 4 to 7 change Mercury code and wait for Martin's word, each.

## Not in this ADR

- The write path, and `QuadStore`'s index fields and profile branches (Q4).
- Which Cognitive read methods exist, and the words that select by time. That is ADR-061.
- The stored layout of any profile. ADR-061's later part changes the Cognitive entry; this ADR reads whatever the
  entry holds.
- What the Graph profile's version fields are for (Q6).

## References

- [ADR-029](ADR-029-store-profiles.md): the four store profiles; Decision 3, concrete variants and not generics.
- [ADR-061](ADR-061-a-facts-own-times.md): a memory's two times, the words, and the `Legacy` attributes.
- [ADR-003](../ADR-003-mercury-encapsulation-and-api-surface.md): Mercury's public surface.
- [ADR-011](ADR-011-queryresults-stack-reduction.md): why the enumerator is a `struct` held in arrays.
- [ADR-045](ADR-045-graph-clause-feature-parity.md), [ADR-047](ADR-047-default-path-cutover.md),
  [ADR-055](ADR-055-query-where-one-parse.md): one path through the SPARQL engine.
- Commits `ce189c4`, `b3e2964`, `fffc3d1`, `88598d4`: the history in Context.
- Mercury memory: `urn:sky-omega:obs:2026-11-12-resolved-temporal-quad-shared-by-all-profiles`.
