# ADR-066: A Write Waits for the Reads in Progress, Outside the Store's Lock — Forward Progress When SERVICE Crosses an Execution Boundary

## Status

**Status:** Accepted — 2026-11-23

Drafted by zbook on 2026-11-21, from what ADR-065 Step 6 found when it gave every query an HTTP SERVICE executor.
Martin asked for the assessment the same day: "The question is not whether Mercury should support self-SERVICE. It is
whether Mercury can guarantee forward progress when SERVICE crosses an execution boundary." He asked that no special
case be built yet, and for reproducible concurrency tests that show the issue and verify the eventual correction.

First revision, 2026-11-22, after Martin's review. In his words: "The direction is sound. Keep ReaderWriterLockSlim for
now. Do not redesign Mercury's concurrency architecture or introduce MVCC as part of this ADR. [...] The problem is not
the primitive itself. It is holding a store lock across an external execution boundary." He asked for the locking and
lifecycle invariants, the consistency established on the actual execution and publication paths, cancellation
reconciled with ADR-060, the guarantee stated precisely, the open questions resolved, deterministic tests for each
invariant, and an investigation of how much of a query needs the read lock; no implementation yet.

Second revision, the same day, from Martin's reading of the first. The first revision released the read lock for the
SERVICE wait, let writes in, and evaluated the query again when one came. Martin: "It is true that a service call
doesn't read *our* store but it is still a read. From a logical standpoint. The results of the SERVICE call may very
well be matched to *our* store. The fact that we *could* relax things does not at all mean that we *should* do it."
And: "I don't see why an ordinary write that just happens to occur during a read should be allowed priority. We have
no control whatsoever over the when in these situation and no writing client has a clue as to what is going on in the
store." This revision keeps the read whole and changes what a write does while it waits for it. What each revision
changed is in [What the revisions changed](#what-the-revisions-changed).

Martin also decided, on the first revision: "Prune is not intended for a live store. It should be an error." (D8)

Accepted on 2026-11-23. Martin: "ADR-066 is approved in principle. The second revision has the correct architecture."
He decided R1 to R3 and asked for four corrections before the status changed: a wake-up protocol for waiting writers,
with a deterministic test (D1); the closed state checked by readers and writers after they take the lock, with a test
of a close racing a queued write (D2); which callers can cancel (D4); and the order stated as it is, reads first, with
writer starvation accepted (D1, R4). "No further architecture investigation is requested." What acceptance changed is
in [What the revisions changed](#what-the-revisions-changed).

Steps 1 to 4 done and on `main` on 2026-11-23 (Steps 1 to 3 in `216ebbf8`, `25778f45` and `836b6279`). Step 3 is D1
and D2 in `QuadStore` and `QuadStorePool`, with the tests written with the correction. Step 4 is D3 and D4 in
`ServiceCall`; the executor's part of D3 is held with Step 6's executor. Every held test passes.

## Why

ADR-065 D10 gives each query an `HttpSparqlServiceExecutor`, so that a session's mercury-mcp can read the shared store
on elite with `SERVICE <http://elite…:3032/sparql> { … }`. Built on zbook, it works: one store's query reads another
store over HTTP, with every kind of term, named graphs, ASK and `text:match` (measured).

It also makes a query wait on another machine while it holds its own store's read lock. That is a read in progress,
and a write to the store waits for it, as it should. What is wrong is what the lock does with that write while it
waits: it holds every later read of the store behind it. When the wait leads back to the store, through its own
endpoint or another's, the read that would answer is one of those, and nothing moves until the HTTP client gives up.

A self-referential SERVICE is valid SPARQL: the Federated Query recommendation does not require the endpoint to be in
another process. So the question is not which SERVICE to forbid, but what Mercury must change so that a query that
waits on SERVICE holds up only what comes after it: the writes to its store.

## Context

### Measured (zbook, 2026-11-21)

- **A SERVICE wait holds up its store.** During a 3 s SERVICE call, a write to the querying store waited 2.6 s, and a
  plain read issued after the write waited 2.3 s. The write's wait is the read's due (D1); the later read's is the
  defect.
- **A store's SERVICE to itself, with a write queued, deadlocked** until the HTTP client's timeout: the query answered
  after 102 s, with "Query cancelled" (with Step 6's executor as it now is, "did not answer within 100 s").
- **An endpoint that never answers** (`192.0.2.1`) took 100 s, the HttpClient default.
- **A tailnet machine that is off black-holes the connection**: a request to asus, offline for 3 days, timed out after
  130 s, curl's limit. So "elite is off" is the 100 s case, not a fast refusal.
- **The thread pool.** K queries at once, each with a SERVICE to its own process's endpoint and no writes: 16 answered
  in 49 ms, 32 in 0.74 s, 64 in 3.8 s, 128 in 15 s (8 minimum worker threads). None failed; the pool adds threads
  slowly.

### Measured (zbook, 2026-11-22): what the lock does with a write that waits

A read R1 holds a `ReaderWriterLockSlim(NoRecursion)`, as a query waiting on SERVICE does; a write W comes, then a read
R2, as the SERVICE's handler would
([probe](../../../tools/probes/write-waits-for-reads/a-write-that-waits-outside-the-lock.cs)):

| How W waits | R2 waited | W took effect |
|---|--:|---|
| `EnterWriteLock`, as every write enters today | 1,909 ms | when R1 ended (2,001 ms), not before |
| `TryEnterWriteLock(0)`, and between tries a wait outside the lock until a read ends | 0 ms | when R1 ended (2,000 ms), not before |

The probe's wait outside the lock is a semaphore that a read's end releases, polled every 50 ms. It shows what the lock
allows; the store's wake-up is D1's protocol, which loses no wake-up and polls nothing.

The worst case for the second way, a write trying `TryEnterWriteLock(0)` without pause beside a read that holds the
lock and 4 threads of reads entering and leaving
([probe](../../../tools/probes/write-waits-for-reads/a-writes-attempts-and-the-reads-beside-them.cs)): 7,982,964
tries, none entered while a read held the lock; 800,000 reads, each waiting to enter 0 µs at the median, 412 µs at
the 99.9th percentile and 1.9 ms at most, the lock's own spin, not a wait behind the write.

### Measured (zbook, 2026-11-23): what a read's release costs with the wake-up

On `QuadStore` with D1 built, 4 threads each take and release the read lock 200,000 times, timed around each call
([probe](../../../tools/probes/write-waits-for-reads/a-reads-release-with-the-wake-up.cs)); three runs of each:

| A read's release | Median | 99.9th percentile | At most |
|---|--:|--:|--:|
| Before D1 (`25778f45`), no write | < 0.1 µs, the timer's resolution | 26.5–26.8 µs | 0.18–0.34 ms |
| D1, no write | 0.3–0.4 µs | 25.3–28.9 µs | 0.47–0.56 ms |
| D1, a write waiting behind a held read, woken by every release | 0.3–0.4 µs | 41.3–44.6 µs | 0.35–0.40 ms |

The wake-up, the monitor and its `PulseAll`, adds about 0.3 µs to a read's release at the median. With a write
waiting, each release meets the write's try at the monitor: about 15 µs more at the 99.9th percentile. A read's wait to
enter did not grow: 7.7–14.7 µs at the 99.9th percentile with D1, 26.4 µs before.

### Read in the code (2026-11-22)

Line numbers are of the working tree with Step 6's executor (`55648f5f` plus the uncommitted Step 6 files).

**The lock.**
- One `ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion)` per store (`QuadStore.cs:58`), which prefers writers:
  once a writer waits, a new reader waits behind it. No `TryEnter`, no timeout, no upgradeable lock anywhere in `src`.
- Every read enters the lock in one place, `AcquireReadLock` (3431), and leaves it in one, `ReleaseReadLock` (3445).
- The write lock is entered in twelve places, all inside `QuadStore`: `AddCurrent` (620), `DeleteCurrent` (798),
  `RepairDeletes` (847), `BeginBatch` (956, held to `CommitBatch` or `RollbackBatch`), the Reference bulk finalize and
  drain (1488, 1591), the four `RebuildSecondaryIndexes` branches (1721, 1736, 1926, 2096), `Checkpoint` (2833) and
  `Clear` (3517). No caller outside `QuadStore` can take it.
- `SparqlEngine.QueryCore` holds the read lock from the end of parsing to its last row (75–108). An update holds it
  for each WHERE, then releases it and takes the write lock for the changes (`UpdateExecutor.cs:261-273`, 387).
- Mutations outside the write lock: WAL replay at open (before the store is published), `Dispose`'s checkpoint
  (3485), the tail of `FlushToDisk` (1468–1481: flushes, and the counts saved in the index headers; no change of
  content), and `RebuildMphf` (1658: rewrites a Reference store's `.mphf` and `.idx` files; the CLI's
  `--rebuild-mphf`, before it serves anything).

**What a query publishes.** No row reaches a caller before `SparqlEngine.Query` returns. `QueryResult` holds
`List<Dictionary<string,string>>` rows of term strings, built after evaluation ends (`ExecuteSelect`, 349–363); the
HTTP endpoint formats the whole result into a body before it writes it (`SparqlHttpServer.cs:183`, 226–310); the MCP
tools, the REPL and the pipe sessions, and `mercury-sparql` format the complete result after the call. No callback,
`yield`, `IAsyncEnumerable` or streaming writer exists on this path. DESCRIBE reads the store after its WHERE, still
under the lock (`DescribeResults.cs:57-122`). With the read lock held for the whole evaluation, every row comes from
one state of the store.

**SERVICE.**
- `TreeJoinExecutor.ServiceCall` blocks on the HTTP call with `GetAwaiter().GetResult()`, on the query's thread, with
  no token (`TreeJoinExecutor.cs:726`). The executor is one per process (`SparqlEngine.cs:27`), a bare `HttpClient`:
  100 s for a call, no connect timeout of its own.
- The request is the group's text with the prologue's prefixes (699–706); it does not depend on the query's rows. A
  `SERVICE ?ep` makes one call per input row (713–718); under OPTIONAL, EXISTS or `GRAPH ?g` a SERVICE is called once
  per row or graph. No answer is cached anywhere.
- A remote blank node becomes `_:` plus the label as received (`ISparqlServiceExecutor.cs:102`): two calls that both
  answer `_:b0` give blank nodes that compare equal, and a self-SERVICE returns the store's own labels.
- `catch when (silent)` catches every exception the call and its rows can raise, cancellation and a closed store
  included, and answers no solutions (748–755).
- An update's WHERE cannot evaluate SERVICE: its executor has no SERVICE executor and throws
  (`UpdateExecutor.cs:295-296`, `TreeJoinExecutor.cs:689-690`).

**Lifecycle.**
- `QuadStore.Clear` is called by the pool's `Clear(name)`, which prune runs on the store it replaced, after its switch
  (`PruneEngine.cs:113-114`); by prune on its target before the transfer (94); and by `Rent` on a reused store
  (`QuadStorePool.cs:538`), which nothing in `src` calls. The CLI serves its store over HTTP while its REPL runs (on
  unless `--no-http`, `Program.cs:432-455`), so its prune switches and clears a store that its endpoint serves, with
  any query of that endpoint still on it.
- `QuadStore.Dispose` takes no lock (3450–3502) and disposes the lock last; `AcquireReadLock` checks for a closed
  store before it enters the lock, not after (3433). `QuadStorePool.Dispose` disposes its named stores while holding
  `_namedLock` (702–709), the lock `Pool.Active` takes.
- mercuryd's stop (ADR-065 D3) stops the pipe and then the HTTP endpoint, each after the work in progress, and only
  then closes the store (`StoreHost.cs:79-96`; `HttpListenerLoop.StopAsync`, `PipeServer.StopAsync`). Neither drain has
  a bound of its own. mercury-mcp's MCP tool calls are not drained (ADR-065 Step 4), and the CLI disposes its HTTP
  endpoint without draining it (`Mercury.Cli/Program.cs:592-608`): both can close a store under a running query today.
- Prune runs only in the mercury CLI's REPL (`Program.cs:594-595`); the sessions attached to mercuryd and mercury-mcp
  do not offer it (`ReplCommands.None`, `PipeServerHostedService.cs:26`). No other process can have the store open
  meanwhile: the pool holds `store.lock` with `FileShare.None` (`QuadStorePool.cs:219`, 791–814). The CLI's
  `--repair-deletes` already runs "with no HTTP endpoint and no REPL" (`Program.cs:417-418`).

**Cancellation.** The query's token is put in a thread-static (`QueryCancellation`, `SparqlEngine.cs:74`), checked only
in `TriplePatternScan`, not in the lock wait, and not passed to SERVICE. No serving path passes a token: HTTP, MCP,
the REPL, the pipe and `mercury-sparql` all call `Query` without one. `SparqlHttpServerOptions.QueryTimeoutMs` is
declared and not read.

### What a query needs its lock for (measured 2026-11-22)

A query reads the store only in its scans: the triple patterns of each group (index lookups, and atom ids turned into
strings, `QuadStore.cs:3654-3736`), the `text:match` lookup, `GRAPH ?g`'s list of graphs, and DESCRIBE's reads. Joins,
OPTIONAL, UNION, MINUS, BIND, FILTER, VALUES, ORDER BY, GROUP BY, DISTINCT, LIMIT, the SERVICE call and the rows'
drain work on values already copied out.

Measured by subtraction on zbook, 80,000 quads, medians of 15 (each query whole, then its triple patterns alone;
[probe](../../../tools/probes/service-lock-share/what-a-query-needs-the-store-for.cs)):

| Query | Whole, ms | Its patterns alone, ms | The patterns' share |
|---|--:|--:|--:|
| one subject's facts | 0.02 | 0.03 | 100% |
| a class, ordered by value, LIMIT 10 | 7.86 | 7.08 | 90% |
| count per class, ordered | 148 | 158 | 100% |
| a FILTER over values | 110 | 52 | 47% |
| a two-hop join, DISTINCT | 10.8 | 11.2 | 100% |
| labels by substring (`text:match`), ordered | 2.94 | 3.06 | 100% |

So in these shapes most of a query's time under the lock is reading the store; the hold with no bound is the SERVICE
wait. That wait is part of the read: its answer is joined with what the query read and reads after it. The lock stays
as it is, held for the whole read; what changes is how a write waits for it (D1).

### The wait-for graph

Three kinds of wait cross the boundary:

1. A query that holds its store's read lock waits on an endpoint's answer.
2. The endpoint's handler waits to read its store: behind a writer that holds the lock, or behind a writer that waits
   for it.
3. A waiting writer waits for every reader that holds the lock.

A cycle needs (1), and (2) behind a *waiting* writer. A writer that holds the lock waits on nothing outside the store,
so it ends. The measured deadlocks are such cycles:

- **One store:** the query holds R(A) and waits on A's own endpoint; the handler waits behind W(A); W(A) waits for the
  query.
- **Two stores, two queries:** q1 holds R(A) and waits on B; q2 holds R(B) and waits on A; each handler waits behind
  the other store's queued writer. Neither query refers to itself: no rule about self-SERVICE sees this cycle.
- **Through a foreign endpoint:** A's query waits on another product's endpoint, which queries A. Mercury sees only a
  request from a foreign client.

A cycle is broken by removing either edge. Removing (1), releasing the lock for the wait (the first revision), lets a
write take effect inside a read that has begun: the read is then answered from a state after it was asked, or
evaluated again after a write that came later. Removing (2) behind a waiting writer, for every read, keeps every read
whole: no write takes effect during a read, and a write takes effect after the reads in progress when it enters. It
needs no recognition of the request, since no read waits behind a waiting write at all. Its cost is the write's: reads
go first, and a write waits as long as reads keep coming without a gap (R4).

## The six questions

### 1. Can Mercury reliably identify its own endpoints and the stores behind them?

Not by address. A process knows its listener prefixes (`localhost`, `127.0.0.1`, each `--listen` name, and `+` or `*`
for every address the machine has) and the store each serves (the pool's active store). A SERVICE IRI can name the
same endpoint through another name: a DNS alias, `/etc/hosts`, a tailnet name or IP, IPv6, a port forward,
`tailscale serve`, a reverse proxy. `+` and `*` match every local address, which change while the process runs.
Matching gives false negatives for certain.

In-band identity is reliable between Mercury processes: a request header carrying the store's identity, which the
receiving endpoint compares with its own. It does not pass through a foreign endpoint.

So identification can be best-effort, never complete, and the forward-progress guarantee must not depend on it. None
of the four stores read below tries it: where SERVICE runs in-process, the query names it so (`repository:`,
`urn:x-arq:self`), and an HTTP address goes over HTTP. D1 needs none: no read waits behind a waiting write, whoever
sent it.

### 2. Can same-store SERVICE run internally, with SPARQL's semantics?

Yes, if it does what the endpoint does with the request:

- The text sent: `SELECT * WHERE { group }`, with the prologue's prefixes (BASE is resolved before evaluation).
- The endpoint's own dataset (its default graph), not the outer query's dataset, active graph or `INCLUDING FORGOTTEN`.
- A query of its own: its own clock reading for `NOW()`.
- The terms as the endpoint's JSON gives them: blank-node labels, lexical forms, datatypes.

It cannot take the store's lock again: the lock refuses recursion, and the outer query holds it on the same thread.
It would need an entry that evaluates under a lock already held: a second way into the engine.

Over HTTP, with D1, a self-SERVICE reads the state the outer query reads: no write takes effect while the outer query's
read is in progress. So internal execution would gain no semantics, only the HTTP round trip. It is an optimization to
consider after forward progress is guaranteed, and it depends on question 1.

### 3. Should known unsafe self-SERVICE be rejected until internal execution is supported?

No. A rejection by address catches some self-references and misses the aliases, the two-store cycle and the foreign
one: it looks like safety without being safe. Before the correction the safe state is the one before Step 6: SERVICE
refused by every tool, as today on `main`. Step 6 ships with this ADR's correction, not before it (Q3).

### 4. Can the outer query's read-lock lifetime be separated from waiting on SERVICE, whatever the destination?

It can be, and it should not be. The first revision did it, with three conditions: no cursor into the store held across
the call (true, above), a count of the store's exclusive sections to detect a write between the read's parts, and a
lease that kept the store from being cleared or closed between them. A query whose count moved was evaluated again, up
to three times.

That gives the write that arrives during a read the priority: it takes effect inside the read, and the read pays, with
a second evaluation from a later state, a new `NOW()`, and a failure after three writes it knew nothing of. The SERVICE
call is part of the read, and its answer is matched to the store: the read lock spans it. Nothing in the
forward-progress requirement needs the read split; the cycle is broken at the waiting write instead (D1).

### 5. How to bound nested federation and prevent resource-dependency cycles?

- **Locks:** with no read waiting behind a waiting write (D1), and nothing waiting for reads while it holds a lock a
  read may need (D2), no cycle passes through a store's lock (the proof is under [Invariants](#invariants)).
- **Depth:** a Mercury endpoint sends a SERVICE group as it is written, so each hop removes one level of nesting, and
  the depth is the text's. A foreign endpoint can recurse without that bound; its calls hold no Mercury write lock,
  and the timeouts end them.
- **Threads:** each waiting SERVICE holds a pool thread, because evaluation is synchronous on the query's thread (the
  lock is thread-affine, the token thread-static, the pattern tree passed by `ref`). That is a dependency through a
  shared resource, measured above: no deadlock, and latency that grows faster than the number of queries. A cap on
  SERVICE calls in flight would be a new resource that nested in-process SERVICE could cycle on, so it is not proposed.
- **Time:** a connect timeout, short, so that an endpoint that is off fails in seconds (the black-holed tailnet
  machine), and the overall timeout kept for an endpoint that answers slowly (D5).

### 6. SERVICE SILENT, cancellation, and endpoint authorization

- **SILENT is not the recommendation's today.** SPARQL 1.1 Federated Query, §2.3: "The failed `SERVICE` clause is
  treated as if it had a result of a single solution with no bindings"; §3.2 evaluates a failed invocation with
  SilentOp to Ω0, "the solution set with one empty solution". Mercury's `ServiceCall` returns no solutions, and its
  comment rejects Ω0 because it "would silently make the SERVICE optional", which is what the recommendation decides:
  `Join(input, Ω0)` is the input. Two tests hold the wrong answer
  (`Execute_ServiceClause_Silent_ReturnsEmptyOnError`, `Execute_ServiceWithLocalPatterns_Silent_HandlesErrors`), and
  ADR-048 writes "the empty multiset" into its design. The W3C SERVICE tests are skipped as needing the network, so
  none caught it. It came with ADR-047's cutover (`aa826a2`). It is a correction of its own (D7).
- **SILENT covers the endpoint's failures.** §2.3 names them: "the remote service may be down, the service IRI may not
  be dereferenceable, or the endpoint may return an error to the query." The query's own cancellation, and a failure
  of its local evaluation, are not among them, and SILENT must not hide them (D3).
- **Cancellation.** The query's token does not reach the HTTP call today, so a cancelled query waits for its endpoint.
  [ADR-060](ADR-060-query-time-limit-and-cancellation.md) (Deferred) decided the direction on 2026-10-31: a time
  limit beside the caller's token, one deadline for the whole operation, lock waits in bounded slices, "SERVICE and
  LOAD take the linked token, and SILENT does not swallow the query's own end" (its D2). D4 takes that one clause.
- **Authorization.** SERVICE makes every endpoint an HTTP client (D6).

## The guarantee

**What is guaranteed.** A query waiting on SERVICE holds its store's read lock, as for the rest of its read, and
nothing else: no write lock, no lock of its pool or process. While it waits, every read of its store, and every
operation on every other store, proceeds. A write to its store (an update, a batch, a checkpoint, a rebuild, a clear,
a close) waits for it, as for every read in progress, and holds no read behind it (D1, D2). Every wait of the query
ends: the endpoint answers or fails, the connect timeout (D5) or the call's timeout ends the call, or, for a caller
that passed a token, the query is cancelled (D4); and with it ends every write's wait for it. The query is evaluated
once, from one state of its store. A read or write that takes the lock after the store is closed finds it closed and
touches nothing (D2). A prune does not run beside it: a prune of a served store is an error (D8).

**What is not guaranteed.** That a federated query answers: an endpoint may fail. How long a federated query takes: its
calls are bounded one by one, and their number is the query's (one per row for `SERVICE ?ep`). That reads and writes
take effect in the order they arrive: reads go first (D1). When a write takes effect: after every read in progress
when it enters, reads that arrived after it included, so a query's SERVICE waits delay the writes to its store, and
reads that keep coming without a gap delay a write until they stop, without bound (R4). Which of several waiting
writes takes effect first. An endpoint that waits on a write to the querying store before it answers (a foreign
endpoint with a side effect; a Mercury endpoint only reads to answer a query) waits until the call's timeout, since
that write comes after the read (R4). That a query of a serving path can be cancelled: none passes a token (D4). The
thread pool's capacity: each wait holds a thread (question 5). A bound on a stop: the drains wait for the queries in
progress, and the service manager is the bound (ADR-065 D3, R3). Anything about endpoints outside Mercury.

## Decision

The smallest change that guarantees forward progress, with the lock as it is: a read is never held behind a write
that has not entered, and a write never takes effect during a read.

- **D1. A write takes effect only when no read is in progress, and waits for that outside the lock.** The write lock is
  entered in one place, for every exclusive section (today twelve), with `TryEnterWriteLock(0)`: it enters when no
  read and no write holds the lock, and otherwise does not wait inside it. Until it enters, it waits outside the lock,
  woken by every release of the lock, a read's or a write's (the protocol below). A read waits only for a write that
  holds the lock, which is local work (I1), never for one that waits. A query keeps its read lock from the end of
  parsing to its last row, as today, its SERVICE waits included. Measured above: a read behind a waiting write,
  1,909 ms today and 0 ms with D1; the write still after the read.
  - **The order: reads first.** The store does not order reads and writes by their arrival; it prefers reads.
    - A read is answered from one state of the store, the one it finds when it takes the lock, and no write takes
      effect while it runs.
    - A write takes effect at the moment it enters, after every read in progress at that moment, reads that arrived
      after the write included. A read that arrives while a write waits goes first, and is answered from the store
      before the write: the write has not happened.
    - Writes that wait together enter one at a time, in no order the store promises.
    - A caller's own operations keep their order: each returns only after it has taken effect.
    - A write can wait without bound while reads keep overlapping. That writer starvation is accepted (R4).
  - **The wake-up protocol.** Beside the lock, each store has one monitor, and a count of the writes waiting on it.
    - A write takes the monitor and tries `TryEnterWriteLock(0)`. While the try fails, it counts itself waiting and
      waits on the monitor (`Monitor.Wait`, which gives the monitor up while it waits and takes it again before it
      returns), then no longer counts itself and tries again. When a try succeeds, it leaves the monitor holding the
      write lock.
    - Every release of the lock, a read's or a write's, first leaves the lock, then takes the monitor and wakes every
      write waiting on it (`Monitor.PulseAll`). The lock is left in two places only, `ReleaseReadLock` and
      `ReleaseWriteLock`: every exit goes through them, that of a read that finds a committed write not applied, or the
      store closed, included.
    - Several waiting writes: each release wakes them all, and each tries again in turn under the monitor. One enters;
      the others wait again, for the next release, which the one that entered makes when it ends. A read's release
      while other reads still hold the lock wakes them too: their tries fail and they wait again.
    - No wake-up is lost. A write waits only after a try that failed under the monitor. A release that left the lock
      before that try is seen by the try. A release that leaves it after the try wakes no one until it holds the
      monitor, which the write gives up only by waiting: its wake-up finds the write waiting. And a release wakes the
      writes only after it has left the lock, so their next tries do not meet its hold.
    - The monitor adds no wait for the lock: nothing that holds it waits for the lock (`TryEnterWriteLock(0)` does not
      wait), and a read does not take it to enter. A release may wait for it while a write holds it for a try, or
      another release for its wake-up, and neither waits on anything.
    - The count is kept under the monitor and read only under it: a write is counted only while it is in
      `Monitor.Wait`. Nothing decides by it. It is internal, so that a test waits for a state, two writes waiting,
      instead of for a time.
- **D2. `Clear` and `Dispose` are writes, under D1, and the closed state is checked under the lock.** `Clear` waits for
  the reads in progress, then clears. `Dispose` waits like any write, then closes the store under the write lock; the
  lock itself is left to the collector, since `ReaderWriterLockSlim.Dispose` refuses while a thread waits on it. Both
  kinds of lock check the closed state after they are taken, in the one place each is taken: a read in
  `AcquireReadLock` after `EnterReadLock`, beside the check for a committed write not applied; a write in the one
  entry, after its try succeeds. A read or write that finds the store closed releases the lock, through D1's release,
  and fails, "The store is closed", having read or changed nothing. The checks before the lock decide nothing then,
  and go. A `Dispose` that finds the store already closed releases the lock and returns.
  - **A close racing a queued write.** A write that waits while `Dispose` also waits enters before or after it, in no
    order the store promises (D1). Entering first, it takes effect, and the close after it keeps it. Entering after
    the close, it finds the store closed and fails, with nothing written. Either way both end, and neither touches a
    closed store.
  - `QuadStorePool.Dispose` disposes its stores after releasing `_namedLock`: a read in progress may wait on a SERVICE
    handled by the same pool's endpoint, whose `Pool.Active` takes `_namedLock`.
- **D3. SILENT hides the endpoint's failures only**: the call that fails, an answer that is not a success, an answer
  that cannot be read, and the call's own timeouts. Not the query's cancellation, its store being closed, a write the
  store could not apply, nor any failure of local evaluation. The executor says each of the endpoint's failures as a
  `SparqlServiceException`, and SILENT catches that alone, around the call and not the rows made from its answer. An
  IRI that is not an HTTP endpoint is a call that fails ("the service IRI may not be dereferenceable", §2.3). JSON of
  another shape, a member missing or an element of the wrong kind, is an answer that cannot be read, and so is an
  answer in a character set that is not one.
- **D4. The call carries the query's cancellation token**, ADR-060 D2's clause for SERVICE and only that clause. A
  query cancelled during the call ends there, and `QueryCore`'s `finally` releases its read lock. The call takes the
  token from `QueryCancellation`, where `SparqlEngine.Query` puts it for the query's thread, as the scans read it. A
  read's wait for a write that holds the lock, and a write's wait for reads, are ADR-060 D2's bounded slices, later.
  - **Who can cancel.** Only a caller that passes a token to `SparqlEngine.Query`: code that uses Mercury as a
    library, and the tests. No serving path passes one: the HTTP endpoint (`SparqlHttpServer.cs:183`), the MCP tools
    (`MercuryTools.cs:43`), the REPL sessions of the CLI and of the attach pipe (`ReplSessions.cs:42`), and
    `mercury-sparql` (`SparqlTool.cs:105`) call `Query` without a token. Their queries are not cancelled: one that
    waits on SERVICE waits until the endpoint answers or fails, or the call's timeouts end it (D5). A client that goes
    away cancels nothing. Giving them a token (a client's disconnect, a time limit, a stop) is ADR-060's, not this
    ADR's.
- **D5. A connect timeout for SERVICE**, 5 seconds by default, so that an endpoint that is off fails quickly: RDF4J uses
  5 s, Jena 10 s. It is configured in the arguments mercury, mercury-mcp and mercuryd share (`StoreArguments`), for the
  process, since the executor is one per process (R1, decided). The call's overall timeout stays 100 s until ADR-060's
  deadline.
- **D6. SERVICE's outbound boundary is the tailnet's, and it is written down.** Whoever can query a Mercury endpoint
  can make its process send a SPARQL query, as an HTTP GET, to any address the process can reach: the tailnet, the
  internet, and services that listen only on that machine, `localhost` included. ADR-065 D5 makes the tailnet the
  boundary ("We consider this environment safe"); listening on `+` or `*` gives it up, and the help text says so. No
  allow-list.
  - **What a failure says** (R2, decided). An endpoint's failure comes back in the query's error message with its
    status and reason, and the first 4 KiB of its body, cut there; a call that gets no answer says why (refused, the
    connect timeout, the call's timeout). Today the whole body is kept (`HttpSparqlServiceExecutor.cs:98-106`).
- **D7. SILENT answers the recommendation's Ω0**, one empty solution, on a failure D3 lets it hide. Its own correction,
  with the two tests and ADR-048's text, independent of D1 to D6, and in place before Step 6 ships.
- **D8. A prune has its store to itself: a prune of a served store is an error.** Martin's decision, 2026-11-22:
  "Prune is not intended for a live store. It should be an error." A store is live when its process serves it, through
  an endpoint by which anything other than the REPL that asks can read or write it. Only the mercury CLI offers
  `:prune`, and beside its REPL its one way in is its HTTP endpoint: the store lock keeps every other process out. So a
  `:prune` in a CLI that serves its store is refused, with the store unchanged, and the answer says why: a prune is for
  a store that nothing else reads or writes, and it runs in `mercury --no-http`, as `--repair-deletes` already does.
  The smallest form: the CLI offers `ReplCommands.Prune` only when it serves no endpoint, and a session that does not
  offer `:prune` gives that reason. Every session that does not offer it serves its store: the CLI's with HTTP, and
  those attached to mercury-mcp and mercuryd. It removes two faults of today's code: a query that chose the store a
  prune replaces and reads it after the prune cleared it, which reads it empty; and the writes made to the old store
  between the transfer and the switch, which the prune loses. Its own correction, independent of D1 to D7.

### What the revisions changed

The first revision (2026-11-22, after Martin's review):
- It no longer kept SERVICE answers between evaluations: reusing them would have collapsed the repeated calls one
  evaluation makes, and, with blank-node labels copied as received, decided which blank nodes compare equal.
- It stated where its count was advanced, how the lock was taken back, and the lease protocol; the outbound boundary
  and SILENT's Ω0 became decisions; prune of a served store became an error (Martin).
- Two claims of the draft were wrong: an update's WHERE would hold the *read* lock across a wait, not the write lock;
  and ADR-048's bound join does not exist in the code, so no request depends on the query's rows except `SERVICE ?ep`'s
  endpoint.

The second revision (2026-11-22, from Martin's reading of the first):
- **Replaced: its D1 to D3.** They released the read lock for the SERVICE wait, counted the store's exclusive sections,
  evaluated the query again when the count moved (at most three times, then failing with no rows), and gave `Clear`
  and `Dispose` a lease to wait for. A write that arrived during a read took effect inside it, and the read paid for it.
  Now the read keeps its lock, and a write waits for it outside the lock (D1, D2).
- **Gone with them:** the count, the repeated evaluations and their log lines, Q1's three attempts, the leases and the
  refusal of a query on a store being cleared or closed, the "store changed" failure and SILENT's part in it, and the
  cancellation of a lock taken back.
- **Withdrawn: the first revision's R4**, which proposed dropping `Clear`'s lease because nothing calls `Clear` under a
  query and the count would absorb it. Martin: "The fact that we *could* relax things does not at all mean that we
  *should* do it."
- **Kept:** SILENT's scope (D3), the token (D4), the connect timeout (D5), the boundary (D6), Ω0 (D7), prune (D8), and
  the pool's dispose outside `_namedLock` (D2).
- **Renumbered:** the first revision's D4 to D9 are now D3 to D8.

Acceptance (2026-11-23, Martin's four corrections and R1 to R3):
- **The order stated as it is.** The second revision said the order was the arrival's. It is not: a read that arrives
  while a write waits goes before it. D1 now states reader preference, and R4 accepts the writer starvation it allows.
- **The wake-up specified.** The second revision woke a waiting write "each time a read ends". A write that waits
  behind another write was then never woken, and the try and the wait were not said to be one step. D1 now has the
  protocol: every release wakes every waiting write, and the try and the wait are made under one monitor.
- **The closed state checked by both.** The second revision moved the check after the lock for reads only. D2 now
  checks it after the lock for writes too, and says what a write queued during a close does.
- **Who can cancel.** The second revision said a cancelled query ends at its call. D4 now names the callers that can
  cancel, and says that no serving path can.
- **R1 to R3 decided**, in D5, D6 and the stop (below).

## Invariants

- **I1. No wait outside the store under its write lock.** A thread that holds a store's write lock waits on nothing
  outside the store: no HTTP call, no other store, no pool lock. LOAD fetches with no lock (`LoadExecutor.cs:179-214`),
  an update's WHERE cannot call SERVICE, and a batch is held by local code.
- **I2. No write takes effect during a read.** A write enters the lock only when no read holds it (D1).
- **I3. No read waits behind a write that has not entered** (D1). A read waits only for a write that holds the lock.
- **I4. The lock is always released, and every release wakes the waiting writes.** Each hold is taken and released in
  one `try`/`finally`; cancellation and every exception pass through it. Every release goes through `ReleaseReadLock`
  or `ReleaseWriteLock`, which leave the lock and then wake every write waiting (D1).
- **I5. Nothing waits for reads to end while it holds a lock a read may need.** A write waits holding nothing: the
  monitor is given up while it waits. The pool disposes its stores after releasing `_namedLock` (D2).
- **I6. Nothing reads or writes a store's memory after it is closed.** `Dispose` closes under the write lock, after the
  reads in progress, and a read or a write that takes the lock after it finds the store closed before it touches
  anything (D2).

**Why no cycle.** A wait-for cycle needs every party in it to wait on another party of it. A read waits only on a write
that holds the lock (I3), which waits on nothing outside the store (I1): that wait ends. A write that waits holds
nothing (I5), and no read waits on it (I3). A query waits on its SERVICE calls, which end by the endpoint, its timeouts
or the token (D4, D5). A handler of the query's SERVICE, in any process, is a read of its store, and waits only on a
write that holds the lock. So every chain of waits ends in local work or in a SERVICE call, and every SERVICE call
ends. No cycle passes through a store's lock, whatever the destination: itself, a chain, two stores, or a foreign
endpoint that only reads. A foreign endpoint that waits on a write to the querying store before it answers closes a
cycle of its own making, since D1 lets no write take effect during the query's read; the call's timeout ends it (R4).
The wake-up protocol's monitor is in no cycle: nothing waits for the lock while it holds the monitor (D1).

## Admission, draining, pruning, shutdown and disposal

| When | What happens |
|---|---|
| A query starts | It takes the read lock, waiting only for a write that holds it. |
| A query waits on SERVICE | It keeps the read lock (D1). Reads of its store, and every other store, proceed. |
| A write arrives during a read | It waits outside the lock until no read is in progress, then takes effect. Reads that arrive meanwhile go first: they are not held behind it (D1). |
| The store is cleared | `Clear` is a write: it waits for the reads in progress (D2). |
| A prune is asked for | In a CLI that serves its store, refused at once, the store unchanged (D8). Otherwise the REPL that asks is the store's only user, and the store lock keeps every other process out. |
| A process stops | It stops accepting new work before it closes the store: a close is a write, and a read that arrived while it waited would go first (D1). Each process already does: mercuryd and mercury-mcp stop their endpoints in the host, before `StoreHost.RunAsync` closes the store (`StoreHost.cs:79-96`); the CLI disposes its REPL, then its HTTP endpoint, then its pool (`Mercury.Cli/Program.cs:592-605`). It stays the order. |
| mercuryd stops (ADR-065 D3) | The pipe, then the HTTP endpoint, finish their work in progress; a SERVICE back to a stopping endpoint is answered 503 and fails at once. Then `Dispose` finds no read in progress. Unchanged. |
| mercury-mcp stops | Its MCP tool calls are not drained: `Dispose` now waits for a tool call's query to end, at most until its SERVICE calls end, instead of closing under it (R3). |
| The CLI exits | Its HTTP endpoint is disposed without a drain: `Dispose` now waits for an HTTP query instead of closing under it (R3). |
| The pool is disposed | It takes its stores out under `_namedLock` and disposes them after releasing it (I5). |
| A write was queued when the close entered | It finds the store closed when it enters, and fails: "The store is closed", nothing written (D2). |
| A store is closed on the thread that holds its batch | The close is a write, and the lock refuses a thread that already holds it (`LockRecursionException`): the close fails, and the store stays open with its batch; the batch's commit or rollback, then a close, closes it (checked on zbook). Every batch in Mercury ends in its commit or its rollback, on a failure too, so only a caller that does neither meets it. Before D2 the close closed the store, then threw when it disposed the lock it held. |
| A read comes after the close | It finds the store closed: "The store is closed", no rows (I6). |

## The open questions, resolved

- **Q1. No evaluation is repeated.** A write never takes effect during a read (D1), so there is no conflict to retry or
  to observe. The three attempts Martin preferred belonged to the first revision's design, which this one replaces.
- **Q2. A connect timeout of 5 s, configurable** (D5), in `StoreArguments` (R1, decided).
- **Q3. Step 6 does not ship without the correction**, Martin's decision: D1 to D4, with D5 and D7, land with or before
  Step 6's executor. Until then `main` refuses SERVICE in every tool, as it does today.
- **Q4. The outbound boundary is documented** (D6), consistent with ADR-065 D5's trusted tailnet: no allow-list, no
  switch. The error message keeps the endpoint's status and the first 4 KiB of its body (R2, decided).
- **Q5. SILENT answers Ω0**, as the recommendation does, its own correction, independent of the locking one (D7).

## Tests

`tests/Mercury.Tests/Sparql/ServiceForwardProgressTests.cs`, with a gated endpoint of the test's own
(`GatedSparqlEndpoint`): it answers only when the test opens it, and says when it is first asked. The tests and Step
6's executor are held on zbook, not on `main`: the ones that fail today fail until the correction is built.

Of the seven tests Martin asked to keep, one is restated: the first held that a write to the store is not held while
its query waits on SERVICE, which D1 reverses; it now holds that the write waits and that a read after it does not.
Another is renamed, since a store no longer changes during the wait.

Run against today's code on zbook, 2026-11-22, each test method in a process of its own (summary sha256
`d34fe761f11dfb1c…`): of the 23 cases, 4 pass and 19 fail, each at the wait the column names. Every failure is a read
held behind a write that waits, or a deadlock that only the HTTP client's 100 s ends, or a token that does not reach
the call. No case of the coverage test failed at its first check: each of the eleven exclusive sections already waits
for the read in progress today; what each gets wrong is the read it holds behind it.

| Test | Holds | Today | Required |
|---|---|---|---|
| `WhileAQueryWaitsOnService_AWriteToItsStoreWaitsForIt_AndAReadAfterTheWriteDoesNot` | D1, I2, I3 | fails: the read waits behind the waiting write | the write waits; the read is answered at once, from the store before the write; the write takes effect after the query |
| `AStoresServiceToItself_CompletesWithAWriteQueued` | D1, I3: one-store cycle | fails: deadlock to the HTTP timeout | ends |
| `TwoStoresQueryingEachOther_CompleteWithWritesQueuedOnBoth` | D1, I3: two-query cycle | fails: deadlock to the HTTP timeout | both end |
| `ACancelledQuery_StopsWaitingOnItsService` | D4 | fails: the token does not reach the call | "Query cancelled" at once |
| `AWriteQueuedDuringTheWait_IsNotAFailureSilentIgnores` | D3, I2 | passes: the write waits | passes |
| `AQueryReadsOneStateOfItsStore_AcrossItsServiceWait` | I2 | passes: the lock is held | passes |
| `AStoreIsNotClearedUnderAQuery_WaitingOnService` | D2, I2 | passes: the clear waits | passes |
| `AClearOfAStoreWhoseQueryWaitsOnServiceToIt_EndsWithoutADeadlock` | D2, I3 | fails: deadlock to the HTTP timeout (the handler waits behind the clear, the clear for the query) | the clear and the query both end |
| `AReadOfAStoreWhoseClearWaits_IsAnsweredAtOnce_FromTheStoreAsItIs` | D2, I3 | fails: the read waits behind the clear | answered at once, with the store's rows; the clear still waits |
| `AnEndpointStoppedWhileItsQueryWaitsOnService_AnswersIt_AndRefusesItsServiceBackToItself` | ADR-065 D3 | passes | passes |
| `AnExclusiveSectionDuringAServiceWait_WaitsForTheQuery_AndHoldsNoReadBehindIt` (11 cases) | D1, D2: the one entry | fails, all eleven: the read waits behind the section | each waits for the query, and a read beside it is answered at once |
| `ACancelledQuery_LeavesNoLock` | D4, I4 | fails: the token does not reach the call | cancelled; a write and a clear then end at once |
| `ServiceSilent_DoesNotHideTheQuerysCancellation` | D3, D4 | fails: the token does not reach the call | "Query cancelled" |

The eleven cases of the coverage test are the ways an exclusive section begins: `AddCurrent`, `DeleteCurrent`, a batch
committed, a batch rolled back, `INSERT DATA`, `DELETE WHERE`, `LOAD`, `Checkpoint`, `RebuildSecondaryIndexes`,
`RepairDeletes` and `Clear`. The rule is every exclusive section, without exceptions to keep.

Written with the correction, not now, because against today's code they would read a closed store or deadlock the test
host:

- A store disposed while its query waits on SERVICE: `Dispose` waits for the query, the query answers from the store,
  then the store closes; a read after it finds the store closed (D2, I6).
- A pool disposed while a query on one of its stores waits on SERVICE to that pool's own endpoint: the dispose ends
  (I5).
- A reader outside `SparqlEngine.Query` (`GetNamedGraphs`) while `Dispose` runs: it ends before the store closes, or
  finds it closed (I6).

Written with the correction, for the wake-up protocol (D1) and the closed state (D2). Each waits for a state of the
lock, read through the store's count of waiting writes, never for a time, so its outcome does not depend on the
scheduler:

- **Every release wakes the waiting writes, a write's and a read's, and several writes enter one at a time.** A batch,
  begun on a thread of its own, holds the write lock; two writes arrive, and the test waits until the store counts two
  writes waiting. The batch commits: both writes take effect, the second woken by the first's release. Then a read
  holds the lock, a write arrives and is counted waiting, and a second read takes the lock at once beside it (I3). The
  first read ends: the write has not taken effect, since a read still holds the lock. The second ends: the write takes
  effect. A release that wakes no one leaves a write waiting with nothing left to release, and the test fails at its
  bound.
- **A release between a write's failed try and its wait is not lost.** The test's thread holds a read and the store's
  monitor; a write arrives, and the test waits until that thread is blocked. It then releases its read, which leaves the
  lock and wakes no one, since no write waits yet, and gives up the monitor: the write must enter. A write that tried
  outside the monitor would have failed its try before the release, missed the wake-up, and waited for ever.
- **A close racing a queued write.** A read holds the lock; a write and a `Dispose` arrive, and the test waits until
  both are counted waiting. The read ends: the close ends, and the write either took effect and is in the store when it
  is opened again, or failed with "The store is closed" and is not in it. A read after the close finds it closed. Today
  `Dispose` takes no lock and disposes the lock under the waiting write, which throws.

Built with Step 3 (2026-11-23). The three, and the reader outside a query, are in
`tests/Mercury.Tests/Storage/AWriteWaitsOutsideTheLockTests.cs`, which needs no SERVICE and goes to `main` with Step 3;
the store and the pool closed while a query waits on SERVICE are in `ServiceForwardProgressTests`, held with it. Each
part of D1 and D2 was taken out of the code in turn, and a test failed each time: a write's release that wakes no one,
a read's release that wakes no one, the try made outside the monitor, a write that waits inside the lock as today, a
write or a read that does not check for a closed store after the lock (the write then reads a closed store's memory
and the test process dies), and a close that takes no lock. Two parts no test holds:
- That a release wakes the writes only after it has left the lock. A release that woke them first would leave a write
  waiting only when the write's try came before the lock was left, which the scheduler decides; forcing it would need
  a test seam inside the release.
- The pool's close after it has let go of `_namedLock` (I5). Its test passes with the close made holding it too:
  `Active` refuses a pool whose close has begun before it takes the lock, so a SERVICE that comes after the close
  began finds the pool closed. Only a request between that check and the lock would wait, which no test reaches.

Built with Step 4 (2026-11-23): the three cases of D4 pass. On `main`, where no tool sends SERVICE yet,
`Execute_ServiceClause_Silent_ACancelledQueryIsCancelledAtTheCall` holds D3 and D4 with the mock executor: the query's
token is cancelled, and the query is cancelled at the call, not answered with Ω0. The executor's part of D3 is held with
Step 6's executor, its tests in `HttpSparqlServiceExecutorTests`: an IRI that is not an HTTP endpoint, JSON of another shape,
and an answer in a character set that is not one, each a `SparqlServiceException`. Each part was taken out in turn,
and a test failed each time: the call without the token, SILENT catching every failure, the executor without its IRI
check, the parse naming only `JsonException`, and the read without its catch of the character set.

Written with D8, in `Mercury.Hosting.Tests`, which runs `mercury` as a process (`MercuryCli.cs`): a `:prune` in a
mercury that serves its store over HTTP is refused, and the store keeps what it held; with `--no-http` the same
`:prune` completes. Today the first would prune: the CLI offers `:prune` whatever it serves, and `PruneEngine` asks
nothing about the store's users.

## Risks, and Martin's decisions

- **R1. Where the connect timeout is configured.** Decided 2026-11-23, approved: in the shared `StoreArguments`
  configuration, 5 s by default. The executor is one per process, so the setting is one per process (D5).
- **R2. The endpoint's body in the error message** (D6). It helps whoever debugs an endpoint, and it is also how a
  query can read what a service that listens only on that machine answers. Decided 2026-11-23, approved with a bound:
  the message keeps the status and useful error information, and at most 4 KiB of the body. The documentation says
  that SERVICE sends requests to any address reachable from the process, `localhost` included.
- **R3. A stop of mercury-mcp, and the CLI's exit, can now wait** for a query's SERVICE calls, where today they close
  the store under it: each call bounded by D5 and 100 s, while the stop signals are held (ADR-058 Q7). Claude Code
  sends SIGINT and then SIGTERM; the process may stay until those calls end, or until it is killed and the next open
  replays the log. Decided 2026-11-23: accepted as a documented limitation for now. A process stops accepting new work
  before it drains, as each does today (the lifecycle table), so that the close waits only for the work in progress.
  Cancelling the queries in progress at a stop is ADR-060 D6, and this ADR does not take it up.
- **R4. A write's wait, a limit of D1 rather than a decision.** Accepted 2026-11-23: reads go first, and a write can be
  starved. A write waits for every read in progress, and reads that arrive while it waits go first; reads that keep
  overlapping delay it until they stop, without bound. `Clear` and `Dispose` are writes and wait the same way. A
  query's SERVICE waits delay the writes to its store by as long (at most 5 s to connect and 100 s for each call). An
  endpoint that waits on a write to the querying store before it answers waits until the call's timeout. No bound is
  set: a bound would give the write priority over a read in progress. To be recorded in the limits register with the
  probes.
- **R5. Remote blank nodes from different calls compare equal** (`_:b0` from two answers is one node). Not changed by
  this ADR; a correction of its own, kept separate (Martin, 2026-11-23), which would scope each call's labels.
- **R6. Found on the way, outside this ADR and kept separate (Martin, 2026-11-23), not verified by running:** a
  `CommitBatch` that throws leaves the batch
  flag set, so later batched calls pass without the lock and `BeginBatch` refuses for ever; a rollback writes no abort
  marker and leaves its transaction id to the next batch, which recovery may then treat as committed
  (`WriteAheadLog.cs:110`, 746–749); and Solid's `DeleteGraphTriples` takes the read lock while its batch holds the
  write lock, which `NoRecursion` refuses (`ResourceHandler.cs:351`). Each needs its own measurement before anything
  is decided.

## Not part of it, and why

- **No release of the read lock for the wait, no count of exclusive sections, no repeated evaluation, no leases**: the
  first revision's design, replaced (see [What the revisions changed](#what-the-revisions-changed)).
- **No address matching, no internal execution, no rejection.** None is needed for forward progress once D1 holds, and
  each is a second path. Internal execution can be assessed afterwards, as an optimization with its own proof of
  semantics.
- **No change of the locking primitive, no MVCC, no snapshot by time.** Martin's decision. D1 changes how a write waits
  for the lock, through the lock's own `TryEnterWriteLock`, not the lock.
- **No temporary store for SERVICE answers.** ADR-004 (2026-01-03) made SERVICE a materialization boundary: the answer
  loaded into a temporary store, from the pool's rentals (ADR-005, ADR-008), and scanned like a local pattern. ADR-047
  spared it, and ADR-048 rejected it as a second join path and deleted it (`fb05753`, 2026-06-20). It would hold the
  endpoint's answer, not a state of the querying store; and a rental is a resource whose wait has no bound
  (`QuadStorePool.Rent`), which a query that holds one while it waits on its own process could cycle on.
- **No cap on SERVICE calls in flight**, for the reason under question 5. The thread pool's growth is recorded as a
  limit, with its measurement.
- **SERVICE in an update's WHERE stays refused.** The update would write from what it read, with a SERVICE answer
  among it; allowing it is a decision of its own.

## Steps

The tests above are the principal acceptance criteria.

1. **D7, SILENT's Ω0**, with its two tests and ADR-048's text.
2. **D8, a prune of a served store refused**, in the CLI's REPL and the answer of a session that does not offer
   `:prune`, with its test (above).
3. **D1 and D2**, in `QuadStore` and `QuadStorePool`: the one place the write lock is entered, with
   `TryEnterWriteLock(0)` and the wake-up protocol; the two places it is left, `ReleaseReadLock` and
   `ReleaseWriteLock`, which wake the waiting writes; `Clear` and `Dispose` through them; the closed-store check after
   the lock, for reads and writes; the pool's dispose outside `_namedLock`. The coverage test, the rest of the matrix
   that D1 and D2 hold, and the tests written with the correction (above). The attempts probe run again with the
   protocol, for what a read's release costs.
4. **D3 and D4**, in `ServiceCall`: SILENT's scope and the token in the call.
5. **D5**, the connect timeout and its argument (R1), and the error message's 4 KiB bound (R2).
6. **D6** in ADR-065 and the tools' help, `localhost` named, then Step 6's executor on `main`, with this ADR's tests.
   R3 and R4 in the limits register.

## Precedents

Read 2026-11-21 from each project's documentation and its main branch; each claim below was read in the source named.

- **None of the four matches an endpoint's address to its own.** `http://localhost/…` goes over HTTP in all of them.
  In-process SERVICE, where it exists, has a name of its own:
  - GraphDB's `SERVICE <repository:ID>` ("the HTTP transport layer is bypassed and iterators are accessed directly");
    the HTTP URL of the same repository "is also valid but is less efficient"
    ([docs](https://graphdb.ontotext.com/documentation/11.1/sparql-federation.html)).
  - Jena's `SERVICE <urn:x-arq:self>`, only with the optional service-enhancer module, only for the active dataset; it
    runs on the same thread, in the outer query's transaction
    ([ServiceEnhancerInit](https://github.com/apache/jena/blob/main/jena-serviceenhancer/src/main/java/org/apache/jena/sparql/service/enhancer/init/ServiceEnhancerInit.java)).
  - RDF4J only through code, with a fresh connection for each call; Blazegraph not at all.
- **All four give a reader a view that writers do not wait on.** TDB2 and Jena's in-memory dataset take many readers
  and one writer, and a read sees the state before a later commit
  ([Jena transactions](https://jena.apache.org/documentation/txn/)); RDF4J's stores read a snapshot by default
  ([isolation levels](https://rdf4j.org/documentation/programming/repository/#transaction-isolation-levels)); Blazegraph
  is MVCC, "readers never block for writers"; GraphDB reads committed, and its updates "do not block read requests".
  So the cycle through a queued writer does not arise from their storage. In each, a read is answered from the state
  it began with, whatever commits during it. Mercury, with no snapshot, keeps the same order by making the write wait
  for the read (D1), and the cycle by not letting the waiting write hold reads.
- **What they do hit is the thread pool.** Jena [#3837](https://github.com/apache/jena/issues/3837) (open): a cancelled
  query does not cancel its SERVICE calls, and with SERVICE back to the same Fuseki the maintainer got the server "to
  deadlock sometimes" on Jetty's 20-thread pool. A pool with a fixed size turns question 5's slowdown into a deadlock,
  which is why no cap is proposed.
- **No nesting depth limit or recursion detection** was found in any of them.
- **Timeouts.** RDF4J: 5 s to connect and 1 h for the socket, since 5.1.0
  ([#5124](https://github.com/eclipse-rdf4j/rdf4j/issues/5124)). Jena: 10 s to connect, no overall limit unless
  `arq:httpQueryTimeout` is set. Blazegraph: no limit for a SERVICE. Cancellation reaches the SERVICE call in
  Blazegraph and RDF4J, not yet in Jena.
- **Authorization.** Jena switches SERVICE off with `arq:httpServiceAllowed` (on by default), for a server, a dataset
  or an endpoint. Blazegraph has an allow-list of IRIs, off by default. GraphDB checks the user's access only on
  `repository:`. RDF4J has nothing built in; [#5990](https://github.com/eclipse-rdf4j/rdf4j/issues/5990) (open)
  proposes an SSRF policy for its resolver.
- **SILENT.** Jena, RDF4J and Blazegraph answer a failed SILENT SERVICE with the input solution, timeouts included, as
  the recommendation does.

## References

- [ADR-065](ADR-065-mercuryd.md) D3, D5, D10 and Step 6.
- [ADR-060](ADR-060-query-time-limit-and-cancellation.md) (Deferred): the query's time limit and cancellation; D2's
  SERVICE clause is D4 here, and its bounded lock waits will bound D1's.
- [ADR-048](ADR-048-service-federation-efficiency.md): SERVICE's design, SILENT's "empty multiset" among it (D7).
- [ADR-058](ADR-058-wal-replay-restores-consistent-store.md): the log replayed at an open, after a stop that was
  killed.
- [ADR-064](ADR-064-a-facts-states.md): the Graph profile's fact states. [ADR-061](ADR-061-a-facts-own-times.md): memory time.
- The probes: a query's lock share, `tools/probes/service-lock-share/what-a-query-needs-the-store-for.cs`; how a
  write waits, `tools/probes/write-waits-for-reads/`.
