# ADR-060: A Query's Time Limit and Its Cancellation — The Caller's Token and the Query's Limit, Linked by the Engine; REGEX and REPLACE Get the Time That Remains

## Status

**Status:** Proposed — 2026-10-31

Found by CI: on run 41203456690 (2026-10-31, `46b8db8`), the macOS x64 runner failed
`Adr049FunctionReconciliationProbeTests.Functions_ProduceConformantForms`, because
`REPLACE("abracadabra", "a", "X")` threw `RegexMatchTimeoutException` instead of returning `"XbrXcXdXbrX"`.
Every REGEX and REPLACE regex has a fixed 100 ms clock limit, and on a loaded three-core runner a trivial match
took longer. Martin's decisions, 2026-10-31: tie the regex limit to the query's own time limit, and use .NET's
cancellation patterns ("cancellations can be combined, like a real cancellation coupled to a timeout
cancellation"). Investigating showed that no query has a time limit today.

Martin's assessment of the first draft (`1518dae`), 2026-10-31: the direction is approved, the cancellation
architecture is sound, and the production implementation is not yet approved. Before Accepted: revise D4 (regex
time-limit semantics) and D7 (the regex test was not deterministic), and cover the whole query with its deadline.
This revision does that. The assessment's claims were checked against the code and with two probes
(`tools/probes/query-time-limit/`); where the code already behaves as the assessment asks, the ADR now says so and
makes it an invariant.

## Context

### What the engine does today (`1518dae`)

- `SparqlEngine.Query` and `SparqlEngine.Update` take `CancellationToken ct = default`
  ([SparqlEngine.cs](../../../src/Mercury/SparqlEngine.cs)). The token is kept per thread in `QueryCancellation`
  (`Operators/QueryCancellation.cs`), since a query runs synchronously on the caller's thread.
- `QueryCore` sets the token, then takes the store's read lock, and only then enters the `try` whose `finally`
  clears it. If taking the lock throws (`ObjectDisposedException` from a disposed store), the token stays on the
  thread. The `finally` sets the token to `default` rather than restoring what was there before.
- The scans poll the token on every row (`TriplePatternScan`, ten sites, property paths included), and
  `UpdateExecutor.ExecuteSequence` polls it between operations. Nothing else does: sorting (ORDER BY), grouping and
  aggregates, DISTINCT, joins of materialized rows, and the solution-modifier layer (`QueryResults*.cs`,
  `TreeJoinExecutor`) run without a check.
- **The read lock.** A query takes `QuadStore`'s `ReaderWriterLockSlim` (`NoRecursion`) with `EnterReadLock`, which
  waits without a bound. Writers take precedence: "If one or more threads are queued to enter write mode, a thread
  that calls the TryEnterReadLock method blocks until those threads have either timed out or entered write mode and
  then exited from it" ([`TryEnterReadLock`](https://learn.microsoft.com/dotnet/api/system.threading.readerwriterlockslim.tryenterreadlock)).
  So a query waits behind every queued write, and while it holds the lock it blocks every write to that store. An
  update takes the read lock for its WHERE and the write lock (`BeginBatch` → `EnterWriteLock`) for its writes, both
  unbounded. `TryEnterReadLock(TimeSpan)` bounds a wait, but no overload observes a `CancellationToken`.
- **SERVICE** (`TreeJoinExecutor.ServiceCall`) calls `ExecuteSelectAsync(endpoint, query)` without a token and
  blocks on it. With SILENT, `catch when (silent)` catches every exception, so once the call is given the query's
  token, a cancellation or timeout inside it would turn into an empty result and the query would go on.
- **LOAD** (`UpdateExecutor.ExecuteLoad`) calls `LoadExecutor.ExecuteAsync` without a token; its own
  `LoadTimeout` defaults to 5 minutes.
- **Outcomes.** `QueryCore` catches `OperationCanceledException` as "Query cancelled", and any other exception as a
  failed result with the exception's message. The results carry only that string (`QueryResult`, `UpdateResult` in
  `Mercury.Abstractions/Results.cs`), so a caller cannot tell a timeout from a cancellation or from any other
  failure.
