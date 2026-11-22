# ADR-066: SERVICE Waits Outside the Store's Lock — Forward Progress When SERVICE Crosses an Execution Boundary

## Status

**Status:** Proposed — 2026-11-21

Drafted by zbook on 2026-11-21, from what ADR-065 Step 6 found when it gave every query an HTTP SERVICE executor.
Martin asked for the assessment the same day: "The question is not whether Mercury should support self-SERVICE. It is
whether Mercury can guarantee forward progress when SERVICE crosses an execution boundary." He asked that no special
case be built yet, and for reproducible concurrency tests that show the issue and verify the eventual correction.

## Why

ADR-065 D10 gives each query an `HttpSparqlServiceExecutor`, so that a session's mercury-mcp can read the shared store
on elite with `SERVICE <http://elite…:3032/sparql> { … }`. Built on zbook, it works: one store's query reads another
store over HTTP, with every kind of term, named graphs, ASK and `text:match` (measured).

It also makes a query wait on another machine while it holds its own store's read lock. That wait can stop a store,
and when the wait leads back to a store whose lock is in the chain, nothing moves until the HTTP client gives up.

A self-referential SERVICE is valid SPARQL: the Federated Query recommendation does not require the endpoint to be in
another process. So the question is not which SERVICE to forbid, but what Mercury must change so that a query that
waits on SERVICE never stops the progress of anything else.

## Context

### Measured (zbook, 2026-11-21)

- **A SERVICE wait holds up its store.** During a 3 s SERVICE call, a write to the querying store waited 2.6 s, and a
  plain read issued after the write waited 2.3 s.
- **A store's SERVICE to itself, with a write queued, deadlocked** until the HTTP client's timeout: the query answered
  after 102 s, with "Query cancelled".
- **An endpoint that never answers** (`192.0.2.1`) took 100 s, the HttpClient default, and also said "Query cancelled".
- **A tailnet machine that is off black-holes the connection**: a request to asus, offline for 3 days, timed out after
  130 s, curl's limit. So "elite is off" is the 100 s case, not a fast refusal.
- **The thread pool.** K queries at once, each with a SERVICE to its own process's endpoint and no writes: 16 answered
  in 49 ms, 32 in 0.74 s, 64 in 3.8 s, 128 in 15 s (8 minimum worker threads). None failed; the pool adds threads
  slowly.
- **The concurrency tests below**, against today's code: four fail, three pass (`ServiceForwardProgressTests`).

### What the code does

- **One lock per store.** `QuadStore` has one `ReaderWriterLockSlim` (no recursion), which prefers writers: once a
  writer waits, a new reader waits behind it. `SparqlEngine.QueryCore` holds the read lock from the end of parsing to
  the last row.
- **SERVICE runs inside that hold.** `TreeJoinExecutor.ServiceCall` waits on the HTTP call with
  `GetAwaiter().GetResult()`, on the query's thread, with no cancellation token.
- **At a SERVICE call the query holds no cursor into the store.** `EvalGroupPatterns` flushes each run of triple
  patterns before the next operator; EXISTS runs over materialized rows; the aggregate fold and the LIMIT pushed into a
  scan exclude SERVICE (`IsFlatBgp`, `IsPureBgp`). What the query holds at the call is strings.
- **There is no store-wide snapshot.** A Cognitive store's default read is the periods with no forgotten time
  (`QueryCurrent` reads no clock): a forget between two reads changes what the second sees. A Graph store numbers each
  fact's states (ADR-064); Minimal and Reference keep no history.
- **There is no store-wide count of writes.** The WAL's `CurrentTxId` counts write transactions, but some stores have
  no WAL, and `Clear` clears it.
- **A prune switches, then clears.** `PruneEngine` calls `pool.Switch`, then `Clear` on the store that was active.
  `Clear` takes the write lock, so today a query keeps the old store's contents until it ends. `Dispose` takes no
  lock.
- **One process per store** (ADR-065): a store's lock is in one process, so the same store is always the same process.
- **The HTTP endpoint** answers a query with `SparqlEngine.Query`, synchronously, on a thread-pool thread.

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

A cycle is broken by removing either edge. Removing (2) behind a waiting writer (readers that do not queue) leaves
writers to starve under steady reads, and works only for requests Mercury can recognize. Removing (1) breaks every
cycle through a store's lock, whatever the destination.

## The six questions

