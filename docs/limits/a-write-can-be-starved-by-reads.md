# Limit: A write can wait without bound while reads keep overlapping

**Status:**        Latent
**Surfaced:**      2026-11-23, by [ADR-066](../adrs/mercury/ADR-066-a-write-waits-for-the-reads-in-progress.md) R4, a
                   limit of its D1, accepted by Martin the same day.
**Last reviewed:** 2026-11-23
**Promotes to:**   an ADR when a write is seen waiting behind reads for longer than its caller can bear: a client's
                   update that times out while the store answers queries, or a stop whose close does not come.

## Description

A write takes effect only when no read is in progress, and waits outside the lock until then (ADR-066 D1). Reads that
arrive while it waits go first: they are answered from the store before the write. Reads that keep overlapping delay
it until they stop, without bound. `Clear` and `Dispose` are writes and wait the same way.

No bound is set: a bound would give the write priority over a read in progress, which is answered as of when it was
asked.

## Trigger condition

- A store whose reads overlap without a gap: many clients querying at once, or queries that each wait on SERVICE.
- A query's SERVICE waits delay the writes to its store by as long: at most 5 s to connect and 100 s for each call.
- An endpoint that waits on a write to the store that queries it before it answers waits until the call's timeout.

## Current state

- Measured on zbook, 2026-11-22, with
  [tools/probes/write-waits-for-reads](../../tools/probes/write-waits-for-reads/): with the write waiting outside the
  lock, no try entered while a read held the lock, and a read waited 0 µs to enter at the median; the write entered
  when the read ended, not before.
- Measured 2026-11-23, with `a-reads-release-with-the-wake-up.cs` there: a read's release costs 0.3 to 0.4 µs at the
  median with the wake-up, and about 15 µs more at the 99.9th percentile while a write waits.
- No serving path has seen a write starved; the store's users today are one session's queries and writes.

## Candidate mitigations

- **A write's wait in bounded slices, with the caller's time limit** (ADR-060 D2), so that a write that waits too long
  fails instead of waiting on.
- **Reads that see a write waiting wait for it**, the lock's own order: rejected by ADR-066, since a read would then wait
  behind a write that waits on that read's SERVICE.

## References

- [ADR-066](../adrs/mercury/ADR-066-a-write-waits-for-the-reads-in-progress.md) D1, R4, the measured sections.
- [ADR-060](../adrs/mercury/ADR-060-query-time-limit-and-cancellation.md) D2.