- **Updates.** A cancellation between operations reaches `SparqlEngine.Update`, which reports "Update cancelled"
  with `AffectedCount` 0, although the operations before it were committed. A failed operation, by contrast, reports
  the count of the earlier operations and says the later ones were not run. The `catch (Exception)` blocks in
  `UpdateExecutor` wrap only store writes; WHERE evaluation runs outside them.
- **Expression errors do not swallow a regex timeout.** Measured with `sparql-regex.cs`: a regex that times out in
  FILTER, in BIND, in a SELECT expression, in a FILTER inside OPTIONAL and in REPLACE inside BIND fails the whole
  query with the regex's message. No row is dropped or left unbound while the query reports success.
- REGEX and REPLACE (`FilterEvaluator.Functions.cs`) build a new `Regex` on every call, with
  `TimeSpan.FromMilliseconds(100)` (line 119). That limit measures the machine's load as much as the pattern's
  cost: it failed a trivial match on a busy runner.
- **REPLACE follows .NET's replacement syntax, not XPath's.** The replacement string is passed to
  `Regex.Replace(string, string)` unchanged (with the q flag, `$` is doubled). Measured with `sparql-regex.cs`,
  `REPLACE("abc", pattern, replacement)`:

  | Pattern | Replacement (characters) | Mercury | XPath `fn:replace` (F&O 3.1 §5.6.4) |
  |---------|--------------------------|---------|-------------------------------------|
  | `b` | `\$` | `a\$c` | `a$c` |
  | `b` | `\\` | `a\\c` | `a\c` |
  | `b` | `$$` | `a$c` | error FORX0004 |
  | `(b)` | `${1}` | `abc` | error FORX0004 |
  | `(b)` | `$2` | `a$2c` | `ac` (no second group: the zero-length string) |
  | `x*` | `-` | `-a-b-c-` | error FORX0003 (the pattern matches a zero-length string) |
  | `b` | `\x` | `a\xc` | error FORX0004 |
  | `(b)` | `[$1]` | `a[b]c` | `a[b]c` |
  | `b` | `$0$0` | `abbc` | `abbc` |

  Six of nine differ. The W3C suite does not exercise these cases. `regex-facts.cs` shows that `Match.Result` in a
  `MatchEvaluator` gives exactly what `Replace(string, string)` gives in all nine, so moving to an evaluator alone
  would keep today's outputs, deviations included.

### Who passes what

| Entry point | Where | Token passed | Time limit |
|-------------|-------|--------------|------------|
| SPARQL HTTP server, query and update | `SparqlHttpServer.cs:258`, `:438` | none; the handler holds the server's shutdown token but does not pass it, so `StopAsync` does not stop a running query | `QueryTimeoutMs`, documented as "Maximum query execution time ... Default: 30000 ... Set to 0 for unlimited" (`:586`), never read |
| `mercury-mcp` tools | `MercuryTools.cs:42`, `:106` | none; the tools take no token, while DrHook's MCP tools do | none |
| `mercury-mcp` pipe sessions | `Mercury.Mcp/Program.cs:201` | none | none |
| `mercury` CLI | `Mercury.Cli/Program.cs:606` | none; Ctrl+C is not handled, so it ends the process | none |
| `mercury-sparql` | `SparqlTool.cs:105` | none | none |
| WDBench runners | `benchmarks/Mercury.Benchmarks` | a `CancelAfter` token | carried by the token, reported as "Query cancelled" |
| tests | `tests/Mercury.Tests` | xUnit's `TestContext.Current.CancellationToken` in some tests | none |

`DiagnosticCode.QueryTimeout` (3001, "query execution exceeded time limit ({0}ms)") is defined and never raised.
The HTTP server answers every failed query with 400, execution failures included. It runs on `HttpListener`, which
does not watch the connection while a request runs (next section). Solid does not run SPARQL queries; its
`RequestTimeoutMs` is likewise never read (outside this ADR).

### How a server learns that its client is gone (sources read 2026-10-31)

