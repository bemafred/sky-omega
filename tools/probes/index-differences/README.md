# Where the live store's index differences come from

[ADR-061](../../../docs/adrs/mercury/ADR-061-a-facts-own-times.md) M11 counted the live memory store's four index
files entry by entry (2026-11-14). GSPO, GPOS and GOSP hold the same periods. TGSP differs from them in two ways:

| Entries | In GSPO | In TGSP |
|---------|---------|---------|
| 204 | forgotten | open, with the same memorized time |
| 47 | no entry at that memorized time | open: 9 repeat a fact GSPO remembers, 38 are later tellings of 32 facts GSPO holds as forgotten |

M11 left open why TGSP was not changed when the others were. These two probes make a store with the code from before
ADR-054 (1.8.22 and earlier), repair it as the live store was repaired on 2026-10-27, and compare its indexes the same
way. The new store holds one entry of each kind.

## Cause

Before ADR-054, each index found "this quad's entry" by looking where the quad's key sorts, in that index's own
order. GSPO, GPOS and GOSP put the quad first in the key, so a quad's entries sit together. TGSP puts the memorized
time first, so a quad's entries are spread among the entries of every other quad.

- **A delete marked nothing in TGSP.** `DeleteCurrent` searched from the smallest possible time (`ValidFrom =
  DateTimeOffset.MinValue`). In TGSP that search lands on the first leaf, and the scan stopped at the first entry that
  sorts after the search key: the first entry. So TGSP marked a quad deleted only when the quad was TGSP's first
  entry. GSPO, GPOS and GOSP marked it.
- **A telling of a quad that was marked deleted was dropped everywhere but in TGSP.** In GSPO, GPOS and GOSP the
  marked entry was the neighbour, and the insert took it for a duplicate because it was still open (ADR-054, defect
  2). In TGSP the neighbour is another quad, so the telling was inserted.
- **A telling of a quad that was remembered was dropped everywhere but in TGSP**, for the same reason (ADR-061 M11,
  fourth row).
- **The ADR-054 repair then ended each marked entry, on each index's own entries.** GSPO, GPOS and GOSP had the marks;
  TGSP had none, so its entries stayed open.

So all three kinds come from TGSP's key order, not from damage to the file:

- **the 204** are the deletes that TGSP did not mark: the repair ended 204 entries in GSPO, and TGSP holds 204 open;
- **the 38** are tellings after a delete, which only TGSP kept (32 facts, some told again more than once);
- **the 9** are tellings of a fact that was still remembered, which only TGSP kept.

On the live store the numbers fit with nothing left over. The repair's count is GSPO's: it found 323 marked entries,
ended 204 and restored 119 (ADR-054). TGSP differs in exactly 204, so it marked none of them. The 119 that were
restored are open in every index, so they agree. No copy of the store from before the repair was kept, so TGSP's
marks there cannot be counted directly.

Today's code has none of these causes. Since ADR-054 a delete finds the quad's entries through GSPO and changes the
same period in every index. Since ADR-061 P1 a write is decided once, from GSPO, and each index applies the decided
change by quad and memorized time.

## Running

`1-write-before-adr-054.cs` runs on the code before ADR-054. It takes a store directory that does not exist yet.
Clone the repository into a scratch directory, check out `1fa836e` (the commit before `3966e42`, ADR-054), copy the
probe there at the same path, and run it there:

```bash
git clone -q --no-checkout <this repository> /tmp/old && git -C /tmp/old checkout -q --detach 1fa836e
mkdir -p /tmp/old/tools/probes/index-differences
cp 1-write-before-adr-054.cs /tmp/old/tools/probes/index-differences/
(cd /tmp/old/tools/probes/index-differences && dotnet run --no-cache 1-write-before-adr-054.cs -- /tmp/diff-store)
dotnet run --no-cache 2-repair-and-compare.cs -- /tmp/diff-store      # here, on today's code
```

`2-repair-and-compare.cs` changes the store it is given (the repair), so give it a copy when the first output is to
be kept. Both probes name their assembly `Mercury.Benchmarks` to see Mercury's internals, and read the store's private
fields by reflection.

## Results on omega, 2026-11-16 (`ea70cbd`, .NET 10.0.9, osx-arm64)

Times are milliseconds after the first telling of `a`. The subjects `a` to `e` were told 3 ms or more apart.

`1-write-before-adr-054.cs`, on `1fa836e`:

```
delete a: True
delete c: True
add c again
add d again
GSPO, 5 entries: a [0, open) marked deleted; b [15, open); c [24, open) marked deleted; d [33, open); e [43, open)
GPOS, 5 entries: a [0, open) marked deleted; b [15, open); c [24, open) marked deleted; d [33, open); e [43, open)
GOSP, 5 entries: a [0, open) marked deleted; b [15, open); c [24, open) marked deleted; d [33, open); e [43, open)
TGSP, 7 entries: a [0, open) marked deleted; b [15, open); c [24, open); c [70, open); d [33, open); d [79, open); e [43, open)
```

`a`, TGSP's first entry, is the control: TGSP marked it. It did not mark `c`. Only TGSP kept the second telling of
`c` and of `d`.

`2-repair-and-compare.cs`, on `ea70cbd`:

```
repair: ended 2, restored 0, left 0
GSPO, 5 entries: a [0, 51); b [15, open); c [24, 61); d [33, open); e [43, open)
GPOS, 5 entries: a [0, 51); b [15, open); c [24, 61); d [33, open); e [43, open)
GOSP, 5 entries: a [0, 51); b [15, open); c [24, 61); d [33, open); e [43, open)
TGSP, 7 entries: a [0, 51); b [15, open); c [24, open); c [70, open); d [33, open); d [79, open); e [43, open)
GPOS equals GSPO: True
GOSP equals GSPO: True
TGSP against GSPO:
  c [24, open): forgotten in GSPO, open in TGSP, same memorized time (the 204)
  c [70, open): no GSPO entry at that memorized time; GSPO holds the fact as forgotten (the 38)
  d [79, open): no GSPO entry at that memorized time; GSPO remembers the fact (the 9)
  GSPO holds c [24, 61) and TGSP does not hold it so
```

The repair counts GSPO's entries: it ended `a` and `c` there. It ended `a` in TGSP too, so `a` agrees. The last line
is the 204's other side: GSPO's closed period of `c`, which TGSP holds open.
