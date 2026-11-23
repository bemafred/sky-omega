# Limit: A stop of mercury-mcp, and the CLI's exit, wait for a query's SERVICE calls

**Status:**        Latent
**Surfaced:**      2026-11-23, by [ADR-066](../adrs/mercury/ADR-066-a-write-waits-for-the-reads-in-progress.md) R3: its
                   D2 makes a close of a store a write, which waits for the reads in progress, a query's SERVICE calls
                   included.
**Last reviewed:** 2026-11-23
**Promotes to:**   [ADR-060](../adrs/mercury/ADR-060-query-time-limit-and-cancellation.md) D6, when a stop is seen to
                   wait on SERVICE, or when ADR-060 is taken up.

## Description

A process stops taking new work, then closes its store. The close waits for every read in progress, and a query that
waits on SERVICE is one: it holds its read until its endpoint answers or fails, or a call's timeout ends it. Before
ADR-066 the close did not wait, and closed the store under the query.

No serving path gives a query a token, so nothing cancels it at a stop (ADR-066 D4): the HTTP endpoint, the MCP tools,
the REPL sessions and `mercury-sparql` all call `SparqlEngine.Query` without one.

## Trigger condition

- A stop or an exit while a query waits on an endpoint that is slow, or that drops what is sent to it.
- Claude Code ending a session sends mercury-mcp SIGINT, then SIGTERM; a stop that waits past them ends in a kill.

## Current state

- Each call waits at most 5 s to connect (`--service-connect-timeout`) and 100 s for its answer (ADR-066 D5); a query
  makes as many calls as its SERVICE clauses and their rows ask for, one after the other.
- A process that is killed meanwhile loses nothing committed: the next open applies the log again.
- The stop signals are held while the store closes (ADR-058 Q7), so the process stays until those calls end.

## Candidate mitigations

- **Cancel the queries in progress at a stop**: a token for each serving path, cancelled by the stop (ADR-060 D6).
- **A deadline for the whole query**, not for each call (ADR-060 D2).

## References

- [ADR-066](../adrs/mercury/ADR-066-a-write-waits-for-the-reads-in-progress.md) R3, D2, D4, D5; its lifecycle table.
- [ADR-060](../adrs/mercury/ADR-060-query-time-limit-and-cancellation.md) D2, D6.
