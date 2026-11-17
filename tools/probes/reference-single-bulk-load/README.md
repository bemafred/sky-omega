# A second load into a Reference store

ADR-064 D10, Martin, 2026-11-16: a Reference store takes one bulk load, whatever its atom store. "It should stop,
yes, it is read only by design."

`a-second-load-into-a-reference-store.cs` loads four Reference stores once, two triples each, in bulk mode, and
closes them. Then it loads a third triple into each a second time, with a hash or a sorted atom store, opened in bulk
mode or opened to be queried. It prints what GSPO and GPOS answer before and after.

```
dotnet run --no-cache a-second-load-into-a-reference-store.cs
```

## On `f13407c`, before the fix (omega, 2026-11-17)

| Atom store | Second load | Result |
|------------|-------------|--------|
| Hash | opened in bulk mode | taken: GSPO holds the third triple, GPOS does not |
| Hash | opened to be queried, then a batch | taken: GSPO holds the third triple, GPOS does not |
| Sorted | opened in bulk mode | refused (ADR-034 D7) |
| Sorted | opened to be queried, then a batch | `SynchronizationLockException`, see below |

- **Hash:** a store that holds its load takes another both ways, and GSPO and GPOS then disagree: the third triple
  is found by its subject and not by its predicate. ADR-064 named the first way; the probe found the second.
- **Sorted, opened to be queried:** inside the batch, `AddCurrentBatched` threw `NotSupportedException` ("SortedAtomStore
  is read-only; new atoms cannot be added at runtime"). The probe disposed the store with the batch still holding the
  write lock, and that threw `SynchronizationLockException` over the first exception. A batch of facts made only of
  atoms the store holds would have been taken into GSPO as in the hash case.

## After the fix

Each of the four is refused with `ProfileCapabilityException` naming ADR-064 D10, and the store answers as it did:
two triples, the same in GSPO and GPOS. A store that holds atoms when it opens holds its load: it refuses bulk mode
at open, and `BeginBatch` while it is open. One load may still be many batches of one session.

## Seen, not changed

Disposing a store while a batch holds the write lock throws `SynchronizationLockException`, which hides the
exception that ended the batch. A caller that rolls back in its `catch` does not meet it. It is outside ADR-064.
