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

```bash
cd tools/probes/rebuild-indexes
dotnet run --no-cache rebuild-resolves-an-open-tgsp-entry.cs
dotnet run --no-cache rebuild-resolves-an-extra-entry.cs
dotnet run --no-cache rebuild-keeps-retracted-mark.cs
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

**Not measured:** a prune.
