# What HttpListener's stop does to a response its client has not read

Three probes of 2026-11-22, for `HttpListenerLoop` (ADR-065 D3) and the limit
[http-stop-resets-keep-alive-connections-on-windows](../../../docs/limits/http-stop-resets-keep-alive-connections-on-windows.md).
They use the BCL's `HttpListener` alone, not Mercury. They were run on zbook (Ubuntu 26.04, .NET 10.0.14) and on
omega-windows (Windows 11 26200, arm64, .NET 10.0.15), in a clone at `041af4dc`.

The question came from Mercury.Tests run in parallel on omega-windows (2026-11-21): the response the loop's stop
waited for reached its client as "An existing connection was forcibly closed by the remote host".

```bash
dotnet run tools/probes/http-listener-stop/a-response-read-after-the-stop.cs [variant ...]
dotnet run tools/probes/http-listener-stop/what-goes-on-the-wire.cs
dotnet run tools/probes/http-listener-stop/a-port-in-time-wait-binds-again.cs
```

## `a-response-read-after-the-stop.cs`

One listener, one request, one response per run, 30 runs per variant, in a fixed order: the server writes the body and
closes the response; the client has the headers; the listener stops; 50 ms later the client reads the body. The TCP
states are those of the first run, just before the stop.

| Variant | The response | Windows: state before the stop | Windows: read after the stop | Linux |
|---|---|---|---|---|
| `readfirst` | as `plain`, read before the stop (the control) | — | whole 30/30 | whole 30/30 |
| `plain` | chunked, keep-alive: the loop's before 2026-11-22 | Established | reset 30/30 | whole 30/30 |
| `wait` | as `plain`, the stop 200 ms after the response | Established | reset 30/30 | whole 30/30 |
| `close` | chunked, `Connection: close` | Established | reset 30/30 | whole 30/30 |
| `close+wait` | as `close`, the stop 200 ms after | Established | reset 30/30 | whole 30/30 |
| `length` | 8-byte body with its length, keep-alive | Established | whole 30/30 | whole 30/30 |
| `length+big` | 32 KB body with its length, keep-alive | Established | reset 30/30 | whole 30/30 |
| `close+length` | 8 bytes, its length, `Connection: close` | — | whole 30/30 | whole 30/30 |
| `close+length+big+wait` | 32 KB, its length, `Connection: close` | server FinWait2, client CloseWait | whole 30/30 | whole 30/30 |
| `close+nostop` | as `close`, no stop (the control for `close`) | Established | whole 30/30 | whole 30/30 |
| `close+nochunk` | `SendChunked = false`, no length | — | the client waited until its 10 s timeout | 0 bytes of the body |

- On Windows the stop resets every connection http.sys still holds, and the client loses what it has not yet read:
  the 8-byte body was read with the headers and survived; the 32 KB body did not.
- http.sys closes a connection gracefully at once after a response that has a length and closes it, and the stop does
  not reset that connection. A chunked response that closes its connection is not closed before the stop.
- A body of 1 MB never finished: the server's write waits for the client to read it. 32 KB is more than the client
  reads with the headers and less than the sockets' buffers hold.

## `what-goes-on-the-wire.cs`

A socket is the client, so the headers are those sent; the client reads after the stop until the connection ends.

| The response | Windows | Linux |
|---|---|---|
| 204, length not set or 0, close | `Content-Length: 0`, `Connection: close`; graceful | the same |
| 200 or 201 empty, length 0, close | graceful | graceful |
| HEAD, length not set, close | `Transfer-Encoding: chunked`, no body; graceful | chunked, and a chunk terminator after the head |
| HEAD, the GET's length, body not written, close | `Content-Length: 32768`, nothing after the head; graceful | the same |
| HEAD, the GET's length, body written | the connection reset | the 32 KB body after the head |
| 32 KB with its length, keep-alive | reset, 0 bytes | the response, then an unasked-for `HTTP/1.1 200 OK`, `Connection: close` |
| 204, keep-alive | reset, 0 bytes | the response, then the same unasked-for 200 |

- Both listeners send `Content-Length: 0` with a 204 whether or not it is set.
- On Linux the stop writes a 200 that no request asked for onto an idle keep-alive connection.

## `a-port-in-time-wait-binds-again.cs`

A connection the server closed first is in TIME_WAIT on the server's port; a new socket binds that port again on Linux
(zbook). So TIME_WAIT did not cause the "Address already in use" that `HttpListener.Close` after `Stop` threw in
Mercury.Tests in parallel: that Close binds the port again to remove its prefixes, and something else held the port.
`HttpListenerLoopStopTests.AStoppedLoop_IsDisposed_WithItsPortTakenByAnother` gives the port to another listener and
reproduces the exception on every run.