### 1. Can Mercury reliably identify its own endpoints and the stores behind them?

Not by address. A process knows its listener prefixes (`localhost`, `127.0.0.1`, each `--listen` name, and `+` or `*`
for every address the machine has) and the store each serves (the pool's active store, which a prune switches). A
SERVICE IRI can name the same endpoint through another name: a DNS alias, `/etc/hosts`, a tailnet name or IP, IPv6, a
port forward, `tailscale serve`, a reverse proxy. `+` and `*` match every local address, which change while the
process runs. Matching gives false negatives for certain.

In-band identity is reliable between Mercury processes: a request header carrying the store's identity, which the
receiving endpoint compares with its own. It does not pass through a foreign endpoint.

So identification can be best-effort, never complete, and the forward-progress guarantee must not depend on it. None
of the four stores read below tries it: where SERVICE runs in-process, the query names it so (`repository:`,
`urn:x-arq:self`), and an HTTP address goes over HTTP.

### 2. Can same-store SERVICE run internally, with SPARQL's semantics?

Yes, if it does what the endpoint does with the request:

- The text sent: `SELECT * WHERE { group }`, with the prologue's prefixes (BASE is resolved before evaluation).
- The endpoint's own dataset (its default graph), not the outer query's dataset, active graph or `INCLUDING FORGOTTEN`.
- A query of its own: its own clock reading for `NOW()`.
- The terms as the endpoint's JSON gives them: blank-node labels, lexical forms, datatypes.
- The store the endpoint serves at that moment, which after a prune switch is not the outer query's store.

It cannot take the store's lock again: the lock refuses recursion, and the outer query holds it on the same thread.
It would need an entry that evaluates under a lock already held: a second way into the engine.

Internally, self-SERVICE would read the outer query's own state of the store, which over HTTP it does not once the lock
is released for the wait (below). That is a semantic gain, not a requirement: the recommendation promises nothing about
two endpoints' states. Internal execution is an optimization to consider after forward progress is guaranteed, and it
depends on question 1.

### 3. Should known unsafe self-SERVICE be rejected until internal execution is supported?

No. A rejection by address catches some self-references and misses the aliases, the two-store cycle and the foreign
one: it looks like safety without being safe. Before the correction the safe state is the one before Step 6: SERVICE
refused by every tool, as today on `main`. Step 6's executor and this ADR's correction should land together.

### 4. Can the outer query's read-lock lifetime be separated from waiting on SERVICE, whatever the destination?

Yes, for every profile, with three conditions:

- **Memory safety** holds already: at the call the query holds only strings (above).
- **One state per query.** A query that releases the lock and takes it again may find the store changed. Without a
  store-wide snapshot, it must know that it changed: a count of the store's exclusive sections, moved by every write,
  batch, clear, rebuild and repair, read when the query takes the lock and compared each time it takes it back. A
  query that finds it moved has read two states, and is evaluated again.
- **The store is kept.** A prune's `Clear`, or `Dispose`, between two phases would leave the query a cleared or closed
  store. A query in progress holds a lease that `Clear` and `Dispose` wait for, as they wait for its read lock today.

A Cognitive store could instead pin the query to its clock reading: read the periods memorized by it and not forgotten
by it. That holds without a second evaluation, but only on one profile of four, and it changes the default read of
every query.

### 5. How to bound nested federation and prevent resource-dependency cycles?

- **Locks:** with the wait outside the lock, no cycle passes through a store's lock.
- **Depth:** a Mercury endpoint sends a SERVICE group as it is written, so each hop removes one level of nesting, and
  the depth is the text's. A foreign endpoint can recurse without that bound; its calls hold no Mercury lock, and the
  timeouts end them.
- **Threads:** each waiting SERVICE holds a pool thread, because evaluation is synchronous (ref structs over the store's
  memory). That is a dependency through a shared resource, measured above: no deadlock, and latency that grows faster
  than the number of queries. A cap on SERVICE calls in flight would be a new resource that nested in-process SERVICE
  could cycle on, so it is not proposed.
- **Time:** a connect timeout, short, so that an endpoint that is off fails in seconds (the black-holed tailnet
  machine), and the overall timeout kept for an endpoint that answers slowly.

### 6. SERVICE SILENT, cancellation, and endpoint authorization

- **SILENT is not the recommendation's today.** SPARQL 1.1 Federated Query, §2.3: "The failed `SERVICE` clause is
  treated as if it had a result of a single solution with no bindings"; §3.2 evaluates a failed invocation with
  SilentOp to Ω0, "the solution set with one empty solution". Mercury's `ServiceCall` returns no solutions, and its
  comment rejects Ω0 because it "would silently make the SERVICE optional", which is what the recommendation decides:
  `Join(input, Ω0)` is the input. Two tests hold the wrong answer
  (`Execute_ServiceClause_Silent_ReturnsEmptyOnError`, `Execute_ServiceWithLocalPatterns_Silent_HandlesErrors`), and
  ADR-048 writes "the empty multiset" into its design. The W3C SERVICE tests are skipped as needing the network, so
  none caught it. It came with ADR-047's cutover (`aa826a2`). It is a correction of its own, independent of D1 (Q5).
- **SILENT covers the endpoint's failures.** §2.3 names them: "the remote service may be down, the service IRI may not
  be dereferenceable, or the endpoint may return an error to the query." A store that changed during the wait, or
  closed, is not one of them, and SILENT must not hide it: a query whose SILENT SERVICE swallowed a "store changed"
  signal would answer from two states, or from one solution that stands for a failure that did not happen.
- **Cancellation.** The query's token does not reach the HTTP call today, so a cancelled query waits for its endpoint
  (test 4). The HTTP server passes no token to the query either, and a client that goes away leaves its query running.
  `SparqlHttpServerOptions.QueryTimeoutMs` is declared and not used. [ADR-060](ADR-060-query-time-limit-and-cancellation.md)
  found all of this on 2026-10-31 and decided the direction (a time limit beside the caller's token, one deadline for
  the whole operation, the linked token in SERVICE and LOAD, SILENT never swallowing the query's own end); it is
  deferred. Its Q7 records that `HttpListener` cannot tell a server that its client has gone. With D1, a query waits
  on its endpoint and then on its store's lock again: ADR-060's deadline would bound both.
- **Authorization.** SERVICE makes every endpoint an HTTP client: whoever can query mercuryd can make it read every
  endpoint it can reach, the ones that listen only on its own machine included. ADR-065 D10 accepted this on the
  tailnet (D5); a restriction is a decision of its own. Internal execution, if it comes, must apply whatever check the
  HTTP path applies.

## Decision (proposed)

The smallest change that guarantees forward progress:

- **D1. A query never holds its store's read lock while it waits outside the store.** `ServiceCall` releases the read
  lock for the call and takes it again after, on the same thread.
- **D2. A query reads one state of its store.** The store counts its exclusive sections. A query that takes its lock
  back and finds the count moved is evaluated again, keeping the answers its SERVICE calls already received for the
  same endpoint and request: an endpoint's answer is the endpoint's, not a state of this store. After three
  evaluations that each found the store changed, it fails, saying so.
- **D3. A store is kept for a query in progress.** `Clear` and `Dispose` wait for the queries between their phases.
- **D4. SILENT hides the endpoint's failures only.** The store's change, and its closing, pass through it.
- **D5. The call carries the query's cancellation token.** This is ADR-060 D2's clause for SERVICE ("SERVICE and LOAD
  take the linked token, and SILENT does not swallow the query's own end"), and only that clause: ADR-060's time limit,
  its outcomes and its entry points stay deferred with it.
- **D6. A connect timeout for SERVICE**, a few seconds, so that an endpoint that is off fails quickly: RDF4J uses 5 s,
  Jena 10 s.

Not part of it, and why:

- **No address matching, no internal execution, no rejection.** None is needed for forward progress once D1 holds, and
  each is a second path. Internal execution can be assessed afterwards, as an optimization with its own proof of
  semantics.
- **No snapshot by time.** It would serve one profile and change every query's read.
- **No temporary store for SERVICE answers.** ADR-004 (2026-01-03) made SERVICE a materialization boundary: the
  answer loaded into a temporary store, from the pool's rentals (ADR-005, ADR-008), and scanned like a local pattern.
  ADR-047 spared it, and ADR-048 rejected it as a second join path and deleted it (`fb05753`, 2026-06-20). The pool's
  rentals remain, with no SERVICE user. It would not help here:
  - It holds the endpoint's answer, not a state of the querying store. As built, the fetch ran inside the query's
    execution, under the caller's read lock, so it did not move the wait.
  - Fetched before the lock, it would cover only a SERVICE whose request does not depend on the query's rows. ADR-048's
    bound join builds the request from those rows, and `SERVICE ?ep` takes its endpoint from them. That is the
    "fetch before the lock" path again, with a store of its own.
  - A rental is a resource of fixed size whose wait has no bound (`QuadStorePool.Rent` waits on its gate with no
    timeout). A query that holds a rental while it waits on a SERVICE whose handler, in the same process, needs one, is
    the cycle question 5 refuses to build: only the HTTP timeout would end it.
  - D2's kept answers are the same snapshot of an endpoint's answer, in memory, for one query. Should answers outgrow
    memory, that is ADR-048's bounded acceptance.
- **No cap on SERVICE calls in flight**, for the reason under question 5. The thread pool's growth is recorded as a
  limit, with its measurement.
- **An update's WHERE keeps refusing SERVICE.** It would hold the write lock across the wait, and stop the store.

## Consequences

- No wait through a store's lock can form a cycle, whatever the SERVICE destination: itself, a chain, two queries, or
  a foreign endpoint.
- Writes to a store, and the reads after them, no longer wait on another machine.
- A query with SERVICE may be evaluated more than once while its store is written. Its local work is repeated, its
  endpoints are not asked again for the same request, and after three tries it fails with a reason.
- A self-SERVICE reads the store's state at the moment of its call, not the outer query's state, as SERVICE to any
  other endpoint does.
- A prune's `Clear` still waits for the queries on the old store, as today.

## Tests

The tests, and ADR-065 Step 6's HTTP executor they run against, are held on zbook, not on `main`: four of them fail
until the correction is built (2026-11-22).

`tests/Mercury.Tests/Sparql/ServiceForwardProgressTests.cs`, with a gated endpoint of the test's own
(`GatedSparqlEndpoint`) that answers only when the test opens it. Run against today's code on zbook (log sha256
`7f752fda9177d7d2…`):

| Test | Today | What it holds |
|---|---|---|
| `WhileAQueryWaitsOnService_AWriteToItsStoreIsNotHeld` | fails, 10 s | D1 |
| `AStoresServiceToItself_CompletesWithAWriteQueued` | fails, deadlock until 101 s | D1, the one-store cycle |
| `TwoStoresQueryingEachOther_CompleteWithWritesQueuedOnBoth` | fails, deadlock until 101 s | D1, the two-query cycle |
| `ACancelledQuery_StopsWaitingOnItsService` | fails, 10 s | D5 |
| `AStoreChangedDuringTheWait_IsNotAFailureSilentIgnores` | passes | D4: guards the correction |
| `AQueryReadsOneStateOfItsStore_AcrossItsServiceWait` | passes | D2: guards the correction |
| `AStoreIsNotClearedUnderAQuery_WaitingOnService` | passes | D3: guards the correction |

The last three pass today because the lock is held through the wait. Each fails under a correction that releases the
lock without the decision it names.

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
  So the cycle through a queued writer does not arise from their storage. It arises from Mercury's, which has no
  snapshot: D1 and D2 are Mercury's way to the same property.
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
  the recommendation does (below). Jena and Blazegraph also hide a SERVICE that is switched off or not on the
  allow-list.

## Open

- **Q1.** D2's bound of three evaluations, and whether a query should fail at the first change instead.
- **Q2.** D6's connect timeout.
- **Q3.** Whether Step 6 waits for this ADR, as question 3 proposes, or ships behind it.
- **Q4.** Authorization of SERVICE targets on a network-facing endpoint (question 6): Jena's switch, Blazegraph's
  allow-list, or nothing beyond the tailnet (ADR-065 D5).
- **Q5.** SILENT's answer, Ω0 as the recommendation says: its own correction, with the two tests and ADR-048's text
  changed, before or after this ADR.

## References

- [ADR-065](ADR-065-mercuryd.md) D10 and Step 6.
- [ADR-048](ADR-048-service-federation-efficiency.md): the VALUES bound join, which makes a SERVICE request depend on
  the query's rows.
- [ADR-060](ADR-060-query-time-limit-and-cancellation.md) (Deferred): the query's time limit and cancellation, SERVICE's
  token and SILENT among them.
- [ADR-064](ADR-064-a-facts-states.md): the Graph profile's fact states. [ADR-061](ADR-061-a-facts-own-times.md): memory time.