No cancellation token crosses a connection. A server makes one by watching its connection and cancels it when the
connection ends. Read in dotnet/aspnetcore at [`4f556f7`](https://github.com/dotnet/aspnetcore/tree/4f556f73a610b386e5ec29355a300e493d30c4c5)
and dotnet/runtime at [`591622c`](https://github.com/dotnet/runtime/tree/591622c69134f9a790f902e364c7a6a5c572b12d):

- **Kestrel keeps a read pending on every connection.**
  [`SocketConnection.DoReceive`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs#L136-L171)
  loops on `ReceiveAsync`. A read of zero bytes is the client's FIN, and a reset ends the read with an error; either
  ends the loop and cancels the transport's `ConnectionClosed` token. `HttpConnection` is registered on that token
  ([L117](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/HttpConnection.cs#L117),
  [L172–L184](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/HttpConnection.cs#L172-L184))
  and calls `OnInputOrOutputCompleted`, which cancels `RequestAborted`
  ([`Http1Connection`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/Http/Http1Connection.cs#L131-L136)).
  [`HttpContext.RequestAborted`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.httpcontext.requestaborted)
  "Notifies when the connection underlying this request is aborted and thus request operations should be
  cancelled." The loop's exit fires `ConnectionClosed` for a FIN as for a reset
  ([L258–L263](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs#L258-L263)),
  so Kestrel takes a client's half-close as the end, although a half-close "does not imply that the client is no
  longer interested in a response" ([RFC 9112 §9.6](https://www.rfc-editor.org/rfc/rfc9112.html#section-9.6)).
- **ASP.NET Core's HTTP.sys server asks the kernel driver.** Per connection, it calls `HttpWaitForDisconnectEx` with
  an overlapped callback that cancels a `CancellationTokenSource`
  ([`DisconnectListener`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/HttpSys/src/NativeInterop/DisconnectListener.cs#L54-L116)).
  [`HttpWaitForDisconnect`](https://learn.microsoft.com/windows/win32/api/http/nf-http-httpwaitfordisconnect)
  "notifies the application when the connection to an HTTP client is broken for any reason"; it needs the request
  queue's handle and the connection's id.
- **A cancel that crosses the wire is a protocol message, turned into a token at the receiver.** HTTP/2's RST_STREAM
  "is sent to request cancellation of a stream" ([RFC 9113 §6.4](https://www.rfc-editor.org/rfc/rfc9113.html#section-6.4));
  Kestrel maps it to the same token
  ([`ProcessRstStreamFrameAsync`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Connection.cs#L955-L989)
  → [`Http2Stream.AbortRstStreamReceived`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L569-L629)
  → `CancelRequestAbortedToken`). MCP's `notifications/cancelled` is the same pattern one layer up. HTTP/1.1 defines
  no such message; "A client, server, or proxy MAY close the transport connection at any time"
  ([RFC 9112 §9.5](https://www.rfc-editor.org/rfc/rfc9112.html#section-9.5)), and the closing is what a server can
  watch for.
- **`HttpListener`, which Mercury's server uses, reports nothing it watches.** On Windows it calls
  `HttpWaitForDisconnect` only for connection-based authentication, so that cached NTLM or Negotiate state is dropped
  when the connection ends
  ([L985–L988](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Windows/HttpListener.Windows.cs#L985-L988),
  [L1118–L1121](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Windows/HttpListener.Windows.cs#L1118-L1121),
  [L1399–L1414](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Windows/HttpListener.Windows.cs#L1399-L1414));
  the application is not told. The managed implementation used on macOS and Linux reads the socket until the
  request's headers are complete, hands the context over, and issues no further read until the response is done
  ([`HttpConnection.OnReadInternal`](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Managed/HttpConnection.cs#L260-L325)).
  The public API ([reference assembly](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/ref/System.Net.HttpListener.cs))
  exposes no connection id, no request-queue handle, no socket and no token, so an application cannot do the
  watching itself. While a query runs, nothing observes the connection; a client that left is found when the
  response is written.
- **What watching cannot see.** A disconnect is seen only while a read is pending: Kestrel stops receiving when its
  input pipe is full, until the application reads
  ([`DoReceive`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs#L175-L195)).
  A client that vanishes without FIN or reset (power lost, route dropped) is seen only by a failed write or by TCP
  keep-alive, which "MUST default to off" and otherwise to an interval "no less than two hours"
  ([RFC 9293 §3.8.4](https://www.rfc-editor.org/rfc/rfc9293.html#section-3.8.4)). A disconnect token is therefore
  best effort; the query's own time limit is what bounds it.

### What .NET provides (Microsoft Learn, verified 2026-10-31)

- [Cancellation in managed threads](https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads):
  cancellation is cooperative; "Only the requesting object can issue the cancellation request"; library code
  should "provide public methods that accept an external cancellation token". Polling "does not significantly
  impact performance". An operation that must observe an outside token and its own joins them with
  `CreateLinkedTokenSource`, and on `OperationCanceledException` checks which token is cancelled to tell "timed out"
  from "cancelled per user request". Linked sources must be disposed. An operation that cannot poll registers a
  callback that unblocks it.
- [`HttpClient.Timeout`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.timeout) sits beside the
  per-request token, and "only the shorter of the two timeouts will apply". Since .NET 5 a timeout is an
  `OperationCanceledException` that nests a `TimeoutException`
  ([`SendAsync`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.sendasync)), so the caller can
  tell the two apart. `Timeout.InfiniteTimeSpan` means no limit.
- [`CancellationTokenSource(TimeSpan, TimeProvider)`](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtokensource.-ctor)
  (.NET 8 and later) runs a timeout on a clock a test can control. `TimeProvider.GetTimestamp` and
  `GetElapsedTime` measure elapsed time monotonically on the same clock.
- The MCP C# SDK hands a tool method a `CancellationToken` that is cancelled when the client sends
  `notifications/cancelled` ([SDK docs](https://csharp.sdk.modelcontextprotocol.io/concepts/cancellation/cancellation.html)).
- SPARQL 1.1 Protocol §2.1.7: "400 if the SPARQL query supplied in the request is not a legal sequence of
  characters"; "500 if the service fails to execute the query", and a service "may also return a 500 response code
  if they refuse to execute a query"; other 4XX or 5XX codes may be used "as per HTTP". §4.1 lets a service "impose
  time or memory limits on queries"; the informative example in §3.1.10 refuses a query with 500 because data
  "could not be retrieved within the time alloted".

### What a regular expression permits

- A `Regex` cannot observe a `CancellationToken`. Its only bound is a match timeout, a `TimeSpan` fixed when the
  instance is built. The timeout is "the period of time that the regular expression engine will look for a single
  match", and "the execution time might exceed the specified time-out interval"
  ([best practices](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-regex)). The default is
  `Regex.InfiniteMatchTimeout`.
- The constructor rejects a timeout that is "negative, zero, or greater than approximately 24 days"
  ([`Regex(String, RegexOptions, TimeSpan)`](https://learn.microsoft.com/dotnet/api/system.text.regularexpressions.regex.-ctor)).
  `regex-facts.cs`: 24 days is accepted, `int.MaxValue` milliseconds is not.
- No `Regex` constructor takes a `TimeProvider` (`regex-facts.cs`). The regex measures its timeout on its own
  clock, so a test clock moves Mercury's deadline, not the regex's, and near the limit the two can disagree. On
  omega (idle, .NET 10.0.9) a 1 ms limit fired after 0.981 ms by `Stopwatch`, a 1-tick limit after 1.78 ms.
- Timeouts and `RegexOptions.NonBacktracking` "help protect against accidental performance problems in
  developer-authored patterns. They are not intended as a security boundary against malicious patterns." Microsoft
  advises against passing user-provided text as a pattern. SPARQL's REGEX and REPLACE take exactly that.
- `NonBacktracking` is linear in the input, but it rejects atomic groups, back-references, balancing groups,
  conditionals, lookarounds and `\G`
  ([options](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options)), and XPath's regular
  expressions, which SPARQL uses, allow back-references. It also "guards against expensive *input*, not against
  actively malicious *patterns*".
- A catastrophic pattern is expensive, not endless. `(a+)+$` on n `a`s and a `!`, measured on omega: n = 16 took
  9 ms, 20 took 129 ms, 24 took 1.95 s, 26 took 10.5 s, about doubling with each `a`. Given a long enough limit it
  completes, so no input times out "at any limit on any machine", which the first draft's D7 claimed.

So no regex setting contains a hostile pattern completely. A deadline bounds it approximately, and only a process
boundary contains it fully.

## Decision

### Principle

A query's execution budget belongs to the query, not to its individual operators. Cancellation propagates
throughout execution, completion is reported truthfully, and resources are released when the operation actually
ends.

Three things are kept apart. **Cancellation:** the caller asks the work to stop. **The time limit:** the query's own
budget, from the moment the engine accepts it. **Deadline propagation:** an operation inside the query, such as a
regex or a lock wait, gets the time that remains rather than a limit of its own. A fourth bound, the **regex
limit**, is a per-match safety limit independent of the query's budget (D4).

A time limit is a cooperative budget, not a promise that work stops at that instant. The engine stops at its next
check, and every place where it can run past the deadline is named, with its bound: one regex match (D4), one write
batch (D5), and the time between two checks.

### D1. A query has a time limit, beside the caller's token

`SparqlEngine.Query` and `SparqlEngine.Update` gain overloads that take a time limit next to the token, shaped like
`HttpClient`: `(QuadStore store, string sparql, TimeSpan timeLimit, CancellationToken ct = default)`. The existing
overloads keep their meaning: no limit. The caller owns the cancellation; the query owns its limit.

Mercury validates the limit itself, so no .NET constructor decides the API's meaning: `Timeout.InfiniteTimeSpan`
means no limit; any other value must be greater than zero and at most `Int32.MaxValue` milliseconds, or the call
throws `ArgumentOutOfRangeException` before any work starts.

### D2. One deadline for the whole operation, kept in one small context

- **The clock starts when the engine accepts the operation**, before parsing, and runs through parsing, planning,
  waiting for the store's lock, evaluation and building the result. Time is measured monotonically on the engine's
  `TimeProvider` (`GetTimestamp` at the start, `GetElapsedTime` for what has passed); the remaining time is the limit
  minus the elapsed time, never a difference of wall-clock readings.
- **The engine links the sources.** It creates a timeout source for the limit on the same `TimeProvider` and links
  it with the caller's token (`CreateLinkedTokenSource`), as the cancellation guide shows. The linked token goes where
  the caller's token goes today. Both sources are disposed when the operation ends.
- **One small internal context**, in place of today's bare token in `QueryCancellation`: the linked token, the
  caller's token, the start timestamp and the limit. Nothing more until an observed need asks for it. It is set and
  restored in one scope that also covers taking the lock: on exit the previous context is restored, not cleared, so
  a failure while taking the lock cannot leave a context on the thread.
- **The lock wait counts.** The read lock, and an update's write lock, are taken with `TryEnter…Lock` in bounded
  slices, checking the linked token between slices, so both the deadline and the caller's cancellation end a wait.
  The slice length is measured during implementation, not guessed, and recorded with its basis. A wait that reaches
  the deadline ends the operation as timed out, and the outcome says it was waiting for the lock.
- **Checks where work can run long, not everywhere.** Implementation starts with an audit of every path that can run
  long without reaching a scan's check: sorting, grouping and aggregates, DISTINCT, joins of materialized rows, the
  modifier layer, sub-SELECT, building the result, SERVICE and LOAD. A check is added where the audit finds work
  that grows with the data; the audit's table, with each path's check and its test, goes into this ADR before it is
  Accepted.
- **SERVICE and LOAD take the linked token**, and SILENT does not swallow the query's own end: a failure is silenced
  only when the linked token is not cancelled.

### D3. A result says how the query ended, from the state, not from the exception

`QueryResult` and `UpdateResult` carry a typed outcome, with the details separate: completed, failed, timed out or
cancelled. The outcome is read from the state, in this order:

| Condition | Outcome |
|-----------|---------|
| The caller's token is cancelled | Cancelled |
| The query's deadline has passed | Timed out, with the limit (`DiagnosticCode.QueryTimeout`) |
| A regex hit the regex limit while the query's deadline had not passed (D4) | Failed, with a regex-limit diagnostic and the limit |
| Anything else | Failed, with its cause |

The caller's own cancellation takes precedence, as in `HttpClient`.

Cancellation, the query's deadline and the regex limit are never SPARQL expression errors. They are not turned into
an error value, an unbound variable or a dropped row in any expression context: FILTER, BIND, SELECT expressions,
OPTIONAL, EXISTS, aggregates, HAVING and ORDER BY. That is today's behaviour for a regex timeout (measured, Context);
it becomes an invariant with a test in each context.

### D4. REGEX and REPLACE get the time that remains, and no more than the regex limit

- **Two bounds.** The query's remaining time, and the regex limit, a per-match bound set by the entry point (Q1). The
  regex is built with the smaller of the two; an absent bound counts as infinite:
  T<sub>regex</sub> = min(T<sub>remaining</sub>, T<sub>regexLimit</sub>). Only when both are absent is the regex built with
  `Regex.InfiniteMatchTimeout`, and that is a caller's explicit choice to trust its patterns, never the side effect of
  leaving out a query limit. A value above the constructor's maximum is lowered to it. With no time left, the query
  ends as timed out before the regex is built.
- **Which bound fired is known.** The engine records which of the two set the regex's timeout. When
  `RegexMatchTimeoutException` arrives, D3's order decides: the caller's cancellation first; then, if the remaining
  query time set the timeout, the query timed out, even when the regex's clock fired a moment before Mercury's
  deadline is seen to pass; if the regex limit set it, the query failed on the regex limit. The two clocks are never
  compared to decide this.
- **REPLACE follows XPath.** REPLACE builds its result in a `MatchEvaluator` that expands the replacement as
  `fn:replace` does (F&O 3.1 §5.6.4), not with `Match.Result`: `\$` is a dollar sign and `\\` a backslash; `$N` is the
  Nth group, the zero-length string when the group did not match or when the pattern has fewer than N groups and
  N ≤ 9; when N exceeds both the group count and 9, its last digit is a literal character; any other `$` or `\` is
  error FORX0004; a pattern that
  matches a zero-length string is error FORX0003. With the q flag the replacement is literal. Both errors are
  expression errors, as SPARQL's other function errors are. The evaluator checks the linked token before each
  match, so a cancellation or the deadline is seen between matches. This changes the six deviating outputs in
  Context to XPath's.
- The fixed 100 ms goes.

What remains approximate, stated rather than hidden: one match can overrun the deadline, by at most the regex's
timeout plus what the regex engine itself overruns. A caller's cancellation is not seen inside one match, only
between matches and between rows.

### D5. Updates: cancellation until writes begin, and an outcome that says what was applied

- An update observes cancellation and its deadline while its WHERE is evaluated, while it waits for the lock, and
  between operations.
- Once an operation's writes begin, they run to commit or rollback; a write batch is not interrupted. This is the
  one stated exception to the deadline: a batch that has begun finishes past it, and the overrun is that batch's
  write time.
- Operations before a cancelled one stay applied ([ADR-046](ADR-046-multi-operation-update-facade.md)'s
  sequential-with-abort; the sequence is not atomic). The outcome says so: which operation of how many was running
  when the update was cancelled or timed out, that the operations before it were applied, with their affected count,
  and that the ones after it were not run. A cancelled update never reports `AffectedCount` 0 for work it committed.

### D6. Every entry point passes the cancellation it has, and a limit

- **SPARQL HTTP server:** links its shutdown token into each query and update, and applies `QueryTimeoutMs` as
  documented (`0` means no limit). `StopAsync` then stops running queries. Status codes follow SPARQL 1.1 Protocol
  §2.1.7: 400 only for a query that does not parse; 500 for a failure to execute, a timeout included (§3.1.10's
  example refuses an over-time query with 500); 503 when the server's shutdown cancelled the query. The body states
  the outcome. A client that disconnects is not seen before the response is written, since `HttpListener` does not
  watch the connection while a query runs (Context); the query runs until its limit. Cancelling on disconnect needs a
  transport that watches the connection (Q7).
- **`mercury-mcp` tools:** take the SDK's `CancellationToken`, as DrHook's tools do, with a default limit and an
  option to change it.
- **`mercury` CLI and `mercury-sparql`:** Ctrl+C cancels the running query (`Console.CancelKeyPress`, with the
  event cancelled) instead of ending the process; no default limit, and an option to set one.
- **WDBench runners:** pass their timeout as the time limit, so a timeout is reported as one.
- **Tests:** pass `TestContext.Current.CancellationToken`, so a cancelled test run cancels its queries.

### D7. Tests do not measure the machine

- **Deterministic tests, on the engine's clock.** The engine takes a `TimeProvider` through an internal seam
  (`Mercury.Tests` already sees internals). At each check the audit names, the test advances the controlled clock
  past the deadline and expects timed out; it cancels the caller's token and expects cancelled; both, and expects
  cancelled; neither, and expects completed. With a deadline already passed, a REGEX ends as timed out without
  building its regex. After each, the sources are disposed and the previous context is restored.
- **Regex integration tests**, kept apart from the controlled clock, since the regex does not read it. They raise a
  real `RegexMatchTimeoutException` with a costly input and a small limit, such as `(a+)+$` on forty `a`s and a `!`
  (on omega about 16,000 times the 10.5 s that 26 took), and check which outcome D4 assigns when the query's
  remaining time set the timeout and when the regex limit did. They test the integration, not the deadline's
  precision, and assert no elapsed time.
- **Expression contexts:** in each context D3 lists, a timeout and a cancellation fail the query and never drop a
  row.
- **REPLACE conformance:** every row of the Context table, plus escaped backslashes, `$0`, groups that did not
  match, the q flag, and REGEX unchanged.
- A trivial REPLACE with no limit succeeds under any load. The probe test that failed on CI is that case.

## Consequences

- REGEX and REPLACE no longer fail because a machine is busy.
- The HTTP server keeps a promise it documents but never kept: a query that runs longer than 30 seconds, and
  succeeds today, fails there with a timeout. Execution failures change from 400 to 500. Both are changes in
  behaviour for HTTP clients.
- A query on the MCP server can no longer hold the memory store's read lock indefinitely, and can no longer wait for
  it indefinitely: a stuck or very heavy query ends with a typed timeout, and writes go on.
- REPLACE's output changes in the six cases where it deviates from XPath today.
- A cancelled update reports what it applied.
- The public surface grows: new `Query` and `Update` overloads, and the outcome on the result types in
  `Mercury.Abstractions`. The count of public types in CLAUDE.md and the API docs is updated with it.
- A hostile pattern on the HTTP server is bounded only approximately (D4). That is recorded in `docs/limits/`, with
  process isolation as the full containment, not built here.
- Building a `Regex` on every call stays. Caching would interact with D4, since an instance's timeout is fixed when
  it is built; that is a separate question.

## Open questions

- **Q1. Default limits.** Time limits, proposed: HTTP 30 s (as documented), `mercury-mcp` 30 s, CLI and library
  none. The regex limit: its default value, and whether the library has one by default (the caller disables it
  explicitly) or none unless set (the caller sets it explicitly). Recommended: a default for every entry point,
  library included, disabled only on request, so an unlimited query never implies an unlimited regex.
- **Q2. HTTP status for a timed-out query.** Resolved by the assessment: not 400, a documented 5xx. D6 proposes 500
  for a timeout and any execution failure, and 503 for the server's shutdown.
- **Q3. The outcome's shape.** Resolved by the assessment: a typed enum, with the details (limit, diagnostic code,
  update progress) separate. Open: the names, and where the regex limit is passed (a parameter beside the time limit,
  or one small value holding both).
- **Q4. Pipe sessions** (`mercury-mcp` serving a remote REPL): cancelling from the client's side would need a
  message in the pipe protocol.
- **Q5. The `TimeProvider` seam:** internal for tests only, or public for hosts that run on their own clock.
- **Q6. REPLACE's XPath fix ahead of the rest.** D4's replacement semantics are independent of the time limit and
  could land first, as their own change.
- **Q7. Cancelling an HTTP query when its client leaves.** `HttpListener` cannot provide it. It needs a transport
  that keeps a read pending on the connection, as Kestrel does: Mercury's own HTTP over `System.Net.Sockets`
  (BCL only), or ASP.NET Core (outside the BCL). A transport change, outside this ADR.

## Validation plan

- The audit's table (D2): every path that can run long, its check, and the test that reaches it.
- Unit tests for D1 to D3 on the controlled clock, as D7 describes, including the lock wait: a query waiting behind a
  held write lock ends as timed out at its deadline and as cancelled when its caller cancels.
- D1: negative, zero and too-large limits throw before any work starts.
- D4: the regex integration tests; a trivial REPLACE with no limit succeeds while the other test projects run in
  parallel; a REPLACE over many matches stops between matches when the token is cancelled; the REPLACE conformance
  tests.
- D5: an update cancelled during its WHERE leaves the store unchanged; one cancelled between operations keeps the
  earlier operations and reports them, with their count.
- D6: the HTTP server with a small `QueryTimeoutMs` answers the costly pattern with 500 and a timed-out body;
  `StopAsync` ends a running query with 503; an MCP tool call cancelled by the client reports cancelled.
- The probes in `tools/probes/query-time-limit/` re-run after the change; `sparql-regex.cs` then shows XPath's
  outputs.
- CI: the probe test on all six runners.
- Dogfood: on the live `mercury-mcp`, a query given the costly pattern times out with the typed outcome, and a
  memory write right after it succeeds.

## References

- CI run 41203456690, macOS x64; `tests/Mercury.Tests/Sparql/Adr049FunctionReconciliationProbeTests.cs`
- Probes: [`tools/probes/query-time-limit/`](../../../tools/probes/query-time-limit/README.md) (`regex-facts.cs`,
  `sparql-regex.cs`)
- Microsoft Learn: [Cancellation in managed threads](https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads),
  [`HttpClient.Timeout`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.timeout),
  [`HttpClient.SendAsync`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.sendasync),
  [`CancellationTokenSource` constructors](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtokensource.-ctor),
  [`ReaderWriterLockSlim.TryEnterReadLock`](https://learn.microsoft.com/dotnet/api/system.threading.readerwriterlockslim.tryenterreadlock),
  [`Regex` constructors](https://learn.microsoft.com/dotnet/api/system.text.regularexpressions.regex.-ctor),
  [Best practices for regular expressions](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-regex),
  [Regular expression options](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options)
- [MCP C# SDK: cancellation](https://csharp.sdk.modelcontextprotocol.io/concepts/cancellation/cancellation.html)
- Disconnect detection: dotnet/aspnetcore `4f556f7` (Kestrel `SocketConnection`, `HttpConnection`,
  `Http1Connection`, `Http2Connection`, `Http2Stream`; HTTP.sys `DisconnectListener`), dotnet/runtime `591622c`
  (`HttpListener.Windows.cs`, managed `HttpConnection.cs`, reference assembly);
  [`HttpWaitForDisconnect`](https://learn.microsoft.com/windows/win32/api/http/nf-http-httpwaitfordisconnect),
  [`HttpContext.RequestAborted`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.httpcontext.requestaborted);
  RFC 9112 §9.5 and §9.6 (HTTP/1.1 connection close and half-close), RFC 9113 §6.4 (RST_STREAM), RFC 9293 §3.8.4
  (keep-alives)
- SPARQL 1.1 Query §17.4.3.14 (REGEX) and §17.4.3.15 (REPLACE); SPARQL 1.1 Protocol §2.1.7, §3.1.10 and §4.1;
  XPath and XQuery Functions and Operators 3.1 §5.6.1 (regular expression syntax) and §5.6.4 (`fn:replace`)
- [ADR-046](ADR-046-multi-operation-update-facade.md) (multi-operation UPDATE at the engine facade: operations run
  in order, and the first failure stops the rest) and [ADR-049](ADR-049-unified-expression-evaluator.md) (one evaluator)
- [DrHook ADR-016](../drhook/ADR-016-debugger-resilience-and-recovery.md), principle 7: every public operation is
  bounded from the caller's side, even when a call beneath it cannot be interrupted
