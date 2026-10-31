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

## Context

### What the engine does today (`e9219be`)

- `SparqlEngine.Query` and `SparqlEngine.Update` take `CancellationToken ct = default`
  ([SparqlEngine.cs](../../../src/Mercury/SparqlEngine.cs)). The token is kept per thread in `QueryCancellation`
  (`Operators/QueryCancellation.cs`), since a query runs synchronously on the caller's thread, and cleared in a
  `finally`.
- The scans poll it on every row (`TriplePatternScan`, ten sites, property paths included), and
  `UpdateExecutor.ExecuteSequence` polls it between operations.
- `QueryCore` catches `OperationCanceledException` as "Query cancelled", and any other exception as a failed result
  with the exception's message. `Update` reports "Update cancelled". The results carry only that string
  (`QueryResult`, `UpdateResult` in `Mercury.Abstractions/Results.cs`), so a caller cannot tell a timeout from a
  cancellation or from any other failure.
- The `catch (Exception)` blocks in `UpdateExecutor` wrap only store writes. WHERE evaluation runs outside them, so
  a cancellation raised there reaches `SparqlEngine`. No swallowed cancellation was found.
- A query holds `QuadStore`'s `ReaderWriterLockSlim` read lock for its whole run, so a query that never ends blocks
  every write to that store.
- REGEX and REPLACE (`FilterEvaluator.Functions.cs`) build a new `Regex` on every call, with
  `TimeSpan.FromMilliseconds(100)` (line 119). That limit measures the machine's load as much as the pattern's
  cost: it failed a trivial match on a busy runner.

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
Solid does not run SPARQL queries; its `RequestTimeoutMs` is likewise never read (outside this ADR).

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
  (.NET 8 and later) runs a timeout on a clock a test can control.
