# How a server learns that its client is gone

**Date:** 2026-10-31. Sources read in dotnet/runtime at
[`591622c`](https://github.com/dotnet/runtime/tree/591622c69134f9a790f902e364c7a6a5c572b12d) and dotnet/aspnetcore at
[`4f556f7`](https://github.com/dotnet/aspnetcore/tree/4f556f73a610b386e5ec29355a300e493d30c4c5); every link below is
pinned to those commits.

## Why this memo exists

While drafting [Mercury ADR-060](../adrs/mercury/ADR-060-query-time-limit-and-cancellation.md) (a query's time limit
and its cancellation), we wrote that `HttpListener` "has no disconnect signal". The evidence was a reflection listing
of its public API: no `CancellationToken`, nothing about the connection. Martin questioned it: a cancellation token
for a disconnect is made by monitoring the connection's state; no token travels over the connection. He was right
about the mechanism, and the listing could not have shown it. A public surface says what a library exposes, not
what it does or what the platform can do. So we read the sources. The answer matters to anyone who passes
`RequestAborted` into long-running work and expects it to fire.

## The principle

A cancellation token is a local object. It never crosses a connection. A server makes one for a client's departure
in one of two ways, and cancels it when it learns the connection has ended:

1. **Keep a read pending on the connection.** A TCP read that returns zero bytes is the peer's FIN; a reset ends the
   read with an error. The server can only learn this while a read is outstanding.
2. **Ask the operating system.** Windows' HTTP.sys driver owns the connections and can notify the application when
   one breaks.

What can cross the wire is a protocol message, which the receiver turns into its own token: HTTP/2's RST_STREAM,
or MCP's `notifications/cancelled`. HTTP/1.1 has no such message. There, the client's only way to cancel is to close
the connection, which the server can see only by watching.

## The whole loop, in .NET

**The client cancels.** `HttpClient`'s `SocketsHttpHandler` turns the caller's token into something on the wire:

- **HTTP/1.1:** the token's registration disposes the connection: "The registration disposes of the connection,
  tearing it down and causing any pending operations to wake up"
  ([`HttpConnection.RegisterCancellation`](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs#L957-L970)).
  The server sees the connection end.
- **HTTP/2:** the token wakes the stream's waiter
  ([L1454–L1470](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L1454-L1470)),
  the failure calls `Cancel`
  ([L1073–L1077](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L1073-L1077),
  [L402–L429](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L402-L429)),
  and `SendReset` sends RST_STREAM with the error code CANCEL
  ([L362–L378](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L362-L378)).
  The connection stays open for its other streams. RST_STREAM "is sent to request cancellation of a stream"
  ([RFC 9113 §6.4](https://www.rfc-editor.org/rfc/rfc9113.html#section-6.4)).

**Kestrel watches.** It keeps a receive pending on every connection:
[`SocketConnection.DoReceive`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs#L136-L171)
loops on `ReceiveAsync`, and a zero-byte read is logged as a FIN. When the loop ends, for a FIN, a reset or a
shutdown, its `finally` completes the input and fires `ConnectionClosed`
([L258–L263](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs#L258-L263)).
`HttpConnection` is registered on that token
([L117](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/HttpConnection.cs#L117),
[L172–L184](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/HttpConnection.cs#L172-L184))
and calls `OnInputOrOutputCompleted`, which cancels `RequestAborted`
([`Http1Connection`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/Http/Http1Connection.cs#L131-L136)).
For HTTP/2, a received RST_STREAM cancels the same token for that one stream
([`ProcessRstStreamFrameAsync`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Connection.cs#L955-L989)
→ [`AbortRstStreamReceived`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L569-L629)
→ `CancelRequestAbortedToken`).
[`HttpContext.RequestAborted`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.httpcontext.requestaborted)
"Notifies when the connection underlying this request is aborted and thus request operations should be cancelled."

**ASP.NET Core's HTTP.sys server asks the driver.** For each connection it calls `HttpWaitForDisconnectEx` with an
overlapped callback that cancels a `CancellationTokenSource`, one token per connection
([`DisconnectListener`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/HttpSys/src/NativeInterop/DisconnectListener.cs#L54-L116)).
[`HttpWaitForDisconnect`](https://learn.microsoft.com/windows/win32/api/http/nf-http-httpwaitfordisconnect)
"notifies the application when the connection to an HTTP client is broken for any reason". It takes the request
queue's handle and the connection's id.

## `HttpListener`: watching that never reaches the application

- **Windows** (built on HTTP.sys) does call `HttpWaitForDisconnect`, but only for connection-based authentication, so
  that cached NTLM or Negotiate state is dropped when the connection ends
  ([L985–L988](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Windows/HttpListener.Windows.cs#L985-L988),
  [L1118–L1121](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Windows/HttpListener.Windows.cs#L1118-L1121),
  [L1399–L1414](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Windows/HttpListener.Windows.cs#L1399-L1414)).
  The application is not told.
- **macOS and Linux** use a managed implementation. It reads the socket until the request's headers are complete,
  hands the context to the application, and issues no further read until the response is done
  ([`HttpConnection.OnReadInternal`](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/src/System/Net/Managed/HttpConnection.cs#L260-L325)).
  While the application works, nothing watches the connection.
- **The public API** ([reference assembly](https://github.com/dotnet/runtime/blob/591622c69134f9a790f902e364c7a6a5c572b12d/src/libraries/System.Net.HttpListener/ref/System.Net.HttpListener.cs))
  exposes no connection id, no request-queue handle, no socket and no token, so the application cannot do the
  watching itself. A client that left is found when the response is written.

## What watching cannot see

- **No pending read, no signal.** Kestrel stops receiving when its input pipe is full, until the application reads
  ([`DoReceive`](https://github.com/dotnet/aspnetcore/blob/4f556f73a610b386e5ec29355a300e493d30c4c5/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs#L175-L195)).
  An application that does not consume a large request body can stop its own disconnect detection.
- **A FIN is ambiguous in HTTP/1.1.** Kestrel ends the connection on the client's FIN. RFC 9112 §9.6 says a
  half-closed connection "does not delimit a request message, nor does it imply that the client is no longer
  interested in a response" ([§9.6](https://www.rfc-editor.org/rfc/rfc9112.html#section-9.6)). HTTP/1.1 itself
  offers only "A client, server, or proxy MAY close the transport connection at any time"
  ([§9.5](https://www.rfc-editor.org/rfc/rfc9112.html#section-9.5)).
- **A peer that vanishes sends nothing.** After a power loss or a dropped route there is no FIN and no reset. The
  server learns of it only from a failed write, or from TCP keep-alive, which "MUST default to off" and, when on,
  waits "no less than two hours" by default ([RFC 9293 §3.8.4](https://www.rfc-editor.org/rfc/rfc9293.html#section-3.8.4)).

## What follows for any design

1. **A disconnect token is best effort.** It fires when the transport watches, a read is pending and the peer said
   goodbye. Work that must end still needs its own time limit.
2. **The token helps only if it is passed.** `RequestAborted` cancels nothing by itself; the work it should stop must
   take it and check it. Holding a token and not passing it on is the common failure (Mercury's HTTP server held its
   shutdown token and did not pass it to the query; ADR-060).
3. **The transport decides.** If it does not watch the connection, the application cannot add that later from
   above it. With `HttpListener`, cancelling on disconnect means changing the transport.
4. **HTTP/2 can cancel one request without closing the connection**; HTTP/1.1 cannot. A client that must cancel
   long-running requests while keeping its connection gets that only with HTTP/2 or later.
5. **Read the source, not the surface.** A reflection listing answered "what is public", and we read it as "what is
   possible". The mechanism was in the implementation.

## For Mercury

Mercury's SPARQL HTTP server runs on `HttpListener`, so it cannot see a client leave while a query runs. ADR-060
records this and leaves cancelling on disconnect to a transport that watches the connection (its Q7): Mercury's own
HTTP over `System.Net.Sockets`, which keeps Mercury BCL-only, or ASP.NET Core. ADR-060 is deferred while DrHook comes
first; until it lands, a one-second limit on each regex match is the only bound, and a query as a whole has none.
