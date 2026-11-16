# Index rebuild probes

What `QuadStore.RebuildSecondaryIndexes` does on a Cognitive store whose secondary indexes already hold entries, as
the live memory store's do. [ADR-061](../../../docs/adrs/mercury/ADR-061-a-facts-own-times.md) M11 lists two kinds
of difference between GSPO and TGSP on that store, and said a rebuild would resolve them. These probes show that it
does not.

Each probe makes its own store in the system's temporary directory and removes it. Two of them put a difference
into one index with that index's own internal method, so their assembly is named `Mercury.Benchmarks`, which sees
Mercury's internals, and they read the store's private index fields by reflection.

| Probe | Shows |
|---|---|
| `rebuild-resolves-an-open-tgsp-entry.cs` | an entry open in TGSP and closed in GSPO (the kind of M11's 204) is still open in TGSP after the rebuild and after a reopen, and the closed entry is added beside it |
| `rebuild-resolves-an-extra-entry.cs` | an entry that GPOS holds and GSPO does not (the kind of M11's 47) is still in GPOS after the rebuild and after a reopen |
| `rebuild-keeps-retracted-mark.cs` | a version retracted before the rebuild stays retracted in GPOS: the rebuild writes nothing for an entry that is already there |
| `rebuild-reads-only-the-default-graph.cs` | after a bulk load, the rebuild gives GPOS, GOSP and TGSP the default graph's entries and none of a named graph's |
| `rebuild-on-a-copy-of-the-live-store.cs` | after the fix: on a copy of a store directory it is given, what the rebuild does; it refuses a directory under the machine's stores directory |

```bash
cd tools/probes/rebuild-indexes
dotnet run --no-cache rebuild-resolves-an-open-tgsp-entry.cs
dotnet run --no-cache rebuild-resolves-an-extra-entry.cs
dotnet run --no-cache rebuild-keeps-retracted-mark.cs
dotnet run --no-cache rebuild-reads-only-the-default-graph.cs
```

## Results on omega, 2026-11-16 (`e538f83` plus ADR-062 Step 5, .NET 10.0.9, osx-arm64)

`rebuild-resolves-an-open-tgsp-entry.cs`:

```
before the rebuild:
  current through GSPO  : "b"
  changes through TGSP  : "a" open, "b" open
  entries: GSPO 2, GPOS 2, GOSP 2, TGSP 2
after the rebuild:
  current through GSPO  : "b"
  changes through TGSP  : "a" closed, "a" open, "b" open
  entries: GSPO 2, GPOS 2, GOSP 2, TGSP 3
opened again:
  (as after the rebuild)
```

`rebuild-resolves-an-extra-entry.cs`:

```
before the rebuild:
  <s> <p> ?o through GSPO : "a"
  ?s <p> ?o through GPOS  : "a" "x"
  entries: GSPO 2, GPOS 3, GOSP 2, TGSP 2
after the rebuild:
  (the same)
opened again:
  (the same)
```

`rebuild-keeps-retracted-mark.cs`: before the rebuild, after it and after a reopen, the current state is `"b"` through
both GSPO and GPOS, and the history through both is `"a" retracted, "b"`.

**Why.** The Cognitive rebuild (`QuadStore.RebuildIndex`) does not clear its target before it inserts every GSPO
entry with `TemporalQuadIndex.AddRaw`; the Graph rebuild does (`target.Clear()`). `AddRaw` writes nothing for an
entry with the same quad, memorized time and transaction time as the entry before it in the index's order. A closed
entry sorts before the open one with the same memorized time, because the forgotten time comes before the
transaction time in both key orders, so the check does not see the open entry and the closed one is inserted.

`rebuild-reads-only-the-default-graph.cs`, run later the same day on `ea70cbd`:

```
rebuild GPOS: 2 entries
rebuild GOSP: 2 entries
rebuild TGSP: 2 entries
rebuild Trigram: 2 entries
default graph, predicate bound (GPOS): 2 rows
named graph, predicate bound (GPOS): 0 rows
default graph, subjects bound (GSPO): 2 rows
named graph, subjects bound (GSPO): 2 rows
```

The rebuild reads GSPO with `QueryHistory(empty, empty, empty)`, and an empty graph there is the default graph. A
store that is written entry by entry writes all four indexes, so only a bulk-loaded store is affected; the live
memory store never was bulk-loaded.

**Not measured:** a prune.

## After the fix (2026-11-16)

The Cognitive rebuild now does what ADR-061 I14 and I19 ask of it
([`CognitiveRebuildTests`](../../../tests/Mercury.Tests/Storage/CognitiveRebuildTests.cs)):

- It reads every graph of GSPO.
- It first looks for an entry of GPOS, GOSP or TGSP that GSPO does not hold as it is: the same key, the same
  retraction mark, and, when retracted, the same time of retraction. If it finds one, it changes nothing, writes a
  warning to the store's log and throws, naming how many each index holds. Resolving such a difference needs a rule
  (ADR-061 LQ5).
- Otherwise it empties each of the three and carries every GSPO entry into it as it is, with both times, its
  retraction and the time of its last change, past the insert's neighbour rules. It then saves each index's count as
  exact up to the transaction GSPO's is, and logs what each held before and holds now.

The four probes on the fix:

```
rebuild-reads-only-the-default-graph.cs: GPOS, GOSP, TGSP 4 entries each; the named graph through GPOS: 2 rows
rebuild-resolves-an-open-tgsp-entry.cs:  The rebuild changed nothing: GPOS holds 0, GOSP 0 and TGSP 1 entries that
                                         GSPO does not hold as they are. (...)
rebuild-resolves-an-extra-entry.cs:      The rebuild changed nothing: GPOS holds 1, GOSP 0 and TGSP 0 entries (...)
rebuild-keeps-retracted-mark.cs:         "a" retracted, "b" through GSPO and GPOS, after the rebuild and after a reopen
```

The two probes that put a difference into one index now end with the rebuild's refusal, an unhandled exception;
the first state they print is unchanged.

`rebuild-on-a-copy-of-the-live-store.cs`, on a copy of the copy of the live memory store kept in
`~/diag/mcp-omega-snapshot-20261114T0451Z` (ADR-061 M11; the kept copy's hashes checked before and after):

```
profile Cognitive, index state Ready
The rebuild changed nothing: GPOS holds 0, GOSP 0 and TGSP 251 entries that GSPO does not hold as they are. (...)
index state Ready
```

251 is M11's 204 and 47. No other entry of the three differs from GSPO, as M11's count found.