- The MCP C# SDK hands a tool method a `CancellationToken` that is cancelled when the client sends
  `notifications/cancelled` ([SDK docs](https://csharp.sdk.modelcontextprotocol.io/concepts/cancellation/cancellation.html)).

### What a regular expression permits

- A `Regex` cannot observe a `CancellationToken`. Its only bound is a match timeout, a `TimeSpan` fixed when the
  instance is built. The timeout is "the period of time that the regular expression engine will look for a single
  match", and "the execution time might exceed the specified time-out interval"
  ([best practices](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-regex)). The default is
  `Regex.InfiniteMatchTimeout`.
- Timeouts and `RegexOptions.NonBacktracking` "help protect against accidental performance problems in
  developer-authored patterns. They are not intended as a security boundary against malicious patterns." Microsoft
  advises against passing user-provided text as a pattern. SPARQL's REGEX and REPLACE take exactly that.
- `NonBacktracking` is linear in the input, but it rejects atomic groups, back-references, balancing groups,
  conditionals, lookarounds and `\G`
  ([options](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options)), and XPath's regular
  expressions, which SPARQL uses, allow back-references. It also "guards against expensive *input*, not against
  actively malicious *patterns*".

So no regex setting contains a hostile pattern completely. A deadline bounds it approximately, and only a process
boundary contains it fully.

## Decision

### D1. A query has a time limit, beside the caller's token

`SparqlEngine.Query` and `SparqlEngine.Update` gain overloads that take a time limit next to the token, shaped like
`HttpClient`: `(QuadStore store, string sparql, TimeSpan timeLimit, CancellationToken ct = default)`, where
`Timeout.InfiniteTimeSpan` means no limit. The existing overloads keep their meaning: no limit. The caller owns the
cancellation; the query owns its limit.

### D2. The engine links them, so it knows the deadline

The engine creates a timeout source for the limit and links it with the caller's token
(`CreateLinkedTokenSource`), as the cancellation guide shows. The linked token goes where the caller's token goes
today, and the deadline is kept beside it in `QueryCancellation`. Both sources are disposed when the query ends.
The engine can then answer what a token alone cannot: how much time remains.

### D3. A result says how the query ended

`QueryResult` and `UpdateResult` record whether the query completed, failed, ran out of time or was cancelled; a
timeout also gives the limit, with `DiagnosticCode.QueryTimeout`'s message. The cause is read from the sources, not
from the exception: if the caller's token is cancelled, the query was cancelled; otherwise, if the deadline has
passed, it timed out. That follows `HttpClient`, where the caller's own cancellation takes precedence.

### D4. REGEX and REPLACE get the time that remains

- Each regex is built with the time remaining to the deadline, or with `Regex.InfiniteMatchTimeout` when the query
  has no limit. With no time left, the query ends as timed out before the regex is built.
- REPLACE checks the token before each match, through a `MatchEvaluator` that expands the replacement with
  `Match.Result`, so a cancellation or a deadline is seen between matches.
- A `RegexMatchTimeoutException` is the query's time limit running out, and is reported as D3's timeout.
- The fixed 100 ms goes.

What remains approximate, stated rather than hidden: one match can overrun the deadline, by at most the time
remaining when its regex was built, plus what the regex engine itself overruns. A caller's cancellation is not seen
inside one match, only between matches and between rows.

### D5. Updates observe cancellation until their writes begin

An update observes cancellation while its WHERE is evaluated and between operations, as today. Once an operation's
writes begin, they run to commit or rollback; a write batch is not interrupted. Operations before a cancelled one
stay applied ([ADR-046](ADR-046-multi-operation-update-facade.md)'s sequential-with-abort).

### D6. Every entry point passes the cancellation it has, and a limit

- **SPARQL HTTP server:** links its shutdown token into each query and update, and applies `QueryTimeoutMs` as
  documented (`0` means no limit). `StopAsync` then stops running queries.
- **`mercury-mcp` tools:** take the SDK's `CancellationToken`, as DrHook's tools do, with a default limit and an
  option to change it.
- **`mercury` CLI and `mercury-sparql`:** Ctrl+C cancels the running query (`Console.CancelKeyPress`, with the
  event cancelled) instead of ending the process; no default limit, and an option to set one.
- **WDBench runners:** pass their timeout as the time limit, so a timeout is reported as one.
- **Tests:** pass `TestContext.Current.CancellationToken`, so a cancelled test run cancels its queries.

### D7. Tests do not measure the machine

- Token timeouts are tested on a controlled clock: the engine takes a `TimeProvider` through an internal seam
  (`Mercury.Tests` already sees internals).
- The regex timeout is tested with a pattern whose work grows without bound, such as `(a+)+$` on thirty `a`s and a
  `!`. It times out at any limit on any machine, so the test asserts the outcome, not an elapsed time.
- A trivial REPLACE with no limit succeeds under any load. The probe test that failed on CI is that case.

## Consequences

- REGEX and REPLACE no longer fail because a machine is busy.
- The HTTP server keeps a promise it documents but never kept: a query that runs longer than 30 seconds, and
  succeeds today, fails there with a timeout. That is a change in behaviour for HTTP clients.
- A query on the MCP server can no longer hold the memory store's read lock indefinitely: a stuck or very heavy
  query ends with a typed timeout, and writes go on.
- The public surface grows: new `Query` and `Update` overloads, and the outcome on the result types in
  `Mercury.Abstractions`. The count of public types in CLAUDE.md and the API docs is updated with it.
- A hostile pattern on the HTTP server is bounded only approximately (D4). That is recorded in `docs/limits/`, with
  process isolation as the full containment, not built here.
- Building a `Regex` on every call stays. Caching would interact with D4, since an instance's timeout is fixed when
  it is built; that is a separate question.

## Open questions

- **Q1. Default limits.** Proposed: HTTP 30 s (as documented), `mercury-mcp` 30 s, CLI and library none.
- **Q2. HTTP status for a timed-out query.** Every failed query is a 400 today.
- **Q3. The outcome's shape** on `QueryResult` and `UpdateResult`: an enum, its name and its values.
- **Q4. Pipe sessions** (`mercury-mcp` serving a remote REPL): cancelling from the client's side would need a
  message in the pipe protocol.
- **Q5. The `TimeProvider` seam:** internal for tests only, or public for hosts that run on their own clock.

## Validation plan

- Unit tests for D1 to D3 on a controlled clock: the limit expiring gives a timeout with the limit; the caller's
  cancellation gives cancelled; when both have happened, cancelled.
- D4: the unbounded pattern under a small limit gives a timeout; a trivial REPLACE with no limit succeeds while the
  other test projects run in parallel; a REPLACE over many matches stops between matches when the token is
  cancelled.
- D5: an update cancelled during its WHERE leaves the store unchanged; one cancelled between operations keeps the
  earlier operations.
- D6: the HTTP server with a small `QueryTimeoutMs` answers the unbounded pattern with a timeout; `StopAsync` ends a
  running query; an MCP tool call cancelled by the client reports cancelled.
- CI: the probe test on all six runners.
- Dogfood: on the live `mercury-mcp`, a query given the unbounded pattern times out with the typed outcome, and a
  memory write right after it succeeds.

## References

- CI run 41203456690, macOS x64; `tests/Mercury.Tests/Sparql/Adr049FunctionReconciliationProbeTests.cs`
- Microsoft Learn: [Cancellation in managed threads](https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads),
  [`HttpClient.Timeout`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.timeout),
  [`HttpClient.SendAsync`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.sendasync),
  [`CancellationTokenSource` constructors](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtokensource.-ctor),
  [Best practices for regular expressions](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-regex),
  [Regular expression options](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options)
- [MCP C# SDK: cancellation](https://csharp.sdk.modelcontextprotocol.io/concepts/cancellation/cancellation.html)
- SPARQL 1.1 Query §17.4.3.14 (REGEX) and §17.4.3.15 (REPLACE); XPath and XQuery Functions and Operators 3.1
  §5.6.1 (regular expression syntax)
- [ADR-046](ADR-046-multi-operation-update-facade.md) (multi-operation UPDATE at the engine facade: operations run
  in order, and the first failure stops the rest) and [ADR-049](ADR-049-unified-expression-evaluator.md) (one evaluator)
- [DrHook ADR-016](../drhook/ADR-016-debugger-resilience-and-recovery.md), principle 7: every public operation is
  bounded from the caller's side, even when a call beneath it cannot be interrupted
