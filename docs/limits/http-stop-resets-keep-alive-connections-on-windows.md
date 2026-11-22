# Limit: On Windows the HTTP endpoint's stop can take a finished response from a client that has not read it

**Status:**        Latent
**Surfaced:**      2026-11-22, from Mercury.Tests run in parallel on omega-windows (2026-11-21): the response that
                   `HttpListenerLoop`'s stop waited for reached its client as a reset connection. Measured the next
                   day with the probes in [tools/probes/http-listener-stop](../../tools/probes/http-listener-stop/README.md).
**Last reviewed:** 2026-11-22
**Promotes to:**   an ADR when mercuryd runs as a Windows service (ADR-065 Step 9), or when a client of a Windows
                   mercuryd or mercury-mcp reports a reset at a stop.

## Description

On Windows, `HttpListener`'s stop resets every connection http.sys still holds, and its client loses what it has not
yet read of a response on it, the headers included. On Linux and macOS the listener is managed code, and its stop
closes a connection gracefully: nothing is lost.

http.sys closes a connection gracefully, at once, after a response that has a length and closes its connection, and
the stop does not reset that one. So since 2026-11-22 the loop writes every body with its length, and a response
written once the stop has begun closes its connection: the responses the stop waits for, and the 503s it answers
meanwhile, reach their clients whole (`HttpListenerLoopStopTests`).

What remains is a response written before the stop began, on a keep-alive connection. If its client has not read it
when the stop closes the listener, the client gets a reset instead of the response.

## Trigger condition

- A client of an endpoint on Windows that is slow to read its response when the endpoint stops: a large result, a
  loaded machine, a client across a network.
- mercuryd as a Windows service (ADR-065 Step 9) is where it would surface first.

## Current state

- The window is from a response's last write to its client's read, when the stop's close of the listener falls in it.
- The client sees a reset connection, not a short body taken as whole, so it knows the request failed.
- Keep-alive stays on, on every platform (Martin, 2026-11-22: fix the responses the stop waits for, register the rest).

## Candidate mitigations

- **Every response closes its connection**, on every platform: no stop can take a finished response. The cost is a TCP
  connection per request; not measured.
- **A stop that waits for the connections to end:** `HttpListener` shows no connections, and http.sys's own wait for a
  disconnect (`HttpWaitForDisconnect`) is not public in .NET.
- **http.sys's idle-connection timeout** (`HttpListener.TimeoutManager.IdleConnection`) lowered when the stop begins:
  whether it applies to open connections, and whether http.sys then closes them gracefully, is not measured.
- ASP.NET Core's HTTP.sys server does not answer it either: its stop stops the request queue, waits for the requests
  in progress, then disposes the queue, and nothing there waits for connections (`MessagePump.cs`,
  `HttpSysListener.cs` on source.dot.net, read 2026-11-22).

## References

- `src/Mercury/Sparql/Protocol/HttpListenerLoop.cs`, the class's remarks.
- `tests/Mercury.Tests/Sparql/HttpListenerLoopStopTests.cs`.
- [ADR-065](../adrs/mercury/ADR-065-mercuryd.md) D3, and its Step 4.
- [tools/probes/http-listener-stop](../../tools/probes/http-listener-stop/README.md): the measurements.
- Microsoft's documentation of `HttpListener.Stop`: it "terminates processing of all ongoing requests" and "does not
  block to finish sending responses for currently accepted connections".
